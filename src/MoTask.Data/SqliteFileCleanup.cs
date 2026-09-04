using Microsoft.Data.Sqlite;

namespace MoTask.Data;

/// <summary>
/// SQLite の DB ファイルとその副生成物（-wal, -shm, -journal）を削除する処理を1箇所にまとめたもの。
/// 呼び出し前に <see cref="SqliteConnection.ClearAllPools"/> でコネクションプールを解放し、
/// ファイルがロックされたままにならないようにする。テストの後始末（<c>SqliteTestDatabase</c>）と
/// DB リカバリ（Task 13）の両方から使う。
/// </summary>
public static class SqliteFileCleanup
{
    private static readonly string[] Suffixes = { "", "-wal", "-shm", "-journal" };

    public static void DeleteDatabaseFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in Suffixes)
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
