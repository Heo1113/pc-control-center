using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Iron.App.Presentation;

/// <summary>
/// A number typed by the user (UI thresholds, tariff, interval). The text is checked as it is typed: a valid value is
/// applied at once, anything else (not a number, out of range, too many decimals) shows why and keeps the last valid
/// value in effect, so an invalid entry never reaches the screen logic.
/// </summary>
public sealed partial class NumericSetting : ObservableObject
{
    private readonly Action<double>? _applied;
    private bool _syncing;

    public NumericSetting(string label, string unit, double min, double max, double defaultValue, int decimals, Action<double>? applied = null)
    {
        if (min > max || defaultValue < min || defaultValue > max || decimals < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }

        Label = label;
        Unit = unit;
        Min = min;
        Max = max;
        Default = defaultValue;
        Decimals = decimals;
        _applied = applied;
        Value = defaultValue;
        _syncing = true;
        Text = Format(defaultValue);
        _syncing = false;
    }

    public string Label { get; }
    public string Unit { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public int Decimals { get; }

    /// <summary>The value in effect (always within range).</summary>
    public double Value
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string RangeText => $"{Format(Min)}~{Format(Max)}{(Unit.Length > 0 ? " " + Unit : "")}";

    [ObservableProperty] public partial string Text { get; set; }

    /// <summary>Why the current text is not applied; null when it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public string Format(double value) => value.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), CultureInfo.InvariantCulture);

    /// <summary>Checks a text without applying it.</summary>
    public string? Validate(string? text, out double value)
    {
        value = double.NaN;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return $"값을 입력하세요 ({RangeText}).";
        }

        if (!double.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            return $"숫자로 입력하세요 ({RangeText}).";
        }

        var dot = trimmed.IndexOf('.');
        var decimals = dot < 0 ? 0 : trimmed.Length - dot - 1;
        if (decimals > Decimals)
        {
            return Decimals == 0 ? $"정수로 입력하세요 ({RangeText})." : $"소수점 {Decimals}자리까지 입력하세요 ({RangeText}).";
        }

        if (parsed < Min || parsed > Max)
        {
            return $"허용 범위 {RangeText}를 벗어났습니다. 이전 값 {Format(Value)}{UnitSuffix}을(를) 유지합니다.";
        }

        value = parsed;
        return null;
    }

    [RelayCommand]
    public void ResetToDefault() => Set(Default);

    /// <summary>Sets a value from code (command line, tests); out-of-range values are refused the same way.</summary>
    public bool TrySet(double value)
    {
        var error = Validate(Format(value), out var parsed);
        if (error is not null)
        {
            return false;
        }

        Set(parsed);
        return true;
    }

    partial void OnTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        Error = Validate(value, out var parsed);
        if (Error is null && parsed != Value)
        {
            Value = parsed;
            _applied?.Invoke(parsed);
        }
    }

    private string UnitSuffix => Unit.Length > 0 ? " " + Unit : "";

    private void Set(double value)
    {
        var changed = value != Value;
        Value = value;
        _syncing = true;
        Text = Format(value);
        _syncing = false;
        Error = null;
        if (changed)
        {
            _applied?.Invoke(value);
        }
    }
}
