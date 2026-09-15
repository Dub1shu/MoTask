namespace MoTask.Core.Ai;

/// <summary>
/// 1 回の端末起動。Resume が true なら --session-id ではなく --resume を渡す。
/// WorkingDirectory は cwd（プロジェクトの作業フォルダ）で、JobFolder とは別（ターミナル AI 仕様 §6）。
/// OutputDirectoryName は起動プロンプトで「成果物をここに出して」と伝える先。AI 遂行は artifacts/。
/// null でも空文字でも案内そのものを出さない（朝の実行は成果をファイルに出さない・仕様 §5.4）。
/// CloseOnExit は「claude が終わったら窓も畳むか」。朝の実行だけが true で、既定テンプレートが
/// cmd.exe /k ではなく cmd.exe /c になる（MCP 受け渡し仕様 §5.2）。
/// McpConfigPath が入っていれば --mcp-config で渡す。朝の実行だけが使う（仕様 §5.4）。
/// </summary>
public sealed record SessionLaunchRequest(
    Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume,
    string? OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName,
    bool CloseOnExit = false,
    string? McpConfigPath = null);
