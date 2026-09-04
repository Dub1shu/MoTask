using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Core.Services;

public sealed class BoardService : IBoardService
{
    private readonly IBoardRepository _boards;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    // DbContext は同時に1操作しか受け付けないので、ユースケースを直列化する
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardService(IBoardRepository boards, IHistoryRepository history, IUnitOfWork uow, IClock clock)
    {
        _boards = boards;
        _history = history;
        _uow = uow;
        _clock = clock;
    }

    // ---------- 照会 ----------

    public Task<Result<Board>> GetBoardAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        return board is null ? Result.Fail<Board>(Messages.BoardNotFound) : Result.Ok(board);
    }, ct);

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default)
        => GateAsync(() => _history.GetForTaskAsync(taskId, ct), ct);

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => GateAsync(() => _boards.GetProjectsAsync(ct), ct);

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => GateAsync(() => _boards.GetLabelsAsync(ct), ct);

    // ---------- タスク ----------

    public Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default) => RunAsync(async () =>
    {
        title = title.Trim();
        if (title.Length == 0) return Result.Fail<TaskItem>(Messages.TitleRequired);

        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail<TaskItem>(Messages.ColumnNotFound);

        var now = _clock.UtcNow;
        var task = new TaskItem
        {
            Title = title,
            ColumnId = column.Id,
            Position = column.Tasks.Count,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = column.Role == ColumnRole.Done ? now : null,
        };
        column.Tasks.Add(task);
        _boards.AddTask(task);
        _history.Add(new HistoryEntry
        {
            Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Created, ToColumnId = column.Id,
        });

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(task, WipWarnings(column));
    }, ct);

    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 列 ----------

    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 分類 ----------

    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 共通 ----------

    private static string[] WipWarnings(Column column)
        => column.IsOverWip
            ? new[] { string.Format(Messages.WipExceededFormat, column.Name, column.WipLimit) }
            : Array.Empty<string>();

    private async Task<T> GateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<Result> RunAsync(Func<Task<Result>> action, CancellationToken ct) => GateAsync(async () =>
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
        }
    }, ct);

    private Task<Result<T>> RunAsync<T>(Func<Task<Result<T>>> action, CancellationToken ct) => GateAsync(async () =>
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            return Result.Fail<T>($"{Messages.SaveFailed}: {ex.Message}");
        }
    }, ct);
}
