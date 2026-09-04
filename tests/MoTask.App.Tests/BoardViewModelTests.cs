using System.Collections.Specialized;
using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class BoardViewModelTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Label _urgent = TestBoards.Urgent();
    private readonly Board _board;
    private readonly BoardViewModel _vm;

    public BoardViewModelTests()
    {
        _board = TestBoards.Sample(_urgent);
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { _urgent }));
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>()));
        _vm = new BoardViewModel(_service, new TestClock());
    }

    private static int[] Ids(ColumnViewModel c) => c.Cards.Select(x => x.Id).ToArray();

    /// <summary>BoardService が永続化の失敗を伝えるときの文言。検証による却下と区別される。</summary>
    private static string SaveFailure(string detail) => $"{Messages.SaveFailed}: {detail}";

    [Fact]
    public async Task Load_BuildsColumnsAndCards()
    {
        await _vm.LoadAsync();

        _vm.Columns.Select(c => c.Name).Should().Equal("未着手", "進行中", "完了");
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        Ids(_vm.Columns[1]).Should().Equal(12);
        _vm.Columns[1].CountText.Should().Be("1 / 1");
        _vm.Columns[1].IsOverWip.Should().BeFalse();
        _vm.Columns[2].IsDone.Should().BeTrue();
        var a = _vm.Columns[0].Cards[0];
        a.ProjectName.Should().Be("顧客A対応");
        a.DueText.Should().Be("9/8");
        a.Labels.Select(l => l.Name).Should().Equal("至急");
        _vm.Filter.Projects.Select(p => p.Name).Should().Equal("すべてのプロジェクト", "顧客A対応");
        _vm.Filter.Labels.Select(l => l.Name).Should().Equal("至急");
    }

    [Fact]
    public async Task Load_ColumnWithoutWipLimit_ShowsBareCount()
    {
        await _vm.LoadAsync();

        _vm.Columns[0].CountText.Should().Be("2");
    }

    /// <summary>裁定6: チップの文字色は段の濃さから選ぶ。accent-500 は明るい段なので濃い文字。</summary>
    [Fact]
    public async Task Load_LabelChip_PicksReadableTextColorForItsRampStep()
    {
        await _vm.LoadAsync();

        var chip = _vm.Columns[0].Cards[0].Labels[0];
        chip.Color.Should().Be("accent-500");
        chip.TextColor.Should().Be("neutral-900");
        _vm.Filter.Labels[0].TextColor.Should().Be("neutral-900");
    }

    [Fact]
    public async Task Filter_SearchText_ChangesVisibleCards()
    {
        await _vm.LoadAsync();

        _vm.Filter.SearchText = "求人";

        Ids(_vm.Columns[0]).Should().Equal(11);
        Ids(_vm.Columns[1]).Should().BeEmpty();
        _vm.Columns[0].AllCards.Should().HaveCount(2, "フィルタは全件リストを減らさない");

        _vm.Filter.SearchText = "";
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
    }

    [Fact]
    public async Task Filter_ProjectAndLabel_Combine()
    {
        await _vm.LoadAsync();

        _vm.Filter.SelectedProject = _vm.Filter.Projects[1];
        Ids(_vm.Columns[0]).Should().Equal(10);

        _vm.Filter.SelectedProject = _vm.Filter.Projects[0];
        _vm.Filter.Labels[0].IsSelected = true;
        Ids(_vm.Columns[0]).Should().Equal(10);
    }

    [Fact]
    public async Task Filter_ShowDeleted_TogglesDeletedCards()
    {
        _board.Columns[0].Tasks[1].DeletedAt = DateTime.UtcNow;
        await _vm.LoadAsync();

        Ids(_vm.Columns[0]).Should().Equal(10);
        _vm.Filter.ShowDeleted = true;
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        _vm.Columns[0].Cards[1].IsDeleted.Should().BeTrue();
    }

    /// <summary>
    /// 裁定2: 検証で却下されただけならボードは読み直さない。モデルは動いていないので、
    /// 楽観的に動かした表示を両列の組み直しで元に戻す。
    /// </summary>
    [Fact]
    public async Task MoveCard_WhenRejected_RollsBackWithoutReloading()
    {
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail("だめ")));
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be("だめ");
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        Ids(_vm.Columns[1]).Should().Equal(12);
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());

        // 巻き戻しは同じカード VM を元の列へ戻す。作り直すと選択が孤児 VM を指してしまう。
        _vm.Columns[0].Cards[0].Should().BeSameAs(card);
        _vm.Columns[0].AllCards.Should().Contain(card);
        _vm.SelectedCard.Should().BeSameAs(card);
        _vm.Columns[0].SelectedCard.Should().BeSameAs(card);
    }

    /// <summary>
    /// 却下された移動のあとも、選択中のカードは盤面に属したままでなければならない。
    /// 孤児になっていると ColumnOf が null になり、保存は通るのに表示が更新されない。
    /// </summary>
    [Fact]
    public async Task MoveCard_WhenRejected_LeavesSelectedCardUsable()
    {
        var backlog = _board.Columns[0];
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail("だめ")));
        _service.DeleteTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            backlog.Tasks.Single(t => t.Id == 10).DeletedAt = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);
        await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        await _vm.DeleteSelectedCommand.ExecuteAsync(null);

        card.IsDeleted.Should().BeTrue();
        Ids(_vm.Columns[0]).Should().Equal(11);
        _vm.Columns[0].CountText.Should().Be("1");
    }

    /// <summary>裁定2: 保存に失敗したときだけメモリ上の状態が信用できないので読み直す。</summary>
    [Fact]
    public async Task MoveCard_WhenSaveFails_RollsBackAndReloads()
    {
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(SaveFailure("ディスクがいっぱいです"))));
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be(SaveFailure("ディスクがいっぱいです"));
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        Ids(_vm.Columns[1]).Should().Equal(12);
        await _service.Received(2).GetBoardAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveCard_WhenServiceSucceeds_ReflectsModel()
    {
        var backlog = _board.Columns[0];
        var active = _board.Columns[1];
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            TestBoards.Move(backlog, active, taskId: 10, position: 0);
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeTrue();
        _vm.BannerMessage.Should().BeNull();
        Ids(_vm.Columns[0]).Should().Equal(11);
        // 実サービスは移動先のコレクションへ末尾に足すので、コレクション順は Position 順と一致しない。
        // 表示は Position を読むので、どちらでも [10, 12] にならなければならない。
        active.Tasks.Select(t => t.Id).Should().Equal(12, 10);
        Ids(_vm.Columns[1]).Should().Equal(10, 12);
        _vm.Columns[1].IsOverWip.Should().BeTrue();
        _vm.Columns[1].CountText.Should().Be("2 / 1");
        _vm.SelectedCard.Should().BeSameAs(card);
        _vm.Columns[1].SelectedCard.Should().BeSameAs(card);
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 削除済みを表示したまま復元すると、BoardService はそのタスクを列の末尾へ動かす。表示の
    /// AllCards をモデルから組み直さないと、画面はカードを元のスロットに残したまま DB だけが動く。
    /// 次のドラッグは AllCards 上の index で数えられて別の場所に保存され、例外もバナーも出ない。
    /// </summary>
    [Fact]
    public async Task RestoreTask_RebuildsAllCardsInModelPositionOrder()
    {
        var backlog = TestBoards.SeedDeletedInTheMiddle(_board.Columns[0]);
        _service.RestoreTaskAsync(11, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            TestBoards.Restore(backlog, 11);
            return Task.FromResult(Result.Ok());
        });
        _vm.Filter.ShowDeleted = true;
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.AllCards.Select(c => c.Id).Should().Equal(10, 11, 13, 14);

        await _vm.RestoreTaskAsync(column.AllCards.Single(c => c.Id == 11));

        column.AllCards.Select(c => c.Id).Should().Equal(10, 13, 14, 11);
        column.AllCards.Select(c => c.Model.Position).Should().Equal(0, 1, 2, 3);
        Ids(column).Should().Equal(10, 13, 14, 11);
    }

    /// <summary>削除も同じ: 生存カードが繰り上がり、削除済みが後ろへ回る並びを表示も追う。</summary>
    [Fact]
    public async Task DeleteTask_RebuildsAllCardsInModelPositionOrder()
    {
        var backlog = TestBoards.SeedDeletedInTheMiddle(_board.Columns[0]);
        _service.DeleteTaskAsync(13, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            TestBoards.SoftDelete(backlog, 13, new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc));
            return Task.FromResult(Result.Ok());
        });
        _vm.Filter.ShowDeleted = true;
        await _vm.LoadAsync();
        var column = _vm.Columns[0];

        await _vm.DeleteTaskAsync(column.AllCards.Single(c => c.Id == 13));

        column.AllCards.Select(c => c.Id).Should().Equal(10, 14, 11, 13);
        column.AllCards.Select(c => c.Model.Position).Should().Equal(0, 1, 2, 3);
    }


    [Fact]
    public async Task SelectCard_SyncsColumnSelection_AndCloseClearsIt()
    {
        await _vm.LoadAsync();
        var a = _vm.Columns[0].Cards[0];
        var c = _vm.Columns[1].Cards[0];

        _vm.SelectCard(a);
        _vm.SelectedCard.Should().BeSameAs(a);
        _vm.Columns[0].SelectedCard.Should().BeSameAs(a);

        _vm.Columns[1].SelectedCard = c;   // ListBox からの選択
        _vm.SelectedCard.Should().BeSameAs(c);
        _vm.Columns[0].SelectedCard.Should().BeNull();

        _vm.CloseDetailCommand.Execute(null);
        _vm.SelectedCard.Should().BeNull();
        _vm.Columns[1].SelectedCard.Should().BeNull();
    }

    [Fact]
    public async Task NewTask_OpensInlineEditorOnSelectedOrFirstColumn()
    {
        await _vm.LoadAsync();

        _vm.NewTaskCommand.Execute(null);
        _vm.Columns[0].IsAddingTask.Should().BeTrue();

        _vm.Columns[0].CancelAddTaskCommand.Execute(null);
        _vm.SelectCard(_vm.Columns[1].Cards[0]);
        _vm.NewTaskCommand.Execute(null);
        _vm.Columns[1].IsAddingTask.Should().BeTrue();
        _vm.Columns[0].IsAddingTask.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAddTask_EmptyTitle_DoesNotCallService_AndStaysOpen()
    {
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginAddTaskCommand.Execute(null);
        column.NewTaskTitle = "   ";

        await column.CommitAddTaskCommand.ExecuteAsync(null);

        await _service.DidNotReceive().CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        column.IsAddingTask.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAddTask_AddsCardAndSelectsIt()
    {
        var backlog = _board.Columns[0];
        _service.CreateTaskAsync(1, "新規", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var task = new TaskItem { Id = 99, Title = "新規", ColumnId = 1, Position = backlog.Tasks.Count };
            backlog.Tasks.Add(task);
            return Task.FromResult(Result.Ok(task));
        });
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginAddTaskCommand.Execute(null);
        column.NewTaskTitle = "新規";

        await column.CommitAddTaskCommand.ExecuteAsync(null);

        Ids(column).Should().Equal(10, 11, 99);
        _vm.SelectedCard!.Id.Should().Be(99);
        column.IsAddingTask.Should().BeFalse();
    }

    /// <summary>裁定2: 空タイトルの却下は入力途中の編集も既存の列 VM も壊さない。</summary>
    [Fact]
    public async Task CommitAddTask_WhenServiceRejects_KeepsEditorAndColumnInstances()
    {
        _service.CreateTaskAsync(1, "新規", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail<TaskItem>(Messages.TitleRequired)));
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginAddTaskCommand.Execute(null);
        column.NewTaskTitle = "新規";

        await column.CommitAddTaskCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.TitleRequired);
        column.IsAddingTask.Should().BeTrue();
        column.NewTaskTitle.Should().Be("新規");
        _vm.Columns[0].Should().BeSameAs(column, "検証の却下では列 VM を作り直さない");
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteColumn_WhenServiceFails_ShowsReasonAndKeepsColumn()
    {
        _service.DeleteColumnAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail(Messages.ColumnHasTasks)));
        await _vm.LoadAsync();

        await _vm.Columns[0].DeleteColumnCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.ColumnHasTasks);
        _vm.Columns.Should().HaveCount(3);
    }

    /// <summary>列の並び替えも楽観的に動かすので、却下されたらモデルの順序に戻す。</summary>
    [Fact]
    public async Task ReorderColumns_WhenRejected_RestoresModelOrder()
    {
        _service.ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(Messages.ReorderMustIncludeAllColumns)));
        await _vm.LoadAsync();

        var ok = await _vm.ReorderColumnsAsync(new[] { _vm.Columns[1], _vm.Columns[0], _vm.Columns[2] });

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be(Messages.ReorderMustIncludeAllColumns);
        _vm.Columns.Select(c => c.Name).Should().Equal("未着手", "進行中", "完了");
    }

    [Fact]
    public async Task ReorderColumns_WhenServiceSucceeds_KeepsNewOrder()
    {
        _service.ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        await _vm.LoadAsync();

        var ok = await _vm.ReorderColumnsAsync(new[] { _vm.Columns[1], _vm.Columns[0], _vm.Columns[2] });

        ok.Should().BeTrue();
        _vm.Columns.Select(c => c.Name).Should().Equal("進行中", "未着手", "完了");
        await _service.Received(1).ReorderColumnsAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 2, 1, 3 })), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Cards は ListBox の ItemsSource なので、Clear の Reset で Selector が選択を解除し null を
    /// 書き戻す。バインドの無いテストではその書き戻しを模倣して、絞り込み後も残るカードの選択が
    /// 戻ることを確かめる。
    /// </summary>
    [Fact]
    public async Task Filter_KeepsSelection_WhenSelectedCardStaysVisible()
    {
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.Cards.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) column.SelectedCard = null;
        };
        var card = column.Cards[0];
        _vm.SelectCard(card);

        _vm.Filter.SearchText = "請求";   // card は絞り込み後も残る

        Ids(column).Should().Equal(10);
        column.SelectedCard.Should().BeSameAs(card);
    }

    /// <summary>裁定R1: GetBoardAsync も読み取り段の生の例外を素通しするので、呼び出し側で包む。</summary>
    [Fact]
    public async Task Load_WhenBoardQueryThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.GetBoardAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<Board>>(new InvalidOperationException("no such table: Boards")));

        await _vm.LoadAsync();

        _vm.BannerMessage.Should().Be(SaveFailure("no such table: Boards"));
        _vm.IsLoaded.Should().BeFalse();
        _vm.Columns.Should().BeEmpty();
    }

    /// <summary>裁定R2: 取り消しは保存の失敗ではない。バナーにも出さず、リロードも誘発しない。</summary>
    [Fact]
    public async Task Load_WhenQueryIsCancelled_IsNotTreatedAsSaveFailure()
    {
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Project>>(new OperationCanceledException()));

        Func<Task> load = () => _vm.LoadAsync();

        await load.Should().ThrowAsync<OperationCanceledException>();
        _vm.BannerMessage.Should().BeNull();
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>裁定1: Result を返さない照会が投げても落とさず、保存失敗と同じバナーに出す。</summary>
    [Fact]
    public async Task Load_WhenProjectsQueryThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Project>>(new PersistenceException("database is locked")));

        await _vm.LoadAsync();

        _vm.BannerMessage.Should().Be(SaveFailure("database is locked"));
        _vm.IsLoaded.Should().BeFalse();
        _vm.Columns.Should().BeEmpty();
    }

    /// <summary>裁定1: 履歴の照会も同じ。詳細パネルは空の履歴を受け取る。</summary>
    [Fact]
    public async Task GetHistory_WhenQueryThrows_ShowsBannerAndReturnsEmpty()
    {
        _service.GetHistoryAsync(10, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<HistoryEntry>>(new PersistenceException("no such table")));
        await _vm.LoadAsync();

        var history = await _vm.GetHistoryAsync(10);

        history.Should().BeEmpty();
        _vm.BannerMessage.Should().Be(SaveFailure("no such table"));
    }

    /// <summary>裁定1: ラベル作成後の再取得が投げても落とさない。</summary>
    [Fact]
    public async Task CreateLabel_WhenReloadOfLabelsThrows_ShowsBanner()
    {
        _service.CreateLabelAsync("新ラベル", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new Label { Id = 201, Name = "新ラベル" })));
        await _vm.LoadAsync();
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Label>>(new PersistenceException("io error")));

        var label = await _vm.CreateLabelAsync("新ラベル");

        label.Should().BeNull();
        _vm.BannerMessage.Should().Be(SaveFailure("io error"));
    }

    /// <summary>
    /// 裁定5: 更新系も読み取り段の生の例外を素通ししない。BoardService が Result に変えるのは
    /// SaveChanges の PersistenceException だけなので、包まないと AsyncRelayCommand の内部
    /// async void まで届いてプロセスごと落ちる。
    /// </summary>
    [Fact]
    public async Task DeleteTask_WhenServiceThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.DeleteTaskAsync(10, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("no such table: Tasks")));
        await _vm.LoadAsync();
        _vm.SelectCard(_vm.Columns[0].Cards[0]);

        await _vm.DeleteSelectedCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(SaveFailure("no such table: Tasks"));
    }

    /// <summary>裁定5: 列の更新（RunColumnChangeAsync）も同じ。</summary>
    [Fact]
    public async Task SetWipLimit_WhenServiceThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.SetWipLimitAsync(2, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("database is locked")));
        await _vm.LoadAsync();
        var column = _vm.Columns[1];
        column.BeginEditWipCommand.Execute(null);
        column.WipText = "3";

        await column.CommitWipCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(SaveFailure("database is locked"));
    }

    /// <summary>裁定5: 列の追加も同じ。</summary>
    [Fact]
    public async Task CommitAddColumn_WhenServiceThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.AddColumnAsync("確認待ち", ColumnRole.Review, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<Column>>(new InvalidOperationException("disk I/O error")));
        await _vm.LoadAsync();
        _vm.BeginAddColumnCommand.Execute(null);
        _vm.NewColumnName = "確認待ち";
        _vm.NewColumnRole = _vm.NewColumnRoles.Single(r => r.Value == ColumnRole.Review);

        await _vm.CommitAddColumnCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(SaveFailure("disk I/O error"));
        _vm.Columns.Should().HaveCount(3);
    }

    /// <summary>裁定3: 待てない入口（D&amp;D）から始めた操作でも、失敗はバナーに出て消えない。</summary>
    [Fact]
    public async Task RunGuarded_WhenOperationThrows_ShowsBanner()
    {
        await _vm.LoadAsync();

        _vm.RunGuarded(() => throw new InvalidOperationException("boom"));

        _vm.BannerMessage.Should().Be(SaveFailure("boom"));
    }

    [Fact]
    public async Task ColumnAndProjectNames_ResolveFromLoadedBoard()
    {
        await _vm.LoadAsync();

        _vm.ColumnName(2).Should().Be("進行中");
        _vm.ColumnName(999).Should().Be(Strings.UnknownColumn);
        _vm.ProjectName(100).Should().Be("顧客A対応");
        _vm.ProjectName(null).Should().BeNull();
        _vm.Today.Should().Be(new DateOnly(2026, 9, 4));
    }

    // ---- 列ヘッダーのインライン編集（裁定R3・R4） ----

    /// <summary>裁定R4: 却下されたら理由を読みながら直せるよう、入力もエディタも残す。</summary>
    [Fact]
    public async Task CommitRename_WhenRejected_KeepsEditorAndText()
    {
        _service.RenameColumnAsync(1, "   ", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(Messages.ColumnNameRequired)));
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginRenameCommand.Execute(null);
        column.RenameText = "   ";

        await column.CommitRenameCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.ColumnNameRequired);
        column.IsRenaming.Should().BeTrue();
        column.RenameText.Should().Be("   ");
        column.Name.Should().Be("未着手");
    }

    [Fact]
    public async Task CommitRename_WhenAccepted_ClosesEditor()
    {
        _service.RenameColumnAsync(1, "積み残し", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _board.Columns[0].Name = "積み残し";
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginRenameCommand.Execute(null);
        column.RenameText = "積み残し";

        await column.CommitRenameCommand.ExecuteAsync(null);

        column.IsRenaming.Should().BeFalse();
        column.Name.Should().Be("積み残し");
    }

    /// <summary>裁定R3: 打ち間違いで WIP 制限が黙って消えないこと。サービスも呼ばない。</summary>
    [Fact]
    public async Task CommitWip_NonNumericText_KeepsLimitAndEditor()
    {
        await _vm.LoadAsync();
        var column = _vm.Columns[1];   // WIP 1
        column.BeginEditWipCommand.Execute(null);
        column.WipText = "いち";

        await column.CommitWipCommand.ExecuteAsync(null);

        await _service.DidNotReceive().SetWipLimitAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        _vm.BannerMessage.Should().Be(Messages.WipLimitMustBePositive);
        column.WipLimit.Should().Be(1);
        column.CountText.Should().Be("1 / 1");
        column.IsEditingWip.Should().BeTrue();
        column.WipText.Should().Be("いち");
    }

    /// <summary>空欄は従来どおり「制限なし」の意味。</summary>
    [Fact]
    public async Task CommitWip_EmptyText_ClearsLimit()
    {
        _service.SetWipLimitAsync(2, null, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _board.Columns[1].WipLimit = null;
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var column = _vm.Columns[1];
        column.BeginEditWipCommand.Execute(null);
        column.WipText = "";

        await column.CommitWipCommand.ExecuteAsync(null);

        column.WipLimit.Should().BeNull();
        column.CountText.Should().Be("1");
        column.IsEditingWip.Should().BeFalse();
    }

    /// <summary>裁定R4: サービスに却下された WIP 値も入力に残す。</summary>
    [Fact]
    public async Task CommitWip_WhenRejected_KeepsEditorAndText()
    {
        _service.SetWipLimitAsync(2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(Messages.WipLimitMustBePositive)));
        await _vm.LoadAsync();
        var column = _vm.Columns[1];
        column.BeginEditWipCommand.Execute(null);
        column.WipText = "0";

        await column.CommitWipCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.WipLimitMustBePositive);
        column.IsEditingWip.Should().BeTrue();
        column.WipText.Should().Be("0");
        column.WipLimit.Should().Be(1);
    }

    [Fact]
    public async Task CommitWip_WhenAccepted_ClosesEditor()
    {
        _service.SetWipLimitAsync(2, 3, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _board.Columns[1].WipLimit = 3;
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var column = _vm.Columns[1];
        column.BeginEditWipCommand.Execute(null);
        column.WipText.Should().Be("1", "編集開始時は今の制限が入る");
        column.WipText = "3";

        await column.CommitWipCommand.ExecuteAsync(null);

        column.IsEditingWip.Should().BeFalse();
        column.CountText.Should().Be("1 / 3");
    }

    [Fact]
    public async Task DeleteTask_RefreshesCardAndColumnCount()
    {
        var backlog = _board.Columns[0];
        _service.DeleteTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            backlog.Tasks.Single(t => t.Id == 10).DeletedAt = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);

        await _vm.DeleteSelectedCommand.ExecuteAsync(null);

        card.IsDeleted.Should().BeTrue();
        Ids(_vm.Columns[0]).Should().Equal(11);
        _vm.Columns[0].CountText.Should().Be("1");
    }

    // ---- 列の追加（種別は追加するときに選ぶ） ----

    /// <summary>Done 列は1つだけなので、追加時の種別には出さない。</summary>
    [Fact]
    public void NewColumnRoles_OfferBacklogActiveAndReview_ButNeverDone()
    {
        _vm.NewColumnRoles.Select(r => r.Value)
            .Should().Equal(ColumnRole.Backlog, ColumnRole.Active, ColumnRole.Review);
        _vm.NewColumnRoles.Select(r => r.Name)
            .Should().Equal(Strings.RoleBacklog, Strings.RoleActive, Strings.RoleReview);
        _vm.NewColumnRole.Value.Should().Be(ColumnRole.Active, "既定は進行中");
    }

    [Fact]
    public async Task CommitAddColumn_PassesSelectedRoleAndAppendsColumn()
    {
        _service.AddColumnAsync("確認待ち", ColumnRole.Review, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(
                new Column { Id = 4, BoardId = 1, Name = "確認待ち", Order = 3, Role = ColumnRole.Review })));
        await _vm.LoadAsync();
        _vm.BeginAddColumnCommand.Execute(null);
        _vm.NewColumnName = "確認待ち";
        _vm.NewColumnRole = _vm.NewColumnRoles.Single(r => r.Value == ColumnRole.Review);

        await _vm.CommitAddColumnCommand.ExecuteAsync(null);

        _vm.IsAddingColumn.Should().BeFalse();
        _vm.Columns.Select(c => c.Name).Should().Equal("未着手", "進行中", "完了", "確認待ち");
        _vm.Columns[3].Role.Should().Be(ColumnRole.Review);
    }

    /// <summary>選び直したあとでも、次に開いたときは名前が空・種別が既定に戻る。</summary>
    [Fact]
    public void BeginAddColumn_ResetsNameAndRole()
    {
        _vm.NewColumnName = "書きかけ";
        _vm.NewColumnRole = _vm.NewColumnRoles.Single(r => r.Value == ColumnRole.Backlog);

        _vm.BeginAddColumnCommand.Execute(null);

        _vm.IsAddingColumn.Should().BeTrue();
        _vm.NewColumnName.Should().BeEmpty();
        _vm.NewColumnRole.Value.Should().Be(ColumnRole.Active);
    }
}
