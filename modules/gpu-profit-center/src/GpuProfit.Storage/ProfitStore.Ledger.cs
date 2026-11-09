using System.Text.Json;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace GpuProfit.Storage;

public sealed partial class ProfitStore
{
    // ------------------------------------------------------------------ revenue

    public long AddRevenue(RevenueEntry e) => Write(c => InsertRevenue(c, null, e, ignoreDuplicate: false) ?? 0);

    /// <summary>Inserts an imported entry unless the same provider + external id is already stored. Returns null for a duplicate.</summary>
    public long? AddRevenueIfNew(RevenueEntry e) => Write(c => InsertRevenue(c, null, e, ignoreDuplicate: true));

    /// <summary>Imports many entries in one transaction (all or nothing). Returns (inserted, duplicates).</summary>
    public (int Inserted, int Duplicates) ImportRevenue(IReadOnlyList<RevenueEntry> entries) => WriteTx((c, tx) =>
    {
        int inserted = 0, duplicates = 0;
        foreach (var e in entries)
        {
            if (InsertRevenue(c, tx, e, ignoreDuplicate: true) is null)
            {
                duplicates++;
            }
            else
            {
                inserted++;
            }
        }

        return (inserted, duplicates);
    });

    private long? InsertRevenue(SqliteConnection c, SqliteTransaction? tx, RevenueEntry e, bool ignoreDuplicate)
    {
        ValidateRevenue(e);
        var sql = $"""
            INSERT {(ignoreDuplicate ? "OR IGNORE" : "")} INTO revenue_entries (session_id, workload_id, kind, amount, currency, is_gross, occurred_ms, provider,
                external_id, note, origin, krw_per_unit, rate_as_of_ms, rate_source, created_ms)
            VALUES (@s, @w, @k, @a, @cur, @g, @o, @p, @x, @n, @org, @r, @ra, @rs, @c) RETURNING id;
            """;
        return Scalar(c, sql, tx, RevenueParameters(e)) is { } id ? Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private (string, object?)[] RevenueParameters(RevenueEntry e) =>
    [
        ("@s", e.SessionId), ("@w", e.WorkloadId), ("@k", (int)e.Kind), ("@a", DecimalText.Format(e.Amount)), ("@cur", CurrencyCodes.Normalize(e.Currency)),
        ("@g", e.IsGross ? 1 : 0), ("@o", e.Occurred.ToUnixTimeMilliseconds()), ("@p", e.Provider), ("@x", string.IsNullOrWhiteSpace(e.ExternalId) ? null : e.ExternalId.Trim()),
        ("@n", e.Note), ("@org", (int)e.Origin), ("@r", Dec(e.KrwPerUnit)), ("@ra", ToMs(e.RateAsOf)), ("@rs", e.RateSource),
        ("@c", _time.GetUtcNow().ToUnixTimeMilliseconds()),
    ];

    private static void ValidateRevenue(RevenueEntry e)
    {
        if (CurrencyCodes.Normalize(e.Currency) is null)
        {
            throw new ArgumentException($"통화 코드 '{e.Currency}'가 올바르지 않습니다 (영문·숫자 2~10자).");
        }

        if (e.Amount < 0)
        {
            throw new ArgumentException("수익 금액은 0 이상이어야 합니다. 환불·차감은 비용으로 입력하세요.");
        }

        if (e.KrwPerUnit is <= 0)
        {
            throw new ArgumentException("환율은 0보다 커야 합니다.");
        }

        Guard(e.Note, e.Provider, e.RateSource);
    }

    public void UpdateRevenue(RevenueEntry e)
    {
        ValidateRevenue(e);
        Write(c => Execute(c, """
            UPDATE revenue_entries SET session_id = @s, workload_id = @w, kind = @k, amount = @a, currency = @cur, is_gross = @g, occurred_ms = @o,
                provider = @p, external_id = @x, note = @n, origin = @org, krw_per_unit = @r, rate_as_of_ms = @ra, rate_source = @rs
            WHERE id = @id;
            """, null, [.. RevenueParameters(e), ("@id", e.Id)]));
    }

    public void DeleteRevenue(long id) => Write(c => Execute(c, "DELETE FROM revenue_entries WHERE id = @id;", null, ("@id", id)));

    public IReadOnlyList<RevenueEntry> ListRevenue(long? sessionId = null)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, workload_id, kind, amount, currency, is_gross, occurred_ms, provider, external_id, note, origin, krw_per_unit,
                   rate_as_of_ms, rate_source, created_ms
            FROM revenue_entries WHERE (@s IS NULL OR session_id = @s) ORDER BY occurred_ms, id;
            """;
        command.Parameters.AddWithValue("@s", (object?)sessionId ?? DBNull.Value);
        var list = new List<RevenueEntry>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RevenueEntry
            {
                Id = r.GetInt64(0), SessionId = L(r, 1), WorkloadId = L(r, 2), Kind = (RevenueKind)r.GetInt32(3), Amount = DecimalText.Parse(r.GetString(4)),
                Currency = r.GetString(5), IsGross = r.GetInt64(6) != 0, Occurred = At(r.GetInt64(7)), Provider = S(r, 8), ExternalId = S(r, 9),
                Note = S(r, 10), Origin = (ValueOrigin)r.GetInt32(11), KrwPerUnit = Dec(S(r, 12)), RateAsOf = Ms(L(r, 13)), RateSource = S(r, 14),
                Created = At(r.GetInt64(15)),
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ costs

    public long AddCost(CostEntry e)
    {
        ValidateCost(e);
        return Write(c => ScalarLong(c, """
            INSERT INTO cost_entries (session_id, workload_id, category, basis, amount, currency, occurred_ms, note, active, origin, created_ms)
            VALUES (@s, @w, @cat, @b, @a, @cur, @o, @n, @act, @org, @c) RETURNING id;
            """, null, CostParameters(e)));
    }

    public void UpdateCost(CostEntry e)
    {
        ValidateCost(e);
        Write(c => Execute(c, """
            UPDATE cost_entries SET session_id = @s, workload_id = @w, category = @cat, basis = @b, amount = @a, currency = @cur, occurred_ms = @o,
                note = @n, active = @act, origin = @org WHERE id = @id;
            """, null, [.. CostParameters(e), ("@id", e.Id)]));
    }

    private (string, object?)[] CostParameters(CostEntry e) =>
    [
        ("@s", e.SessionId), ("@w", e.WorkloadId), ("@cat", (int)e.Category), ("@b", (int)e.Basis), ("@a", DecimalText.Format(e.Amount)),
        ("@cur", CurrencyCodes.Normalize(e.Currency)), ("@o", e.Occurred.ToUnixTimeMilliseconds()), ("@n", e.Note), ("@act", e.Active ? 1 : 0),
        ("@org", (int)e.Origin), ("@c", _time.GetUtcNow().ToUnixTimeMilliseconds()),
    ];

    private static void ValidateCost(CostEntry e)
    {
        if (CurrencyCodes.Normalize(e.Currency) is null)
        {
            throw new ArgumentException($"통화 코드 '{e.Currency}'가 올바르지 않습니다.");
        }

        if (e.Amount < 0)
        {
            throw new ArgumentException("비용 금액은 0 이상이어야 합니다.");
        }

        if (e.Basis == CostBasis.PercentOfRevenue && e.Amount >= 100)
        {
            throw new ArgumentException("수수료 비율은 100% 미만이어야 합니다.");
        }

        Guard(e.Note);
    }

    public void DeleteCost(long id) => Write(c => Execute(c, "DELETE FROM cost_entries WHERE id = @id;", null, ("@id", id)));

    public IReadOnlyList<CostEntry> ListCosts()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, session_id, workload_id, category, basis, amount, currency, occurred_ms, note, active, origin, created_ms FROM cost_entries ORDER BY id;";
        var list = new List<CostEntry>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new CostEntry
            {
                Id = r.GetInt64(0), SessionId = L(r, 1), WorkloadId = L(r, 2), Category = (CostCategory)r.GetInt32(3), Basis = (CostBasis)r.GetInt32(4),
                Amount = DecimalText.Parse(r.GetString(5)), Currency = r.GetString(6), Occurred = At(r.GetInt64(7)), Note = S(r, 8),
                Active = r.GetInt64(9) != 0, Origin = (ValueOrigin)r.GetInt32(10), Created = At(r.GetInt64(11)),
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ equipment

    public long AddEquipment(EquipmentAsset a)
    {
        ValidateEquipment(a);
        return Write(c => ScalarLong(c, """
            INSERT INTO equipment (name, price_krw, salvage_krw, purchased_ms, method, life_months, life_hours, workload_id, note, active)
            VALUES (@n, @p, @sv, @pd, @m, @lm, @lh, @w, @no, @a) RETURNING id;
            """, null, EquipmentParameters(a)));
    }

    public void UpdateEquipment(EquipmentAsset a)
    {
        ValidateEquipment(a);
        Write(c => Execute(c, """
            UPDATE equipment SET name = @n, price_krw = @p, salvage_krw = @sv, purchased_ms = @pd, method = @m, life_months = @lm, life_hours = @lh,
                workload_id = @w, note = @no, active = @a WHERE id = @id;
            """, null, [.. EquipmentParameters(a), ("@id", a.Id)]));
    }

    private static (string, object?)[] EquipmentParameters(EquipmentAsset a) =>
    [
        ("@n", a.Name.Trim()), ("@p", DecimalText.Format(a.PriceKrw)), ("@sv", DecimalText.Format(a.SalvageKrw)), ("@pd", a.Purchased.ToUnixTimeMilliseconds()),
        ("@m", (int)a.Method), ("@lm", a.LifeMonths), ("@lh", a.LifeHours), ("@w", a.WorkloadId), ("@no", a.Note), ("@a", a.Active ? 1 : 0),
    ];

    private static void ValidateEquipment(EquipmentAsset a)
    {
        if (string.IsNullOrWhiteSpace(a.Name))
        {
            throw new ArgumentException("장비 이름이 필요합니다.");
        }

        if (a.PriceKrw < 0 || a.SalvageKrw < 0 || a.SalvageKrw > a.PriceKrw)
        {
            throw new ArgumentException("장비 가격과 잔존가치는 0 이상이며, 잔존가치는 가격 이하여야 합니다.");
        }

        if (a.Method == DepreciationMethod.StraightLineMonths && !(a.LifeMonths > 0))
        {
            throw new ArgumentException("감가상각 기간(개월)은 0보다 커야 합니다.");
        }

        if (a.Method == DepreciationMethod.UsageHours && a.LifeHours is not > 0)
        {
            throw new ArgumentException("사용 시간 기준 감가상각에는 수명(시간)이 필요합니다.");
        }

        Guard(a.Name, a.Note);
    }

    public void DeleteEquipment(long id) => Write(c => Execute(c, "DELETE FROM equipment WHERE id = @id;", null, ("@id", id)));

    public IReadOnlyList<EquipmentAsset> ListEquipment()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, name, price_krw, salvage_krw, purchased_ms, method, life_months, life_hours, workload_id, note, active FROM equipment ORDER BY id;";
        var list = new List<EquipmentAsset>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EquipmentAsset
            {
                Id = r.GetInt64(0), Name = r.GetString(1), PriceKrw = DecimalText.Parse(r.GetString(2)), SalvageKrw = DecimalText.Parse(r.GetString(3)),
                Purchased = At(r.GetInt64(4)), Method = (DepreciationMethod)r.GetInt32(5), LifeMonths = r.GetDouble(6), LifeHours = D(r, 7),
                WorkloadId = L(r, 8), Note = S(r, 9), Active = r.GetInt64(10) != 0,
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ exchange rates

    public long AddRate(ExchangeRate rate)
    {
        var code = CurrencyCodes.Normalize(rate.Currency) ?? throw new ArgumentException($"통화 코드 '{rate.Currency}'가 올바르지 않습니다.");
        if (code == CurrencyCodes.Krw)
        {
            throw new ArgumentException("KRW는 환율이 필요 없습니다.");
        }

        if (rate.KrwPerUnit <= 0)
        {
            throw new ArgumentException("환율은 0보다 커야 합니다.");
        }

        Guard(rate.Source, rate.Note);
        return Write(c => ScalarLong(c, """
            INSERT INTO exchange_rates (currency, krw_per_unit, as_of_ms, source, origin, note) VALUES (@c, @r, @a, @s, @o, @n)
            ON CONFLICT (currency, as_of_ms, source) DO UPDATE SET krw_per_unit = excluded.krw_per_unit, note = excluded.note
            RETURNING id;
            """, null, ("@c", code), ("@r", DecimalText.Format(rate.KrwPerUnit)), ("@a", rate.AsOf.ToUnixTimeMilliseconds()), ("@s", rate.Source ?? ""),
            ("@o", (int)rate.Origin), ("@n", rate.Note)));
    }

    public void DeleteRate(long id) => Write(c => Execute(c, "DELETE FROM exchange_rates WHERE id = @id;", null, ("@id", id)));

    public IReadOnlyList<ExchangeRate> ListRates()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, currency, krw_per_unit, as_of_ms, source, origin, note FROM exchange_rates ORDER BY currency, as_of_ms;";
        var list = new List<ExchangeRate>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ExchangeRate
            {
                Id = r.GetInt64(0), Currency = r.GetString(1), KrwPerUnit = DecimalText.Parse(r.GetString(2)), AsOf = At(r.GetInt64(3)),
                Source = S(r, 4) is { Length: > 0 } s ? s : null, Origin = (ValueOrigin)r.GetInt32(5), Note = S(r, 6),
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ calculation results

    public static StoredResultSummary Summarize(SessionAccountingResult r)
    {
        var realized = r.View(RevenueView.Realized);
        var unconfirmed = r.View(RevenueView.IncludingUnconfirmed);
        var expected = r.View(RevenueView.Expected);
        return new StoredResultSummary
        {
            RecordedHours = r.RecordedSeconds / 3600,
            BasisHours = r.BasisHours,
            TotalKwh = r.Energy.TotalKwh.Value,
            GpuMeasuredKwh = r.Energy.GpuMeasuredWh / 1000,
            Coverage = r.Energy.Coverage,
            EstimatedShare = r.Energy.EstimatedShare,
            ElectricityCost = r.Electricity.CostKrw.Value,
            RateKrwPerKwh = r.Electricity.RateKrwPerKwh.Value,
            RealizedRevenue = realized.Revenue.Value,
            RealizedNet = realized.NetProfit.Value,
            UnconfirmedRevenue = unconfirmed.Revenue.Value,
            UnconfirmedNet = unconfirmed.NetProfit.Value,
            ExpectedRevenue = expected.Revenue.Value,
            ExpectedNet = expected.NetProfit.Value,
            TotalCost = realized.TotalCost.Value,
            BreakevenRevenue = r.BreakevenRevenue.Value,
            BreakevenRate = realized.BreakevenElectricityRate.Value,
            EnergyBasis = r.Energy.Conditions.Basis.ToString(),
            Warnings = r.Warnings,
        };
    }

    /// <summary>
    /// Saves a calculation unless the latest saved one has the same inputs hash. Returns true when a new row was added
    /// (the result history keeps every distinct calculation with its conditions).
    /// </summary>
    public bool SaveResult(SessionAccountingResult r, string reason) => WriteTx((c, tx) =>
    {
        var last = Scalar(c, "SELECT inputs_hash FROM session_results WHERE session_id = @s ORDER BY computed_ms DESC, id DESC LIMIT 1;", tx, ("@s", r.SessionId)) as string;
        if (string.Equals(last, r.InputsHash, StringComparison.Ordinal))
        {
            return false;
        }

        Execute(c, """
            INSERT INTO session_results (session_id, computed_ms, engine_version, inputs_hash, reason, summary_json, conditions_json)
            VALUES (@s, @t, @v, @h, @r, @sum, @cond);
            """, tx, ("@s", r.SessionId), ("@t", r.ComputedAt.ToUnixTimeMilliseconds()), ("@v", SessionAccountingResult.EngineVersion), ("@h", r.InputsHash),
            ("@r", reason), ("@sum", JsonSerializer.Serialize(Summarize(r), Json)), ("@cond", JsonSerializer.Serialize(r.Conditions, Json)));
        return true;
    });

    public IReadOnlyList<StoredResult> ListResults(long? sessionId = null, bool latestOnly = false)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = latestOnly
            ? """
              SELECT id, session_id, computed_ms, engine_version, inputs_hash, reason, summary_json, conditions_json FROM session_results r
              WHERE (@s IS NULL OR session_id = @s)
                AND id = (SELECT id FROM session_results x WHERE x.session_id = r.session_id ORDER BY computed_ms DESC, id DESC LIMIT 1)
              ORDER BY session_id;
              """
            : """
              SELECT id, session_id, computed_ms, engine_version, inputs_hash, reason, summary_json, conditions_json FROM session_results
              WHERE (@s IS NULL OR session_id = @s) ORDER BY session_id, computed_ms DESC, id DESC;
              """;
        command.Parameters.AddWithValue("@s", (object?)sessionId ?? DBNull.Value);
        var list = new List<StoredResult>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoredResult
            {
                Id = r.GetInt64(0), SessionId = r.GetInt64(1), Computed = At(r.GetInt64(2)), EngineVersion = r.GetInt32(3), InputsHash = r.GetString(4),
                Reason = r.GetString(5),
                Summary = JsonSerializer.Deserialize<StoredResultSummary>(r.GetString(6), Json) ?? new StoredResultSummary(),
                Conditions = JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(r.GetString(7), Json) ?? [],
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ scenarios

    public long SaveScenario(ScenarioRecord s)
    {
        Guard(s.Name, s.Notes);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return Write(c =>
        {
            if (s.Id == 0)
            {
                return ScalarLong(c, """
                    INSERT INTO scenarios (name, kind, params_json, source_session_id, source_hash, notes, last_result_json, created_ms, updated_ms)
                    VALUES (@n, @k, @p, @ss, @sh, @no, @lr, @t, @t) RETURNING id;
                    """, null, ("@n", s.Name.Trim()), ("@k", s.Kind), ("@p", s.ParamsJson), ("@ss", s.SourceSessionId), ("@sh", s.SourceHash), ("@no", s.Notes),
                    ("@lr", s.LastResultJson), ("@t", now));
            }

            Execute(c, """
                UPDATE scenarios SET name = @n, kind = @k, params_json = @p, source_session_id = @ss, source_hash = @sh, notes = @no,
                    last_result_json = @lr, updated_ms = @t WHERE id = @id;
                """, null, ("@n", s.Name.Trim()), ("@k", s.Kind), ("@p", s.ParamsJson), ("@ss", s.SourceSessionId), ("@sh", s.SourceHash), ("@no", s.Notes),
                ("@lr", s.LastResultJson), ("@t", now), ("@id", s.Id));
            return s.Id;
        });
    }

    public void DeleteScenario(long id) => Write(c => Execute(c, "DELETE FROM scenarios WHERE id = @id;", null, ("@id", id)));

    public IReadOnlyList<ScenarioRecord> ListScenarios()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, name, kind, params_json, source_session_id, source_hash, notes, last_result_json, created_ms, updated_ms FROM scenarios ORDER BY kind, name;";
        var list = new List<ScenarioRecord>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ScenarioRecord
            {
                Id = r.GetInt64(0), Name = r.GetString(1), Kind = r.GetInt32(2), ParamsJson = r.GetString(3), SourceSessionId = L(r, 4), SourceHash = S(r, 5),
                Notes = S(r, 6), LastResultJson = S(r, 7), Created = At(r.GetInt64(8)), Updated = At(r.GetInt64(9)),
            });
        }

        return list;
    }
}
