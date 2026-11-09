using System.Text;
using WinDiag.Core.Events;

namespace WinDiag.Export;

/// <summary>The event classification rules as a Markdown table (docs/EVENT_CATALOG.md is generated from this).</summary>
public static class CatalogMarkdown
{
    public static string Build(EventCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 이벤트 분류 카탈로그").AppendLine()
            .AppendLine($"`wdl catalog`로 코드({nameof(EventCatalog)})에서 생성한 문서입니다. 규칙은 항상 **공급자(Provider)**를 포함하며 이벤트 ID만으로 판단하지 않습니다.")
            .AppendLine("ID가 비어 있는 행은 그 공급자의 기본 규칙입니다(특정 ID 규칙이 없을 때 적용). 카탈로그에 없는 공급자의 위험·오류 이벤트는 '기타 오류·경고'로 수집합니다.")
            .AppendLine();
        sb.AppendLine("| 공급자 | 이벤트 ID | 채널 | 분류 | 진단 중요도 | 경고 수준도 수집 | 정보 수준도 수집 | 제목 | 의미 | 이 이벤트만으로 알 수 없는 것 |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var r in catalog.Rules)
        {
            sb.Append("| ").Append(Cell(r.Provider))
                .Append(" | ").Append(r.EventIds is null ? "(기본)" : string.Join(", ", r.EventIds))
                .Append(" | ").Append(r.Channels is null ? "모든 채널" : Cell(string.Join(", ", r.Channels)))
                .Append(" | ").Append(Cell(EventCategories.Name(r.Category)))
                .Append(" | ").Append(r.Severity is { } s ? LevelText.Severity(s) : "Windows 수준 따름")
                .Append(" | ").Append(r.CollectWarnings ? "예" : "아니요")
                .Append(" | ").Append(r.CollectInformational ? "예" : "아니요")
                .Append(" | ").Append(Cell(r.Title))
                .Append(" | ").Append(Cell(r.Meaning ?? ""))
                .Append(" | ").Append(Cell(r.Caution ?? "")).AppendLine(" |");
        }

        sb.AppendLine().AppendLine("기본으로 수집하지 않는 공급자: " + string.Join(", ", EventCatalog.DefaultExcludedProviders) + " (개인정보·소음)");
        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
