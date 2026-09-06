using System.Globalization;
using MoTask.App.Resources;
using MoTask.Core;

namespace MoTask.App.Ai.BoardTools;

/// <summary>
/// id か名前で一意に引く（仕様 §6「共通の約束」）。名前は前後の空白を無視した
/// 大文字小文字を区別しない完全一致。一致なし・複数一致は候補を並べて Fail にする。
/// </summary>
public static class BoardLookup
{
    public static Result<T> Resolve<T>(
        IReadOnlyList<T> items, McpRef reference, Func<T, int> idOf, Func<T, string> nameOf, string kind)
    {
        if (reference.Id is int id)
        {
            var byId = items.FirstOrDefault(x => idOf(x) == id);
            return byId is not null
                ? Result.Ok<T>(byId)
                : Result.Fail<T>(NotFound(kind, reference.ToString(), items, nameOf));
        }

        var name = (reference.Name ?? "").Trim();
        var matches = items.Where(x => string.Equals(nameOf(x).Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => Result.Ok<T>(matches[0]),
            0 => Result.Fail<T>(NotFound(kind, name, items, nameOf)),
            _ => Result.Fail<T>(string.Format(CultureInfo.CurrentCulture,
                Strings.McpRefAmbiguousFormat, kind, name, Candidates(items, nameOf))),
        };
    }

    private static string NotFound<T>(string kind, string value, IReadOnlyList<T> items, Func<T, string> nameOf)
        => string.Format(CultureInfo.CurrentCulture, Strings.McpRefNotFoundFormat, kind, value, Candidates(items, nameOf));

    private static string Candidates<T>(IReadOnlyList<T> items, Func<T, string> nameOf)
        => string.Join("、", items.Select(nameOf));
}
