using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// planning_add_candidate の suggestedLabels（名前）を、アーカイブされていないラベルの id に直す。
/// AI にラベルを作らせないため、1 つでも見つからなければ全体を断って名前を返す。
/// </summary>
public static class SuggestedLabels
{
    public static Result<List<int>> Resolve(IReadOnlyList<string> names, IReadOnlyList<Label> labels)
    {
        var active = labels.Where(l => !l.Archived).ToList();
        var ids = new List<int>();
        var unknown = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var found = active.FirstOrDefault(l => string.Equals(l.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                if (!unknown.Contains(name)) unknown.Add(name);
            }
            else if (!ids.Contains(found.Id))
            {
                ids.Add(found.Id);
            }
        }

        return unknown.Count > 0
            ? Result.Fail<List<int>>(string.Format(Messages.CandidateLabelUnknown, string.Join("、", unknown)))
            : Result.Ok(ids);
    }
}
