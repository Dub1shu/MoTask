namespace MoTask.Core.Model;

public enum ColumnRole
{
    Backlog = 0,
    Active = 1,
    Review = 2,
    Done = 3,
    /// <summary>計画の「今日中」から移す先（仕様 2026-10-05-today-column §3.1）。完了と違い特別な制約は無い。</summary>
    Today = 4,
}
