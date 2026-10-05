using System.Collections;
using System.Windows;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.DragDrop;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// ドロップハンドラの振る舞い。IDropInfo は差し替えられるので、UI スレッドなしで
/// 「どんなドロップがどんな保存になるか」を確かめられる。
/// </summary>
public class DropHandlerTests
{
    private readonly FakeBoardService _service = new();
    /// <summary>GetBoardAsync は毎回この値を読むので、LoadAsync の前なら差し替えられる。</summary>
    private Board _board = TestBoards.Sample();
    private readonly BoardViewModel _vm;

    public DropHandlerTests()
    {
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), new FakeBoardChangeSource());
    }

    private static DropContext Info(object? data, IEnumerable? targetCollection, int insertIndex)
        => new(data, targetCollection, insertIndex);

    private static string SaveFailure(string detail) => $"{Messages.SaveFailed}: {detail}";

    // ---- カード ----

    [Fact]
    public async Task CardDrop_ToOtherColumn_MovesOnce()
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];   // 未着手の 10
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[1].Cards, 0));

        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 2, 0));
    }

    [Fact]
    public async Task CardDrop_WithinColumn_UsesPositionInAllCards()
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];   // 10 は今 0 番目。11 の下（末尾）へ落とす。
        var handler = new CardDropHandler(_vm);

        handler.Drop(Info(card, _vm.Columns[0].Cards, 2));

        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 1, 1));
    }

    /// <summary>
    /// 削除済みカードを含む列（表示 3 枚 / AllCards 4 枚）で、表示上の挿入位置が
    /// AllCards 上の position に読み替えられて渡ること。ここがずれると、画面が見せた場所とは
    /// 別の場所に保存される。
    /// </summary>
    [Fact]
    public async Task CardDrop_WithHiddenDeletedCard_TranslatesVisibleIndexToAllCardsPosition()
    {
        _board = TestBoards.WithDeletedCard();
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.Cards.Select(c => c.Id).Should().Equal(10, 13, 14);
        column.AllCards.Select(c => c.Id).Should().Equal(10, 11, 13, 14);
        var handler = new CardDropHandler(_vm);

        // 14 を、表示上の 10 と 13 のあいだ（表示 index 1）へ落とす。
        handler.Drop(Info(column.Cards[2], column.Cards, 1));

        // 14 を除いた AllCards は [10, 11(削除済み), 13] なので、13 の直前は index 2。
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(14, 1, 2));
    }

    /// <summary>裁定6: 何も変わらないドロップは保存へ行かない。</summary>
    [Theory]
    [InlineData(0)]   // 自分の上
    [InlineData(1)]   // 自分の直下 = 今の位置
    public async Task CardDrop_AtOwnPosition_DoesNotTouchTheService(int insertIndex)
    {
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        var info = Info(card, _vm.Columns[0].Cards, insertIndex);
        var handler = new CardDropHandler(_vm);

        handler.Drop(info);

        _service.MoveTaskCalls.Should().BeEmpty();
        _vm.BannerMessage.Should().BeNull();
        info.NotHandled.Should().BeTrue();   // 何もしなかったのに Drop を握らない
    }

    /// <summary>
    /// 列のドラッグはカード列で握らずに親へ返す。DragOver だけでなく Drop も同じで、
    /// NotHandled を立てないと e.Handled = true になる。カード一覧は列のほぼ
    /// 全面を覆うので、握ると列ヘッダーを別の列へ落としても何も起きない。
    /// </summary>
    [Fact]
    public async Task CardDrop_WithColumnData_LeavesEventForTheParent()
    {
        await _vm.LoadAsync();
        var info = Info(_vm.Columns[0], _vm.Columns[0].Cards, 0);
        var handler = new CardDropHandler(_vm);

        handler.Drop(info);

        info.NotHandled.Should().BeTrue();
        _service.MoveTaskCalls.Should().BeEmpty();
    }

    /// <summary>裁定3: Drop は待てないが、保存の失敗を握りつぶしてはいけない。</summary>
    [Fact]
    public async Task CardDrop_WhenServiceThrows_ShowsBannerInsteadOfEscaping()
    {
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromException<Result>(new InvalidOperationException("database is locked"))
            : Task.FromResult(Result.Ok());
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

        info.NotHandled.Should().BeTrue();
        info.Effects.Should().Be(DragDropEffects.None);
    }

    // ---- 列 ----

    [Fact]
    public async Task ColumnDrop_ReordersColumns()
    {
        await _vm.LoadAsync();
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(Info(_vm.Columns[0], _vm.Columns, 2));   // 未着手を進行中の後ろへ

        _vm.Columns.Select(c => c.Name).Should().Equal("進行中", "未着手", "完了");
        _service.ReorderColumnsCalls.Should().ContainSingle()
            .Which.OrderedColumnIds.Should().Equal(2, 1, 3);
    }

    [Fact]
    public async Task ColumnDrop_AtOwnPosition_DoesNotTouchTheService()
    {
        await _vm.LoadAsync();
        var info = Info(_vm.Columns[1], _vm.Columns, 1);
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(info);

        _service.ReorderColumnsCalls.Should().BeEmpty();
        info.NotHandled.Should().BeTrue();
    }

    /// <summary>受け付けないドロップは Drop でも握らずに返す（DragOver と同じ扱い）。</summary>
    [Fact]
    public async Task ColumnDrop_WithCardData_LeavesEventForTheParent()
    {
        await _vm.LoadAsync();
        var info = Info(_vm.Columns[0].Cards[0], _vm.Columns, 0);
        var handler = new ColumnDropHandler(_vm);

        handler.Drop(info);

        info.NotHandled.Should().BeTrue();
        _service.ReorderColumnsCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnDrop_WhenServiceThrows_ShowsBannerInsteadOfEscaping()
    {
        _service.OnReorderColumns = _ => Task.FromException<Result>(new InvalidOperationException("disk I/O error"));
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

        info.NotHandled.Should().BeTrue();
        info.Effects.Should().Be(DragDropEffects.None);
    }

    // ---- 管理ダイアログの行 ----

    private async Task<ManageClassificationsViewModel> ManageAsync()
    {
        _service.Projects = new[]
        {
            new Project { Id = 100, Name = "A", Order = 0 },
            new Project { Id = 101, Name = "B", Order = 1 },
            new Project { Id = 102, Name = "C", Order = 2 },
            new Project { Id = 103, Name = "Z", Order = 3, Archived = true },
        };
        _service.Labels = new[] { new Label { Id = 200, Name = "x" }, new Label { Id = 201, Name = "y" } };
        await _vm.LoadAsync();
        return new ManageClassificationsViewModel(_vm, ClassificationKind.Project);
    }

    [Fact]
    public async Task ClassificationDrop_MovesTheRow_AndSavesTheLiveIds()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);

        handler.Drop(Info(manage.Projects[2], manage.Projects, 0)); // C を先頭へ
        await manage.PendingChange;

        _service.ReorderProjectsCalls.Single().OrderedIds.Should().Equal(102, 100, 101);
    }

    [Fact]
    public async Task DragOver_PastTheLiveRows_ClampsToTheLiveEnd()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);
        var context = Info(manage.Projects[0], manage.Projects, 4); // アーカイブ済み Z の下

        handler.DragOver(context);

        context.NotHandled.Should().BeFalse();
        context.Effects.Should().Be(DragDropEffects.Move);
        context.InsertIndex.Should().Be(3);
    }

    [Fact]
    public async Task ClassificationDrop_BelowTheArchived_PutsItLastAmongTheLive()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);

        handler.Drop(Info(manage.Projects[0], manage.Projects, 4));
        await manage.PendingChange;

        _service.ReorderProjectsCalls.Single().OrderedIds.Should().Equal(101, 102, 100);
    }

    [Fact]
    public async Task ClassificationDrop_InPlace_DoesNotSave()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);
        var context = Info(manage.Projects[1], manage.Projects, 1);

        handler.Drop(context);

        context.NotHandled.Should().BeTrue();
        _service.ReorderProjectsCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Drop_OnTheOtherList_IsRefused()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);
        var context = Info(manage.Projects[0], manage.Labels, 0);

        handler.DragOver(context);
        handler.Drop(context);

        context.NotHandled.Should().BeTrue();
        _service.ReorderProjectsCalls.Should().BeEmpty();
        _service.ReorderLabelsCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ArchivedRow_IsNotAccepted()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);
        var context = Info(manage.Projects[3], manage.Projects, 0);

        handler.DragOver(context);

        context.NotHandled.Should().BeTrue();
    }

    /// <summary>
    /// 自動スクロールのタイマーも挿入線を引き直す。そこでもハンドラの DragOver を通して、
    /// アーカイブ済みの行の間に線を出さない（仕様 §6）。
    /// </summary>
    [Fact]
    public async Task AutoScrollInsertIndex_GoesThroughTheHandler()
    {
        var manage = await ManageAsync();
        var handler = new ClassificationDropHandler(manage);

        DragDropBehavior.InsertIndexFor(handler, manage.Projects[0], manage.Projects, 4).Should().Be(3);
        DragDropBehavior.InsertIndexFor(handler, manage.Projects[0], manage.Labels, 0).Should().BeNull();
        DragDropBehavior.InsertIndexFor(null, manage.Projects[0], manage.Projects, 4).Should().BeNull();
    }
}
