using System.Globalization;
using System.Text;
using GpuProfit.Core.Domain;

namespace GpuProfit.Export;

/// <summary>Where imported or externally supplied data comes from (recorded with every imported value).</summary>
public sealed record ExternalDataSource(string Provider, string Kind, DateTimeOffset ImportedAt, string? FileName, string Validity);

public sealed record ImportProblem(int Line, string Message);

public sealed record PayoutImportResult(IReadOnlyList<RevenueEntry> Entries, IReadOnlyList<ImportProblem> Problems, ExternalDataSource Source, int RowsRead);

/// <summary>Column names to look for (case-insensitive); the first match wins.</summary>
public sealed record PayoutColumns
{
    public IReadOnlyList<string> Time { get; init; } = ["time", "timestamp", "date", "datetime", "paid_at", "created_at", "일시", "날짜", "시각"];
    public IReadOnlyList<string> Amount { get; init; } = ["amount", "value", "payout", "reward", "revenue", "금액", "수익"];
    public IReadOnlyList<string> Currency { get; init; } = ["currency", "coin", "asset", "unit", "통화", "단위"];
    public IReadOnlyList<string> Id { get; init; } = ["id", "txid", "tx", "tx_id", "transaction", "hash", "payout_id", "거래id", "트랜잭션"];
    public IReadOnlyList<string> Note { get; init; } = ["note", "memo", "description", "comment", "메모", "비고"];
    public IReadOnlyList<string> KrwRate { get; init; } = ["krw_rate", "krw_per_unit", "rate_krw", "원화환율"];
}

/// <summary>
/// Imports a payout or earnings history exported by the user from a pool, marketplace or cloud console (CSV file).
/// No network access: this is the external-data path that needs no unverified API. Each row becomes a revenue entry
/// with its provider, external id (duplicates are skipped by the store) and origin.
/// </summary>
public static class PayoutCsvImporter
{
    public const int MaxRows = 100_000;

