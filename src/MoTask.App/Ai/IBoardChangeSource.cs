namespace MoTask.App.Ai;

/// <summary>
/// MCP 経由でボードが書き換わったことを知らせる口。BoardToolHost が実装し、BoardViewModel が購読する。
/// イベントはワーカースレッドから上がるので、購読側で UI スレッドへ載せ替えること。
/// </summary>
public interface IBoardChangeSource
{
    event EventHandler? BoardChanged;
}
