using Microsoft.Data.Sqlite;

namespace MoTask.Data;

/// <summary>
/// 壊れた DB を <c>motask.db.bak-yyyyMMdd-HHmmss</c> にコピーしてから、本体と WAL/SHM/journal を削除する。
/// アプリ起動時、DB オープンに失敗した時に呼ぶ想定（Task 14 の App.xaml.cs）。この時点では
/// まだ BoardService への呼び出しは走っていない起動シーケンスの中でのみ呼ぶこと。呼び出し前に
/// 対象の DbContext を破棄しておくこと（コネクションプールの解放はこのメソッドが行う）。
/// </summary>
public static class DatabaseRecovery
{
    /// <summary>
    /// 壊れた DB をバックアップして削除する。戻り値はバックアップ先のパス（元ファイルが無ければ作成されない）。
    /// 同一秒内に複数回呼ばれるなどして基本名が衝突する場合は、既存のバックアップを上書きせず
    /// 連番を付けた別名にする。
    /// </summary>
    public static string BackupAndReset(string dbPath, DateTime now)
    {
        // プールされたコネクションがファイルハンドルを保持したままだとコピーが失敗しうるため、
        // コピー前にも解放しておく（削除時にも SqliteFileCleanup が再度クリアするが無害）。
        SqliteConnection.ClearAllPools();

        var backup = NextAvailableBackupPath(dbPath, now);
        if (File.Exists(dbPath))
        {
            // overwrite: false により、衝突時に既存バックアップを黙って潰すことはあり得ない。
            File.Copy(dbPath, backup, overwrite: false);
        }
        SqliteFileCleanup.DeleteDatabaseFiles(dbPath);
        return backup;
    }

    private static string NextAvailableBackupPath(string dbPath, DateTime now)
    {
        var basePath = $"{dbPath}.bak-{now:yyyyMMdd-HHmmss}";
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{basePath}-{i}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
