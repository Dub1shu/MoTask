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
    /// <summary>壊れた DB をバックアップして削除する。戻り値はバックアップ先のパス（元ファイルが無ければ作成されない）。</summary>
    public static string BackupAndReset(string dbPath, DateTime now)
    {
        // プールされたコネクションがファイルハンドルを保持したままだとコピーが失敗しうるため、
        // コピー前にも解放しておく（削除時にも SqliteFileCleanup が再度クリアするが無害）。
        SqliteConnection.ClearAllPools();

        var backup = $"{dbPath}.bak-{now:yyyyMMdd-HHmmss}";
        if (File.Exists(dbPath))
        {
            File.Copy(dbPath, backup, overwrite: true);
        }
        SqliteFileCleanup.DeleteDatabaseFiles(dbPath);
        return backup;
    }
}
