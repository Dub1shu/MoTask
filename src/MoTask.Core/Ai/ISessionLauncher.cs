namespace MoTask.Core.Ai;

/// <summary>端末で claude を起こす口（App の TerminalLauncher）。起こしたら手放す。</summary>
public interface ISessionLauncher
{
    /// <summary>ジョブ開始前の事前確認。claude が見つからなければ Fail(Messages.ClaudeNotFound)。</summary>
    Result CheckAvailable();

    /// <summary>起動コマンドを組み立てる。実行はしない（テストはここだけを見る）。</summary>
    Result<TerminalCommand> BuildCommand(SessionLaunchRequest request);

    /// <summary>端末を開いて手放す。プロセスは所有しない。</summary>
    Result Launch(TerminalCommand command);
}
