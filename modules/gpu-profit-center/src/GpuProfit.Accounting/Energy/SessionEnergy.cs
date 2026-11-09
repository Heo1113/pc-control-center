using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Energy;

/// <summary>One part of a session's energy, with where it comes from and how it was obtained.</summary>
public sealed record EnergyComponent(string Key, string Label, double Wh, ValueOrigin Origin, string Basis);

public static class EnergyComponentKeys
{
    public const string GpuMeasured = "gpu_measured";
    public const string GpuUnobservedEstimate = "gpu_unobserved_estimate";
    public const string SystemAssumption = "system_assumption";
    public const string IdleDeduction = "idle_deduction";
    public const string WallMeter = "wall_meter";
}

/// <summary>Inputs that decide how a session's energy is counted.</summary>
public sealed record EnergyConditions
{
    public EnergyBasis Basis { get; init; }
    public double ExtraSystemPowerW { get; init; }
    public double IdleGpuPowerW { get; init; }
    public double MaxSampleGapSeconds { get; init; } = 10;
    public UnobservedPolicy UnobservedPolicy { get; init; }
    public double GpuActiveUtilizationPercent { get; init; } = 10;
    public double? WallMeterKwh { get; init; }

    public static EnergyConditions From(AccountingProfile profile, WorkSession session) => new()
    {
        Basis = session.EnergyBasis ?? profile.DefaultEnergyBasis,
        ExtraSystemPowerW = session.ExtraSystemPowerW ?? profile.ExtraSystemPowerW,
        IdleGpuPowerW = session.IdleGpuPowerW ?? profile.IdleGpuPowerW,
        MaxSampleGapSeconds = profile.MaxSampleGapSeconds,
        UnobservedPolicy = session.UnobservedPolicy ?? profile.UnobservedPolicy,
        GpuActiveUtilizationPercent = profile.GpuActiveUtilizationPercent,
        WallMeterKwh = session.WallMeterKwh,
    };
}

/// <summary>A session's energy: per-GPU measurement plus the components of the chosen basis.</summary>
public sealed record SessionEnergyResult
{
    public required EnergyConditions Conditions { get; init; }
    public IReadOnlyList<DeviceEnergy> Devices { get; init; } = [];

    /// <summary>Total recorded (segment) time of the session in seconds.</summary>
    public double WindowSeconds { get; init; }

    public double GpuMeasuredWh => Devices.Sum(d => d.MeasuredWh);

    /// <summary>The lowest coverage among the accounted GPUs (null without GPU data).</summary>
    public double? Coverage => Devices.Count == 0 ? null : Devices.Min(d => d.Coverage ?? 0);

    public IReadOnlyList<EnergyComponent> Components { get; init; } = [];

    /// <summary>Total energy of the basis; undefined when the basis cannot be computed (for example no meter reading).</summary>
    public Maybe<double> TotalWh { get; init; }

    public Maybe<double> TotalKwh => TotalWh.HasValue ? Maybe<double>.Of(TotalWh.Value!.Value / 1000.0) : Maybe<double>.None(TotalWh.Reason!);

    public double MeasuredShare => Share(ValueOrigin.Measured);
    public double EstimatedShare => Share(ValueOrigin.Estimated);
    public double UserEnteredShare => Share(ValueOrigin.UserEntered);

    /// <summary>Observed GPU activity (utilisation at or above the threshold) as a share of observed time, over all accounted GPUs.</summary>
    public double? GpuActiveShare
    {
        get
        {
            var observed = Devices.Sum(d => d.UtilizationObservedSeconds);
            return observed > 0 ? Devices.Sum(d => d.GpuActiveSeconds) / observed : null;
        }
    }

    public double? CounterWh => Devices.All(d => d.CounterWh is not null) && Devices.Count > 0 ? Devices.Sum(d => d.CounterWh!.Value) : null;

    /// <summary>Relative difference between the counter and the integration over the counter's observed time (cross-check).</summary>
    public double? CounterDifference
    {
        get
        {
            if (CounterWh is not double counter || Devices.Count == 0)
            {
                return null;
            }

            var integratedOverSameTime = Devices.Sum(d => d.ObservedSeconds > 0 ? d.MeasuredWh * Math.Min(1, d.CounterObservedSeconds / d.ObservedSeconds) : 0);
            return integratedOverSameTime > 0 ? (counter - integratedOverSameTime) / integratedOverSameTime : null;
        }
    }

