using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class BoardRepository : IBoardRepository
{
    private readonly MoTaskDbContext _db;

    public BoardRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public Task<Board?> GetBoardAsync(CancellationToken ct = default)
        => _db.Boards
            .Include(b => b.Columns.OrderBy(c => c.Order))
            .ThenInclude(c => c.Tasks.OrderBy(t => t.Position))
            .ThenInclude(t => t.Labels)
            .FirstOrDefaultAsync(ct);

    public Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default)
        => _db.Columns
            .Include(c => c.Tasks.OrderBy(t => t.Position))
            .ThenInclude(t => t.Labels)
            .FirstOrDefaultAsync(c => c.Id == columnId, ct);

    public Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default)
        => _db.Tasks.Include(t => t.Labels).FirstOrDefaultAsync(t => t.Id == taskId, ct);

    public Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default)
        => _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

    // 名前は CurrentCulture で比べたいので、並べるのは読み出した後（件数は数十件）
    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => (await _db.Projects.ToListAsync(ct)).InDisplayOrder().ToList();

    public Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default)
        => _db.Labels.FirstOrDefaultAsync(l => l.Id == labelId, ct);

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => (await _db.Labels.ToListAsync(ct)).InDisplayOrder().ToList();

    public void AddColumn(Column column) => _db.Columns.Add(column);
    public void RemoveColumn(Column column) => _db.Columns.Remove(column);
    public void AddTask(TaskItem task) => _db.Tasks.Add(task);
    public void AddProject(Project project) => _db.Projects.Add(project);
    public void AddLabel(Label label) => _db.Labels.Add(label);
}
