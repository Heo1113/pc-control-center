using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Accounting.Scenarios;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation.ViewModels;

public sealed record TornadoDisplayRow(string Variable, string Low, string NetAtLow, string High, string NetAtHigh, string Swing);

public sealed record ScenarioCompareRow(string Scenario, string Kind, string Hours, string Energy, string Revenue, string Electricity, string TotalCost, string Net,
    string NetPerHour, string BreakevenRate, string Payback);

/// <summary>
/// "What if" calculations: optimistic / base / pessimistic / custom scenarios, one-at-a-time sensitivity with the
/// breakeven point, and a tornado table. These are conditional calculations on the user's assumptions, not forecasts.
/// </summary>
public sealed partial class ScenariosViewModel : PageViewModel
{
    public const string Disclaimer =
        "시나리오는 입력한 조건이 그대로 유지된다고 가정한 조건부 계산입니다. 시장가격·작업 수익·전기요금의 미래 값을 예측하지 않습니다.";

    private sealed record StoredScenarioResult(decimal NetProfit, double EnergyKwh, DateTimeOffset Computed);

    private ScenarioRecord? _current;

    public ScenariosViewModel(ViewContext context) : base(context)
    {
        Kinds = ScenarioKindText.All.Select(k => new Option<ScenarioKind>(ScenarioKindText.ToKorean(k), k)).ToList();
        Variables = SensitivityVariableText.All.Select(v => new Option<SensitivityVariable>(SensitivityVariableText.ToKorean(v), v)).ToList();
        SelectedKind = Kinds[(int)ScenarioKind.Base];
        SelectedVariable = Variables[(int)SensitivityVariable.ElectricityRate];
        LoadFields(Defaults());
    }

    public override string Title => "시나리오";
    public override string Glyph => "";

    public string Note => Disclaimer;
    public IReadOnlyList<Option<ScenarioKind>> Kinds { get; }
    public IReadOnlyList<Option<SensitivityVariable>> Variables { get; }
    public ObservableCollection<ScenarioRecord> Scenarios { get; } = [];
    public ObservableCollection<Option<long>> SessionOptions { get; } = [];
    public ObservableCollection<NameValueRow> Result { get; } = [];
    public ObservableCollection<TornadoDisplayRow> Tornado { get; } = [];
    public ObservableCollection<ScenarioCompareRow> Comparison { get; } = [];

