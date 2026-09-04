namespace MoTask.Core.Services;

/// <summary>詳細パネルからの一括更新。Labels は SetTaskLabelsAsync で別に扱う。</summary>
public sealed record TaskUpdate(int TaskId, string Title, string Description, int? ProjectId, DateOnly? DueDate);
