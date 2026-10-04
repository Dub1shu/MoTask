using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

/// <summary>
/// 保存はしない。呼ばれた操作を記録し、既定では成功を返す。テストが変えたい所だけ
/// On～ にデリゲートを挿す（null なら既定の応答）。
/// </summary>
public sealed class FakeBoardService : IBoardService
{
    // ---- 照会 ----

    /// <summary>GetBoardAsync が返す盤。呼ばれるたびに読むので、テストが途中で差し替えられる。</summary>
    public Board Board { get; set; } = new();

    /// <summary>GetProjectsAsync が返す一覧。</summary>
    public IReadOnlyList<Project> Projects { get; set; } = Array.Empty<Project>();

    /// <summary>GetLabelsAsync が返す一覧。</summary>
    public IReadOnlyList<Label> Labels { get; set; } = Array.Empty<Label>();

    /// <summary>GetHistoryAsync が返す履歴。</summary>
    public IReadOnlyList<HistoryEntry> History { get; set; } = Array.Empty<HistoryEntry>();

    /// <summary>GetBoardAsync の既定応答を差し替える。null なら Board を Result.Ok で返す。</summary>
    public Func<Task<Result<Board>>>? OnGetBoard { get; set; }

    /// <summary>GetProjectsAsync の既定応答を差し替える。null なら Projects を返す。</summary>
    public Func<Task<IReadOnlyList<Project>>>? OnGetProjects { get; set; }

    /// <summary>GetLabelsAsync の既定応答を差し替える。null なら Labels を返す。</summary>
    public Func<Task<IReadOnlyList<Label>>>? OnGetLabels { get; set; }

    /// <summary>GetHistoryAsync の既定応答を差し替える。null なら History を返す。</summary>
    public Func<GetHistoryCall, Task<IReadOnlyList<HistoryEntry>>>? OnGetHistory { get; set; }

    /// <summary>GetBoardAsync が呼ばれた回数。</summary>
    public int GetBoardCalls { get; private set; }

    /// <summary>GetHistoryAsync に渡された引数を呼ばれた順に。</summary>
    public List<GetHistoryCall> GetHistoryCalls { get; } = new();

