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
        // MigrateAsync は当てるものが無くても移行ロックの取得・解除とモデル差分の確認をするので、
        // 起動のたびに 0.5 秒ほどかかる。適用待ちがあるときだけ呼ぶ（DB ファイルが無ければ全件が適用待ち）。
        if ((await _db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            await _db.Database.MigrateAsync(ct);
        }
        if (!await _db.Boards.AnyAsync(ct))
        {
            _db.Boards.Add(DefaultBoard.Create());
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
}
