namespace MoTask.Core.Ai;

/// <summary>端末で claude を起こす口（App の TerminalLauncher）。</summary>
public interface ISessionLauncher
{
    /// <summary>ジョブ開始前の事前確認。claude が見つからなければ Fail(Messages.ClaudeNotFound)。</summary>
    Result CheckAvailable();

    /// <summary>起動コマンドを組み立てる。実行はしない（テストはここだけを見る）。</summary>
    Result<TerminalCommand> BuildCommand(SessionLaunchRequest request);

    /// <summary>所有しない起動。ハンドルはその場で捨てる（AI 遂行）。</summary>
    Result Launch(TerminalCommand command);

    /// <summary>所有する起動。ownerId に紐づけて Process を保持する（朝の実行）。</summary>
    Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command);

    /// <summary>プロセスツリーごと終了させる。知らない ownerId は黙って無視する。</summary>
    void CloseOwned(int ownerId);

    /// <summary>
    /// 再起動後に掛け直す。生きていて開始時刻（UTC）が一致すれば true。
    /// false なら「閉じる能力」だけを諦める。実行の追跡そのものは続けてよい。
    /// </summary>
    bool TryReattach(int ownerId, int processId, DateTime startedAt);

    /// <summary>
    /// 所有しているプロセスが終わった。渡すのは ownerId だけで、理由は問わない。
    /// CloseOwned で自分から閉じた分は上がらない（意図した終了なので）。
    /// </summary>
    event EventHandler<int>? OwnedSessionExited;
}

/// <summary>所有した端末 1 本。StartedAt は UTC（pid の再利用を見分けるための照合材料）。</summary>
public sealed record OwnedSession(int ProcessId, DateTime StartedAt);
