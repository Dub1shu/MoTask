using MoTask.Core.Model;

namespace MoTask.Core.Planning;

/// <summary>
/// 候補の根拠をタスクの説明に残す文面（仕様 §10）。登録と統合で同じものを使う。
/// これが無いと、タスクになった後で「なぜこれをやるのか」が辿れなくなる。
/// </summary>
public static class CandidateNote
{
    public static string Format(TriageCandidate candidate)
    {
        var lines = new List<string> { string.Format(Messages.CandidateNoteHeaderFormat, candidate.Source) };
        if (candidate.From.Length > 0) lines.Add(string.Format(Messages.CandidateNoteFromFormat, candidate.From));
        lines.Add(candidate.Evidence);
        if (candidate.Link.Length > 0) lines.Add(candidate.Link);
        return string.Join("\n", lines);
    }
}
