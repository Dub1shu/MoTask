using MoTask.Core.Services;

namespace MoTask.App.Ai;

/// <summary>
/// ボード画面を通らない書き込みをまとめて 1 本の通知にする。MCP の board ツール(BoardToolHost)と、
/// 計画の仕分け(登録・統合)がボードを書き換える。BoardViewModel はこれだけを購読する。
/// どちらもワーカースレッドから上がりうるので、購読側で UI スレッドへ載せ替えること。
/// </summary>
public sealed class BoardChangeSources : IBoardChangeSource
{
    public event EventHandler? BoardChanged;

    public BoardChangeSources(IBoardChangeSource mcp, IPlanningService planning)
    {
        // どちらもアプリに 1 つずつの singleton なので、外すことはしない。
        mcp.BoardChanged += (_, e) => BoardChanged?.Invoke(this, e);
        planning.BoardChanged += (_, e) => BoardChanged?.Invoke(this, e);
    }
}
