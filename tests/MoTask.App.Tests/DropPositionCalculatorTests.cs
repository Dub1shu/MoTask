using FluentAssertions;
using MoTask.App.DragDrop;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class DropPositionCalculatorTests
{
    private static TaskCardViewModel Card(int id) => new(new TaskItem { Id = id, Title = id.ToString() });

    private static readonly TaskCardViewModel A = Card(1), B = Card(2), C = Card(3), X = Card(9);
    private static readonly TaskCardViewModel[] All = { A, B, C };

    [Theory]
    [InlineData(0, null)]  // 自分の上 → 変化なし
    [InlineData(1, 0)]     // A と B の間 → 位置 0（変化なし相当）
    [InlineData(2, 1)]     // B と C の間 → [B, A, C]
    [InlineData(3, 2)]     // 末尾 → [B, C, A]
    public void SameColumn_MovingFirstCard(int insertIndex, int? expected)
    {
        DropPositionCalculator.ToPosition(All, All, A, insertIndex).Should().Be(expected);
    }

    [Fact]
    public void SameColumn_MovingLastCardToTop()
    {
        DropPositionCalculator.ToPosition(All, All, C, 0).Should().Be(0);
    }

    [Fact]
    public void OtherColumn_InsertsBeforeAnchor_OrAtEnd()
    {
        DropPositionCalculator.ToPosition(All, All, X, 1).Should().Be(1);
        DropPositionCalculator.ToPosition(All, All, X, 3).Should().Be(3);
        DropPositionCalculator.ToPosition(Array.Empty<TaskCardViewModel>(), Array.Empty<TaskCardViewModel>(), X, 0).Should().Be(0);
    }

    [Fact]
    public void HiddenCards_AreCountedInAllButNotInVisible()
    {
        var visible = new[] { A, C }; // B はフィルタで非表示
        DropPositionCalculator.ToPosition(visible, All, X, 1).Should().Be(2, "C の前 = 全件では index 2");
        DropPositionCalculator.ToPosition(visible, All, A, 2).Should().Be(2, "A を除いた [B, C] の末尾");
    }

    /// <summary>裁定6: 同じ列の今いる場所へ落としただけなら、サービスを呼ばずに済ませる。</summary>
    [Theory]
    [InlineData(1, 0, true)]   // A は今 0 番目 → 0 に入れ直しても並びは変わらない
    [InlineData(1, 1, false)]  // A を B と C の間へ
    [InlineData(3, 2, true)]   // C は今 2 番目 = 末尾へ置き直すだけ
    [InlineData(3, 0, false)]  // C を先頭へ
    [InlineData(9, 0, false)]  // 他列のカードは「今の場所」を持たない
    public void IsNoOp_TrueOnlyWhenPositionMatchesCurrentIndex(int movingId, int position, bool expected)
    {
        var moving = All.FirstOrDefault(c => c.Id == movingId) ?? X;

        DropPositionCalculator.IsNoOp(All, moving, position).Should().Be(expected);
    }

    [Fact]
    public void Reorder_MovesItemToInsertIndex()
    {
        var items = new[] { "a", "b", "c", "d" };
        DropPositionCalculator.Reorder(items, "a", 3).Should().Equal("b", "c", "a", "d");
        DropPositionCalculator.Reorder(items, "d", 0).Should().Equal("d", "a", "b", "c");
        DropPositionCalculator.Reorder(items, "b", 1).Should().Equal("a", "b", "c", "d");
        DropPositionCalculator.Reorder(items, "b", 4).Should().Equal("a", "c", "d", "b");
    }

    /// <summary>並び替え対象が見つからないときは、勝手に順序を作り替えず今の順序をそのまま返す。</summary>
    [Fact]
    public void Reorder_UnknownItem_KeepsCurrentOrder()
    {
        var items = new[] { "a", "b", "c" };

        DropPositionCalculator.Reorder(items, "z", 0).Should().Equal("a", "b", "c");
    }
}
