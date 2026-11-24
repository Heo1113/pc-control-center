using WindowsAiOperator.Core.Actions;

namespace WindowsAiOperator.Core.Automation;

/// <summary>
/// Coordinate rules for the vision fallback. A model answers in 0..1000 coordinates relative to the window screenshot
/// (UI-TARS convention); the point is mapped onto the window rectangle in physical pixels at execution time, and only
/// when the window has not moved, resized or changed DPI since the screenshot. Old coordinates are never reused.
/// </summary>
public static class CoordinateMapper
{
    public static readonly TimeSpan MaxScreenshotAge = TimeSpan.FromSeconds(90);

    public static PixelPoint ToScreen(NormalizedPoint point, PixelRect windowBounds)
    {
        var x = windowBounds.Left + (int)Math.Round(point.X / 1000.0 * Math.Max(0, windowBounds.Width - 1));
        var y = windowBounds.Top + (int)Math.Round(point.Y / 1000.0 * Math.Max(0, windowBounds.Height - 1));
        return new PixelPoint(x, y);
    }

    public static PixelPoint ToImage(NormalizedPoint point, int imageWidth, int imageHeight) =>
        new((int)Math.Round(point.X / 1000.0 * Math.Max(0, imageWidth - 1)), (int)Math.Round(point.Y / 1000.0 * Math.Max(0, imageHeight - 1)));

    public static NormalizedPoint FromScreen(PixelPoint point, PixelRect windowBounds) =>
        new((int)Math.Round((point.X - windowBounds.Left) * 1000.0 / Math.Max(1, windowBounds.Width - 1)),
            (int)Math.Round((point.Y - windowBounds.Top) * 1000.0 / Math.Max(1, windowBounds.Height - 1)));

    /// <summary>Logical (DIP) to physical pixels for a monitor scale such as 1.75 (175 %).</summary>
    public static PixelPoint LogicalToPhysical(double x, double y, double scale) =>
        new((int)Math.Round(x * scale), (int)Math.Round(y * scale));

    public static (double X, double Y) PhysicalToLogical(PixelPoint point, double scale) =>
        (point.X / scale, point.Y / scale);

    /// <summary>Returns why a click at <paramref name="screenPoint"/> must not happen, or null when it is safe to proceed.</summary>
    public static string? ValidateClick(ScreenImage shot, WindowInfo current, PixelPoint screenPoint, DateTimeOffset now)
    {
        if (current.Handle != shot.WindowHandle)
        {
            return "스크린샷을 찍은 창과 현재 대상 창이 다릅니다.";
        }

        if (current.IsMinimized)
        {
            return "대상 창이 최소화되어 있습니다.";
        }

        if (current.Bounds != shot.SourceBounds)
        {
            return $"스크린샷 이후 창 위치나 크기가 바뀌었습니다({shot.SourceBounds} → {current.Bounds}). 화면을 다시 분석해야 합니다.";
        }

        if (current.Dpi != shot.Dpi)
        {
            return $"스크린샷 이후 화면 배율(DPI)이 바뀌었습니다({shot.Dpi} → {current.Dpi}).";
        }

        if (now - shot.CapturedAt > MaxScreenshotAge)
        {
            return "스크린샷이 오래되어 좌표를 쓰지 않습니다. 화면을 다시 분석해야 합니다.";
        }

        if (!current.Bounds.Contains(screenPoint))
        {
            return "클릭 위치가 대상 창 밖입니다.";
        }

        return null;
    }

    /// <summary>Number of differing bits between two 64-bit image signatures.</summary>
    public static int SignatureDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