    public static PayoutImportResult Parse(string path, string provider, RevenueKind kind, string? defaultCurrency, TimeZoneInfo zone, DateTimeOffset now,
        PayoutColumns? columns = null)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader, provider, kind, defaultCurrency, zone, now, Path.GetFileName(path), columns);
    }

    public static PayoutImportResult Parse(TextReader text, string provider, RevenueKind kind, string? defaultCurrency, TimeZoneInfo zone, DateTimeOffset now,
        string? fileName = null, PayoutColumns? columns = null)
    {
        columns ??= new PayoutColumns();
        provider = (provider ?? "").Trim();
        var source = new ExternalDataSource(provider, "payout_csv", now, fileName,
            kind == RevenueKind.Received ? "사용자가 '실제 수령'으로 지정" : "외부 서비스 보고값 (수령 확인 전)");
        var problems = new List<ImportProblem>();
        var entries = new List<RevenueEntry>();
        if (provider.Length == 0)
        {
            problems.Add(new ImportProblem(0, "공급자(서비스) 이름이 필요합니다."));
            return new PayoutImportResult(entries, problems, source, 0);
        }

        if (SensitiveTextGuard.Check(provider) is { } providerReason)
        {
            problems.Add(new ImportProblem(0, providerReason));
            return new PayoutImportResult(entries, problems, source, 0);
        }

        var rows = ReadCsv(text).ToList();
        if (rows.Count == 0)
        {
            problems.Add(new ImportProblem(0, "빈 파일입니다."));
            return new PayoutImportResult(entries, problems, source, 0);
        }

        var header = rows[0].Fields.Select(h => h.Trim().TrimStart('﻿').ToLowerInvariant()).ToList();
        int Find(IReadOnlyList<string> names) => names.Select(n => header.IndexOf(n.ToLowerInvariant())).FirstOrDefault(i => i >= 0, -1);
        var cTime = Find(columns.Time);
        var cAmount = Find(columns.Amount);
        var cCurrency = Find(columns.Currency);
        var cId = Find(columns.Id);
        var cNote = Find(columns.Note);
        var cRate = Find(columns.KrwRate);
        if (cTime < 0 || cAmount < 0)
        {
            problems.Add(new ImportProblem(1, $"필수 열을 찾지 못했습니다 (시각: {string.Join("/", columns.Time.Take(4))}…, 금액: {string.Join("/", columns.Amount.Take(4))}…). 첫 줄은 열 이름이어야 합니다."));
            return new PayoutImportResult(entries, problems, source, rows.Count - 1);
        }

        if (cCurrency < 0 && CurrencyCodes.Normalize(defaultCurrency) is null)
        {
            problems.Add(new ImportProblem(1, "통화 열이 없으면 기본 통화를 지정해야 합니다."));
            return new PayoutImportResult(entries, problems, source, rows.Count - 1);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, fields) in rows.Skip(1).Take(MaxRows))
        {
            string? Get(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : null;
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (!TryParseTime(Get(cTime), zone, out var occurred))
            {
                problems.Add(new ImportProblem(line, $"시각을 읽을 수 없습니다: '{Get(cTime)}'"));
                continue;
            }

            if (!DecimalText.TryParseUser(Get(cAmount), out var amount))
            {
                problems.Add(new ImportProblem(line, $"금액을 읽을 수 없습니다: '{Get(cAmount)}'"));
                continue;
            }

            if (amount < 0)
            {
                problems.Add(new ImportProblem(line, "음수 금액은 수익으로 가져오지 않습니다 (차감·환불은 비용으로 입력하세요)."));
                continue;
            }

            var currency = CurrencyCodes.Normalize(Get(cCurrency)) ?? CurrencyCodes.Normalize(defaultCurrency);
            if (currency is null)
            {
                problems.Add(new ImportProblem(line, $"통화 코드를 알 수 없습니다: '{Get(cCurrency)}'"));
                continue;
            }

            decimal? rate = null;
            if (Get(cRate) is { Length: > 0 } rateText)
            {
                if (!DecimalText.TryParseUser(rateText, out var r) || r <= 0)
                {
                    problems.Add(new ImportProblem(line, $"원화 환율을 읽을 수 없습니다: '{rateText}'"));
                    continue;
                }

                rate = r;
            }

            var note = Get(cNote);
            if (SensitiveTextGuard.Check(note) is not null)
            {
                note = null; // secrets are never stored; the rest of the row is still imported
                problems.Add(new ImportProblem(line, "메모 열에 비밀값으로 보이는 내용이 있어 메모를 비우고 가져왔습니다."));
            }

            var id = Get(cId);
            if (string.IsNullOrEmpty(id))
            {
                // Without an id the row's content identifies it, so importing the same file twice adds nothing.
                id = $"row:{occurred.ToUnixTimeMilliseconds()}:{DecimalText.Format(amount)}:{currency}";
            }

            if (!seenIds.Add(id))
            {
                problems.Add(new ImportProblem(line, $"같은 파일 안에 같은 식별자가 다시 나왔습니다: {id}"));
                continue;
            }

            entries.Add(new RevenueEntry
            {
                Kind = kind,
                Amount = amount,
                Currency = currency,
                IsGross = false,
                Occurred = occurred,
                Provider = provider,
                ExternalId = id,
                Note = note,
                Origin = ValueOrigin.ExternalReported, // the numbers come from the provider's file, whatever kind the user assigns
                KrwPerUnit = rate,
                RateAsOf = rate is null ? null : occurred,
                RateSource = rate is null ? null : $"{provider} 가져오기 파일",
            });
        }

        if (rows.Count - 1 > MaxRows)
        {
            problems.Add(new ImportProblem(MaxRows + 1, $"한 번에 {MaxRows:N0}행까지만 가져옵니다."));
        }

        return new PayoutImportResult(entries, problems, source, rows.Count - 1);
    }

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd", "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd",
    ];

    /// <summary>ISO 8601 with offset, Unix seconds/milliseconds, or a local date/time in <paramref name="zone"/>.</summary>
    internal static bool TryParseTime(string? text, TimeZoneInfo zone, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            if (unix is > 100_000_000 and < 10_000_000_000)
            {
                value = DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }

            if (unix is >= 10_000_000_000 and < 10_000_000_000_000)
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(unix);
                return true;
            }

            return false;
        }

        var hasOffset = text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");
        if (hasOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        if (DateTime.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            value = new DateTimeOffset(local, zone.GetUtcOffset(local));
            return true;
        }

        return false;
    }

    /// <summary>RFC 4180 reader (quoted fields, doubled quotes, line breaks inside quotes). Yields (line number, fields).</summary>
    internal static IEnumerable<(int Line, List<string> Fields)> ReadCsv(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var startLine = 1;
        int ch;
        var any = false;
        while ((ch = reader.Read()) != -1)
        {
            any = true;
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (startLine, fields);
                    fields = [];
                    line++;
                    startLine = line;
                    any = false;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (any || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (startLine, fields);
        }
    }

    /// <summary>Assigns each entry to the session whose recorded segments contain its time (or leaves it unassigned).</summary>
    public static IReadOnlyList<RevenueEntry> AssignToSessions(IReadOnlyList<RevenueEntry> entries,
        IReadOnlyDictionary<long, IReadOnlyList<SessionSegment>> segments, DateTimeOffset now)
        => entries.Select(e =>
        {
            var match = segments.FirstOrDefault(kv => kv.Value.Any(g => e.Occurred >= g.Start && e.Occurred <= (g.End ?? now)));
            return match.Value is null ? e : e with { SessionId = match.Key };
        }).ToList();
}
