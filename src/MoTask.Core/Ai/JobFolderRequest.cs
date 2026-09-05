namespace MoTask.Core.Ai;

/// <summary>ジョブフォルダを作るのに要る材料。置き場所は実装が設定から決める。</summary>
public sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction);
