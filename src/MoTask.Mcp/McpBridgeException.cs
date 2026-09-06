namespace MoTask.Mcp;

/// <summary>利用者（Claude 越しの人）に見せる日本語のメッセージを持つ失敗。JSON-RPC エラーになる。</summary>
public sealed class McpBridgeException : Exception
{
    public McpBridgeException(string message) : base(message)
    {
    }
}
