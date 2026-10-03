using MoTask.Core.Model;

namespace MoTask.Core.Services;

public interface IBoardService
{
    // 照会
    Task<Result<Board>> GetBoardAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default);

    // タスク
    Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default);
    Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default);
    /// <param name="position">移動先の列で、移動するタスクを除いた Position 順リストへの挿入位置。範囲外は端に丸める。</param>
    Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default);
    Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default);
    Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default);
    Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default);

    // 列
    Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default);
    Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default);
    Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default);
    Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default);
    Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default);
    Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default);

    // 分類
    /// <summary>同名（前後の空白・大文字小文字を無視）があれば作らずにそれを返す。アーカイブ済みなら復元する。</summary>
    Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default);
    Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default);
    Task<Result> UnarchiveProjectAsync(int projectId, CancellationToken ct = default);
    /// <summary>AI ジョブの cwd。空白だけなら null（既定ワークフォルダを使う）に戻す。</summary>
    Task<Result> SetProjectWorkingDirectoryAsync(int projectId, string? path, CancellationToken ct = default);
    /// <summary>同名があれば作らずにそれを返す。アーカイブ済みなら復元し、色は元のまま。</summary>
    Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default);
    /// <summary>一覧から退けるだけ。既にこのラベルが付いているタスクからは外さない。</summary>
    Task<Result> ArchiveLabelAsync(int labelId, CancellationToken ct = default);
    Task<Result> UnarchiveLabelAsync(int labelId, CancellationToken ct = default);
}
