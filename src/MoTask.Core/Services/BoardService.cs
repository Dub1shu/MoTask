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

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);
        if (task.IsDeleted) return Result.Fail(Messages.TaskAlreadyDeleted);

        var column = await _boards.GetColumnAsync(task.ColumnId, ct).ConfigureAwait(false);

        var now = _clock.UtcNow;
        task.DeletedAt = now;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry { Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Deleted });

        // 削除済みタスクが Position の枠を占有しないよう、残った未削除タスクを詰め直す
        if (column is not null) RenumberColumn(column);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);
        if (!task.IsDeleted) return Result.Fail(Messages.TaskNotDeleted);

        var now = _clock.UtcNow;
        task.DeletedAt = null;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry { Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Restored });

        // 復元後は列の末尾に置き、未削除タスクを再採番する（元の位置には戻さない）
        var column = await _boards.GetColumnAsync(task.ColumnId, ct).ConfigureAwait(false);
        if (column is not null) RenumberColumn(column, moveToEnd: task);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

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

    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Column>(Messages.ColumnNameRequired);
        if (role == ColumnRole.Done) return Result.Fail<Column>(Messages.CannotAssignDoneRole);

        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail<Column>(Messages.BoardNotFound);

        var column = new Column
        {
            BoardId = board.Id,
            Name = name,
            Role = role,
            Order = board.Columns.Count == 0 ? 0 : board.Columns.Max(c => c.Order) + 1,
        };
        board.Columns.Add(column);
        _boards.AddColumn(column);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(column);
    }, ct);

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail(Messages.ColumnNameRequired);
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Name == name) return Result.Ok();

        column.Name = name;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default) => RunAsync(async () =>
    {
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Role == ColumnRole.Done) return Result.Fail(Messages.DoneColumnCannotChangeRole);
        if (role == ColumnRole.Done) return Result.Fail(Messages.CannotAssignDoneRole);
        if (column.Role == role) return Result.Ok();

        column.Role = role;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail(Messages.BoardNotFound);

        var existing = board.Columns.Select(c => c.Id).ToHashSet();
        var requested = orderedColumnIds.ToHashSet();
        if (orderedColumnIds.Count != existing.Count || !requested.SetEquals(existing))
        {
            return Result.Fail(Messages.ReorderMustIncludeAllColumns);
        }

        for (var i = 0; i < orderedColumnIds.Count; i++)
        {
            board.Columns.First(c => c.Id == orderedColumnIds[i]).Order = i;
        }
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (wipLimit is int limit && limit < 1) return Result.Fail(Messages.WipLimitMustBePositive);
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);

        column.WipLimit = wipLimit;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(WipWarnings(column));
    }, ct);

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail(Messages.BoardNotFound);
        var column = board.Columns.FirstOrDefault(c => c.Id == columnId);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Role == ColumnRole.Done) return Result.Fail(Messages.DoneColumnCannotBeDeleted);
        if (column.Tasks.Count > 0) return Result.Fail(Messages.ColumnHasTasks);

        board.Columns.Remove(column);
        _boards.RemoveColumn(column);
        var remaining = board.Columns.OrderBy(c => c.Order).ToList();
        for (var i = 0; i < remaining.Count; i++) remaining[i].Order = i;

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    // ---------- 分類 ----------

    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Project>(Messages.ProjectNameRequired);

        var project = new Project { Name = name };
        _boards.AddProject(project);
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(project);
    }, ct);

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var project = await _boards.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null) return Result.Fail(Messages.ProjectNotFound);
        if (project.Archived) return Result.Ok();

        project.Archived = true;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Label>(Messages.LabelNameRequired);

        var label = new Label { Name = name, Color = string.IsNullOrWhiteSpace(color) ? Label.DefaultColor : color.Trim() };
        _boards.AddLabel(label);
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(label);
    }, ct);

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

    /// <summary>
    /// 列のタスクを Position 順に並べたリストを返す（削除済みも含む）。
    /// Position が並んだときの決着は Id で付ける。表示側の
    /// <c>ColumnViewModel.SyncCardsFromModel</c> と同じ規則にしておかないと、同じ列を
    /// Core と表示で別の順序に見て、ドロップ位置が1つずれたまま保存されてしまう。
    /// </summary>
    private static List<TaskItem> Ordered(Column column)
        => column.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id).ToList();

    /// <summary>
    /// 列全体の Position を 0 から振り直す。未削除タスクを Position 順に詰め、削除済みはその後ろへ回す。
    /// 削除済みを後ろへ送るのは、列内の Position を必ず一意に保つため（削除済みが古い Position を
    /// 抱えたままだと、繰り上がった生存タスクと重複する）。
    /// </summary>
    /// <param name="moveToEnd">未削除タスクの中で末尾に置きたいタスク（復元したタスク）。</param>
    private static void RenumberColumn(Column column, TaskItem? moveToEnd = null)
    {
        var ordered = Ordered(column);
        var renumbered = ordered.Where(t => !t.IsDeleted && !ReferenceEquals(t, moveToEnd)).ToList();
        if (moveToEnd is { IsDeleted: false }) renumbered.Add(moveToEnd);
        renumbered.AddRange(ordered.Where(t => t.IsDeleted));
        Renumber(renumbered);
    }

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
