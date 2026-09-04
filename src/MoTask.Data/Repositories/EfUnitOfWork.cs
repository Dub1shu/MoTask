using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;

namespace MoTask.Data.Repositories;

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly MoTaskDbContext _db;

    public EfUnitOfWork(MoTaskDbContext db)
    {
        _db = db;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException)
        {
            // 失敗した変更を追跡から捨て、次の GetBoard で DB の状態を読み直せるようにする
            _db.ChangeTracker.Clear();
            throw new PersistenceException(ex.GetBaseException().Message, ex);
        }
    }
}
