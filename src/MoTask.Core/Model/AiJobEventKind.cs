namespace MoTask.Core.Model;

public enum AiJobEventKind
{
    AssistantText = 0,
    ToolUse = 1,
    ToolResult = 2,
    /// <summary>3 と 4 は廃止した承認イベント（PermissionAsked / PermissionDecided）の番号。空けたままにする。</summary>
    Error = 5,
    Result = 6,
    /// <summary>知らないフック、パースできない行。捨てずに残すための受け皿。</summary>
    System = 7,
    /// <summary>SessionStart フック。</summary>
    SessionStarted = 8,
    /// <summary>SessionEnd フック。ジョブの完了。</summary>
    SessionEnded = 9,
    /// <summary>Stop フック。モデルの応答が終わって人の入力待ちになった。</summary>
    TurnEnded = 10,
}
