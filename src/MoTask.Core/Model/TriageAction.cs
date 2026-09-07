namespace MoTask.Core.Model;

/// <summary>
/// Claude の推薦（仕様 §8）。<b>推薦であって実行ではない。</b>決めるのは常に人。
/// </summary>
public enum TriageAction
{
    Register = 0,
    Merge = 1,
    Later = 2,
    Reject = 3,
}
