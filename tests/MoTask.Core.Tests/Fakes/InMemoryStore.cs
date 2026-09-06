using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Core.Tests.Fakes;

/// <summary>
/// IBoardRepository / IHistoryRepository / IUnitOfWork / IAiJobRepository を
/// まとめて実装するテスト用ストア。
/// 参照は常に同一インスタンスを返す（EF の追跡と同じ契約）。Id は Add 時に即採番する。
/// </summary>
public sealed class InMemoryStore : IBoardRepository, IHistoryRepository, IUnitOfWork, IAiJobRepository
{
    private int _nextId = 1;
    private long _nextHistoryId = 1;

    public Board Board { get; } = new() { Id = 1, Name = "テスト" };
    public List<Project> Projects { get; } = new();
    public List<Label> Labels { get; } = new();
    public List<HistoryEntry> History { get; } = new();
    public List<AiJob> Jobs { get; } = new();
    public int SaveCount { get; private set; }
    public bool FailNextSave { get; set; }

    // ---- テスト用の投入ヘルパー ----

    public Column SeedColumn(string name, ColumnRole role, int? wipLimit = null)
    {
        var column = new Column
        {
            Id = _nextId++, BoardId = Board.Id, Name = name, Role = role,
            Order = Board.Columns.Count, WipLimit = wipLimit,
        };
        Board.Columns.Add(column);
        return column;
    }

    public TaskItem SeedTask(Column column, string title, DateTime? createdAt = null)
    {
        var at = createdAt ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem
        {
            Id = _nextId++, Title = title, ColumnId = column.Id, Position = column.Tasks.Count,
            CreatedAt = at, UpdatedAt = at,
            CompletedAt = column.Role == ColumnRole.Done ? at : null,
        };
        column.Tasks.Add(task);
        return task;
    }

    public Project SeedProject(string name)
    {
        var p = new Project { Id = _nextId++, Name = name };
        Projects.Add(p);
        return p;
    }

    public Label SeedLabel(string name, string color = Label.DefaultColor)
    {
        var l = new Label { Id = _nextId++, Name = name, Color = color };
        Labels.Add(l);
        return l;
    }

    public IEnumerable<TaskItem> AllTasks => Board.Columns.SelectMany(c => c.Tasks);

    // ---- IBoardRepository ----

    public Task<Board?> GetBoardAsync(CancellationToken ct = default) => Task.FromResult<Board?>(Board);

    public Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default)
        => Task.FromResult(Board.Columns.FirstOrDefault(c => c.Id == columnId));

    public Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult(AllTasks.FirstOrDefault(t => t.Id == taskId));

    public Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Id == projectId));

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>(Projects.OrderBy(p => p.Name).ToList());

    public Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default)
        => Task.FromResult(Labels.FirstOrDefault(l => l.Id == labelId));

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Label>>(Labels.OrderBy(l => l.Name).ToList());

    public void AddColumn(Column column) { if (column.Id == 0) column.Id = _nextId++; }

    /// <summary>EF の Remove と同じく、この呼び出しでボードから列が消える。</summary>
    public void RemoveColumn(Column column) => Board.Columns.Remove(column);

    public void AddTask(TaskItem task) { if (task.Id == 0) task.Id = _nextId++; }
    public void AddProject(Project project) { if (project.Id == 0) project.Id = _nextId++; Projects.Add(project); }
    public void AddLabel(Label label) { if (label.Id == 0) label.Id = _nextId++; Labels.Add(label); }

    // ---- IHistoryRepository ----

    public void Add(HistoryEntry entry)
    {
        entry.Id = _nextHistoryId++;
        if (entry.Task is not null) entry.TaskId = entry.Task.Id;
        History.Add(entry);
    }

    public Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HistoryEntry>>(
            History.Where(h => h.TaskId == taskId).OrderByDescending(h => h.At).ThenByDescending(h => h.Id).ToList());

    // ---- IAiJobRepository ----
    //
    // GetForTaskAsync は IHistoryRepository と「同じ名前・同じ引数・違う戻り値」で衝突する。
    // C# は戻り値だけのオーバーロードを許さないので、衝突する分は明示的実装にする
    // （テストは Jobs / History を直接見るので支障は無い）。

    public void Add(AiJob job)
    {
        if (job.Id == 0) job.Id = _nextId++;
        Jobs.Add(job);
    }

    public Task<AiJob?> GetAsync(int jobId, CancellationToken ct = default)
        => Task.FromResult(Jobs.FirstOrDefault(j => j.Id == jobId));

    Task<IReadOnlyList<AiJob>> IAiJobRepository.GetForTaskAsync(int taskId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AiJob>>(Jobs.Where(j => j.TaskId == taskId).OrderByDescending(j => j.Id).ToList());

    public Task<IReadOnlyList<AiJob>> GetByStatusAsync(IReadOnlyCollection<AiJobStatus> statuses, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiJob>>(Jobs.Where(j => statuses.Contains(j.Status)).OrderBy(j => j.Id).ToList());

    // ---- IUnitOfWork ----

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new PersistenceException("テスト用の保存失敗");
        }
        SaveCount++;
        return Task.CompletedTask;
    }
}
