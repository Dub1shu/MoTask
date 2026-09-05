namespace MoTask.Core.Ai;

/// <summary>
/// events.jsonl 1 本の追従。SkipLines は既に取り込んだ行数（events.jsonl は「1 行 = 1 イベント」
/// なので、保存済みイベントの件数がそのままオフセットになる）。
/// OnLine は行の順序どおり、直列に呼ばれる。OnProblem は追えなくなった理由（仕様 §12）。
/// </summary>
public sealed record JobEventSubscription(
    int JobId,
    string EventsPath,
    int SkipLines,
    Func<string, Task> OnLine,
    Func<string, Task> OnProblem);
