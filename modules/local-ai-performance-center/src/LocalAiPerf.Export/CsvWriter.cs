using System.Globalization;
using System.Text;

namespace LocalAiPerf.Export;

/// <summary>Minimal RFC 4180 CSV writer. UTF-8 with BOM so that Excel shows Korean text correctly.</summary>
internal sealed class CsvWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvWriter(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public void Row(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                _writer.Write(',');
            }

            _writer.Write(Escape(Format(values[i])));
        }

        _writer.Write("\r\n");
    }

    public void Dispose() => _writer.Dispose();

    internal static string Format(object? value) => value switch
    {
        null => "",
        double d when !double.IsFinite(d) => "",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.######", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    internal static string Escape(string value)
    {
        // Neutralise spreadsheet formula injection from text that came from a server (model names etc.).
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }
        else if (value.Length > 0 && value[0] == '-' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
