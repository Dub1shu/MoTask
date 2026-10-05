namespace MoTask.Core.Model;

/// <summary>
/// プロジェクトとラベルの並べ方。選択肢・絞り込み・カード・MCP・履歴の文言のどこでも、この 1 か所の規則で並べる。
/// 既存の行は Order がすべて 0 なので、ユーザーが並べ替えるまでは名前順になる。
/// </summary>
public static class ClassificationOrder
{
    public static IEnumerable<T> InDisplayOrder<T>(this IEnumerable<T> items) where T : IClassification
        => items.OrderBy(x => x.Order)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Id);
}
