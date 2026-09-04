namespace MoTask.Data.Tests;

/// <summary>テストごとに一時ファイルの SQLite を作り、Dispose で消す。</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public string Path { get; }

    public SqliteTestDatabase()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MoTaskTests");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.db");
    }

    public MoTaskDbContext CreateContext()
        => new(MoTaskDbContextOptions.Create(DbPaths.ConnectionString(Path)));

    public void Dispose() => SqliteFileCleanup.DeleteDatabaseFiles(Path);
}
