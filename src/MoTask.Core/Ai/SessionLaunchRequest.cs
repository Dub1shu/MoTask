namespace MoTask.Core.Ai;

/// <summary>
/// 1 回の端末起動。Resume が true なら --session-id ではなく --resume を渡す。
/// WorkingDirectory は cwd（プロジェクトの作業フォルダ）で、JobFolder とは別（仕様 §6）。
/// </summary>
public sealed record SessionLaunchRequest(Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume);
