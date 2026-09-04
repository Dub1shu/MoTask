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

    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default) => RunAsync(async () =>
    {
        var title = update.Title.Trim();
        if (title.Length == 0) return Result.Fail(Messages.TitleRequired);

        var task = await _boards.GetTaskAsync(update.TaskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var description = update.Description ?? "";
        var changes = new Dictionary<string, FieldChange>();

        if (task.Title != title) changes["Title"] = new FieldChange(task.Title, title);
        if (task.Description != description) changes["Description"] = new FieldChange(task.Description, description);
        if (task.ProjectId != update.ProjectId)
        {
            Project? newProject = null;
            if (update.ProjectId is int pid)
            {
                newProject = await _boards.GetProjectAsync(pid, ct).ConfigureAwait(false);
                if (newProject is null) return Result.Fail(Messages.ProjectNotFound);
            }
            var oldProject = task.ProjectId is int oid ? await _boards.GetProjectAsync(oid, ct).ConfigureAwait(false) : null;
            changes["Project"] = new FieldChange(oldProject?.Name, newProject?.Name);
        }
        if (task.DueDate != update.DueDate)
        {
            changes["DueDate"] = new FieldChange(FormatDate(task.DueDate), FormatDate(update.DueDate));
        }

        if (changes.Count == 0) return Result.Ok();

        task.Title = title;
        task.Description = description;
        task.ProjectId = update.ProjectId;
        task.DueDate = update.DueDate;
        AddEditedHistory(task, changes);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var source = await _boards.GetColumnAsync(task.ColumnId, ct).ConfigureAwait(false);
        var target = await _boards.GetColumnAsync(toColumnId, ct).ConfigureAwait(false);
        if (source is null || target is null) return Result.Fail(Messages.ColumnNotFound);

        var columnChanged = source.Id != target.Id;

        // 移動タスクを除いた順序リストを作り、そこへ挿入して再採番する
        var sourceTasks = Ordered(source).Where(t => t.Id != task.Id).ToList();
        var targetTasks = columnChanged ? Ordered(target).ToList() : sourceTasks;
        position = Math.Clamp(position, 0, targetTasks.Count);
        targetTasks.Insert(position, task);

        if (columnChanged)
        {
            source.Tasks.Remove(task);
            target.Tasks.Add(task);
            task.ColumnId = target.Id;
            Renumber(sourceTasks);
        }
        Renumber(targetTasks);

        if (columnChanged)
        {
            var now = _clock.UtcNow;
            task.UpdatedAt = now;
            task.CompletedAt = target.Role == ColumnRole.Done ? now : null;
            _history.Add(new HistoryEntry
            {
                Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Moved,
                FromColumnId = source.Id, ToColumnId = target.Id,
            });
        }

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(columnChanged ? WipWarnings(target) : Array.Empty<string>());
    }, ct);

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var all = await _boards.GetLabelsAsync(ct).ConfigureAwait(false);
        var wanted = labelIds.Distinct().OrderBy(id => id).ToList();
        var newLabels = wanted.Select(id => all.FirstOrDefault(l => l.Id == id)).ToList();
        if (newLabels.Any(l => l is null)) return Result.Fail(Messages.LabelNotFound);

        var current = task.Labels.Select(l => l.Id).OrderBy(id => id).ToList();
        if (current.SequenceEqual(wanted)) return Result.Ok();

        var changes = new Dictionary<string, FieldChange>
        {
            ["Labels"] = new FieldChange(JoinNames(task.Labels), JoinNames(newLabels!)),
        };
        task.Labels.Clear();
        task.Labels.AddRange(newLabels!);
        AddEditedHistory(task, changes);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

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

    private void AddEditedHistory(TaskItem task, Dictionary<string, FieldChange> changes)
    {
        var now = _clock.UtcNow;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry
        {
            Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Edited,
            Detail = HistoryDetail.Serialize(changes),
        });
    }

    private static string? FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    private static string JoinNames(IEnumerable<Label> labels)
        => string.Join(", ", labels.OrderBy(l => l.Name).Select(l => l.Name));

    private static string[] WipWarnings(Column column)
        => column.IsOverWip
            ? new[] { string.Format(Messages.WipExceededFormat, column.Name, column.WipLimit) }
            : Array.Empty<string>();

    /// <summary>列のタスクを Position 順に並べたリストを返す（削除済みも含む）。</summary>
    private static List<TaskItem> Ordered(Column column) => column.Tasks.OrderBy(t => t.Position).ToList();

    /// <summary>渡された順序どおりに Position を 0 から振り直す。</summary>
    private static void Renumber(List<TaskItem> tasks)
    {
        for (var i = 0; i < tasks.Count; i++) tasks[i].Position = i;
    }

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