    [ObservableProperty] public partial ScenarioRecord? SelectedScenario { get; set; }
    [ObservableProperty] public partial Option<long>? SourceSession { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial Option<ScenarioKind> SelectedKind { get; set; }
    [ObservableProperty] public partial string RevenuePerHour { get; set; } = "";
    [ObservableProperty] public partial string GpuPower { get; set; } = "";
    [ObservableProperty] public partial string SystemPower { get; set; } = "";
    [ObservableProperty] public partial string Rate { get; set; } = "";
    [ObservableProperty] public partial string Utilization { get; set; } = "";
    [ObservableProperty] public partial string HoursPerDay { get; set; } = "";
    [ObservableProperty] public partial string Days { get; set; } = "";
    [ObservableProperty] public partial string Fee { get; set; } = "";
    [ObservableProperty] public partial string FixedPerMonth { get; set; } = "";
    [ObservableProperty] public partial string EquipmentCost { get; set; } = "";
    [ObservableProperty] public partial string Salvage { get; set; } = "";
    [ObservableProperty] public partial string LifeMonths { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial Option<SensitivityVariable> SelectedVariable { get; set; }
    [ObservableProperty] public partial string RangeLow { get; set; } = "";
    [ObservableProperty] public partial string RangeHigh { get; set; } = "";
    [ObservableProperty] public partial string TornadoPercent { get; set; } = "20";
    [ObservableProperty] public partial ChartModel? SensitivityChart { get; set; }
    [ObservableProperty] public partial string BreakevenText { get; set; } = "";
    [ObservableProperty] public partial string TrackingText { get; set; } = "";
    [ObservableProperty] public partial bool HasResult { get; set; }

    partial void OnSelectedScenarioChanged(ScenarioRecord? value)
    {
        if (value is null)
        {
            return;
        }

        _current = value;
        Name = value.Name;
        SelectedKind = Kinds.FirstOrDefault(k => (int)k.Value == value.Kind) ?? Kinds[3];
        Notes = value.Notes ?? "";
        try
        {
            LoadFields(JsonSerializer.Deserialize<ScenarioParameters>(value.ParamsJson) ?? Defaults());
        }
        catch (JsonException)
        {
            StatusText = "저장된 시나리오 조건을 읽을 수 없어 기본값을 표시합니다 (저장된 값은 그대로 둠).";
            LoadFields(Defaults());
        }

        _ = Busy(CalculateCoreAsync);
    }

    partial void OnSelectedVariableChanged(Option<SensitivityVariable> value)
    {
        if (TryRead(out var p))
        {
            var (low, high) = SensitivityAnalyzer.DefaultRange(p, value.Value);
            RangeLow = low.ToString("0.##", CultureInfo.InvariantCulture);
            RangeHigh = high.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    public override Task OnActivatedAsync() => Busy(async () =>
    {
        var keep = SelectedScenario?.Id;
        Replace(Scenarios, await Task.Run(() => Context.Store.ListScenarios()));
        var sessions = Context.Store.ListSessions(new SessionQuery { Limit = 500 }).Where(s => !s.IsOpen);
        Replace(SessionOptions, sessions.Select(s => new Option<long>($"#{s.Id} {s.Title} · {Fmt.ShortTime(s.Started)}{(s.IsMock ? " · MOCK" : "")}", s.Id)));
        SourceSession ??= SessionOptions.FirstOrDefault();
        if (Scenarios.FirstOrDefault(s => s.Id == keep) is { } same)
        {
            SelectedScenario = same;
        }
        else if (SelectedScenario is null && Scenarios.Count > 0)
        {
            SelectedScenario = Scenarios[0];
        }
        else
        {
            await CalculateCoreAsync();
        }

        await CompareCoreAsync();
    });

    private ScenarioParameters Defaults()
    {
        var profile = Context.Engine.Profile;
        return new ScenarioParameters
        {
            ElectricityRateKrwPerKwh = profile.Tariff.FlatRateKrwPerKwh,
            SystemExtraPowerW = profile.ExtraSystemPowerW,
            GpuPowerW = 300,
            HoursPerDay = 24,
            Days = 30,
        };
    }

    private void LoadFields(ScenarioParameters p)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        RevenuePerHour = Fmt.Number(p.RevenuePerHourKrw);
        GpuPower = N(p.GpuPowerW);
        SystemPower = N(p.SystemExtraPowerW);
        Rate = Fmt.Number(p.ElectricityRateKrwPerKwh);
        Utilization = N(p.UtilizationPercent);
        HoursPerDay = N(p.HoursPerDay);
        Days = N(p.Days);
        Fee = N(p.PlatformFeePercent);
        FixedPerMonth = Fmt.Number(p.FixedCostPerMonthKrw);
        EquipmentCost = Fmt.Number(p.EquipmentCostKrw);
        Salvage = Fmt.Number(p.EquipmentSalvageKrw);
        LifeMonths = N(p.EquipmentLifeMonths);
        var (low, high) = SensitivityAnalyzer.DefaultRange(p, SelectedVariable.Value);
        RangeLow = low.ToString("0.##", CultureInfo.InvariantCulture);
        RangeHigh = high.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private ScenarioParameters Read()
    {
        var p = new ScenarioParameters
        {
            RevenuePerHourKrw = ParseMoney(RevenuePerHour, "시간당 매출"),
            GpuPowerW = ParseNumber(GpuPower, "GPU 소비전력"),
            SystemExtraPowerW = ParseNumber(SystemPower, "시스템 추가 전력"),
            ElectricityRateKrwPerKwh = ParseMoney(Rate, "전기요금"),
            UtilizationPercent = ParseNumber(Utilization, "가동률"),
            HoursPerDay = ParseNumber(HoursPerDay, "하루 작업 시간"),
            Days = ParseNumber(Days, "기간(일)"),
            PlatformFeePercent = ParseNumber(Fee, "수수료"),
            FixedCostPerMonthKrw = ParseMoney(FixedPerMonth, "월 고정비"),
            EquipmentCostKrw = ParseMoney(EquipmentCost, "장비 비용"),
            EquipmentSalvageKrw = ParseMoney(Salvage, "잔존가치"),
            EquipmentLifeMonths = ParseNumber(LifeMonths, "감가상각 기간"),
        };
        var problems = p.Validate();
        return problems.Count > 0 ? throw new ArgumentException(string.Join(" ", problems)) : p;
    }

    private bool TryRead(out ScenarioParameters p)
    {
        try
        {
            p = Read();
            return true;
        }
        catch (ArgumentException)
        {
            p = Defaults();
            return false;
        }
    }

    [RelayCommand]
    private Task Calculate() => Act(CalculateCoreAsync);

    private Task CalculateCoreAsync()
    {
        ScenarioParameters p;
        try
        {
            p = Read();
        }
        catch (ArgumentException ex)
        {
            StatusText = "입력 확인: " + ex.Message;
            HasResult = false;
            return Task.CompletedTask;
        }

        var r = ScenarioEngine.Compute(p);
        Replace(Result,
        [
            new NameValueRow("가동 시간", Fmt.Hours(r.OperatingHours), $"하루 {Fmt.Number(p.HoursPerDay)}시간 × {Fmt.Number(p.Days)}일 × 가동률 {Fmt.Number(p.UtilizationPercent)}%"),
            new NameValueRow("에너지", Fmt.Kwh(r.EnergyKwh), $"(GPU {Fmt.Number(p.GpuPowerW)} W + 시스템 {Fmt.Number(p.SystemExtraPowerW)} W) × 가동 시간"),
            new NameValueRow("매출 (수수료 전)", Fmt.Krw(r.GrossRevenue), $"시간당 {Fmt.Krw(p.RevenuePerHourKrw)} × 가동 시간 (가정)"),
            new NameValueRow("수수료", Fmt.Krw(r.PlatformFees), $"{Fmt.Number(p.PlatformFeePercent)} %"),
            new NameValueRow("전력비", Fmt.Krw(r.ElectricityCost), $"{Fmt.Krw(p.ElectricityRateKrwPerKwh)}/kWh"),
            new NameValueRow("고정비", Fmt.Krw(r.FixedCost), "월 고정비 × 기간"),
            new NameValueRow("감가상각", Fmt.Krw(r.Depreciation), "(장비 비용 − 잔존가치) ÷ 기간(개월) × 기간"),
            new NameValueRow("총비용", Fmt.Krw(r.TotalCost)),
            new NameValueRow("순수익", Fmt.SignedKrw(Maybe<decimal>.Of(r.NetProfit))),
            new NameValueRow("시간당 순수익", Fmt.KrwPerHour(r.NetPerHour), Fmt.Reason(r.NetPerHour)),
            new NameValueRow("에너지당 순수익", Fmt.KrwPerKwh(r.NetPerKwh), Fmt.Reason(r.NetPerKwh)),
            new NameValueRow("수익률", Fmt.Ratio(r.Margin), Fmt.Reason(r.Margin)),
            new NameValueRow("손익분기 전력 단가", Fmt.KrwPerKwh(r.BreakevenElectricityRate), Fmt.Reason(r.BreakevenElectricityRate)),
            new NameValueRow("손익분기 시간당 매출", Fmt.KrwPerHour(r.BreakevenRevenuePerHour), Fmt.Reason(r.BreakevenRevenuePerHour)),
            new NameValueRow("장비 회수 기간", r.PaybackMonths.HasValue ? $"{r.PaybackMonths.Value:0.0}개월" : Fmt.NA, Fmt.Reason(r.PaybackMonths)),
        ]);

        var low = ParseNumber(RangeLow, "범위 최소");
        var high = ParseNumber(RangeHigh, "범위 최대");
        var series = SensitivityAnalyzer.Sweep(p, SelectedVariable.Value, low, high);
        SensitivityChart = Charts.Sensitivity(series);
        BreakevenText = series.Breakeven.HasValue
            ? $"손익분기점: {SensitivityVariableText.ToKorean(series.Variable)} = {series.Breakeven.Value:0.##} (다른 조건 고정){(series.BreakevenInRange ? "" : " — 그래프 범위 밖")}"
            : $"손익분기점 없음: {series.Breakeven.Reason}";
        var percent = DecimalText.TryParseUserDouble(TornadoPercent, out var t) && t is > 0 and <= 100 ? t : 20;
        Replace(Tornado, SensitivityAnalyzer.Tornado(p, percent).Select(b => new TornadoDisplayRow(SensitivityVariableText.ToKorean(b.Variable),
            b.LowValue.ToString("0.##", CultureInfo.InvariantCulture), Fmt.SignedKrw(Maybe<decimal>.Of(b.NetAtLow)),
            b.HighValue.ToString("0.##", CultureInfo.InvariantCulture), Fmt.SignedKrw(Maybe<decimal>.Of(b.NetAtHigh)), Fmt.Krw(b.Swing))));
        HasResult = true;
        UpdateTracking(r);
        return Task.CompletedTask;
    }

    /// <summary>Tracks changes of the source data: the session the values came from, and the result saved last time.</summary>
    private void UpdateTracking(ScenarioResult current)
    {
        var notes = new List<string>();
        if (_current?.SourceSessionId is long sid)
        {
            if (Context.Store.GetSession(sid) is null)
            {
                notes.Add($"원본 세션 #{sid}이(가) 삭제되었습니다. 조건은 저장된 값 그대로입니다.");
            }
            else
            {
                var hash = ProfitabilityEngine.HashInputs(Context.Sessions.BuildInput(sid));
                notes.Add(hash == _current.SourceHash
                    ? $"원본 세션 #{sid}의 데이터는 시나리오를 만든 뒤 바뀌지 않았습니다."
                    : $"원본 세션 #{sid}의 데이터나 회계 조건이 시나리오를 만든 뒤 바뀌었습니다. '세션에서 가져오기'로 조건을 다시 가져올 수 있습니다.");
            }
        }

        if (_current?.LastResultJson is { } json)
        {
            try
            {
                var saved = JsonSerializer.Deserialize<StoredScenarioResult>(json);
                if (saved is not null && saved.NetProfit != current.NetProfit)
                {
                    notes.Add($"저장 당시({Fmt.Time(saved.Computed)}) 순수익 {Fmt.Krw(saved.NetProfit)} → 현재 조건 {Fmt.Krw(current.NetProfit)} (저장하지 않은 변경 포함).");
                }
            }
            catch (JsonException)
            {
            }
        }

        TrackingText = string.Join(" ", notes);
    }

    [RelayCommand]
    private void New()
    {
        _current = null;
        SelectedScenario = null;
        Name = "새 시나리오";
        SelectedKind = Kinds[(int)ScenarioKind.Custom];
        Notes = "";
        LoadFields(Defaults());
        _ = Busy(CalculateCoreAsync);
    }

    [RelayCommand]
    private Task FromSession() => Act(async () =>
    {
        if (SourceSession is not { } option)
        {
            throw new InvalidOperationException("조건을 가져올 세션을 고르세요.");
        }

        var (result, hash) = await Task.Run(() =>
        {
            var input = Context.Sessions.BuildInput(option.Value);
            return (ProfitabilityEngine.Compute(input), ProfitabilityEngine.HashInputs(input));
        });
        LoadFields(ScenarioEngine.FromSession(result, Context.Engine.Profile));
        _current = (_current ?? new ScenarioRecord { Name = $"세션 #{option.Value} 기준", ParamsJson = "{}" }) with { SourceSessionId = option.Value, SourceHash = hash };
        if (string.IsNullOrWhiteSpace(Name) || Name == "새 시나리오")
        {
            Name = $"세션 #{option.Value} 기준";
        }

        await CalculateCoreAsync();
        StatusText = $"세션 #{option.Value}의 실측 평균 전력·단가·수익 단가로 조건을 채웠습니다 (가동률 100%, 하루 24시간, 30일 가정). 필요하면 고친 뒤 저장하세요.";
    });

    [RelayCommand]
    private Task Save() => Act(async () =>
    {
        var p = Read();
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("시나리오 이름을 입력하세요.");
        }

        var r = ScenarioEngine.Compute(p);
        var record = new ScenarioRecord
        {
            Id = _current?.Id ?? 0,
            Name = Name.Trim(),
            Kind = (int)SelectedKind.Value,
            ParamsJson = JsonSerializer.Serialize(p),
            SourceSessionId = _current?.SourceSessionId,
            SourceHash = _current?.SourceHash,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            LastResultJson = JsonSerializer.Serialize(new StoredScenarioResult(r.NetProfit, r.EnergyKwh, Context.Now)),
        };
        var id = await Task.Run(() => Context.Store.SaveScenario(record));
        Replace(Scenarios, Context.Store.ListScenarios());
        SelectedScenario = Scenarios.FirstOrDefault(s => s.Id == id);
        await CompareCoreAsync();
    }, "시나리오를 저장했습니다.");

    [RelayCommand]
    private Task Variants() => Act(async () =>
    {
        var p = Read();
        var baseName = string.IsNullOrWhiteSpace(Name) ? "시나리오" : Name.Trim();
        foreach (var kind in new[] { ScenarioKind.Optimistic, ScenarioKind.Pessimistic })
        {
            var v = SensitivityAnalyzer.Variant(p, kind);
            var r = ScenarioEngine.Compute(v);
            await Task.Run(() => Context.Store.SaveScenario(new ScenarioRecord
            {
                Name = $"{baseName} ({ScenarioKindText.ToKorean(kind)})",
                Kind = (int)kind,
                ParamsJson = JsonSerializer.Serialize(v),
                SourceSessionId = _current?.SourceSessionId,
                SourceHash = _current?.SourceHash,
                Notes = $"'{baseName}'에서 수익 단가 {(kind == ScenarioKind.Optimistic ? "+" : "−")}20%, GPU 전력·전기요금 {(kind == ScenarioKind.Optimistic ? "−" : "+")}20%로 만든 조건",
                LastResultJson = JsonSerializer.Serialize(new StoredScenarioResult(r.NetProfit, r.EnergyKwh, Context.Now)),
            }));
        }

        Replace(Scenarios, Context.Store.ListScenarios());
        await CompareCoreAsync();
    }, "낙관·비관 시나리오를 추가했습니다 (수익 ±20%, 전력·요금 ∓20%).");

    [RelayCommand]
    private Task Delete() => Act(async () =>
    {
        if (SelectedScenario is not { } s)
        {
            throw new InvalidOperationException("삭제할 시나리오를 고르세요.");
        }

        if (!Context.Dialogs.Confirm("시나리오 삭제", $"'{s.Name}' 시나리오를 삭제할까요? 세션 기록에는 영향이 없습니다."))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteScenario(s.Id));
        _current = null;
        Replace(Scenarios, Context.Store.ListScenarios());
        SelectedScenario = Scenarios.FirstOrDefault();
        await CompareCoreAsync();
    }, "삭제했습니다.");

    private Task CompareCoreAsync()
    {
        var rows = new List<ScenarioCompareRow>();
        foreach (var s in Scenarios)
        {
            ScenarioParameters? p;
            try
            {
                p = JsonSerializer.Deserialize<ScenarioParameters>(s.ParamsJson);
            }
            catch (JsonException)
            {
                p = null;
            }

            if (p is null || p.Validate().Count > 0)
            {
                rows.Add(new ScenarioCompareRow(s.Name, ScenarioKindText.ToKorean((ScenarioKind)s.Kind), Fmt.NA, Fmt.NA, Fmt.NA, Fmt.NA, Fmt.NA, "조건 오류", Fmt.NA, Fmt.NA, Fmt.NA));
                continue;
            }

            var r = ScenarioEngine.Compute(p);
            rows.Add(new ScenarioCompareRow(s.Name, ScenarioKindText.ToKorean((ScenarioKind)s.Kind), Fmt.Hours(r.OperatingHours), Fmt.Kwh(r.EnergyKwh), Fmt.Krw(r.GrossRevenue),
                Fmt.Krw(r.ElectricityCost), Fmt.Krw(r.TotalCost), Fmt.SignedKrw(Maybe<decimal>.Of(r.NetProfit)), Fmt.KrwPerHour(r.NetPerHour),
                Fmt.KrwPerKwh(r.BreakevenElectricityRate), r.PaybackMonths.HasValue ? $"{r.PaybackMonths.Value:0.0}개월" : Fmt.NA));
        }

        Replace(Comparison, rows);
        return Task.CompletedTask;
    }
}
