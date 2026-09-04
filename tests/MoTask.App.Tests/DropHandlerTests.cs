using System.Collections;
using FluentAssertions;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.DragDrop;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// ドロップハンドラの振る舞い。IDropInfo は差し替えられるので、UI スレッドなしで
/// 「どんなドロップがどんな保存になるか」を確かめられる。
/// </summary>
public class DropHandlerTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Board _board = TestBoards.Sample();
    private readonly BoardViewModel _vm;

    public DropHandlerTests()
    {
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(Array.Empty<Project>()));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(Array.Empty<Label>()));
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _service.ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _vm = new BoardViewModel(_service, new TestClock());
    }

    private static IDropInfo Info(object? data, IEnumerable? targetCollection, int insertIndex)
    {
        var info = Substitute.For<IDropInfo>();
        info.Data.Returns(data);
        info.TargetCollection.Returns(targetCollection);
        info.InsertIndex.Returns(insertIndex);
        return info;
    }

    private static string SaveFailure(string detail) => $"{Messages.SaveFailed}: {detail}";

    // ---- カード ----

    [Fact]
    public async Task CardDrop_ToOtherColumn_MovesOnce()
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];   // 未着手の 10
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[1].Cards, 0));

        await _service.Received(1).MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CardDrop_WithinColumn_UsesPositionInAllCards()
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];   // 10 は今 0 番目。11 の下（末尾）へ落とす。
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[0].Cards, 2));

        await _service.Received(1).MoveTaskAsync(10, 1, 1, Arg.Any<CancellationToken>());
    }

    /// <summary>裁定6: 何も変わらないドロップは保存へ行かない。</summary>
    [Theory]
    [InlineData(0)]   // 自分の上
    [InlineData(1)]   // 自分の直下 = 今の位置
    public async Task CardDrop_AtOwnPosition_DoesNotTouchTheService(int insertIndex)
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[0].Cards, insertIndex));

        await _service.DidNotReceive()
            .MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _vm.BannerMessage.Should().BeNull();
    }

    /// <summary>裁定3: Drop は待てないが、保存の失敗を握りつぶしてはいけない。</summary>
    [Fact]
    public async Task CardDrop_WhenServiceThrows_ShowsBannerInsteadOfEscaping()
    {
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("database is locked")));
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[1].Cards, 0));

        _vm.BannerMessage.Should().Be(SaveFailure("database is locked"));
    }

    /// <summary>列のドラッグはカード列で握らずに親へ返す。握ると列をどこにも落とせない。</summary>
    [Fact]
    public async Task CardDragOver_WithColumnData_LeavesEventForTheParent()
    {
        await _vm.LoadAsync();
        var info = Info(_vm.Columns[0], _vm.Columns[0].Cards, 0);
        var handler = new CardDropHandler(_vm);

        handler.DragOver(info);

        info.Received().NotHandled = true;
        info.DidNotReceiveWithAnyArgs().Effects = default;
    }

    // ---- 列 ----

    [Fact]
    public async Task ColumnDrop_ReordersColumns()
    {
        await _vm.LoadAsync();
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(Info(_vm.Columns[0], _vm.Columns, 2));   // 未着手を進行中の後ろへ

        _vm.Columns.Select(c => c.Name).Should().Equal("進行中", "未着手", "完了");
        await _service.Received(1).ReorderColumnsAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 2, 1, 3 })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ColumnDrop_AtOwnPosition_DoesNotTouchTheService()
    {
        await _vm.LoadAsync();
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(Info(_vm.Columns[1], _vm.Columns, 1));

        await _service.DidNotReceive()
            .ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ColumnDrop_WhenServiceThrows_ShowsBannerInsteadOfEscaping()
    {
        _service.ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("disk I/O error")));
        await _vm.LoadAsync();
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(Info(_vm.Columns[0], _vm.Columns, 2));

        _vm.BannerMessage.Should().Be(SaveFailure("disk I/O error"));
    }

    [Fact]
    public async Task ColumnDragOver_WithCardData_LeavesEventForTheParent()
    {
        await _vm.LoadAsync();
        var info = Info(_vm.Columns[0].Cards[0], _vm.Columns, 0);
        var handler = new ColumnDropHandler(_vm);

        handler.DragOver(info);

        info.Received().NotHandled = true;
        info.DidNotReceiveWithAnyArgs().Effects = default;
    }
}
