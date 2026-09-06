namespace MoTask.App;

/// <summary>
/// 多重起動の防止（仕様 §5.3）。ブリッジがアプリを自動起動するので、同じ SQLite に 2 つの
/// 書き手ができないようにする。取れなかった 2 つ目は何も表示せずに終了する
/// （既存ウィンドウを前面に出す処理は今回は入れない）。
/// </summary>
public static class SingleInstance
{
    /// <summary>Local\ 接頭辞でログオンセッション内に閉じる（他ユーザーの起動を邪魔しない）。</summary>
    public const string MutexName = @"Local\MoTask.SingleInstance";

    /// <summary>取れたら Mutex を返す。呼び手が保持し、終了時に Dispose すること。取れなければ null。</summary>
    public static Mutex? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }
}
