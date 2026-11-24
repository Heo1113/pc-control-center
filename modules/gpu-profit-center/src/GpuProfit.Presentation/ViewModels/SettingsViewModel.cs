using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Recording;
using GpuProfit.Storage;

namespace GpuProfit.Presentation.ViewModels;

public sealed record EquipmentRow(long Id, string Name, string Price, string Method, string Scope, string Active, string Note);

public sealed record RateRow(long Id, string Currency, string KrwPerUnit, string AsOf, string Source, string Origin);

/// <summary>Collection, storage, accounting conditions (tariff and assumptions), equipment, exchange rates, backup.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(ViewContext context) : base(context)
    {
        TariffModes = [new("사용자 지정 유효 단가 (정확)", TariffMode.FlatRate), new("누진 단계 단순 추정", TariffMode.TieredEstimate)];
        Bases = EnergyBasisText.All.Select(b => new Option<EnergyBasis>(EnergyBasisText.ToKorean(b), b)).ToList();
        Policies = [new("미관측 구간 제외 (보간 안 함)", UnobservedPolicy.Exclude), new("관측 평균 전력으로 추정해 별도 표시", UnobservedPolicy.EstimateFromObservedAverage)];
        Methods = [new("정액법 (개월)", DepreciationMethod.StraightLineMonths), new("사용 시간 기준", DepreciationMethod.UsageHours)];
        FontScales = [new("100%", 1.0), new("115%", 1.15), new("130%", 1.3), new("150%", 1.5)];
        RateAsOf = Fmt.InputTime(context.Now);
        LoadFromCurrent();
    }

    public override string Title => "설정";
    public override string Glyph => "";

    public IReadOnlyList<Option<TariffMode>> TariffModes { get; }
    public IReadOnlyList<Option<EnergyBasis>> Bases { get; }
    public IReadOnlyList<Option<UnobservedPolicy>> Policies { get; }
    public IReadOnlyList<Option<DepreciationMethod>> Methods { get; }
    public IReadOnlyList<Option<double>> FontScales { get; }
    public ObservableCollection<Option<long?>> WorkloadScopes { get; } = [];
    public ObservableCollection<NameValueRow> DatabaseInfo { get; } = [];
    public ObservableCollection<EquipmentRow> Equipment { get; } = [];
    public ObservableCollection<RateRow> Rates { get; } = [];

    // ---- collection and display (settings.json)
    [ObservableProperty] public partial string IntervalSeconds { get; set; } = "";
    [ObservableProperty] public partial bool GpuEnabled { get; set; }
    [ObservableProperty] public partial bool SystemEnabled { get; set; }
    [ObservableProperty] public partial string RetentionDays { get; set; } = "";
    [ObservableProperty] public partial Option<double>? FontScale { get; set; }
    [ObservableProperty] public partial bool UseUtc { get; set; }

    // ---- accounting conditions (database)
    [ObservableProperty] public partial Option<TariffMode>? Mode { get; set; }
    [ObservableProperty] public partial string FlatRate { get; set; } = "";
    [ObservableProperty] public partial string TiersText { get; set; } = "";
    [ObservableProperty] public partial string HouseholdKwh { get; set; } = "";
    [ObservableProperty] public partial string WorkloadKwh { get; set; } = "";
    [ObservableProperty] public partial string Surcharge { get; set; } = "";
    [ObservableProperty] public partial Option<EnergyBasis>? DefaultBasis { get; set; }
    [ObservableProperty] public partial string ExtraW { get; set; } = "";
    [ObservableProperty] public partial string IdleW { get; set; } = "";
    [ObservableProperty] public partial string MaxGap { get; set; } = "";
    [ObservableProperty] public partial Option<UnobservedPolicy>? Policy { get; set; }
    [ObservableProperty] public partial string ActiveUtil { get; set; } = "";

    // ---- equipment and exchange rates
    [ObservableProperty] public partial string EqName { get; set; } = "";
    [ObservableProperty] public partial string EqPrice { get; set; } = "";
    [ObservableProperty] public partial string EqSalvage { get; set; } = "0";
    [ObservableProperty] public partial Option<DepreciationMethod>? EqMethod { get; set; }
    [ObservableProperty] public partial string EqLifeMonths { get; set; } = "36";
    [ObservableProperty] public partial string EqLifeHours { get; set; } = "";
    [ObservableProperty] public partial Option<long?>? EqScope { get; set; }
    [ObservableProperty] public partial EquipmentRow? SelectedEquipment { get; set; }
    [ObservableProperty] public partial string RateCurrency { get; set; } = "USD";
    [ObservableProperty] public partial string RateValue { get; set; } = "";
    [ObservableProperty] public partial string RateAsOf { get; set; }
    [ObservableProperty] public partial string RateSource { get; set; } = "";
    [ObservableProperty] public partial RateRow? SelectedRate { get; set; }

    public string TariffNote =>
        "정확한 계산은 '사용자 지정 유효 단가'입니다(고지서 총액 ÷ 사용량 등으로 사용자가 정한 원/kWh). 한국 주택용 누진제는 가구 전체 사용량·검침 기간·기본요금·할인에 따라 달라 "
        + "이 앱이 정확히 계산할 수 없습니다. 누진 단계 추정은 사용자가 입력한 단계표와 가구 사용량으로 '작업이 더한 요금'을 단순 추정하며, 결과에 추정으로 표시됩니다. 앱은 요금표를 내장하지 않습니다.";

    public string TiersHelp => "한 줄에 한 단계: 상한kWh,단가(원/kWh),기본요금(원). 마지막 단계는 상한을 비웁니다. 예) 200,120,910 / 400,215,1600 / ,307,7300 (예시 값이며 실제 요금표가 아님)";

    private void LoadFromCurrent()
    {
        var s = Context.Settings;
        IntervalSeconds = s.Sampling.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        GpuEnabled = s.Sampling.GpuEnabled;
        SystemEnabled = s.Sampling.SystemEnabled;
        RetentionDays = s.Storage.SampleRetentionDays.ToString(CultureInfo.InvariantCulture);
        FontScale = FontScales.MinBy(f => Math.Abs(f.Value - s.Ui.FontScale));
        UseUtc = s.Ui.UseUtc;

        var p = Context.Engine.Profile;
        Mode = TariffModes.First(m => m.Value == p.Tariff.Mode);
        FlatRate = Fmt.Number(p.Tariff.FlatRateKrwPerKwh);
        TiersText = string.Join(Environment.NewLine, p.Tariff.Tiers.Select(t =>
            $"{Fmt.Number(t.UpToKwh)},{Fmt.Number(t.EnergyRateKrwPerKwh)},{Fmt.Number(t.BaseChargeKrw)}"));
        HouseholdKwh = Fmt.Number(p.Tariff.HouseholdMonthlyKwh);
        WorkloadKwh = Fmt.Number(p.Tariff.WorkloadMonthlyKwh);
        Surcharge = Fmt.Number(p.Tariff.SurchargeMultiplier);
        DefaultBasis = Bases.First(b => b.Value == p.DefaultEnergyBasis);
        ExtraW = Fmt.Number(p.ExtraSystemPowerW);
        IdleW = Fmt.Number(p.IdleGpuPowerW);
        MaxGap = Fmt.Number(p.MaxSampleGapSeconds);
        Policy = Policies.First(x => x.Value == p.UnobservedPolicy);
        ActiveUtil = Fmt.Number(p.GpuActiveUtilizationPercent);
        EqMethod = Methods[0];
    }

    public override Task OnActivatedAsync() => Busy(() =>
    {
        LoadLists();
        return Task.CompletedTask;
    });

    private void LoadLists()
    {
        var workloads = Context.Store.ListWorkloads();
        var keep = EqScope?.Value;
        Replace(WorkloadScopes, [new Option<long?>("모든 세션", null), .. workloads.Select(w => new Option<long?>(w.Name, w.Id))]);
        EqScope = WorkloadScopes.FirstOrDefault(w => w.Value == keep) ?? WorkloadScopes[0];
        var names = workloads.ToDictionary(w => w.Id, w => w.Name);
        Replace(Equipment, Context.Store.ListEquipment().Select(a => new EquipmentRow(a.Id, a.Name, $"{Fmt.Krw(a.PriceKrw)} (잔존 {Fmt.Krw(a.SalvageKrw)})",
            a.Method == DepreciationMethod.UsageHours ? $"사용 {Fmt.Number(a.LifeHours)} 시간" : $"정액 {Fmt.Number(a.LifeMonths)}개월",
            a.WorkloadId is long w ? names.GetValueOrDefault(w, $"작업 #{w}") : "모든 세션", a.Active ? "적용" : "꺼짐", a.Note ?? "")));
        Replace(Rates, Context.Store.ListRates().Select(r => new RateRow(r.Id, r.Currency, Fmt.Number(r.KrwPerUnit) + " 원", Fmt.Time(r.AsOf), r.Source ?? "", Fmt.Origin(r.Origin))));
        var st = Context.Store.GetStats();
        Replace(DatabaseInfo,
        [
            new NameValueRow("데이터 폴더", Context.Paths.IsPortable ? "포터블 (실행 파일 옆 data 폴더)" : Context.Paths.DataDirectory),
            new NameValueRow("데이터베이스", $"{st.FileBytes / (1024.0 * 1024):0.0} MB (WAL {st.WalBytes / 1024.0:0} KB)", "SQLite, 이 앱 전용"),
            new NameValueRow("세션 / 작업", $"{st.Sessions} / {st.Workloads}", $"계산 이력 {st.Results}건, 시나리오 {st.Scenarios}개"),
            new NameValueRow("수익 / 비용 항목", $"{st.RevenueEntries} / {st.CostEntries}"),
            new NameValueRow("센서 기록", $"GPU {st.GpuSamples:N0}행, 시스템 {st.SystemSamples:N0}행", $"{Fmt.Time(st.FirstSample)} ~ {Fmt.Time(st.LastSample)}"),
            new NameValueRow("공백 기록", st.Gaps.ToString(CultureInfo.InvariantCulture), "앱 종료·절전·센서 끊김 등"),
            new NameValueRow("앱 버전", AppInfo.Version),
        ]);
    }

    // ------------------------------------------------------------------ settings.json

    [RelayCommand]
    private Task SaveAppSettings() => Act(async () =>
    {
        var s = Context.Settings;
        var interval = int.TryParse(IntervalSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i is >= 1 and <= 60
            ? i : throw new ArgumentException("샘플 간격은 1~60초 정수여야 합니다.");
        var retention = int.TryParse(RetentionDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 3650
            ? d : throw new ArgumentException("보존 기간은 0~3650일이어야 합니다 (0은 무기한).");
        var restart = interval != s.Sampling.IntervalSeconds || GpuEnabled != s.Sampling.GpuEnabled || SystemEnabled != s.Sampling.SystemEnabled;
        s.Sampling.IntervalSeconds = interval;
        s.Sampling.GpuEnabled = GpuEnabled;
        s.Sampling.SystemEnabled = SystemEnabled;
        s.Storage.SampleRetentionDays = retention;
        s.Ui.FontScale = FontScale?.Value ?? 1.0;
        s.Ui.UseUtc = UseUtc;
        await Task.Run(() => Context.SettingsStore.Save(s));
        Fmt.UseUtc = UseUtc;
        Context.Host?.ApplyFontScale(s.Ui.FontScale);
        StatusText = "설정을 저장했습니다." + (restart ? " 수집 설정은 앱을 다시 시작하면 적용됩니다." : "");
    });

    // ------------------------------------------------------------------ accounting profile

    [RelayCommand]
    private Task SaveProfile() => Act(async () =>
    {
        var profile = new AccountingProfile
        {
            Tariff = new ElectricityTariff
            {
                Mode = Mode?.Value ?? TariffMode.FlatRate,
                FlatRateKrwPerKwh = ParseMoney(FlatRate, "유효 단가"),
                Tiers = ParseTiers(TiersText),
                HouseholdMonthlyKwh = ParseOptionalNumber(HouseholdKwh, "가구 월 사용량"),
                WorkloadMonthlyKwh = ParseOptionalNumber(WorkloadKwh, "작업 월 사용량"),
                SurchargeMultiplier = ParseOptionalMoney(Surcharge, "세금·기금 배율") ?? 1m,
            },
            DefaultEnergyBasis = DefaultBasis?.Value ?? EnergyBasis.GpuMeasured,
            ExtraSystemPowerW = ParseNumber(ExtraW, "시스템 추가 전력"),
            IdleGpuPowerW = ParseNumber(IdleW, "GPU 유휴 전력"),
            MaxSampleGapSeconds = ParseNumber(MaxGap, "최대 샘플 간격"),
            UnobservedPolicy = Policy?.Value ?? UnobservedPolicy.Exclude,
            GpuActiveUtilizationPercent = ParseNumber(ActiveUtil, "GPU 활성 판정 사용률"),
        };
        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }

        await Task.Run(() => Context.Engine.SaveProfile(profile));
        var recalc = Context.Dialogs.Confirm("과거 기록 재계산",
            "회계 조건을 저장했습니다. 지금까지 기록한 모든 세션을 새 조건으로 다시 계산할까요? 이전 계산 결과는 이력에 그대로 남습니다.");
        var changed = recalc ? await Task.Run(() => Context.Sessions.RecalculateAll("회계 조건 변경")) : 0;
        Context.NotifyDataChanged();
        StatusText = recalc ? $"회계 조건을 저장하고 결과가 바뀐 세션 {changed}개를 다시 계산했습니다." : "회계 조건을 저장했습니다. 각 화면은 새 조건으로 계산합니다(이력 저장은 하지 않음).";
    });

    internal static IReadOnlyList<TariffTier> ParseTiers(string? text)
    {
        var tiers = new List<TariffTier>();
        foreach (var raw in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length is < 2 or > 3)
            {
                throw new ArgumentException($"누진 단계 '{line}': '상한kWh,단가,기본요금' 형식이어야 합니다.");
            }

            double? upTo = string.IsNullOrWhiteSpace(parts[0]) ? null
                : DecimalText.TryParseUserDouble(parts[0], out var u) && u > 0 ? u : throw new ArgumentException($"누진 단계 '{line}': 상한이 올바르지 않습니다.");
            var rate = DecimalText.TryParseUser(parts[1], out var r) ? r : throw new ArgumentException($"누진 단계 '{line}': 단가가 올바르지 않습니다.");
            var baseCharge = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? DecimalText.TryParseUser(parts[2], out var b) ? b : throw new ArgumentException($"누진 단계 '{line}': 기본요금이 올바르지 않습니다.")
                : 0m;
            tiers.Add(new TariffTier { UpToKwh = upTo, EnergyRateKrwPerKwh = rate, BaseChargeKrw = baseCharge });
        }

        return tiers;
    }

    // ------------------------------------------------------------------ equipment

    [RelayCommand]
    private Task AddEquipment() => Act(async () =>
    {
        var method = EqMethod?.Value ?? DepreciationMethod.StraightLineMonths;
        var asset = new EquipmentAsset
        {
            Name = string.IsNullOrWhiteSpace(EqName) ? throw new ArgumentException("장비 이름을 입력하세요.") : EqName.Trim(),
            PriceKrw = ParseMoney(EqPrice, "구입 가격"),
            SalvageKrw = ParseOptionalMoney(EqSalvage, "잔존가치") ?? 0,
            Method = method,
            LifeMonths = ParseOptionalNumber(EqLifeMonths, "감가상각 기간(개월)") ?? 36,
            LifeHours = ParseOptionalNumber(EqLifeHours, "수명(시간)"),
            WorkloadId = EqScope?.Value,
            Purchased = Context.Now,
        };
        await Task.Run(() => Context.Store.AddEquipment(asset));
        EqName = "";
        EqPrice = "";
        await AfterLedgerChange("장비 추가");
    }, "장비를 추가했습니다. 감가상각은 각 세션의 시간만큼 배분됩니다(추정).");

    [RelayCommand]
    private Task ToggleEquipment() => Act(async () =>
    {
        if (SelectedEquipment is not { } row || Context.Store.ListEquipment().FirstOrDefault(a => a.Id == row.Id) is not { } asset)
        {
            throw new InvalidOperationException("장비를 고르세요.");
        }

        await Task.Run(() => Context.Store.UpdateEquipment(asset with { Active = !asset.Active }));
        await AfterLedgerChange("장비 적용 변경");
    }, "장비 적용 여부를 바꿨습니다.");

    [RelayCommand]
    private Task DeleteEquipment() => Act(async () =>
    {
        if (SelectedEquipment is not { } row)
        {
            throw new InvalidOperationException("장비를 고르세요.");
        }

        if (!Context.Dialogs.Confirm("장비 삭제", $"'{row.Name}'을(를) 삭제합니다. 관련 세션의 감가상각 비용이 빠집니다. 삭제할까요?"))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteEquipment(row.Id));
        await AfterLedgerChange("장비 삭제");
    }, "장비를 삭제했습니다.");

    // ------------------------------------------------------------------ exchange rates

    [RelayCommand]
    private Task AddRate() => Act(async () =>
    {
        var rate = new ExchangeRate
        {
            Currency = CurrencyCodes.Normalize(RateCurrency) ?? throw new ArgumentException("통화 코드를 입력하세요 (예: USD, BTC)."),
            KrwPerUnit = ParseMoney(RateValue, "1단위당 원화"),
            AsOf = ParseTime(RateAsOf, "기준 시각"),
            Source = string.IsNullOrWhiteSpace(RateSource) ? "사용자 입력" : RateSource.Trim(),
            Origin = ValueOrigin.UserEntered,
        };
        await Task.Run(() => Context.Store.AddRate(rate));
        RateValue = "";
        await AfterLedgerChange("환율 추가");
    }, "환율을 추가했습니다. 수익은 발생 시각 이전의 가장 최근 환율로 환산합니다.");

    [RelayCommand]
    private Task DeleteRate() => Act(async () =>
    {
        if (SelectedRate is not { } row)
        {
            throw new InvalidOperationException("환율을 고르세요.");
        }

        if (!Context.Dialogs.Confirm("환율 삭제", $"{row.Currency} {row.KrwPerUnit} ({row.AsOf}) 환율을 삭제할까요?"))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteRate(row.Id));
        await AfterLedgerChange("환율 삭제");
    }, "환율을 삭제했습니다.");

    private async Task AfterLedgerChange(string reason)
    {
        await Task.Run(() => Context.Sessions.RecalculateAll(reason));
        LoadLists();
        Context.NotifyDataChanged();
    }

    // ------------------------------------------------------------------ data

    [RelayCommand]
    private Task Integrity() => Act(async () =>
    {
        var result = await Task.Run(() => Context.Store.IntegrityCheck());
        StatusText = result == "ok" ? "무결성 검사: 정상 (ok)" : $"무결성 검사: {result} — 백업에서 복원을 검토하세요.";
    });

    [RelayCommand]
    private Task Backup() => Act(async () =>
    {
        var path = Context.Dialogs.SaveFile("데이터베이스 백업", "SQLite 백업 (*.db)|*.db", $"gpc-backup-{Context.Now.ToLocalTime():yyyyMMdd-HHmmss}.db", Context.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        if (File.Exists(path))
        {
            // The dialog asked before overwriting; keep the old file aside instead of losing it.
            File.Move(path, path + $".old-{Context.Now:yyyyMMddHHmmss}", overwrite: false);
        }

        await Task.Run(() => Context.Store.BackupTo(path));
        var v = ProfitStore.ValidateBackup(path);
        StatusText = v.IsValid ? $"백업했습니다: {Path.GetFileName(path)} (세션 {v.Sessions}개, 검증 정상)" : $"백업 검증 실패: {v.Message}";
    });

    [RelayCommand]
    private Task Restore() => Act(async () =>
    {
        var path = Context.Dialogs.OpenFile("백업에서 복원", "SQLite 백업 (*.db)|*.db|모든 파일 (*.*)|*.*", Context.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var v = await Task.Run(() => ProfitStore.ValidateBackup(path));
        if (!v.IsValid)
        {
            Context.Dialogs.Error("복원할 수 없음", $"이 파일은 복원할 수 없습니다: {v.Message}\n현재 데이터는 그대로입니다.");
            return;
        }

        if (!Context.Dialogs.Confirm("백업에서 복원", $"백업(세션 {v.Sessions}개)으로 현재 데이터베이스를 바꿉니다. 현재 데이터베이스는 삭제하지 않고 'before-restore' 이름으로 옆에 보관합니다. 기록을 멈추고 앱을 다시 시작합니다. 계속할까요?"))
        {
            return;
        }

        if (Context.Host is null)
        {
            throw new InvalidOperationException("이 환경에서는 복원을 실행할 수 없습니다.");
        }

        await Context.Host.RestoreAndRestartAsync(path);
    });

    [RelayCommand]
    private Task ExportLedger() => Act(async () =>
    {
        var path = Context.Dialogs.SaveFile("전체 기록 내보내기 (JSON)", "JSON (*.json)|*.json", $"gpc-ledger-{Context.Now.ToLocalTime():yyyyMMdd}.json", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var items = await Context.AccountAsync(Context.Store.ListSessions(new SessionQuery { Limit = 100_000 }));
        await Task.Run(() => Exporters.LedgerJson(path, Context.Store, items, Context.Now));
        StatusText = $"내보냈습니다: {Path.GetFileName(path)} (세션 {items.Count}개). 메모 등 직접 입력한 내용이 들어 있으니 공유 전에 확인하세요.";
    });

    [RelayCommand]
    private Task ExportSessions() => Act(async () =>
    {
        var path = Context.Dialogs.SaveFile("세션 목록 내보내기 (CSV)", "CSV (*.csv)|*.csv", $"gpc-sessions-{Context.Now.ToLocalTime():yyyyMMdd}.csv", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var items = await Context.AccountAsync(Context.Store.ListSessions(new SessionQuery { Limit = 100_000 }));
        await Task.Run(() => Exporters.SessionsCsv(path, items));
        var revenuePath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-revenue.csv");
        var costPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-costs.csv");
        await Task.Run(() =>
        {
            Exporters.RevenueCsv(revenuePath, Context.Store.ListRevenue());
            Exporters.CostsCsv(costPath, Context.Store.ListCosts(), Context.Store.ListEquipment());
        });
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}, {Path.GetFileName(revenuePath)}, {Path.GetFileName(costPath)}";
    });

    [RelayCommand]
    private void OpenDataFolder() => Context.Shell.OpenFolder(Context.Paths.DataDirectory);

    [RelayCommand]
    private Task RecalculateAll() => Act(async () =>
    {
        var changed = await Task.Run(() => Context.Sessions.RecalculateAll("사용자 전체 재계산"));
        Context.NotifyDataChanged();
        StatusText = $"모든 세션을 다시 계산했습니다. 결과가 바뀐 세션 {changed}개를 이력에 저장했습니다.";
    });

    [RelayCommand]
    private Task AddDemoData() => Act(async () =>
    {
        if (!Context.Dialogs.Confirm("데모 데이터", "MOCK 가상 데모 세션 6개와 예시 비용·시나리오를 추가합니다. 모두 [데모]·MOCK으로 표시되며 실제 측정이 아닙니다. 추가할까요?"))
        {
            return;
        }

        var n = await Task.Run(() => DemoDataSeeder.Seed(Context.Store, Context.Sessions, Context.Now));
        LoadLists();
        Context.NotifyDataChanged();
        StatusText = n == 0 ? "데모 데이터가 이미 있습니다." : $"MOCK 데모 세션 {n}개를 추가했습니다.";
    });
}