    public Task<Result<Board>> GetBoardAsync(CancellationToken ct = default)
    {
        GetBoardCalls++;
        return OnGetBoard?.Invoke() ?? Task.FromResult(Result.Ok(Board));
    }

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => OnGetProjects?.Invoke() ?? Task.FromResult(Projects);

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => OnGetLabels?.Invoke() ?? Task.FromResult(Labels);

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default)
    {
        var call = new GetHistoryCall(taskId);
        GetHistoryCalls.Add(call);
        return OnGetHistory?.Invoke(call) ?? Task.FromResult(History);
    }

    // ---- タスク ----

    /// <summary>CreateTaskAsync に渡された引数を呼ばれた順に。</summary>
    public List<CreateTaskCall> CreateTaskCalls { get; } = new();

    /// <summary>UpdateTaskAsync に渡された引数を呼ばれた順に。</summary>
    public List<UpdateTaskCall> UpdateTaskCalls { get; } = new();

    /// <summary>MoveTaskAsync に渡された引数を呼ばれた順に。</summary>
    public List<MoveTaskCall> MoveTaskCalls { get; } = new();

    /// <summary>DeleteTaskAsync に渡された taskId を呼ばれた順に。</summary>
    public List<int> DeleteTaskCalls { get; } = new();

    /// <summary>RestoreTaskAsync に渡された taskId を呼ばれた順に。</summary>
    public List<int> RestoreTaskCalls { get; } = new();

    /// <summary>SetTaskLabelsAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetTaskLabelsCall> SetTaskLabelsCalls { get; } = new();

    /// <summary>CreateTaskAsync の既定応答を差し替える。null なら Title だけ設定した TaskItem を Result.Ok で返す。</summary>
    public Func<CreateTaskCall, Task<Result<TaskItem>>>? OnCreateTask { get; set; }

    /// <summary>UpdateTaskAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<UpdateTaskCall, Task<Result>>? OnUpdateTask { get; set; }

    /// <summary>MoveTaskAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<MoveTaskCall, Task<Result>>? OnMoveTask { get; set; }

    /// <summary>DeleteTaskAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnDeleteTask { get; set; }

    /// <summary>RestoreTaskAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnRestoreTask { get; set; }

    /// <summary>SetTaskLabelsAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetTaskLabelsCall, Task<Result>>? OnSetTaskLabels { get; set; }

    public Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default)
    {
        var call = new CreateTaskCall(columnId, title);
        CreateTaskCalls.Add(call);
        return OnCreateTask?.Invoke(call) ?? Task.FromResult(Result.Ok(new TaskItem { Title = title }));
    }

    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default)
    {
        var call = new UpdateTaskCall(update);
        UpdateTaskCalls.Add(call);
        return OnUpdateTask?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default)
    {
        var call = new MoveTaskCall(taskId, toColumnId, position);
        MoveTaskCalls.Add(call);
        return OnMoveTask?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default)
    {
        DeleteTaskCalls.Add(taskId);
        return OnDeleteTask?.Invoke(taskId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default)
    {
        RestoreTaskCalls.Add(taskId);
        return OnRestoreTask?.Invoke(taskId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var call = new SetTaskLabelsCall(taskId, labelIds.ToList());
        SetTaskLabelsCalls.Add(call);
        return OnSetTaskLabels?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    // ---- 列 ----

    /// <summary>AddColumnAsync に渡された引数を呼ばれた順に。</summary>
    public List<AddColumnCall> AddColumnCalls { get; } = new();

    /// <summary>RenameColumnAsync に渡された引数を呼ばれた順に。</summary>
    public List<RenameColumnCall> RenameColumnCalls { get; } = new();

    /// <summary>SetColumnRoleAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetColumnRoleCall> SetColumnRoleCalls { get; } = new();

    /// <summary>ReorderColumnsAsync に渡された引数を呼ばれた順に。</summary>
    public List<ReorderColumnsCall> ReorderColumnsCalls { get; } = new();

    /// <summary>SetWipLimitAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetWipLimitCall> SetWipLimitCalls { get; } = new();

    /// <summary>DeleteColumnAsync に渡された columnId を呼ばれた順に。</summary>
    public List<int> DeleteColumnCalls { get; } = new();

    /// <summary>AddColumnAsync の既定応答を差し替える。null なら Name/Role を設定した Column を Result.Ok で返す。</summary>
    public Func<AddColumnCall, Task<Result<Column>>>? OnAddColumn { get; set; }

    /// <summary>RenameColumnAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<RenameColumnCall, Task<Result>>? OnRenameColumn { get; set; }

    /// <summary>SetColumnRoleAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetColumnRoleCall, Task<Result>>? OnSetColumnRole { get; set; }

    /// <summary>ReorderColumnsAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<ReorderColumnsCall, Task<Result>>? OnReorderColumns { get; set; }

    /// <summary>SetWipLimitAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetWipLimitCall, Task<Result>>? OnSetWipLimit { get; set; }

    /// <summary>DeleteColumnAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnDeleteColumn { get; set; }

    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default)
    {
        var call = new AddColumnCall(name, role);
        AddColumnCalls.Add(call);
        return OnAddColumn?.Invoke(call) ?? Task.FromResult(Result.Ok(new Column { Name = name, Role = role }));
    }

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default)
    {
        var call = new RenameColumnCall(columnId, name);
        RenameColumnCalls.Add(call);
        return OnRenameColumn?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default)
    {
        var call = new SetColumnRoleCall(columnId, role);
        SetColumnRoleCalls.Add(call);
        return OnSetColumnRole?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default)
    {
        var call = new ReorderColumnsCall(orderedColumnIds.ToList());
        ReorderColumnsCalls.Add(call);
        return OnReorderColumns?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default)
    {
        var call = new SetWipLimitCall(columnId, wipLimit);
        SetWipLimitCalls.Add(call);
        return OnSetWipLimit?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default)
    {
        DeleteColumnCalls.Add(columnId);
        return OnDeleteColumn?.Invoke(columnId) ?? Task.FromResult(Result.Ok());
    }

    // ---- 分類 ----

    /// <summary>CreateProjectAsync に渡された name を呼ばれた順に。</summary>
    public List<string> CreateProjectCalls { get; } = new();

    /// <summary>ArchiveProjectAsync に渡された projectId を呼ばれた順に。</summary>
    public List<int> ArchiveProjectCalls { get; } = new();

    /// <summary>UnarchiveProjectAsync に渡された projectId を呼ばれた順に。</summary>
    public List<int> UnarchiveProjectCalls { get; } = new();

    /// <summary>SetProjectWorkingDirectoryAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetProjectWorkingDirectoryCall> SetProjectWorkingDirectoryCalls { get; } = new();

    /// <summary>CreateLabelAsync に渡された引数を呼ばれた順に。</summary>
    public List<CreateLabelCall> CreateLabelCalls { get; } = new();

    /// <summary>ArchiveLabelAsync に渡された labelId を呼ばれた順に。</summary>
    public List<int> ArchiveLabelCalls { get; } = new();

    /// <summary>UnarchiveLabelAsync に渡された labelId を呼ばれた順に。</summary>
    public List<int> UnarchiveLabelCalls { get; } = new();

    /// <summary>CreateProjectAsync の既定応答を差し替える。null なら Name を設定した Project を Result.Ok で返す。</summary>
    public Func<string, Task<Result<Project>>>? OnCreateProject { get; set; }

    /// <summary>ArchiveProjectAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnArchiveProject { get; set; }

    /// <summary>UnarchiveProjectAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnUnarchiveProject { get; set; }

    /// <summary>SetProjectWorkingDirectoryAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetProjectWorkingDirectoryCall, Task<Result>>? OnSetProjectWorkingDirectory { get; set; }

    /// <summary>CreateLabelAsync の既定応答を差し替える。null なら Name/Color を設定した Label を Result.Ok で返す。</summary>
    public Func<CreateLabelCall, Task<Result<Label>>>? OnCreateLabel { get; set; }

    /// <summary>ArchiveLabelAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnArchiveLabel { get; set; }

    /// <summary>UnarchiveLabelAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<int, Task<Result>>? OnUnarchiveLabel { get; set; }

    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default)
    {
        CreateProjectCalls.Add(name);
        return OnCreateProject?.Invoke(name) ?? Task.FromResult(Result.Ok(new Project { Name = name }));
    }

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default)
    {
        ArchiveProjectCalls.Add(projectId);
        return OnArchiveProject?.Invoke(projectId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> UnarchiveProjectAsync(int projectId, CancellationToken ct = default)
    {
        UnarchiveProjectCalls.Add(projectId);
        return OnUnarchiveProject?.Invoke(projectId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetProjectWorkingDirectoryAsync(int projectId, string? path, CancellationToken ct = default)
    {
        var call = new SetProjectWorkingDirectoryCall(projectId, path);
        SetProjectWorkingDirectoryCalls.Add(call);
        return OnSetProjectWorkingDirectory?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default)
    {
        var call = new CreateLabelCall(name, color);
        CreateLabelCalls.Add(call);
        return OnCreateLabel?.Invoke(call) ?? Task.FromResult(Result.Ok(new Label { Name = name, Color = color }));
    }

    public Task<Result> ArchiveLabelAsync(int labelId, CancellationToken ct = default)
    {
        ArchiveLabelCalls.Add(labelId);
        return OnArchiveLabel?.Invoke(labelId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> UnarchiveLabelAsync(int labelId, CancellationToken ct = default)
    {
        UnarchiveLabelCalls.Add(labelId);
        return OnUnarchiveLabel?.Invoke(labelId) ?? Task.FromResult(Result.Ok());
    }

    /// <summary>RenameProjectAsync に渡された引数を呼ばれた順に。</summary>
    public List<RenameCall> RenameProjectCalls { get; } = new();

    /// <summary>RenameLabelAsync に渡された引数を呼ばれた順に。</summary>
    public List<RenameCall> RenameLabelCalls { get; } = new();

    /// <summary>SetLabelColorAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetLabelColorCall> SetLabelColorCalls { get; } = new();

    /// <summary>RenameProjectAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<RenameCall, Task<Result>>? OnRenameProject { get; set; }

    /// <summary>RenameLabelAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<RenameCall, Task<Result>>? OnRenameLabel { get; set; }

    /// <summary>SetLabelColorAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetLabelColorCall, Task<Result>>? OnSetLabelColor { get; set; }

    public Task<Result> RenameProjectAsync(int projectId, string name, CancellationToken ct = default)
    {
        var call = new RenameCall(projectId, name);
        RenameProjectCalls.Add(call);
        return OnRenameProject?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> RenameLabelAsync(int labelId, string name, CancellationToken ct = default)
    {
        var call = new RenameCall(labelId, name);
        RenameLabelCalls.Add(call);
        return OnRenameLabel?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    /// <summary>SetProjectColorAsync に渡された引数を呼ばれた順に。</summary>
    public List<SetProjectColorCall> SetProjectColorCalls { get; } = new();

    /// <summary>SetProjectColorAsync の既定応答を差し替える。null なら Result.Ok を返す。</summary>
    public Func<SetProjectColorCall, Task<Result>>? OnSetProjectColor { get; set; }

    public Task<Result> SetProjectColorAsync(int projectId, string? color, CancellationToken ct = default)
    {
        var call = new SetProjectColorCall(projectId, color);
        SetProjectColorCalls.Add(call);
        return OnSetProjectColor?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetLabelColorAsync(int labelId, string color, CancellationToken ct = default)
    {
        var call = new SetLabelColorCall(labelId, color);
        SetLabelColorCalls.Add(call);
        return OnSetLabelColor?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }
}
