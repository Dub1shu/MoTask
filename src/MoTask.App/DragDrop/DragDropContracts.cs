using System.Collections;
using System.Windows;

namespace MoTask.App.DragDrop;

/// <summary>
/// ドロップ 1 回分の情報。以前使っていた D&amp;D ライブラリの IDropInfo のうち、MoTask が実際に
/// 使っていた分だけを写したもの。
/// </summary>
public interface IDropContext
{
    /// <summary>運ばれてきた物。カードなら TaskCardViewModel、列なら ColumnViewModel。</summary>
    object? Data { get; }

    /// <summary>ドロップ先の ItemsControl が束ねている一覧。</summary>
    IEnumerable? TargetCollection { get; }

    /// <summary>この添字の項目の手前に挿す、という意味の値。末尾なら項目数。</summary>
    int InsertIndex { get; }

    /// <summary>
    /// 受けなかったドロップに立てる。DragDropBehavior が e.Handled = !NotHandled を書くので、
    /// 立てておくとルーティングイベントが親へ届く（カード一覧で受けない列のドロップを
    /// 親の列一覧へ通すのに要る）。
    /// </summary>
    bool NotHandled { get; set; }

    DragDropEffects Effects { get; set; }
}

/// <summary>ドラッグ開始 1 回分の情報。以前使っていた D&amp;D ライブラリの IDragInfo のうち使っていた分だけ。</summary>
public interface IDragContext
{
    /// <summary>ドラッグ元として設定された要素。</summary>
    FrameworkElement? VisualSource { get; }

    /// <summary>運ぶ物。ハンドラが載せる。</summary>
    object? Data { get; set; }

    DragDropEffects Effects { get; set; }
}

/// <summary>ドロップを受ける側の判断。View ではなくここに置くことで単体テストできる。</summary>
public interface IDropHandler
{
    void DragOver(IDropContext context);
    void Drop(IDropContext context);
}

/// <summary>ドラッグを始める側の判断。</summary>
public interface IDragHandler
{
    bool CanStartDrag(IDragContext context);
    void StartDrag(IDragContext context);
}

/// <summary>IDropContext の実装。テストからも直接組み立てられるよう素直な作りにする。</summary>
public sealed class DropContext : IDropContext
{
    public DropContext(object? data, IEnumerable? targetCollection, int insertIndex)
    {
        Data = data;
        TargetCollection = targetCollection;
        InsertIndex = insertIndex;
    }

    public object? Data { get; }
    public IEnumerable? TargetCollection { get; }
    public int InsertIndex { get; }
    public bool NotHandled { get; set; }
    public DragDropEffects Effects { get; set; } = DragDropEffects.None;
}

/// <summary>IDragContext の実装。</summary>
public sealed class DragContext : IDragContext
{
    public DragContext(FrameworkElement? visualSource)
    {
        VisualSource = visualSource;
    }

    public FrameworkElement? VisualSource { get; }
    public object? Data { get; set; }
    public DragDropEffects Effects { get; set; } = DragDropEffects.None;
}
