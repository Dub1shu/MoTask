using System.Diagnostics;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Mcp;

/// <summary>ファイル・プロセス・時間。テストから丸ごと差し替えて EndpointResolver を純関数にする。</summary>
public interface IAppHost
{
    McpEndpoint? ReadEndpoint();

    bool IsRunning(int pid);

    /// <summary>MoTask.exe を起動する。見つからなければ McpBridgeException。</summary>
    void LaunchApp();

    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>本物の実装。</summary>
public sealed class SystemAppHost : IAppHost
{
    /// <summary>開発中に bin フォルダの exe を指すための逃げ道（仕様からの意図的な追加）。</summary>
    public const string ExeOverrideVariable = "MOTASK_APP_EXE";

    /// <summary>発行時にブリッジと並べて置く前提（AssemblyName が MoTask なので MoTask.exe）。</summary>
    public const string DefaultExeName = "MoTask.exe";

    public McpEndpoint? ReadEndpoint() => McpEndpointFile.TryRead(AppPaths.EndpointFile);

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void LaunchApp()
    {
        var path = ExePath();
        if (!File.Exists(path)) throw new McpBridgeException(string.Format(Messages.McpAppNotFoundFormat, path));
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new McpBridgeException(string.Format(Messages.McpAppNotFoundFormat, $"{path} ({ex.Message})"));
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    private static string ExePath()
    {
        var overridden = Environment.GetEnvironmentVariable(ExeOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(AppContext.BaseDirectory, DefaultExeName)
            : overridden;
    }
}
