using System.Net;
using System.Text;

namespace WinDiag.Export;

/// <summary>A small document model rendered both as Markdown and as a self-contained HTML page.</summary>
public sealed class ReportDocument(string title)
{
    private abstract record Block;

    private sealed record Heading(int Level, string Text) : Block;

    private sealed record Paragraph(string Text) : Block;

    private sealed record Note(string Text, bool Warning) : Block;

    private sealed record Bullets(IReadOnlyList<string> Items, bool Numbered) : Block;

    private sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

    private readonly List<Block> _blocks = [];

    public string Title { get; } = title;

    public ReportDocument H(int level, string text)
    {
        _blocks.Add(new Heading(Math.Clamp(level, 2, 4), text));
        return this;
    }

    public ReportDocument P(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _blocks.Add(new Paragraph(text));
        }

        return this;
    }

    public ReportDocument Warn(string text)
    {
        _blocks.Add(new Note(text, true));
        return this;
    }

    public ReportDocument Info(string text)
    {
        _blocks.Add(new Note(text, false));
        return this;
    }

    public ReportDocument List(IEnumerable<string> items, bool numbered = false)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (list.Count > 0)
        {
            _blocks.Add(new Bullets(list, numbered));
        }

        return this;
    }

    public ReportDocument Grid(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var list = rows.ToList();
        if (list.Count > 0)
        {
            _blocks.Add(new Table(headers, list));
        }

        return this;
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Md(Title)).AppendLine();
        foreach (var block in _blocks)
        {
            switch (block)
            {
                case Heading h:
                    sb.Append(new string('#', h.Level)).Append(' ').AppendLine(Md(h.Text)).AppendLine();
                    break;
                case Paragraph p:
                    sb.AppendLine(Md(p.Text)).AppendLine();
                    break;
                case Note n:
                    sb.Append("> ").Append(n.Warning ? "**주의:** " : "").AppendLine(Md(n.Text)).AppendLine();
                    break;
                case Bullets b:
                    for (var i = 0; i < b.Items.Count; i++)
                    {
                        sb.Append(b.Numbered ? $"{i + 1}. " : "- ").AppendLine(Md(b.Items[i]));
                    }

                    sb.AppendLine();
                    break;
                case Table t:
                    sb.Append("| ").Append(string.Join(" | ", t.Headers.Select(Cell))).AppendLine(" |");
                    sb.Append('|').Append(string.Concat(t.Headers.Select(_ => " --- |"))).AppendLine();
                    foreach (var row in t.Rows)
                    {
                        sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
                    }

                    sb.AppendLine();
                    break;
            }
        }

        return sb.ToString();
    }

    public string ToHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>").AppendLine("<html lang=\"ko\"><head><meta charset=\"utf-8\">")
            .AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(H(Title)).AppendLine("</title>")
            .AppendLine("""
                <style>
                :root { color-scheme: light dark; --bg:#ffffff; --fg:#1d1d1b; --muted:#5f5e5a; --line:#d9d8d2; --warn-bg:#fff4dc; --warn:#8a5a00; --info-bg:#eef4fc; }
                @media (prefers-color-scheme: dark) { :root { --bg:#121211; --fg:#f0efec; --muted:#a8a69f; --line:#2c2c2a; --warn-bg:#3a2f12; --warn:#fab219; --info-bg:#1f2733; } }
                body { background:var(--bg); color:var(--fg); font-family:"Malgun Gothic","Segoe UI",sans-serif; margin:24px auto; max-width:1100px; padding:0 16px; line-height:1.55; }
                h1 { font-size:1.6rem; } h2 { font-size:1.25rem; border-bottom:1px solid var(--line); padding-bottom:4px; margin-top:28px; } h3 { font-size:1.05rem; margin-bottom:4px; } h4 { font-size:.95rem; }
                table { border-collapse:collapse; width:100%; margin:8px 0 16px; font-size:.9rem; } th, td { border:1px solid var(--line); padding:4px 8px; text-align:left; vertical-align:top; }
                .note { padding:8px 12px; border-radius:6px; background:var(--info-bg); margin:8px 0; } .warn { background:var(--warn-bg); color:var(--warn); }
                p, li { word-break:break-word; } @media print { body { max-width:none; } }
                </style>
                """)
            .AppendLine("</head><body>")
            .Append("<h1>").Append(H(Title)).AppendLine("</h1>");
        foreach (var block in _blocks)
        {
            switch (block)
            {
                case Heading h:
                    sb.Append("<h").Append(h.Level).Append('>').Append(H(h.Text)).Append("</h").Append(h.Level).AppendLine(">");
                    break;
                case Paragraph p:
                    sb.Append("<p>").Append(H(p.Text).Replace("\n", "<br>", StringComparison.Ordinal)).AppendLine("</p>");
                    break;
                case Note n:
                    sb.Append("<div class=\"note").Append(n.Warning ? " warn" : "").Append("\">").Append(H(n.Text)).AppendLine("</div>");
                    break;
                case Bullets b:
                    var tag = b.Numbered ? "ol" : "ul";
                    sb.Append('<').Append(tag).Append('>');
                    foreach (var item in b.Items)
                    {
                        sb.Append("<li>").Append(H(item)).Append("</li>");
                    }

                    sb.Append("</").Append(tag).AppendLine(">");
                    break;
                case Table t:
                    sb.Append("<table><thead><tr>");
                    foreach (var header in t.Headers)
                    {
                        sb.Append("<th>").Append(H(header)).Append("</th>");
                    }

                    sb.Append("</tr></thead><tbody>");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row)
                        {
                            sb.Append("<td>").Append(H(cell)).Append("</td>");
                        }

                        sb.Append("</tr>");
                    }

                    sb.AppendLine("</tbody></table>");
                    break;
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Markdown-safe text: characters that would start markup or break tables are escaped.</summary>
    private static string Md(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r", "", StringComparison.Ordinal))
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|')
            {
                sb.Append('\\');
            }

            sb.Append(ch == '\n' ? ' ' : ch);
        }

        return sb.ToString();
    }

    private static string Cell(string text) => Md(text);
}
