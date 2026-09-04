using Microsoft.EntityFrameworkCore;

namespace MoTask.Data;

/// <summary>起動時に呼ぶ。マイグレーションを適用し、ボードが無ければ既定のボードと4列を投入する。</summary>
public sealed class DatabaseInitializer
{
    private readonly MoTaskDbContext _db;

    public DatabaseInitializer(MoTaskDbContext db)
    {
        _db = db;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);
        if (!await _db.Boards.AnyAsync(ct))
        {
            _db.Boards.Add(DefaultBoard.Create());
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
}
