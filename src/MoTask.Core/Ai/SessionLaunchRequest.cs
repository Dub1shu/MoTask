namespace MoTask.Core.Ai;

/// <summary>
/// 1 回の端末起動。Resume が true なら --session-id ではなく --resume を渡す。
/// WorkingDirectory は cwd（プロジェクトの作業フォルダ）で、JobFolder とは別（仕様 §6）。
/// OutputDirectoryName は起動プロンプトで「成果物をここに出して」と伝える先。AI 遂行は
/// artifacts/、朝の実行は result/ と、JobFolderRequest.OutputDirectoryName に合わせて呼び分ける
/// （既定を artifacts のままにして、AiJobService の呼び出しを変えずに済ませる）。
/// </summary>
public sealed record SessionLaunchRequest(
    Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume,
    string OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName);
