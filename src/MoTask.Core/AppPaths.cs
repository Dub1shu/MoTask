namespace MoTask.Core;

/// <summary>アプリの利用者データの置き場所。DB・設定・endpoint.json が同じフォルダに並ぶ。</summary>
public static class AppPaths
{
    public static string DataDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoTask");

    /// <summary>アプリが立てた MCP サーバの URL とトークン（仕様 §5）。</summary>
    public static string EndpointFile => Path.Combine(DataDirectory, "endpoint.json");
}
