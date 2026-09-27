namespace MoTask.App.Ai;

/// <summary>
/// ボード画面を通らずにボードが書き換わったことを知らせる口。BoardToolHost(MCP 経由)が実装し、
/// BoardViewModel は計画の仕分けと合わせた BoardChangeSources を購読する。
/// イベントはワーカースレッドから上がるので、購読側で UI スレッドへ載せ替えること。
/// </summary>
public interface IBoardChangeSource
{
    event EventHandler? BoardChanged;
}
