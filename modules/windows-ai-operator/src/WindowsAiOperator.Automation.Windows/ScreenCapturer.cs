using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WindowsAiOperator.Core.Automation;
using static WindowsAiOperator.Automation.Windows.NativeMethods;

namespace WindowsAiOperator.Automation.Windows;

/// <summary>
/// Captures one window (PrintWindow with full content, falling back to the screen) and returns a PNG scaled to a maximum
/// width, together with the physical rectangle and DPI it was taken at. Nothing is written to disk here.
/// </summary>
internal static class ScreenCapturer
{
    public static ScreenImage? Capture(IntPtr handle, int maxWidth)
    {
        var bounds = WindowEnumerator.Bounds(handle);
        if (bounds.IsEmpty || bounds.Width > 16384 || bounds.Height > 16384)
        {
            return null;
        }

        var dpi = (int)Math.Max(96, GetDpiForWindow(handle));
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        var captured = false;
        using (var graphics = Graphics.FromImage(full))
        {
            var hdc = graphics.GetHdc();
            try
            {
                captured = PrintWindow(handle, hdc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (!captured || LooksBlank(full))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }
        }

        var scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(full, 0, 0, width, height);
        }

        var rgb = ToRgb(scaled);
        using var stream = new MemoryStream();
        scaled.Save(stream, ImageFormat.Png);
        return new ScreenImage
        {
            Png = stream.ToArray(),
            ImageWidth = width,
            ImageHeight = height,
            SourceBounds = bounds,
            Dpi = dpi,
            WindowHandle = handle.ToInt64(),
            Signature = PngWriter.Signature(width, height, rgb),
        };
    }

    private static bool LooksBlank(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        for (var i = 1; i < 9; i++)
        {
            var p = bitmap.GetPixel(bitmap.Width * i / 10, bitmap.Height * ((i * 3) % 10) / 10);
            if (p != first)
            {
                return false;
            }
        }

        return first.R == 0 && first.G == 0 && first.B == 0;
    }

    private static byte[] ToRgb(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rgb = new byte[bitmap.Width * bitmap.Height * 3];
            var row = new byte[data.Stride];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var i = (y * bitmap.Width + x) * 3;
                    rgb[i] = row[x * 3 + 2];
                    rgb[i + 1] = row[x * 3 + 1];
                    rgb[i + 2] = row[x * 3];
                }
            }

            return rgb;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