    public IReadOnlyList<string> Notes { get; init; } = [];

    private double Share(ValueOrigin origin)
    {
        var positive = Components.Where(c => c.Wh > 0).ToList();
        var total = positive.Sum(c => c.Wh);
        return total > 0 ? positive.Where(c => c.Origin == origin).Sum(c => c.Wh) / total : 0;
    }
}

public static class SessionEnergyCalculator
{
    /// <param name="samples">Samples of every GPU around the windows (include up to one max-gap before and after).</param>
    /// <param name="devices">GPU indices to account; null = every GPU present in <paramref name="samples"/>.</param>
    public static SessionEnergyResult Compute(IReadOnlyList<PowerSample> samples, IReadOnlyList<TimeWindow> windows,
        IReadOnlyList<int>? devices, EnergyConditions conditions)
    {
        var ws = TimeWindow.Normalize(windows);
        var windowSeconds = ws.Sum(w => w.Seconds);
        var indices = devices is { Count: > 0 } ? devices.Distinct().Order().ToList() : samples.Select(s => s.DeviceIndex).Distinct().Order().ToList();
        var deviceResults = indices
            .Select(i => EnergyIntegrator.Integrate(i, samples, ws, conditions.MaxSampleGapSeconds, conditions.GpuActiveUtilizationPercent))
            .ToList();

        var components = new List<EnergyComponent>();
        var notes = new List<string>();
        var measured = deviceResults.Sum(d => d.MeasuredWh);
        var hasGpu = deviceResults.Any(d => d.ObservedSeconds > 0);
        var gapSeconds = deviceResults.Sum(d => d.UnobservedSeconds);
        Maybe<double> total;

        if (conditions.Basis == EnergyBasis.WallMeter)
        {
            if (conditions.WallMeterKwh is double kwh && kwh >= 0 && double.IsFinite(kwh))
            {
                components.Add(new EnergyComponent(EnergyComponentKeys.WallMeter, "PC 전체 전력 (외부 전력계)", kwh * 1000, ValueOrigin.UserEntered,
                    "사용자가 외부 전력계에서 읽어 입력한 세션 전체 에너지. GPU 실측값은 참고로만 표시하며 합산하지 않습니다."));
                total = Maybe<double>.Of(kwh * 1000);
            }
            else
            {
                total = Maybe<double>.None("외부 전력계 값(kWh)이 입력되지 않았습니다.");
            }

            return new SessionEnergyResult
            {
                Conditions = conditions,
                Devices = deviceResults,
                WindowSeconds = windowSeconds,
                Components = components,
                TotalWh = total,
                Notes = notes,
            };
        }

        if (!hasGpu && windowSeconds > 0)
        {
            notes.Add(deviceResults.Count == 0
                ? "이 세션 구간에 GPU 전력 샘플이 없습니다 (GPU 미지원·센서 꺼짐·기록 없음). GPU 실측 에너지는 0 Wh가 아니라 '관측 안 됨'입니다."
                : "이 세션 구간에 유효한 GPU 전력 값이 없습니다. GPU 실측 에너지는 0 Wh가 아니라 '관측 안 됨'입니다.");
        }

        components.Add(new EnergyComponent(EnergyComponentKeys.GpuMeasured, "GPU 보드 전력 실측", measured, ValueOrigin.Measured,
            $"NVML GPU 보드 전력 샘플을 실제 시각으로 사다리꼴 적분. 최대 샘플 간격 {conditions.MaxSampleGapSeconds:0.#}초를 넘는 구간은 보간하지 않음."));

        if (gapSeconds > 0.5)
        {
            if (conditions.UnobservedPolicy == UnobservedPolicy.EstimateFromObservedAverage)
            {
                double estimate = 0;
                var withoutAverage = 0;
                foreach (var d in deviceResults)
                {
                    if (d.UnobservedSeconds <= 0)
                    {
                        continue;
                    }

                    if (d.AveragePowerW is double avg)
                    {
                        estimate += avg * d.UnobservedSeconds / 3600.0;
                    }
                    else
                    {
                        withoutAverage++;
                    }
                }

                if (estimate > 0)
                {
                    components.Add(new EnergyComponent(EnergyComponentKeys.GpuUnobservedEstimate, "미관측 구간 GPU 전력 추정", estimate, ValueOrigin.Estimated,
                        "관측 구간의 시간 가중 평균 전력 × 미관측 시간 (사용자가 선택한 추정, 실측 아님)."));
                }

                if (withoutAverage > 0)
                {
                    notes.Add($"GPU {withoutAverage}개는 관측된 전력이 없어 미관측 구간을 추정할 수 없습니다.");
                }
            }
            else
            {
                notes.Add($"미관측 구간 {FormatSeconds(gapSeconds)}의 GPU 에너지는 포함하지 않았습니다 (보간하지 않음).");
            }
        }

        if (conditions.Basis == EnergyBasis.IncrementalGpu && conditions.IdleGpuPowerW > 0)
        {
            // Deduct idle power only over the time each GPU was observed (and estimated, if that was added).
            double deduction = 0;
            var estimatedIncluded = components.Any(c => c.Key == EnergyComponentKeys.GpuUnobservedEstimate);
            foreach (var d in deviceResults)
            {
                var seconds = d.ObservedSeconds + (estimatedIncluded && d.AveragePowerW is not null ? d.UnobservedSeconds : 0);
                var gpuWh = d.MeasuredWh + (estimatedIncluded && d.AveragePowerW is double avg ? avg * d.UnobservedSeconds / 3600.0 : 0);
                deduction += Math.Min(gpuWh, conditions.IdleGpuPowerW * seconds / 3600.0); // never below zero per GPU
            }

            if (deduction > 0)
            {
                components.Add(new EnergyComponent(EnergyComponentKeys.IdleDeduction, "GPU 유휴 전력 차감 (가정)", -deduction, ValueOrigin.Estimated,
                    $"GPU당 유휴 {conditions.IdleGpuPowerW:0.#} W × 관측 시간을 차감 (GPU별로 0 Wh 미만이 되지 않도록 제한)."));
            }
        }

        if (conditions.Basis is (EnergyBasis.GpuPlusSystemAssumption or EnergyBasis.IncrementalGpu) && conditions.ExtraSystemPowerW > 0)
        {
            var label = conditions.Basis == EnergyBasis.IncrementalGpu ? "작업으로 늘어난 시스템 전력 (가정)" : "시스템 추가 전력 (가정)";
            components.Add(new EnergyComponent(EnergyComponentKeys.SystemAssumption, label, conditions.ExtraSystemPowerW * windowSeconds / 3600.0, ValueOrigin.Estimated,
                $"사용자 가정 {conditions.ExtraSystemPowerW:0.#} W × 세션 기록 시간 {FormatSeconds(windowSeconds)} (CPU·메인보드·팬·전원 손실 등, 실측 아님)."));
        }
        else if (conditions.Basis == EnergyBasis.GpuPlusSystemAssumption)
        {
            notes.Add("시스템 추가 전력 가정이 0 W라 GPU 실측값만 사용했습니다.");
        }

        // Without any observed GPU power the GPU part is unknown, not zero: the total cannot be stated.
        // A session with no recorded time has a defined total of 0 Wh.
        total = !hasGpu && windowSeconds > 0
            ? Maybe<double>.None("GPU 전력이 관측되지 않아 에너지를 계산할 수 없습니다 (외부 전력계 값을 입력하면 그 값으로 계산할 수 있습니다).")
            : Maybe<double>.Of(components.Sum(c => c.Wh));
        return new SessionEnergyResult
        {
            Conditions = conditions,
            Devices = deviceResults,
            WindowSeconds = windowSeconds,
            Components = components,
            TotalWh = total,
            Notes = notes,
        };
    }

    internal static string FormatSeconds(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" : t.TotalMinutes >= 1 ? $"{t.Minutes}분 {t.Seconds}초" : $"{t.Seconds}초";
    }
}
