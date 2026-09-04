using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

/// <summary>
/// 読み取りは追跡された同一インスタンスを返す（同じ Id のエンティティは同じ参照）。
/// 一覧の順序は Column は Order、TaskItem は Position。TaskItem には論理削除済みも含む。
/// </summary>
public interface IBoardRepository
{
    /// <summary>列（Order 順）と各列のタスク（Position 順、削除済み含む）とラベルまで含めて返す。</summary>
    Task<Board?> GetBoardAsync(CancellationToken ct = default);

    /// <summary>Tasks（削除済み含む）と各タスクの Labels を含めて返す。</summary>
    Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default);

    /// <summary>Labels を含めて返す。</summary>
    Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default);

    Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default);
    Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default);

    void AddColumn(Column column);
    void RemoveColumn(Column column);
    void AddTask(TaskItem task);
    void AddProject(Project project);
    void AddLabel(Label label);
}
