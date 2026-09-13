using MoTask.App.Ai;

namespace MoTask.App.Tests.Fakes;

/// <summary>MCP からの書き換え通知。テストが好きなときに上げる。</summary>
public sealed class FakeBoardChangeSource : IBoardChangeSource
{
    public event EventHandler? BoardChanged;

    /// <summary>BoardChanged を発火させる。</summary>
    public void RaiseBoardChanged() => BoardChanged?.Invoke(this, EventArgs.Empty);
}
