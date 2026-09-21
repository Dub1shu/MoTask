using System.Collections.Specialized;
using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class BoardViewModelTests
{
    private readonly FakeBoardService _service = new();
    private readonly FakeBoardChangeSource _externalChanges = new();
    private readonly Label _urgent = TestBoards.Urgent();
    private readonly Board _board;
    private readonly BoardViewModel _vm;

    public BoardViewModelTests()
    {
        _board = TestBoards.Sample(_urgent);
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { _urgent };
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), _externalChanges);
    }

    private static int[] Ids(ColumnViewModel c) => c.Cards.Select(x => x.Id).ToArray();

    /// <summary>BoardService が永続化の失敗を伝えるときの文言。検証による却下と区別される。</summary>
    private static string SaveFailure(string detail) => $"{Messages.SaveFailed}: {detail}";

    [Fact]
    public async Task ExternalBoardChange_ReloadsTheBoard()
    {
        await _vm.LoadAsync();
        _service.GetBoardCalls.Should().Be(1);

        _externalChanges.RaiseBoardChanged();

        _service.GetBoardCalls.Should().Be(2);
    }

    /// <summary>
    /// BoardChanged は短時間に連続発火しうる（MCP から add_task を続けて呼ぶなど）。ガードを入れる前は、
    /// 先に始まった読み込みが後から終わると、後から始まって先に終わった読み込みの結果を古いデータで
    /// 上書きしていた。世代番号（_reloadGeneration）で古い方が実際に降りることを固定する。
    /// </summary>
    [Fact]
    public async Task OverlappingExternalReloads_DoNotOverwriteNewerDataWithStaleData()
    {
        await _vm.LoadAsync();
        _service.GetBoardCalls.Should().Be(1);

        var staleBoard = _board;
        var freshBoard = TestBoards.Sample(_urgent);
        freshBoard.Columns[0].Name = "更新後";

        var staleGate = new TaskCompletionSource();
        var callCount = 0;
        _service.OnGetBoard = () =>
        {
            callCount++;
            // 1 回目（先に始まる読み込み）はゲートで止め、2 回目（後から始まって先に終わる読み込み）は即座に返す。
            return callCount == 1 ? WaitThenReturnAsync(staleGate.Task, staleBoard) : Task.FromResult(Result.Ok(freshBoard));
        };

        // 1 回目: GetBoardAsync がまだ戻っていない（先に始まった読み込み）。
        _externalChanges.RaiseBoardChanged();
        // 2 回目: GetBoardAsync が同期的に戻る（後から始まって先に終わる読み込み）。
        _externalChanges.RaiseBoardChanged();

        _vm.Columns[0].Name.Should().Be("更新後", "後から始まった読み込みが先に終わっている");

        staleGate.SetResult();

        // 継続は既定では SetResult を呼んだこのスレッド上で同期的に走るはずだが、
        // 念のため短時間だけ安定を待ってから確認する（成功時は待たずに抜ける）。
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_vm.Columns[0].Name != "更新後" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        _vm.Columns[0].Name.Should().Be("更新後", "遅れて戻った古い読み込みは新しい表示を上書きしない");
    }

    private static async Task<Result<Board>> WaitThenReturnAsync(Task gate, Board board)
    {
        await gate;
        return Result.Ok(board);
    }

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
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromResult(Result.Fail("だめ"))
            : Task.FromResult(Result.Ok());
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be("だめ");
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        Ids(_vm.Columns[1]).Should().Equal(12);
        _service.GetBoardCalls.Should().Be(1);

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
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromResult(Result.Fail("だめ"))
            : Task.FromResult(Result.Ok());
        _service.OnDeleteTask = taskId =>
        {
            if (taskId == 10)
            {
                backlog.Tasks.Single(t => t.Id == 10).DeletedAt = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromResult(Result.Fail(SaveFailure("ディスクがいっぱいです")))
            : Task.FromResult(Result.Ok());
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be(SaveFailure("ディスクがいっぱいです"));
        Ids(_vm.Columns[0]).Should().Equal(10, 11);
        Ids(_vm.Columns[1]).Should().Equal(12);
        _service.GetBoardCalls.Should().Be(2);
    }

    [Fact]
    public async Task MoveCard_WhenServiceSucceeds_ReflectsModel()
    {
        var backlog = _board.Columns[0];
        var active = _board.Columns[1];
        _service.OnMoveTask = call =>
        {
            if (call is { TaskId: 10, ToColumnId: 2, Position: 0 })
            {
                TestBoards.Move(backlog, active, taskId: 10, position: 0);
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.GetBoardCalls.Should().Be(1);
    }

    // ---------- ワンタッチ完了（カードの ✓ と詳細パネルの「完了にする」の実体） ----------

    /// <summary>完了は「Role が Done の列の末尾へ移す」。既に入っているカードの後ろに付く。</summary>
    [Fact]
    public async Task CompleteCard_MovesTheCardToTheEndOfTheDoneColumn()
    {
        var backlog = _board.Columns[0];
        var done = SeedDoneCard();
        _service.OnMoveTask = call =>
        {
            if (call is { TaskId: 10, ToColumnId: 3 }) TestBoards.Move(backlog, done, taskId: 10, position: call.Position);
            return Task.FromResult(Result.Ok());
        };
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];

        var ok = await _vm.CompleteCardAsync(card);

        ok.Should().BeTrue();
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 3, 1));
        Ids(_vm.Columns[0]).Should().Equal(11);
        Ids(_vm.Columns[2]).Should().Equal(20, 10);
    }

    /// <summary>完了列のカードで押されても同じ列へ動かし直さない（✓ は出さないが、念のため）。</summary>
    [Fact]
    public async Task CompleteCard_DoesNothing_WhenTheCardIsAlreadyInTheDoneColumn()
    {
        SeedDoneCard();
        await _vm.LoadAsync();
        var card = _vm.Columns[2].Cards.Single();

        var ok = await _vm.CompleteCardAsync(card);

        ok.Should().BeFalse();
        _service.MoveTaskCalls.Should().BeEmpty();
    }

    /// <summary>削除済みのカードは完了させない（Delete と同じく、消えたものは動かさない）。</summary>
    [Fact]
    public async Task CompleteCard_DoesNothing_WhenTheCardIsDeleted()
    {
        TestBoards.SeedDeletedInTheMiddle(_board.Columns[0]);
        await _vm.LoadAsync();
        var card = _vm.Columns[0].AllCards.Single(c => c.Id == 11);
        card.IsDeleted.Should().BeTrue();

        var ok = await _vm.CompleteCardAsync(card);

        ok.Should().BeFalse();
        _service.MoveTaskCalls.Should().BeEmpty();
    }

    /// <summary>カードの ✓ が押す口。XAML からはコマンド経由でしか呼べないので、そこも固定する。</summary>
    [Fact]
    public async Task CompleteCommand_CompletesTheGivenCard()
    {
        var backlog = _board.Columns[0];
        var done = _board.Columns[2];
        _service.OnMoveTask = call =>
        {
            if (call is { TaskId: 10, ToColumnId: 3 }) TestBoards.Move(backlog, done, taskId: 10, position: call.Position);
            return Task.FromResult(Result.Ok());
        };
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];

        await _vm.CompleteCommand.ExecuteAsync(card);

        Ids(_vm.Columns[2]).Should().Equal(10);
    }

    /// <summary><see cref="TestBoards.Sample"/> の完了列(3)に既に終わったカード 20 を 1 枚置く。</summary>
    private Column SeedDoneCard()
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var done = _board.Columns[2];
        done.Tasks.Add(new TaskItem { Id = 20, Title = "先週片づけたもの", ColumnId = 3, Position = 0, CreatedAt = t, UpdatedAt = t });
        return done;
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
        _service.OnRestoreTask = taskId =>
        {
            if (taskId == 11)
            {
                TestBoards.Restore(backlog, 11);
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.OnDeleteTask = taskId =>
        {
            if (taskId == 13)
            {
                TestBoards.SoftDelete(backlog, 13, new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc));
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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

        _service.CreateTaskCalls.Should().BeEmpty();
        column.IsAddingTask.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAddTask_AddsCardAndSelectsIt()
    {
        var backlog = _board.Columns[0];
        _service.OnCreateTask = call =>
        {
            if (call is { ColumnId: 1, Title: "新規" })
            {
                var task = new TaskItem { Id = 99, Title = "新規", ColumnId = 1, Position = backlog.Tasks.Count };
                backlog.Tasks.Add(task);
                return Task.FromResult(Result.Ok(task));
            }
            return Task.FromResult(Result.Ok(new TaskItem { Title = call.Title }));
        };
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
        _service.OnCreateTask = call => call is { ColumnId: 1, Title: "新規" }
            ? Task.FromResult(Result.Fail<TaskItem>(Messages.TitleRequired))
            : Task.FromResult(Result.Ok(new TaskItem { Title = call.Title }));
        await _vm.LoadAsync();
        var column = _vm.Columns[0];
        column.BeginAddTaskCommand.Execute(null);
        column.NewTaskTitle = "新規";

        await column.CommitAddTaskCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.TitleRequired);
        column.IsAddingTask.Should().BeTrue();
        column.NewTaskTitle.Should().Be("新規");
        _vm.Columns[0].Should().BeSameAs(column, "検証の却下では列 VM を作り直さない");
        _service.GetBoardCalls.Should().Be(1);
    }

    [Fact]
    public async Task DeleteColumn_WhenServiceFails_ShowsReasonAndKeepsColumn()
    {
        _service.OnDeleteColumn = columnId => columnId == 1
            ? Task.FromResult(Result.Fail(Messages.ColumnHasTasks))
            : Task.FromResult(Result.Ok());
        await _vm.LoadAsync();

        await _vm.Columns[0].DeleteColumnCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.ColumnHasTasks);
        _vm.Columns.Should().HaveCount(3);
    }

    /// <summary>列の並び替えも楽観的に動かすので、却下されたらモデルの順序に戻す。</summary>
    [Fact]
    public async Task ReorderColumns_WhenRejected_RestoresModelOrder()
    {
        _service.OnReorderColumns = _ => Task.FromResult(Result.Fail(Messages.ReorderMustIncludeAllColumns));
        await _vm.LoadAsync();

        var ok = await _vm.ReorderColumnsAsync(new[] { _vm.Columns[1], _vm.Columns[0], _vm.Columns[2] });

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be(Messages.ReorderMustIncludeAllColumns);
        _vm.Columns.Select(c => c.Name).Should().Equal("未着手", "進行中", "完了");
    }

    [Fact]
    public async Task ReorderColumns_WhenServiceSucceeds_KeepsNewOrder()
    {
        await _vm.LoadAsync();

        var ok = await _vm.ReorderColumnsAsync(new[] { _vm.Columns[1], _vm.Columns[0], _vm.Columns[2] });

        ok.Should().BeTrue();
        _vm.Columns.Select(c => c.Name).Should().Equal("進行中", "未着手", "完了");
        _service.ReorderColumnsCalls.Should().ContainSingle()
            .Which.OrderedColumnIds.Should().Equal(2, 1, 3);
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
        _service.OnGetBoard = () => Task.FromException<Result<Board>>(new InvalidOperationException("no such table: Boards"));

        await _vm.LoadAsync();

        _vm.BannerMessage.Should().Be(SaveFailure("no such table: Boards"));
        _vm.IsLoaded.Should().BeFalse();
        _vm.Columns.Should().BeEmpty();
    }

    /// <summary>裁定R2: 取り消しは保存の失敗ではない。バナーにも出さず、リロードも誘発しない。</summary>
    [Fact]
    public async Task Load_WhenQueryIsCancelled_IsNotTreatedAsSaveFailure()
    {
        _service.OnGetProjects = () => Task.FromException<IReadOnlyList<Project>>(new OperationCanceledException());

        Func<Task> load = () => _vm.LoadAsync();

        await load.Should().ThrowAsync<OperationCanceledException>();
        _vm.BannerMessage.Should().BeNull();
        _service.GetBoardCalls.Should().Be(1);
    }

    /// <summary>裁定1: Result を返さない照会が投げても落とさず、保存失敗と同じバナーに出す。</summary>
    [Fact]
    public async Task Load_WhenProjectsQueryThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.OnGetProjects = () => Task.FromException<IReadOnlyList<Project>>(new PersistenceException("database is locked"));

        await _vm.LoadAsync();

        _vm.BannerMessage.Should().Be(SaveFailure("database is locked"));
        _vm.IsLoaded.Should().BeFalse();
        _vm.Columns.Should().BeEmpty();
    }

    /// <summary>裁定1: 履歴の照会も同じ。詳細パネルは空の履歴を受け取る。</summary>
    [Fact]
    public async Task GetHistory_WhenQueryThrows_ShowsBannerAndReturnsEmpty()
    {
        _service.OnGetHistory = call => call.TaskId == 10
            ? Task.FromException<IReadOnlyList<HistoryEntry>>(new PersistenceException("no such table"))
            : Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>());
        await _vm.LoadAsync();

        var history = await _vm.GetHistoryAsync(10);

        history.Should().BeEmpty();
        _vm.BannerMessage.Should().Be(SaveFailure("no such table"));
    }

    /// <summary>裁定1: ラベル作成後の再取得が投げても落とさない。</summary>
    [Fact]
    public async Task CreateLabel_WhenReloadOfLabelsThrows_ShowsBanner()
    {
        _service.OnCreateLabel = call => call.Name == "新ラベル"
            ? Task.FromResult(Result.Ok(new Label { Id = 201, Name = "新ラベル" }))
            : Task.FromResult(Result.Ok(new Label { Name = call.Name }));
        await _vm.LoadAsync();
        _service.OnGetLabels = () => Task.FromException<IReadOnlyList<Label>>(new PersistenceException("io error"));

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
        _service.OnDeleteTask = taskId => taskId == 10
            ? Task.FromException<Result>(new InvalidOperationException("no such table: Tasks"))
            : Task.FromResult(Result.Ok());
        await _vm.LoadAsync();
        _vm.SelectCard(_vm.Columns[0].Cards[0]);

        await _vm.DeleteSelectedCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(SaveFailure("no such table: Tasks"));
    }

    /// <summary>裁定5: 列の更新（RunColumnChangeAsync）も同じ。</summary>
    [Fact]
    public async Task SetWipLimit_WhenServiceThrows_ShowsBannerInsteadOfCrashing()
    {
        _service.OnSetWipLimit = call => call is { ColumnId: 2, WipLimit: 3 }
            ? Task.FromException<Result>(new InvalidOperationException("database is locked"))
            : Task.FromResult(Result.Ok());
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
        _service.OnAddColumn = call => call is { Name: "確認待ち", Role: ColumnRole.Review }
            ? Task.FromException<Result<Column>>(new InvalidOperationException("disk I/O error"))
            : Task.FromResult(Result.Ok(new Column { Name = call.Name, Role = call.Role }));
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
        _service.OnRenameColumn = call => call is { ColumnId: 1, Name: "   " }
            ? Task.FromResult(Result.Fail(Messages.ColumnNameRequired))
            : Task.FromResult(Result.Ok());
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
        _service.OnRenameColumn = call =>
        {
            if (call is { ColumnId: 1, Name: "積み残し" })
            {
                _board.Columns[0].Name = "積み残し";
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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

        _service.SetWipLimitCalls.Should().BeEmpty();
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
        _service.OnSetWipLimit = call =>
        {
            if (call is { ColumnId: 2, WipLimit: null })
            {
                _board.Columns[1].WipLimit = null;
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.OnSetWipLimit = call => call is { ColumnId: 2, WipLimit: 0 }
            ? Task.FromResult(Result.Fail(Messages.WipLimitMustBePositive))
            : Task.FromResult(Result.Ok());
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
        _service.OnSetWipLimit = call =>
        {
            if (call is { ColumnId: 2, WipLimit: 3 })
            {
                _board.Columns[1].WipLimit = 3;
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.OnDeleteTask = taskId =>
        {
            if (taskId == 10)
            {
                backlog.Tasks.Single(t => t.Id == 10).DeletedAt = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);
                return Task.FromResult(Result.Ok());
            }
            return Task.FromResult(Result.Ok());
        };
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
        _service.OnAddColumn = call => call is { Name: "確認待ち", Role: ColumnRole.Review }
            ? Task.FromResult(Result.Ok(
                new Column { Id = 4, BoardId = 1, Name = "確認待ち", Order = 3, Role = ColumnRole.Review }))
            : Task.FromResult(Result.Ok(new Column { Name = call.Name, Role = call.Role }));
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

    [Fact]
    public async Task SelectTask_SelectsTheCardById_AndOpensTheDetail()
    {
        await _vm.LoadAsync();

        _vm.SelectTask(12);

        _vm.SelectedCard!.Id.Should().Be(12);
        _vm.Detail.Should().NotBeNull("朝のプランの『ボードで開く』は詳細パネルまで開く");
    }

    [Fact]
    public async Task SelectTask_DoesNothing_ForAnUnknownId()
    {
        await _vm.LoadAsync();
        _vm.SelectTask(10);

        _vm.SelectTask(999);

        _vm.SelectedCard!.Id.Should().Be(10, "無ければ選択を変えない（仕様 §8）");
    }
}
