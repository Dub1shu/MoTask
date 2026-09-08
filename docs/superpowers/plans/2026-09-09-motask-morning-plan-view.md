# 朝の実行プラン（2/2）プランの確定と画面 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 保存済みの `MorningRun.PlanJson` を画面の行に解決し、ワイヤー 4a / 4b のとおり「左＝仕分け ⇄ 最初にやる1件、右＝候補キュー＋4区分のプラン」の画面に仕上げる。あわせてタブの候補バッジ、キーボード、一括操作、統合先の選択を足す。

**Architecture:** 契約（`candidates.jsonl` / `plan.json` / `instruction.md`）とテーブルは変えない。新しいのは Core の純関数 `MorningPlanResolver`（`taskId` / `externalId` を実タスクと候補に解決）と、`MorningService` の一括操作2つ、App 側の VM 分割（`TriagePanelViewModel` / `FirstThingViewModel` / `PlanSectionViewModel`）である。左パネルは `ContentControl` の `DataTemplate` 切替。

**Tech Stack:** .NET 10 / WPF / SQLite / EF Core 10.0.11 / CommunityToolkit.Mvvm 8.4.2 / xunit 2.9.3 + FluentAssertions 7.2.2 + NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md`（親仕様: `docs/superpowers/specs/2026-09-07-motask-morning-plan-triage-design.md`）

**作業場所:** ワークツリー `.claude/worktrees/morning-plan-view`、ブランチ `worktree-morning-plan-view`。すべてのコマンドはこのディレクトリで実行する。

---

## Global Constraints

このプランのすべてのタスクに、暗黙にこの節の要求が含まれる。

- **契約を変えない。** `MorningResultReader` / `MorningInstruction` / `BoardSnapshot` / `Messages.MorningInstructionContractFormat` には触らない
- **`AiJob` / `AiJobService` / `AiJobStatus` / `AiJobs` テーブルは一切変更しない。** `Tasks` / `TriageCandidates` / `MorningRuns` にも列を足さない。マイグレーションは作らない
- **`MorningService` の一括操作は既存の4アクションを内側で順に呼ぶだけ。** 一括操作自身は `OperationGate` を取らない（ゲートの中から `IBoardService` を呼ぶとデッドロックする）
- **Core は UI 非依存・EF 非依存**を維持する。`MoTask.Core.csproj` に参照を足さない
- 利用者向け文言はハードコードしない。Core は `src/MoTask.Core/Resources/Messages.resx`（アクセサ `Messages.cs`）、App は `src/MoTask.App/Resources/Strings.resx`（アクセサ `Strings.cs`）。**resx に値を足したら同じ名前のプロパティを .cs に足す**（`StringsTests.AllProperties_ResolveToNonEmptyValuesDistinctFromTheirNames` が守っている）
- パッケージのバージョンは `Directory.Packages.props` にあるので `PackageReference` に `Version` を書かない
- テストは xunit + FluentAssertions。アサーションは `Should()` 形式。ビルドは `-p:TreatWarningsAsErrors=true` で警告ゼロ
- **git の扱い:** `git rebase` / `git reset --hard` / 素の `git stash` は使わない。loose object の書き込みが `Permission denied` で落ちたら（ウイルス対策ソフトのロック）**同じコマンドをそのまま再実行する**
- 各タスクの最後に1コミット。コミットメッセージは日本語または英語の慣用形で、末尾に必ず次の行を付ける:

  ```
  Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
  ```

### 仕様からの意図的な逸脱（2件）

1. **`TriageSummary` は `Later` と `Pending` を分けて数える。** 仕様 §4 は「`Pending` と `Later` は『未処理』にまとめる」と言うが、同じ仕様の §7 が「登録2・統合1・却下1・あとで1」を出す。今日の `Later` はキューに乗らない（キューは今日の `Pending` ＋ 過去の `Later`）ので、「あとで」は決着済みとして数えるのが正しい。Task 1 で仕様 §4 の該当行も直す
2. **`CandidateItemViewModel.CanMerge` は `IsMergeSuggested` に改名する。** 仕様 §6 は「バッジの判定に用途を変える」と言うだけだが、`TriagePanelViewModel.CanMerge`（統合先が選ばれているか）と同名のままだと読み違える

---

## File Structure

### 新規（Core）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.Core/Morning/ResolvedPlan.cs` | 解決結果の型: `PlanGroupKey` / `TaskRowOrigin` / `PlanRow`（`TaskRow` / `CandidateRow`）/ `PlanGroup` / `TriageSummary` / `ResolvedPlan` |
| `src/MoTask.Core/Morning/MorningPlanResolver.cs` | `PlanJson` ＋ 候補 ＋ 盤面 → `ResolvedPlan`。純関数 |
| `src/MoTask.Core/Services/BulkOutcome.cs` | 一括操作の結果（適用件数と見送り理由） |

### 新規（App）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.App/ViewModels/TriagePanelViewModel.cs` | 左・状態1。編集フォーム、統合先の選択、4アクション |
| `src/MoTask.App/ViewModels/FirstThingViewModel.cs` | 左・状態2。最初にやる1件と「ボードで開く」 |
| `src/MoTask.App/ViewModels/PlanSectionViewModel.cs` | 右の区分1つ（`PlanRowViewModel` も同じファイル） |
| `src/MoTask.App/MorningKeyMap.cs` | キー → 仕分け操作の対応（純関数） |

### 変更

| ファイル | 変更 |
| --- | --- |
| `src/MoTask.Core/Abstractions/IMorningRepository.cs` | `GetCandidatesOfRunAsync` |
| `src/MoTask.Core/Services/IMorningService.cs` / `MorningService.cs` | `GetCandidatesOfRunAsync` / `ApplySuggestionsAsync` / `PostponeAllAsync` |
| `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs` | 見送り理由の文言 |
| `src/MoTask.Data/Repositories/MorningRepository.cs` | `GetCandidatesOfRunAsync` |
| `src/MoTask.App/ViewModels/MorningPlanViewModel.cs` | 画面の VM。フォームを `TriagePanelViewModel` へ移し、プランの解決・`LeftPanel`・一括操作・`PendingCount`・`NavigateToTask` を足す |
| `src/MoTask.App/ViewModels/CandidateItemViewModel.cs` | `IsMergeSuggested` / 推奨バッジ文言 |
| `src/MoTask.App/ViewModels/BoardViewModel.cs` | `SelectTask(int)` |
| `src/MoTask.App/Views/MorningPlanView.xaml` | 左 `ContentControl` ＋ 右カラム |
| `src/MoTask.App/Views/MainWindow.xaml` / `.xaml.cs` | タブのバッジ、起動時の読み込み、`NavigateToTask`、キー |
| `src/MoTask.App/Resources/Strings.resx` / `Strings.cs` | 画面の文言 |
| `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs` | `GetCandidatesOfRunAsync` |
| `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` | 偽サービスの拡張と VM 分割への追従 |

### テスト（新規）

`tests/MoTask.Core.Tests/MorningPlanResolverTests.cs`、`tests/MoTask.Core.Tests/MorningServiceBulkTests.cs`、
`tests/MoTask.App.Tests/TriagePanelViewModelTests.cs`、`tests/MoTask.App.Tests/MorningKeyMapTests.cs`

---
### Task 1: MorningPlanResolver（PlanJson を画面の行に解決する）

仕様 §4。`result/` の形を知るのは `MorningResultReader` だけだが、**保存済み `PlanJson` の中身を読むのはここだけ**にする。

**Files:**
- Create: `src/MoTask.Core/Morning/ResolvedPlan.cs`
- Create: `src/MoTask.Core/Morning/MorningPlanResolver.cs`
- Modify: `docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md`（§4 の `Summary` の1行）
- Test: `tests/MoTask.Core.Tests/MorningPlanResolverTests.cs`

**Interfaces:**
- Consumes: `MorningRun.PlanJson`（string）、`TriageCandidate`、`Board` / `Column` / `TaskItem` / `ColumnRole`
- Produces:
  - `MorningPlanResolver.Resolve(string planJson, IReadOnlyList<TriageCandidate> candidates, Board? board, Func<int?, string?> projectName) : ResolvedPlan`
  - `ResolvedPlan(PlanRow? FirstThing, string FirstThingReason, bool FirstThingIsFallback, IReadOnlyList<PlanGroup> Groups, TriageSummary Summary)` と `ResolvedPlan.Empty(TriageSummary)`
  - `PlanGroup(PlanGroupKey Key, IReadOnlyList<PlanRow> Rows)` の `TaskCount` / `CandidateCount`
  - `TaskRow(int TaskId, string Title, string? ProjectName, DateOnly? DueDate, string ColumnName, bool IsDone, TaskRowOrigin Origin)`
  - `CandidateRow(int CandidateId, string Title, string Source, DateOnly? SuggestedDueDate, string SuggestedProject)`
  - `TriageSummary(int Total, int Registered, int Merged, int Rejected, int Later, int Pending)`、`TriageSummary.None`、`TriageSummary.Of(IEnumerable<TriageCandidate>)`
  - `enum PlanGroupKey { Today, IfTime, AiReady, Waiting }`、`enum TaskRowOrigin { None, RegisteredThisMorning, MergedThisMorning }`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningPlanResolverTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>仕様 §4 の解決ルール。1 ルール 1 テスト。</summary>
public class MorningPlanResolverTests
{
    private readonly Board _board = new() { Id = 1, Name = "テスト" };
    private readonly Column _active = new() { Id = 2, Name = "今日中", Order = 1, Role = ColumnRole.Active };
    private readonly Column _done = new() { Id = 3, Name = "完了", Order = 2, Role = ColumnRole.Done };
    private readonly List<TriageCandidate> _candidates = new();

    public MorningPlanResolverTests()
    {
        _board.Columns.Add(_active);
        _board.Columns.Add(_done);
        _active.Tasks.Add(new TaskItem { Id = 45, Title = "Q4企画書の内容を確定する", ColumnId = 2, ProjectId = 100, DueDate = new DateOnly(2026, 9, 8) });
        _active.Tasks.Add(new TaskItem { Id = 52, Title = "会場候補を3つに絞る", ColumnId = 2 });
        _active.Tasks.Add(new TaskItem { Id = 61, Title = "削除済み", ColumnId = 2, DeletedAt = new DateTime(2026, 9, 1) });
        _done.Tasks.Add(new TaskItem { Id = 70, Title = "請求書を承認", ColumnId = 3 });
    }

    private static string? ProjectName(int? id) => id == 100 ? "プロジェクトQ4" : null;

    private TriageCandidate Candidate(string externalId, TriageStatus status, int? resultTaskId = null)
    {
        var candidate = new TriageCandidate
        {
            Id = _candidates.Count + 1, MorningRunId = 1, ExternalId = externalId, Source = "Outlook",
            Title = "請求先情報を更新する", Evidence = "「9月8日までに」", Status = status, ResultTaskId = resultTaskId,
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
        };
        _candidates.Add(candidate);
        return candidate;
    }

    private ResolvedPlan Resolve(string planJson) => MorningPlanResolver.Resolve(planJson, _candidates, _board, ProjectName);

    private static string Plan(string todayItems, string firstThing = "null", string others = "")
        => $"{{\"date\":\"2026-09-07\",\"firstThing\":{firstThing},\"groups\":[{{\"key\":\"today\",\"items\":[{todayItems}]}}{others}]}}";

    [Fact]
    public void ATaskOnTheBoard_BecomesATaskRow()
    {
        var plan = Resolve(Plan("{\"taskId\":45}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<TaskRow>().Subject;
        row.TaskId.Should().Be(45);
        row.Title.Should().Be("Q4企画書の内容を確定する");
        row.ProjectName.Should().Be("プロジェクトQ4");
        row.DueDate.Should().Be(new DateOnly(2026, 9, 8));
        row.ColumnName.Should().Be("今日中");
        row.IsDone.Should().BeFalse();
        row.Origin.Should().Be(TaskRowOrigin.None);
    }

    [Fact]
    public void ATaskInTheDoneColumn_StaysWithADoneMark()
    {
        var plan = Resolve(Plan("{\"taskId\":70}"));

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<TaskRow>().Which.IsDone.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"taskId\":61}")]   // 論理削除済み
    [InlineData("{\"taskId\":999}")]  // 盤面に無い
    [InlineData("{}")]                 // どちらも持たない
    [InlineData("\"not an object\"")]
    public void AnUnresolvableTaskItem_IsDropped(string item)
    {
        var plan = Resolve(Plan(item));

        plan.Groups[0].Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TriageStatus.Pending)]
    [InlineData(TriageStatus.Later)]
    public void AnUndecidedCandidate_BecomesACandidateRow(TriageStatus status)
    {
        Candidate("outlook:001", status);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<CandidateRow>().Subject;
        row.CandidateId.Should().Be(1);
        row.Title.Should().Be("請求先情報を更新する");
        row.Source.Should().Be("Outlook");
        row.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        row.SuggestedProject.Should().Be("顧客A");
    }

    [Theory]
    [InlineData(TriageStatus.Registered, TaskRowOrigin.RegisteredThisMorning)]
    [InlineData(TriageStatus.Merged, TaskRowOrigin.MergedThisMorning)]
    public void ADecidedCandidate_ResolvesToItsResultTask(TriageStatus status, TaskRowOrigin origin)
    {
        Candidate("outlook:001", status, resultTaskId: 52);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}"));

        var row = plan.Groups[0].Rows.Should().ContainSingle().Subject.Should().BeOfType<TaskRow>().Subject;
        row.TaskId.Should().Be(52);
        row.Origin.Should().Be(origin);
    }

    [Fact]
    public void ARegisteredCandidateWhoseTaskIsGone_IsDropped()
    {
        Candidate("outlook:001", TriageStatus.Registered, resultTaskId: 61);

        Resolve(Plan("{\"externalId\":\"outlook:001\"}")).Groups[0].Rows.Should().BeEmpty();
    }

    [Fact]
    public void ARejectedOrUnknownCandidate_IsDropped()
    {
        Candidate("outlook:001", TriageStatus.Rejected);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"},{\"externalId\":\"outlook:never-ingested\"}"));

        plan.Groups[0].Rows.Should().BeEmpty();
    }

    [Fact]
    public void AnItemWithBothIds_PrefersTheTaskId()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"taskId\":45,\"externalId\":\"outlook:001\"}"));

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(45);
    }

    [Fact]
    public void TheSameTask_AppearsOnlyInTheFirstGroupThatNamesIt()
    {
        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":45}",
            others: ",{\"key\":\"ifTime\",\"items\":[{\"taskId\":45},{\"taskId\":52}]}"));

        plan.Groups[0].Rows.Select(r => ((TaskRow)r).TaskId).Should().Equal(45);
        plan.Groups[1].Rows.Select(r => ((TaskRow)r).TaskId).Should().Equal(52);
    }

    [Fact]
    public void TheSameCandidate_AppearsOnlyOnce()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"}",
            others: ",{\"key\":\"waiting\",\"items\":[{\"externalId\":\"outlook:001\"}]}"));

        plan.Groups[0].Rows.Should().ContainSingle();
        plan.Groups[3].Rows.Should().BeEmpty();
    }

    [Fact]
    public void FirstThing_ResolvesAndStillAppearsInItsGroup()
    {
        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":52}",
            firstThing: "{\"taskId\":45,\"reason\":\"送付前に部長の確認が必要\"}"));

        plan.FirstThing.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(45);
        plan.FirstThingReason.Should().Be("送付前に部長の確認が必要");
        plan.FirstThingIsFallback.Should().BeFalse();
        plan.Groups[0].Rows.Should().HaveCount(2, "最初にやる1件は重複排除に参加しない");
    }

    [Fact]
    public void FirstThing_FallsBackToTheFirstTodayRow_WhenItCannotBeResolved()
    {
        Candidate("outlook:001", TriageStatus.Rejected);

        var plan = Resolve(Plan("{\"externalId\":\"outlook:001\"},{\"taskId\":52}",
            firstThing: "{\"externalId\":\"outlook:001\",\"reason\":\"却下された\"}"));

        plan.FirstThing.Should().BeOfType<TaskRow>().Which.TaskId.Should().Be(52);
        plan.FirstThingIsFallback.Should().BeTrue();
        plan.FirstThingReason.Should().BeEmpty("繰り下げた行に元の理由は当てはまらない");
    }

    [Fact]
    public void FirstThing_IsNull_WhenTodayIsEmptyToo()
    {
        var plan = Resolve(Plan("", firstThing: "{\"taskId\":999,\"reason\":\"消えた\"}"));

        plan.FirstThing.Should().BeNull();
        plan.FirstThingIsFallback.Should().BeTrue();
    }

    [Fact]
    public void MissingGroups_AreFilledInAsEmpty_InTheFixedOrder()
    {
        var plan = Resolve(Plan("{\"taskId\":45}", others: ",{\"key\":\"waiting\",\"items\":[{\"taskId\":52}]}"));

        plan.Groups.Select(g => g.Key).Should().Equal(
            PlanGroupKey.Today, PlanGroupKey.IfTime, PlanGroupKey.AiReady, PlanGroupKey.Waiting);
        plan.Groups[1].Rows.Should().BeEmpty();
        plan.Groups[2].Rows.Should().BeEmpty();
        plan.Groups[3].Rows.Should().ContainSingle();
    }

    [Fact]
    public void Counts_SeparateTasksFromCandidates()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = Resolve(Plan("{\"taskId\":45},{\"taskId\":52},{\"externalId\":\"outlook:001\"}"));

        plan.Groups[0].TaskCount.Should().Be(2);
        plan.Groups[0].CandidateCount.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    public void AnEmptyOrBrokenPlan_HasFourEmptyGroupsButStillASummary(string planJson)
    {
        Candidate("outlook:001", TriageStatus.Registered, resultTaskId: 45);

        var plan = Resolve(planJson);

        plan.Groups.Should().HaveCount(4);
        plan.Groups.Should().OnlyContain(g => g.Rows.Count == 0);
        plan.FirstThing.Should().BeNull();
        plan.Summary.Registered.Should().Be(1);
    }

    [Fact]
    public void Summary_CountsEveryStatusOfTheRun()
    {
        Candidate("a", TriageStatus.Registered, 45);
        Candidate("b", TriageStatus.Registered, 52);
        Candidate("c", TriageStatus.Merged, 45);
        Candidate("d", TriageStatus.Rejected);
        Candidate("e", TriageStatus.Later);
        Candidate("f", TriageStatus.Pending);

        var summary = Resolve("").Summary;

        summary.Should().Be(new TriageSummary(Total: 6, Registered: 2, Merged: 1, Rejected: 1, Later: 1, Pending: 1));
    }

    [Fact]
    public void WithoutABoard_TaskRowsAreDroppedButCandidateRowsSurvive()
    {
        Candidate("outlook:001", TriageStatus.Pending);

        var plan = MorningPlanResolver.Resolve(
            Plan("{\"taskId\":45},{\"externalId\":\"outlook:001\"}"), _candidates, board: null, ProjectName);

        plan.Groups[0].Rows.Should().ContainSingle().Which.Should().BeOfType<CandidateRow>();
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningPlanResolverTests" -nologo -v q`
Expected: ビルドエラー（`MorningPlanResolver` / `ResolvedPlan` が無い）

- [ ] **Step 3: 型を書く**

`src/MoTask.Core/Morning/ResolvedPlan.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>plan.json の groups[].key の 4 値。並びは表示順であり、重複排除の走査順でもある（仕様 §4）。</summary>
public enum PlanGroupKey
{
    Today = 0,
    IfTime = 1,
    AiReady = 2,
    Waiting = 3,
}

/// <summary>タスク行が今朝の候補から生まれたものかどうか。「新規」「統合」の印に使う。</summary>
public enum TaskRowOrigin
{
    None = 0,
    RegisteredThisMorning = 1,
    MergedThisMorning = 2,
}

/// <summary>プランの 1 行。実タスクか、まだ仕分けていない候補かのどちらか。</summary>
public abstract record PlanRow(string Title);

public sealed record TaskRow(
    int TaskId,
    string Title,
    string? ProjectName,
    DateOnly? DueDate,
    string ColumnName,
    bool IsDone,
    TaskRowOrigin Origin) : PlanRow(Title);

public sealed record CandidateRow(
    int CandidateId,
    string Title,
    string Source,
    DateOnly? SuggestedDueDate,
    string SuggestedProject) : PlanRow(Title);

public sealed record PlanGroup(PlanGroupKey Key, IReadOnlyList<PlanRow> Rows)
{
    public int TaskCount => Rows.Count(r => r is TaskRow);
    public int CandidateCount => Rows.Count(r => r is CandidateRow);

    public static PlanGroup Empty(PlanGroupKey key) => new(key, Array.Empty<PlanRow>());
}

/// <summary>その実行の候補の内訳。今日の Later はキューに乗らないので決着済みとして数える。</summary>
public sealed record TriageSummary(int Total, int Registered, int Merged, int Rejected, int Later, int Pending)
{
    public static readonly TriageSummary None = new(0, 0, 0, 0, 0, 0);

    public static TriageSummary Of(IEnumerable<TriageCandidate> candidates)
    {
        var all = candidates.ToList();
        int Count(TriageStatus status) => all.Count(c => c.Status == status);
        return new TriageSummary(
            all.Count,
            Count(TriageStatus.Registered), Count(TriageStatus.Merged),
            Count(TriageStatus.Rejected), Count(TriageStatus.Later), Count(TriageStatus.Pending));
    }
}

/// <summary>
/// PlanJson を画面の行に解決した結果（仕様 §4）。Groups は 4 つ固定・PlanGroupKey の順。
/// FirstThingIsFallback は firstThing が解決できず today の先頭へ繰り下げたとき（today も空なら FirstThing は null）。
/// </summary>
public sealed record ResolvedPlan(
    PlanRow? FirstThing,
    string FirstThingReason,
    bool FirstThingIsFallback,
    IReadOnlyList<PlanGroup> Groups,
    TriageSummary Summary)
{
    public static ResolvedPlan Empty(TriageSummary summary) => new(
        null, "", false,
        Enum.GetValues<PlanGroupKey>().Select(PlanGroup.Empty).ToList(),
        summary);
}
```

- [ ] **Step 4: 解決を書く**

`src/MoTask.Core/Morning/MorningPlanResolver.cs`:

```csharp
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// 保存済み PlanJson の中身を読む唯一の場所（仕様 §4）。純関数。
/// 各項目が taskId か externalId のどちらかを指す、という契約の要（親仕様 §8）をここで画面の行に落とす。
/// 候補を登録すればその行が実タスクに解決し、却下・あとでにすれば行が消える。
/// </summary>
public static class MorningPlanResolver
{
    /// <summary>JSON の key と enum の対応。MorningResultReader.PlanGroupKeys と同じ並び。</summary>
    private static readonly (string Json, PlanGroupKey Key)[] Keys =
    {
        ("today", PlanGroupKey.Today),
        ("ifTime", PlanGroupKey.IfTime),
        ("aiReady", PlanGroupKey.AiReady),
        ("waiting", PlanGroupKey.Waiting),
    };

    public static ResolvedPlan Resolve(
        string planJson,
        IReadOnlyList<TriageCandidate> candidates,
        Board? board,
        Func<int?, string?> projectName)
    {
        var summary = TriageSummary.Of(candidates);
        if (string.IsNullOrWhiteSpace(planJson)) return ResolvedPlan.Empty(summary);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(planJson);
        }
        catch (JsonException)
        {
            // MorningResultReader が検証済みのはずだが、DB の中身を信用しきらない
            return ResolvedPlan.Empty(summary);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ResolvedPlan.Empty(summary);
            var root = doc.RootElement;
            var index = new Index(candidates, board, projectName);

            var seenTasks = new HashSet<int>();
            var seenCandidates = new HashSet<int>();
            var groups = new List<PlanGroup>(Keys.Length);
            foreach (var (json, key) in Keys)
            {
                var rows = new List<PlanRow>();
                foreach (var item in ItemsOf(root, json))
                {
                    var row = index.Resolve(item);
                    var isNew = row switch
                    {
                        TaskRow task => seenTasks.Add(task.TaskId),
                        CandidateRow candidate => seenCandidates.Add(candidate.CandidateId),
                        _ => false,
                    };
                    if (isNew) rows.Add(row!);
                }
                groups.Add(new PlanGroup(key, rows));
            }

            PlanRow? firstThing = null;
            var reason = "";
            if (root.TryGetProperty("firstThing", out var first) && first.ValueKind == JsonValueKind.Object)
            {
                firstThing = index.Resolve(first);
                if (firstThing is not null) reason = Text(first, "reason");
            }
            var fallback = firstThing is null;
            if (fallback) firstThing = groups[0].Rows.FirstOrDefault();

            return new ResolvedPlan(firstThing, reason, fallback, groups, summary);
        }
    }

    /// <summary>groups[] から key の一致する要素の items[] を返す。無ければ空。</summary>
    private static IEnumerable<JsonElement> ItemsOf(JsonElement root, string key)
    {
        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || Text(group, "key") != key) continue;
            if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray()) yield return item;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static bool TryInt(JsonElement element, string name, out int number)
    {
        number = 0;
        return element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out number);
    }

    /// <summary>盤面と候補の引き当て。論理削除済みのタスクは最初から入れない。</summary>
    private sealed class Index
    {
        private readonly Dictionary<int, (TaskItem Task, Column Column)> _tasks = new();
        private readonly Dictionary<string, TriageCandidate> _byExternalId;
        private readonly Func<int?, string?> _projectName;

        public Index(IReadOnlyList<TriageCandidate> candidates, Board? board, Func<int?, string?> projectName)
        {
            _projectName = projectName;
            _byExternalId = new Dictionary<string, TriageCandidate>(StringComparer.Ordinal);
            foreach (var candidate in candidates) _byExternalId.TryAdd(candidate.ExternalId, candidate);
            if (board is null) return;
            foreach (var column in board.Columns)
            foreach (var task in column.Tasks)
            {
                if (!task.IsDeleted) _tasks[task.Id] = (task, column);
            }
        }

        public PlanRow? Resolve(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            // 両方持っていたら taskId を優先する（契約は「どちらか一方」だが、両方来ても落とさない）
            if (TryInt(item, "taskId", out var taskId)) return TaskRowFor(taskId, TaskRowOrigin.None);

            var externalId = Text(item, "externalId");
            if (externalId.Length == 0 || !_byExternalId.TryGetValue(externalId, out var candidate)) return null;
            return candidate.Status switch
            {
                TriageStatus.Pending or TriageStatus.Later => new CandidateRow(
                    candidate.Id, candidate.Title, candidate.Source, candidate.SuggestedDueDate, candidate.SuggestedProject),
                TriageStatus.Registered when candidate.ResultTaskId is int registered
                    => TaskRowFor(registered, TaskRowOrigin.RegisteredThisMorning),
                TriageStatus.Merged when candidate.ResultTaskId is int merged
                    => TaskRowFor(merged, TaskRowOrigin.MergedThisMorning),
                _ => null, // Rejected、または ResultTaskId の無い決着済み
            };
        }

        private TaskRow? TaskRowFor(int taskId, TaskRowOrigin origin)
        {
            if (!_tasks.TryGetValue(taskId, out var found)) return null;
            var (task, column) = found;
            return new TaskRow(
                task.Id, task.Title, _projectName(task.ProjectId), task.DueDate,
                column.Name, column.Role == ColumnRole.Done, origin);
        }
    }
}
```

- [ ] **Step 5: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningPlanResolverTests" -nologo -v q`
Expected: 全件 PASS（Theory を含めて 24 件）

- [ ] **Step 6: 仕様 §4 の Summary の1行を直す**

`docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md` の

```
`Summary` は `candidates` の `Status` を数えるだけ。`Pending` と `Later` は「未処理」にまとめる。
```

を次に置き換える:

```
`Summary` は `candidates` の `Status` を数えるだけ。`Registered` / `Merged` / `Rejected` / `Later` / `Pending` を
それぞれ数える。今日の `Later` はキューに乗らない（キューは今日の `Pending` ＋ 過去の `Later`）ので、
「あとで」は決着済みとして数える。`Pending` が 0 でないのは仕分け中だけ。
```

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core/Morning/ResolvedPlan.cs src/MoTask.Core/Morning/MorningPlanResolver.cs tests/MoTask.Core.Tests/MorningPlanResolverTests.cs docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md
git commit -m "feat(core): resolve the saved plan into task and candidate rows

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: 実行の候補を状態を問わず返す照会

仕様 §5。解決には `Registered` / `Merged` の候補も要るので、キュー（Pending ＋ 過去の Later）とは別の照会を足す。
`IMorningService` にメンバーが増えるので、App テストの偽サービスもこのタスクで追従させる
（さもないと `MoTask.App.Tests` がコンパイルできない）。ついでに偽サービスを「状態を持つ」形に直し、
Task 5 で「登録した候補の行が実タスクに変わる」をテストできるようにする。

**Files:**
- Modify: `src/MoTask.Core/Abstractions/IMorningRepository.cs`
- Modify: `src/MoTask.Data/Repositories/MorningRepository.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`
- Modify: `src/MoTask.Core/Services/IMorningService.cs` / `MorningService.cs`
- Modify: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（偽サービス）
- Test: `tests/MoTask.Data.Tests/MorningRepositoryTests.cs`（追記）

**Interfaces:**
- Produces:
  - `IMorningRepository.GetCandidatesOfRunAsync(int runId, CancellationToken ct = default) : Task<IReadOnlyList<TriageCandidate>>`（Id 昇順、状態を問わない）
  - `IMorningService.GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)`（同じ形。ゲート越し）
  - App テストの `FakeMorningService.Candidates`（`List<TriageCandidate>`、全状態）と、4アクションが `Status` / `ResultTaskId` を実サービスと同じに書き換えること

- [ ] **Step 1: Data の失敗するテストを書く**

`tests/MoTask.Data.Tests/MorningRepositoryTests.cs` の `GetUnfinishedRun_FindsOnlyPendingOrRunning` の直前に追加:

```csharp
    [Fact]
    public async Task GetCandidatesOfRun_ReturnsEveryStatus_ButOnlyThatRun()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var yesterday = NewRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        var today = NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
        repo.Add(yesterday);
        repo.Add(today);
        await ctx.SaveChangesAsync();
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:old", TriageStatus.Later));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:registered", TriageStatus.Registered));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:rejected", TriageStatus.Rejected));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:pending"));
        await ctx.SaveChangesAsync();

        var all = await repo.GetCandidatesOfRunAsync(today.Id);

        all.Select(c => c.ExternalId).Should().Equal("outlook:registered", "outlook:rejected", "outlook:pending",
            "解決には決着済みの候補も要る。他の実行の候補は含めない");
    }
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/MoTask.Data.Tests --filter "FullyQualifiedName~GetCandidatesOfRun" -nologo -v q`
Expected: ビルドエラー（`GetCandidatesOfRunAsync` が無い）

- [ ] **Step 3: リポジトリに足す**

`src/MoTask.Core/Abstractions/IMorningRepository.cs` の `GetQueueAsync` の下に:

```csharp
    /// <summary>この実行の候補を状態を問わず Id 昇順で。プランの解決に使う（仕様 §5）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);
```

`src/MoTask.Data/Repositories/MorningRepository.cs` の末尾（`GetQueueAsync` の下）に:

```csharp
    public async Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => await _db.TriageCandidates
            .Where(c => c.MorningRunId == runId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
```

`tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs` の `GetQueueAsync` の下に:

```csharp
    public Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TriageCandidate>>(
            Candidates.Where(c => c.MorningRunId == runId).OrderBy(c => c.Id).ToList());
```

- [ ] **Step 4: サービスに足す**

`src/MoTask.Core/Services/IMorningService.cs` の `GetQueueAsync` の下に:

```csharp
    /// <summary>この実行の候補を状態を問わず。プランの解決に使う（仕様 §5）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);
```

`src/MoTask.Core/Services/MorningService.cs` の `GetQueueAsync` の下に:

```csharp
    public Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetCandidatesOfRunAsync(runId, ct), ct);
```

- [ ] **Step 5: App テストの偽サービスを状態を持つ形に直す**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` の `FakeMorningService` を丸ごと次に置き換える
（`Queue` は `Candidates` になり、キューは状態から計算する。4アクションは実サービスと同じように
`Status` / `ResultTaskId` を書き換える）:

```csharp
    /// <summary>
    /// 呼ばれた操作を記録する偽サービス。候補は全状態を 1 つのリストに持ち、キューは実リポジトリと
    /// 同じ規則（この実行の Pending ＋ 他の実行の Later）で計算する。4 アクションは実サービスと同じく
    /// Status / ResultTaskId を書き換える（そうしないと「登録した行が実タスクに変わる」を試せない）。
    /// </summary>
    private sealed class FakeMorningService : IMorningService
    {
        public event EventHandler<MorningRunChangedEventArgs>? RunChanged;

        public MorningRun? Current { get; set; }
        public List<TriageCandidate> Candidates { get; } = new();
        public List<string> Calls { get; } = new();
        public Result<MorningRun> StartResult { get; set; } = Result.Ok(new MorningRun());
        public Result<TaskItem> RegisterResult { get; set; } = Result.Ok(new TaskItem { Id = 1 });
        public CandidateDecision? LastDecision { get; private set; }

        /// <summary>RefreshAsync が例外を握りつぶさずバナーへ回すことを確かめるためのフック。</summary>
        public Exception? FailNextGetCurrentRun { get; set; }

        public void Raise(MorningRun run, string? warning = null, bool candidates = false)
            => RunChanged?.Invoke(this, new MorningRunChangedEventArgs(
                new MorningRunSnapshot(run.Id, run.Date, run.Status, 0, run.ErrorMessage, run.JobFolder),
                warning, candidates));

        public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default)
        {
            if (FailNextGetCurrentRun is { } ex)
            {
                FailNextGetCurrentRun = null;
                throw ex;
            }
            return Task.FromResult(Current);
        }

        public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TriageCandidate>>(Candidates
                .Where(c => (c.MorningRunId == runId && c.Status == TriageStatus.Pending)
                            || (c.MorningRunId != runId && c.Status == TriageStatus.Later))
                .OrderBy(c => c.Id).ToList());

        public Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TriageCandidate>>(
                Candidates.Where(c => c.MorningRunId == runId).OrderBy(c => c.Id).ToList());

        public Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public int TurnCountOf(int runId) => 0;

        public Task<Result<MorningRun>> StartAsync(CancellationToken ct = default)
        {
            Calls.Add("Start");
            if (StartResult.IsSuccess) Current = StartResult.Value;
            return Task.FromResult(StartResult);
        }

        public Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
        {
            Calls.Add("Complete");
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
        {
            Calls.Add("StopTracking");
            return Task.FromResult(Result.Ok());
        }

        public Task RecoverOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

        private void Decide(int candidateId, TriageStatus status, int? resultTaskId = null)
        {
            var candidate = Candidates.Single(c => c.Id == candidateId);
            candidate.Status = status;
            candidate.ResultTaskId = resultTaskId;
        }

        public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
        {
            Calls.Add("Register");
            LastDecision = decision;
            if (RegisterResult.IsSuccess) Decide(decision.CandidateId, TriageStatus.Registered, RegisterResult.Value!.Id);
            return Task.FromResult(RegisterResult);
        }

        public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
        {
            Calls.Add($"Merge:{targetTaskId}");
            Decide(candidateId, TriageStatus.Merged, targetTaskId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Postpone");
            Decide(candidateId, TriageStatus.Later);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Reject");
            Decide(candidateId, TriageStatus.Rejected);
            return Task.FromResult(Result.Ok());
        }
    }
```

同じファイルで `_service.Queue.Add(` をすべて `_service.Candidates.Add(` に置き換える:

```bash
sed -i 's/_service\.Queue\.Add(/_service.Candidates.Add(/g' tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
```

- [ ] **Step 6: 全テストが通ることを確認する**

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS（Data の新テスト 1 件を含む。App の既存テストは偽サービスの挙動が実サービスに近づいただけなので変わらず通る）

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core/Abstractions/IMorningRepository.cs src/MoTask.Data/Repositories/MorningRepository.cs tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs src/MoTask.Core/Services/IMorningService.cs src/MoTask.Core/Services/MorningService.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs tests/MoTask.Data.Tests/MorningRepositoryTests.cs
git commit -m "feat(core): query every candidate of a morning run for plan resolution

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: 一括操作（推奨をまとめて適用・すべて後で）

仕様 §5。既存の4アクションを順に呼ぶだけ。**1件失敗しても止めず、見送り理由を積んで次へ進む。**
一括操作自身はゲートを取らない（4アクションが呼び出しごとにゲートを取る）。

**Files:**
- Create: `src/MoTask.Core/Services/BulkOutcome.cs`
- Modify: `src/MoTask.Core/Services/IMorningService.cs` / `MorningService.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs`
- Modify: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（偽サービスに 2 メソッド）
- Test: `tests/MoTask.Core.Tests/MorningServiceBulkTests.cs`

**Interfaces:**
- Consumes: Task 2 までの `IMorningService`
- Produces:
  - `BulkOutcome(int Applied, IReadOnlyList<string> Skipped)`
  - `IMorningService.ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default) : Task<Result<BulkOutcome>>`
  - `IMorningService.PostponeAllAsync(int runId, CancellationToken ct = default) : Task<Result<BulkOutcome>>`
  - `Messages.BulkSkippedFormat`（`{0}: {1}`）、`Messages.MergeTargetMissing`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceBulkTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 一括操作（仕様 §5）。4 アクションを順に呼ぶだけで、1 件の失敗で止まらない。
/// MorningServiceTriageTests と同じく、ゲートの中から IBoardService を呼ぶ実装にすると返ってこない。
/// </summary>
public class MorningServiceBulkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly MorningService _service;
    private readonly Column _backlog;
    private readonly TaskItem _target;
    private readonly MorningRun _run;

    public MorningServiceBulkTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _target = _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            new FakeSessionLauncher(), new FakeJobFolder(), new FakeJobEventSource(), new InMemorySettingsStore(), boardService);
        _run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
    }

    private TriageCandidate Candidate(string externalId, TriageAction suggested, int? mergeTarget = null)
    {
        var candidate = _store.SeedCandidate(_run, externalId, suggested: suggested);
        candidate.Title = $"候補 {externalId}";
        candidate.SuggestedMergeTaskId = mergeTarget;
        return candidate;
    }

    private static async Task<T> WithinLimitAsync<T>(Task<T> work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(Limit));
        finished.Should().BeSameAs(work, "ゲートの中から IBoardService を呼ぶとデッドロックする");
        return await work;
    }

    [Fact]
    public async Task ApplySuggestions_DispatchesEachCandidateByItsSuggestion()
    {
        var register = Candidate("a", TriageAction.Register);
        var merge = Candidate("b", TriageAction.Merge, _target.Id);
        var later = Candidate("c", TriageAction.Later);
        var reject = Candidate("d", TriageAction.Reject);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue(outcome.Error);
        outcome.Value!.Applied.Should().Be(4);
        outcome.Value.Skipped.Should().BeEmpty();
        register.Status.Should().Be(TriageStatus.Registered);
        _store.AllTasks.Should().Contain(t => t.Id == register.ResultTaskId && t.ColumnId == _backlog.Id && t.Title == "候補 a",
            "登録は候補のタイトルのまま、指定した列へ入る");
        merge.Status.Should().Be(TriageStatus.Merged);
        merge.ResultTaskId.Should().Be(_target.Id);
        later.Status.Should().Be(TriageStatus.Later);
        reject.Status.Should().Be(TriageStatus.Rejected);
    }

    [Fact]
    public async Task ApplySuggestions_SkipsAFailure_AndKeepsGoing()
    {
        var broken = Candidate("a", TriageAction.Merge, mergeTarget: 999);
        var fine = Candidate("b", TriageAction.Reject);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue("全件見送りでも一括操作としては成功");
        outcome.Value!.Applied.Should().Be(1);
        outcome.Value.Skipped.Should().ContainSingle()
            .Which.Should().Be(string.Format(Messages.BulkSkippedFormat, "候補 a", Messages.TaskNotFound));
        broken.Status.Should().Be(TriageStatus.Pending, "見送った候補はキューに残る");
        fine.Status.Should().Be(TriageStatus.Rejected, "1 件目の失敗で 2 件目を止めない");
    }

    [Fact]
    public async Task ApplySuggestions_SkipsAMergeWithoutASuggestedTarget()
    {
        Candidate("a", TriageAction.Merge, mergeTarget: null);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.Value!.Applied.Should().Be(0);
        outcome.Value.Skipped.Should().ContainSingle()
            .Which.Should().Be(string.Format(Messages.BulkSkippedFormat, "候補 a", Messages.MergeTargetMissing));
    }

    [Fact]
    public async Task ApplySuggestions_CollectsTheWarningsOfSuccessfulActions()
    {
        var tight = _store.SeedColumn("狭い列", ColumnRole.Active, wipLimit: 1);
        _store.SeedTask(tight, "既にある");
        Candidate("a", TriageAction.Register);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, tight.Id));

        outcome.Value!.Applied.Should().Be(1);
        outcome.Warnings.Should().NotBeEmpty("WIP 超過の警告は成功の警告として集約する");
    }

    [Fact]
    public async Task ApplySuggestions_WithAnEmptyQueue_DoesNothing()
    {
        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.Applied.Should().Be(0);
        outcome.Value.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task PostponeAll_MarksEveryQueuedCandidateLater()
    {
        var a = Candidate("a", TriageAction.Register);
        var b = Candidate("b", TriageAction.Reject);
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        var carried = _store.SeedCandidate(yesterday, "old", TriageStatus.Later);

        var outcome = await WithinLimitAsync(_service.PostponeAllAsync(_run.Id));

        outcome.Value!.Applied.Should().Be(3, "過去の Later もキューに乗っているので対象");
        a.Status.Should().Be(TriageStatus.Later);
        b.Status.Should().Be(TriageStatus.Later);
        carried.Status.Should().Be(TriageStatus.Later);
        _store.AllTasks.Should().ContainSingle("タスクは作らない");
    }
}
```

`_store.AllTasks` は `InMemoryStore` にある既存のプロパティ（`Board.Columns.SelectMany(c => c.Tasks)`）。
`SeedColumn` の第 3 引数 `wipLimit` は既存のシグネチャ（`SeedColumn(string name, ColumnRole role, int? wipLimit = null)`）。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceBulkTests" -nologo -v q`
Expected: ビルドエラー（`ApplySuggestionsAsync` / `BulkOutcome` / `Messages.BulkSkippedFormat` が無い）

- [ ] **Step 3: 文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の `</root>` の直前に:

```xml
  <data name="BulkSkippedFormat" xml:space="preserve"><value>{0}: {1}</value></data>
  <data name="MergeTargetMissing" xml:space="preserve"><value>統合先の推薦がありません</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の `MorningCandidatesDiscardedFormat` の下に:

```csharp
    public static string BulkSkippedFormat => Get(nameof(BulkSkippedFormat));
    public static string MergeTargetMissing => Get(nameof(MergeTargetMissing));
```

- [ ] **Step 4: 型とサービスを書く**

`src/MoTask.Core/Services/BulkOutcome.cs`:

```csharp
namespace MoTask.Core.Services;

/// <summary>一括操作の結果（仕様 §5）。Skipped は「候補のタイトル: 理由」の形で画面にそのまま出す。</summary>
public sealed record BulkOutcome(int Applied, IReadOnlyList<string> Skipped);
```

`src/MoTask.Core/Services/IMorningService.cs` の「仕分け」の節の末尾に:

```csharp
    // 一括（仕様 §5）。既存の 4 アクションを順に呼ぶだけで、1 件の失敗で止まらない
    /// <summary>キューの候補を SuggestedAction どおりに処理する。登録先は registerColumnId。</summary>
    Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default);
    /// <summary>キューの候補をすべて「あとで」にする。</summary>
    Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default);
```

`src/MoTask.Core/Services/MorningService.cs` の `// ---------- 補助 ----------` の直前に:

```csharp
    // ---------- 一括 ----------

    /// <summary>
    /// 既存の 4 アクションを順に呼ぶだけ（仕様 §5）。各アクションが呼び出しごとにゲートを取るので、
    /// ここ自身はゲートを取らない（取ると中の IBoardService でデッドロックする）。
    /// 1 件失敗しても止めず、見送り理由を積んで次へ進む。全件見送りでも Result は成功。
    /// </summary>
    public Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default)
        => RunBulkAsync(runId, candidate => candidate.SuggestedAction switch
        {
            TriageAction.Register => RegisterAsync(new CandidateDecision(
                candidate.Id, candidate.Title, candidate.SuggestedDueDate, candidate.SuggestedProject, registerColumnId), ct)
                .ContinueWith(t => (Result)t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default),
            TriageAction.Merge => candidate.SuggestedMergeTaskId is int target
                ? MergeAsync(candidate.Id, target, ct)
                : Task.FromResult(Result.Fail(Messages.MergeTargetMissing)),
            TriageAction.Later => PostponeAsync(candidate.Id, ct),
            TriageAction.Reject => RejectAsync(candidate.Id, ct),
            _ => Task.FromResult(Result.Fail(Messages.CandidateAlreadyDecided)),
        }, ct);

    public Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default)
        => RunBulkAsync(runId, candidate => PostponeAsync(candidate.Id, ct), ct);

    private async Task<Result<BulkOutcome>> RunBulkAsync(
        int runId, Func<TriageCandidate, Task<Result>> action, CancellationToken ct)
    {
        var queue = await GetQueueAsync(runId, ct).ConfigureAwait(false);
        var applied = 0;
        var skipped = new List<string>();
        var warnings = new List<string>();
        foreach (var candidate in queue)
        {
            var result = await action(candidate).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                applied++;
                warnings.AddRange(result.Warnings);
            }
            else
            {
                skipped.Add(string.Format(Messages.BulkSkippedFormat, candidate.Title, result.Error));
            }
        }
        return Result.Ok(new BulkOutcome(applied, skipped), warnings);
    }
```

`ContinueWith` が読みにくければ、`Register` の分岐だけローカル関数に切り出してよい:

```csharp
    private async Task<Result> RegisterBySuggestionAsync(TriageCandidate candidate, int columnId, CancellationToken ct)
        => await RegisterAsync(new CandidateDecision(
            candidate.Id, candidate.Title, candidate.SuggestedDueDate, candidate.SuggestedProject, columnId), ct)
            .ConfigureAwait(false);
```

（`Result<TaskItem>` は `Result` を継承しているので、そのまま `Result` として返せる。）この形なら
`TriageAction.Register => RegisterBySuggestionAsync(candidate, registerColumnId, ct),` と書ける。**こちらを推奨。**

- [ ] **Step 5: App テストの偽サービスに 2 メソッドを足す**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` の `FakeMorningService` の `RejectAsync` の下に:

```csharp
        public Result<BulkOutcome> BulkResult { get; set; } = Result.Ok(new BulkOutcome(0, Array.Empty<string>()));

        public Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default)
        {
            Calls.Add($"ApplySuggestions:{registerColumnId}");
            foreach (var candidate in Candidates.Where(c => c.Status == TriageStatus.Pending).ToList())
            {
                Decide(candidate.Id, candidate.SuggestedAction switch
                {
                    TriageAction.Register => TriageStatus.Registered,
                    TriageAction.Merge => TriageStatus.Merged,
                    TriageAction.Later => TriageStatus.Later,
                    _ => TriageStatus.Rejected,
                });
            }
            return Task.FromResult(BulkResult);
        }

        public Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default)
        {
            Calls.Add("PostponeAll");
            foreach (var candidate in Candidates.Where(c => c.Status == TriageStatus.Pending).ToList())
                Decide(candidate.Id, TriageStatus.Later);
            return Task.FromResult(BulkResult);
        }
```

- [ ] **Step 6: 全テストが通ることを確認する**

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS（Bulk の 6 件を含む）

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core/Services/BulkOutcome.cs src/MoTask.Core/Services/IMorningService.cs src/MoTask.Core/Services/MorningService.cs src/MoTask.Core/Resources/Messages.resx src/MoTask.Core/Resources/Messages.cs tests/MoTask.Core.Tests/MorningServiceBulkTests.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit -m "feat(core): apply every suggestion or postpone every candidate in one go

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: TriagePanelViewModel（左・状態1）への分割と統合先の選択

仕様 §6。今の `MorningPlanViewModel` から編集フォームと4アクションを `TriagePanelViewModel` へ移し、
左パネルを `ContentControl` の `DataTemplate` 切替にする。統合先は盤面の未完了タスクから選べるようにする。
画面の VM は `LeftPanel`（このタスクでは `Triage` か `null`。Task 5 で `FirstThingViewModel` が加わる）を持つ。

**Files:**
- Create: `src/MoTask.App/ViewModels/TriagePanelViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/MorningPlanViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/CandidateItemViewModel.cs`
- Modify: `src/MoTask.App/Views/MorningPlanView.xaml`
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Test: `tests/MoTask.App.Tests/TriagePanelViewModelTests.cs`（新規）
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（分割に追従）

**Interfaces:**
- Consumes: Task 3 までの `IMorningService`、既存の `ColumnChoice(int Id, string Name)`、`CandidateItemViewModel`
- Produces:
  - `TaskChoice(int Id, string Title, string ColumnName)` と `Display`
  - `TriagePanelViewModel(IMorningService service, Func<Result, Task> afterDecision, Action<string> openPath)`
    - `Show(CandidateItemViewModel? candidate, int index, int count)`、`SetChoices(IEnumerable<ColumnChoice>, IEnumerable<TaskChoice>)`
    - `Selected` / `PositionText` / `HeadingText` / `KeyHint` / `ColumnChoices` / `MergeTargets`
    - `EditTitle` / `EditDueDate` / `EditProjectName` / `EditColumnId` / `EditMergeTargetId`（`int?`）/ `CanMerge`
    - `RegisterCommand` / `MergeCommand` / `PostponeCommand` / `RejectCommand` / `OpenLinkCommand`
  - `MorningPlanViewModel.Triage`（`TriagePanelViewModel`）、`MorningPlanViewModel.LeftPanel`（`object?`）
  - `CandidateItemViewModel.IsMergeSuggested`（旧 `CanMerge`）

- [ ] **Step 1: 文言を足す**

`src/MoTask.App/Resources/Strings.resx` の `</root>` の直前に:

```xml
  <data name="MorningTaskChoiceFormat" xml:space="preserve"><value>{0}（{1}）</value></data>
  <data name="MorningMergeTarget" xml:space="preserve"><value>統合先</value></data>
  <data name="MorningKeyHint" xml:space="preserve"><value>T: 登録　E: 統合　X: 却下　L: あとで</value></data>
```

`src/MoTask.App/Resources/Strings.cs` の `MorningRefreshFailedFormat` の下に:

```csharp
    public static string MorningTaskChoiceFormat => Get(nameof(MorningTaskChoiceFormat));
    public static string MorningMergeTarget => Get(nameof(MorningMergeTarget));
    public static string MorningKeyHint => Get(nameof(MorningKeyHint));
```

- [ ] **Step 2: 失敗するテストを書く（新規の分）**

`tests/MoTask.App.Tests/TriagePanelViewModelTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>左パネル・状態 1（仕様 §6）。編集フォームと統合先の選択。</summary>
public class TriagePanelViewModelTests
{
    private readonly IMorningService _service = Substitute.For<IMorningService>();
    private readonly List<Result> _decisions = new();
    private readonly List<string> _opened = new();
    private readonly TriagePanelViewModel _panel;

    public TriagePanelViewModelTests()
    {
        _service.MergeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _service.RegisterAsync(Arg.Any<CandidateDecision>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new TaskItem { Id = 99 })));
        _panel = new TriagePanelViewModel(_service, r => { _decisions.Add(r); return Task.CompletedTask; }, _opened.Add);
        _panel.SetChoices(
            new[] { new ColumnChoice(1, "未着手"), new ColumnChoice(2, "進行中") },
            new[] { new TaskChoice(10, "請求先情報を更新する", "未着手"), new TaskChoice(12, "週次レポートを作成する", "進行中") });
    }

    private static CandidateItemViewModel Candidate(int? mergeTarget = null) => new(new TriageCandidate
    {
        Id = 1, MorningRunId = 1, ExternalId = "outlook:001", Source = "Outlook", Title = "請求先情報を更新する",
        Evidence = "「9月8日までに」", Link = "https://outlook.office.com/x",
        SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
        SuggestedAction = mergeTarget is null ? TriageAction.Register : TriageAction.Merge, SuggestedMergeTaskId = mergeTarget,
    });

    [Fact]
    public void SetChoices_DefaultsToTheFirstColumn_AndFormatsTargets()
    {
        _panel.EditColumnId.Should().Be(1);
        _panel.MergeTargets.Select(t => t.Display).Should().Equal("請求先情報を更新する（未着手）", "週次レポートを作成する（進行中）");
    }

    [Fact]
    public void Show_FillsTheEditorFromTheCandidate()
    {
        _panel.Show(Candidate(), index: 1, count: 3);

        _panel.EditTitle.Should().Be("請求先情報を更新する");
        _panel.EditDueDate.Should().Be(new DateTime(2026, 9, 8));
        _panel.EditProjectName.Should().Be("顧客A");
        _panel.PositionText.Should().Be("2 / 3");
    }

    [Fact]
    public void Show_PreselectsTheSuggestedMergeTarget_WhenItIsOnTheBoard()
    {
        _panel.Show(Candidate(mergeTarget: 12), 0, 1);

        _panel.EditMergeTargetId.Should().Be(12);
        _panel.CanMerge.Should().BeTrue();
    }

    [Fact]
    public void Show_LeavesTheTargetUnselected_WhenTheSuggestionIsGone()
    {
        _panel.Show(Candidate(mergeTarget: 999), 0, 1);

        _panel.EditMergeTargetId.Should().BeNull("推薦されたタスクが盤面に無ければ選ばせるだけ");
        _panel.CanMerge.Should().BeFalse();
        _panel.MergeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_UsesTheTargetThePersonChose()
    {
        _panel.Show(Candidate(), 0, 1);
        _panel.EditMergeTargetId = 10;

        await _panel.MergeCommand.ExecuteAsync(null);

        await _service.Received(1).MergeAsync(1, 10, Arg.Any<CancellationToken>());
        _decisions.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_DoesNothingWithoutATarget()
    {
        _panel.Show(Candidate(), 0, 1);

        await _panel.MergeCommand.ExecuteAsync(null);

        await _service.DidNotReceive().MergeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _decisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Register_PassesTheEditedValues()
    {
        _panel.Show(Candidate(), 0, 1);
        _panel.EditTitle = "書き換えた題名";
        _panel.EditDueDate = new DateTime(2026, 9, 10);
        _panel.EditProjectName = "別プロジェクト";
        _panel.EditColumnId = 2;

        await _panel.RegisterCommand.ExecuteAsync(null);

        await _service.Received(1).RegisterAsync(
            new CandidateDecision(1, "書き換えた題名", new DateOnly(2026, 9, 10), "別プロジェクト", 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OpenLink_OpensTheCandidateLink()
    {
        _panel.Show(Candidate(), 0, 1);

        _panel.OpenLinkCommand.Execute(null);

        _opened.Should().Equal("https://outlook.office.com/x");
    }
}
```

- [ ] **Step 3: 失敗を確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TriagePanelViewModelTests" -nologo -v q`
Expected: ビルドエラー（`TriagePanelViewModel` / `TaskChoice` が無い）

- [ ] **Step 4: TriagePanelViewModel を書く**

`src/MoTask.App/ViewModels/TriagePanelViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>統合先の選択肢。完了列と論理削除済みは出さない（仕様 §6）。</summary>
public sealed record TaskChoice(int Id, string Title, string ColumnName)
{
    public string Display => string.Format(Strings.MorningTaskChoiceFormat, Title, ColumnName);
}

/// <summary>
/// 左パネル・状態 1「仕分け中」（仕様 §6、ワイヤー 4a）。編集フォームと 4 アクションを持つ。
/// 候補キューと実行の状態は画面（MorningPlanViewModel）が持ち、片づいた後の読み直しも
/// 画面に任せる（afterDecision）。この VM はキューの中身を知らない。
/// </summary>
public sealed partial class TriagePanelViewModel : ObservableObject
{
    private readonly IMorningService _service;
    private readonly Func<Result, Task> _afterDecision;
    private readonly Action<string> _openPath;

    public TriagePanelViewModel(IMorningService service, Func<Result, Task> afterDecision, Action<string> openPath)
    {
        _service = service;
        _afterDecision = afterDecision;
        _openPath = openPath;
    }

    public string HeadingText => Strings.MorningTriageHeading;
    public string KeyHint => Strings.MorningKeyHint;

    /// <summary>登録先に選べる列。完了列は選ばせない（親仕様 §11）。</summary>
    public ObservableCollection<ColumnChoice> ColumnChoices { get; } = new();

    /// <summary>統合先に選べるタスク。</summary>
    public ObservableCollection<TaskChoice> MergeTargets { get; } = new();

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private string _positionText = "";

    // 編集フォーム。期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）。
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editProjectName = "";
    [ObservableProperty] private int _editColumnId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private int? _editMergeTargetId;

    /// <summary>統合先が選ばれているか。E キーと「統合」ボタンの活性。</summary>
    public bool CanMerge => EditMergeTargetId is not null;

    public void SetChoices(IEnumerable<ColumnChoice> columns, IEnumerable<TaskChoice> targets)
    {
        ColumnChoices.Clear();
        foreach (var column in columns) ColumnChoices.Add(column);
        MergeTargets.Clear();
        foreach (var target in targets) MergeTargets.Add(target);
        if (ColumnChoices.All(c => c.Id != EditColumnId)) EditColumnId = ColumnChoices.FirstOrDefault()?.Id ?? 0;
        if (EditMergeTargetId is int chosen && MergeTargets.All(t => t.Id != chosen)) EditMergeTargetId = null;
    }

    /// <summary>候補を 1 件見せる。推薦された統合先が一覧にあれば初期選択にする。</summary>
    public void Show(CandidateItemViewModel? candidate, int index, int count)
    {
        Selected = candidate;
        EditTitle = candidate?.Title ?? "";
        EditDueDate = candidate?.SuggestedDueDate?.ToDateTime(TimeOnly.MinValue);
        EditProjectName = candidate?.SuggestedProject ?? "";
        EditMergeTargetId = candidate?.SuggestedMergeTaskId is int suggested && MergeTargets.Any(t => t.Id == suggested)
            ? suggested
            : null;
        PositionText = candidate is null ? "" : string.Format(Strings.MorningPositionFormat, index + 1, count);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Selected is null) return;
        var due = EditDueDate is DateTime date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var registered = await _service.RegisterAsync(new CandidateDecision(
            Selected.CandidateId, EditTitle, due, EditProjectName, EditColumnId)).ConfigureAwait(true);
        await _afterDecision(registered).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (Selected is null || EditMergeTargetId is not int target) return;
        var merged = await _service.MergeAsync(Selected.CandidateId, target).ConfigureAwait(true);
        await _afterDecision(merged).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PostponeAsync()
    {
        if (Selected is null) return;
        await _afterDecision(await _service.PostponeAsync(Selected.CandidateId).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (Selected is null) return;
        await _afterDecision(await _service.RejectAsync(Selected.CandidateId).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Selected is { HasLink: true } candidate) _openPath(candidate.Link);
    }
}
```

- [ ] **Step 5: CandidateItemViewModel の `CanMerge` を `IsMergeSuggested` にする**

`src/MoTask.App/ViewModels/CandidateItemViewModel.cs` の

```csharp
    /// <summary>統合先が推薦されているときだけ「統合」を出す（選ばせるのは 2 本目の計画）。</summary>
    public bool CanMerge => _candidate.SuggestedMergeTaskId is not null;
```

を次に置き換える:

```csharp
    /// <summary>統合先が推薦されているか。候補キューの「統合が推奨」バッジに使う（統合できるかは TriagePanelViewModel.CanMerge）。</summary>
    public bool IsMergeSuggested => _candidate.SuggestedMergeTaskId is not null;
```

- [ ] **Step 6: MorningPlanViewModel からフォームを外す**

`src/MoTask.App/ViewModels/MorningPlanViewModel.cs` を丸ごと次に置き換える（実行の状態・バナー・
`RunChanged` の扱いは今のまま。変わるのは「フォームと 4 アクションが `Triage` へ移った」
「`LeftPanel` が増えた」「盤面から統合先の一覧も作る」の 3 点）:

```csharp
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Ai;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// 朝の実行プラン画面（仕様 §6・§7）。実行の状態と候補キューを持ち、左パネル（LeftPanel）を
/// 状態で切り替える。編集フォームと 4 アクションは TriagePanelViewModel（Triage）が持つ。
/// RunChanged はワーカースレッドから来るので UI スレッドへ載せ替える（BoardViewModel と同じ）。
/// </summary>
public sealed partial class MorningPlanViewModel : ObservableObject
{
    /// <summary>
    /// 画面が持つ「今の実行」の複製。MorningService が返す MorningRun / MorningRunSnapshot から
    /// 必要な分だけ写し取る。DbContext が追跡するエンティティ(GetCurrentRunAsync / StartAsync の
    /// 戻り値)への参照はここでは絶対に保持しない。追従スレッドから届く MorningRunChangedEventArgs
    /// は非同期に(SynchronizationContext.Post 経由で)届くので、その時点で実行がすでに
    /// Cancelled / Ingested になっていることがある。そのタイミングでも、追跡中のエンティティへ
    /// 書き戻すと(共有 DbContext の) 次の SaveChangesAsync が古い状態を復活させてしまうため。
    /// </summary>
    private sealed record RunState(
        int Id, DateOnly Date, MorningRunStatus Status, string? ErrorMessage, string JobFolder, bool HasPlan)
    {
        public static RunState From(MorningRun run) => new(
            run.Id, run.Date, run.Status, run.ErrorMessage, run.JobFolder,
            run.Status == MorningRunStatus.Ingested);

        public static RunState From(MorningRunSnapshot snapshot) => new(
            snapshot.RunId, snapshot.Date, snapshot.Status, snapshot.ErrorMessage, snapshot.JobFolder,
            snapshot.Status == MorningRunStatus.Ingested);
    }

    private readonly IMorningService _service;
    private readonly IBoardService _boardService;
    private readonly SynchronizationContext? _ui;
    private RunState? _run;

    /// <summary>リンクや成果物を開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();

    /// <summary>左パネル・状態 1。生成は 1 度だけで、Show で中身を差し替える。</summary>
    public TriagePanelViewModel Triage { get; }

    /// <summary>左パネルに出す VM。Triage か null（Task 5 で FirstThingViewModel が加わる）。</summary>
    [ObservableProperty] private object? _leftPanel;

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _hasNoCandidates;
    /// <summary>
    /// 「今日のプランはまだありません」を出すべきか。取り込み済み(Ingested)なら候補が 0 件でも
    /// この朝のプランは存在するので出さない(仕様 §4・§11)。CanStart とは目的が違う値なので
    /// 分けている(CanStart はボタンの活性、こちらは案内文の要否)。
    /// </summary>
    [ObservableProperty] private bool _hasNoPlanYet;
    /// <summary>プラン未生成のときの「前回: 9/5」（仕様 §11）。無ければ空文字。</summary>
    [ObservableProperty] private string _lastRunText = "";
    /// <summary>実行中の進捗。ターン数と直近のツール使用（仕様 §11）。</summary>
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _warningMessage;

    public MorningPlanViewModel(IMorningService service, IBoardService boardService)
    {
        _service = service;
        _boardService = boardService;
        _ui = SynchronizationContext.Current;
        Debug.Assert(_ui is not null || Application.Current is null,
            "MorningPlanViewModel は UI スレッドで生成すること。");

        Triage = new TriagePanelViewModel(service, AfterDecisionAsync, path => OpenPath(path));
        service.RunChanged += (_, e) => Post(() => OnRunChanged(e));
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public async Task LoadAsync()
    {
        var run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        _run = run is null ? null : RunState.From(run);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    /// <summary>
    /// 登録先の列（完了以外）と統合先のタスク（完了列と論理削除済み以外）を盤面から作る。
    /// 盤面の取得に失敗したら選択肢は空のままにして、画面そのものは出す。
    /// </summary>
    private async Task LoadBoardChoicesAsync()
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        if (!board.IsSuccess) return;
        var columns = board.Value!.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order).ToList();
        Triage.SetChoices(
            columns.Select(c => new ColumnChoice(c.Id, c.Name)),
            columns.SelectMany(c => c.Tasks.Where(t => !t.IsDeleted).OrderBy(t => t.Position)
                .Select(t => new TaskChoice(t.Id, t.Title, c.Name))));
    }

    private async Task ReloadQueueAsync()
    {
        await LoadBoardChoicesAsync().ConfigureAwait(true);
        var previous = Selected?.CandidateId;
        Candidates.Clear();
        if (_run is not null)
        {
            foreach (var candidate in await _service.GetQueueAsync(_run.Id).ConfigureAwait(true))
                Candidates.Add(new CandidateItemViewModel(candidate));
        }
        Select(Candidates.FirstOrDefault(c => c.CandidateId == previous) ?? Candidates.FirstOrDefault());
    }

    private void Select(CandidateItemViewModel? candidate)
    {
        Selected = candidate;
        Triage.Show(candidate, candidate is null ? 0 : Candidates.IndexOf(candidate), Candidates.Count);
        UpdateCounters();
    }

    /// <summary>候補キューで別の 1 件を選んだとき（ListBox の SelectedItem から）。</summary>
    partial void OnSelectedChanged(CandidateItemViewModel? value)
    {
        if (!ReferenceEquals(Triage.Selected, value))
            Triage.Show(value, value is null ? 0 : Candidates.IndexOf(value), Candidates.Count);
    }

    private void UpdateCounters()
    {
        var ingested = _run is { Status: MorningRunStatus.Ingested };
        HasNoCandidates = Candidates.Count == 0 && ingested;
        CanStart = _run is null || (_run.Status.IsTerminal() && Candidates.Count == 0);
        IsRunning = _run is not null && _run.Status.IsActive();
        IsFailed = _run is { Status: MorningRunStatus.Failed };
        CanControl = IsRunning;
        ProgressText = IsRunning ? string.Format(Strings.MorningTurnsFormat, _service.TurnCountOf(_run!.Id)) : "";
        // 「今日のプランはまだありません（前回: 9/5）」（仕様 §11）。取り込み済み(Ingested)なら
        // 候補が 0 件でもこの朝のプランは存在するので出さない（仕様 §4）。実行中も
        // 「実行中」表示と重ねて出さない。
        HasNoPlanYet = !IsRunning && _run is not { HasPlan: true };
        LastRunText = _run is null || !CanStart
            ? ""
            : string.Format(Strings.MorningLastRunFormat, _run.Date.ToString("M/d", CultureInfo.InvariantCulture));
        ErrorMessage = IsFailed ? _run!.ErrorMessage : ErrorMessage;
        LeftPanel = ingested && Candidates.Count > 0 ? Triage : null;
    }

    private void OnRunChanged(MorningRunChangedEventArgs e)
    {
        if (_run is not null && _run.Id != e.Run.RunId) return;
        WarningMessage = e.Warning ?? WarningMessage;
        if (_run is not null)
        {
            // 実行の状態遷移は Pending/Running → 終了状態の一方向で、終了状態から後戻りする遷移は
            // 無い(MorningService)。なので、こちらが既に終了状態を知っているのに違う状態の
            // スナップショットが届いたら、それは配送が追い越された古い通知でしかない。無視する
            // (終了状態のまま維持する)ことで、蘇ったように見せない。
            if (!_run.Status.IsTerminal())
            {
                // 複製のフィールドを更新するだけで、リポジトリが返したオブジェクトには一切触れない
                // (仕様どおり、追跡中のエンティティへ書き戻さない)。
                _run = _run with
                {
                    Status = e.Run.Status, ErrorMessage = e.Run.ErrorMessage,
                    HasPlan = e.Run.Status == MorningRunStatus.Ingested,
                };
            }
        }
        else if (e.Run.Status.IsActive())
        {
            // 開始直後の 1 通目より先にフックの行が届くことがある。実行を知らないまま捨てない。
            _run = RunState.From(e.Run);
        }
        UpdateCounters();
        if (e.CandidatesChanged) PendingLoad = RefreshAsync();
    }

    /// <summary>
    /// 候補が動いた後の読み直し。ここで投げた例外は誰も待っていない Task(PendingLoad はテスト
    /// 専用のハンドル)の中で握りつぶされてしまうので、ここで捕まえて警告バナーへ回す
    /// (MainWindow.OnLoaded が起動失敗をバナーへ回すのと同じ考え方)。
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            var run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
            _run = run is null ? null : RunState.From(run);
            await ReloadQueueAsync().ConfigureAwait(true);
            UpdateCounters();
        }
        catch (Exception ex)
        {
            WarningMessage = string.Format(CultureInfo.CurrentCulture, Strings.MorningRefreshFailedFormat, ex.Message);
        }
    }

    // ---------- 操作 ----------

    /// <summary>
    /// 何か操作を始める前にバナーを消す。古いエラー・警告は今回の操作の結果ではないので、
    /// 成功すれば消えているべきで、居座らせない（失敗・警告が出るならこの後で改めて立つ）。
    /// </summary>
    private void ClearBanners()
    {
        ErrorMessage = null;
        WarningMessage = null;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ClearBanners();
        var started = await _service.StartAsync().ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            ErrorMessage = started.Error;
            return;
        }
        _run = RunState.From(started.Value!);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var done = await _service.CompleteAsync(_run.Id).ConfigureAwait(true);
        if (!done.IsSuccess) ErrorMessage = done.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var stopped = await _service.StopTrackingAsync(_run.Id).ConfigureAwait(true);
        if (!stopped.IsSuccess) ErrorMessage = stopped.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Triage の 4 アクションが終わった後。片づいたら次の 1 件へ。失敗したらキューはそのままで理由だけ出す。
    /// 警告は結果から無条件に写す: 今回は無ければ null にして、前回の警告を居座らせない。
    /// バナーは操作の前に消す（ClearBanners と同じ意図。Triage は画面のバナーを知らない）。
    /// </summary>
    private async Task AfterDecisionAsync(Result result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        ErrorMessage = null;
        WarningMessage = result.Warnings.Count > 0 ? string.Join(" / ", result.Warnings) : null;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private void OpenJobFolder()
    {
        if (_run is { JobFolder.Length: > 0 } run) OpenPath(run.JobFolder);
    }

    [RelayCommand]
    private void DismissBanner()
    {
        ErrorMessage = null;
        WarningMessage = null;
    }
}

/// <summary>登録先の列の選択肢。</summary>
public sealed record ColumnChoice(int Id, string Name);
```

- [ ] **Step 7: 既存の VM テストを分割に追従させる**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` で、フォームとアクションの参照を `Triage` 経由にする:

```bash
sed -i -E 's/_vm\.(ColumnChoices|EditColumnId|EditTitle|EditDueDate|EditProjectName|PositionText|RegisterCommand|MergeCommand|PostponeCommand|RejectCommand|OpenLinkCommand)\b/_vm.Triage.\1/g' tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
```

そのうえで、統合の 2 テストを次に置き換える（`CanMerge` の意味が変わったため）:

```csharp
    [Fact]
    public async Task Merge_PreselectsTheSuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate(suggested: TriageAction.Merge));
        await _vm.LoadAsync();

        _vm.Selected!.IsMergeSuggested.Should().BeTrue("候補キューの『統合が推奨』バッジ");
        _vm.Triage.EditMergeTargetId.Should().Be(12, "推薦された統合先が盤面にあるので初期選択");
        await _vm.Triage.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:12");
    }

    [Fact]
    public async Task Merge_LetsThePersonChooseATarget_WhenNothingWasSuggested()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.Triage.CanMerge.Should().BeFalse("推薦が無ければ未選択から始まる");
        _vm.Triage.MergeTargets.Select(t => t.Id).Should().Equal(10, 11, 12, "完了列以外の未削除タスク");
        _vm.Triage.EditMergeTargetId = 11;
        await _vm.Triage.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:11");
    }
```

さらに、左パネルの切替のテストを追加:

```csharp
    [Fact]
    public async Task LeftPanel_IsTheTriagePanel_WhileCandidatesRemain()
    {
        _service.Current = IngestedRun();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.LeftPanel.Should().BeSameAs(_vm.Triage);
        _vm.Triage.Selected.Should().BeSameAs(_vm.Selected);
    }

    [Fact]
    public async Task LeftPanel_IsEmpty_BeforeTheFirstRun()
    {
        await _vm.LoadAsync();

        _vm.LeftPanel.Should().BeNull("実行前・実行中・失敗は上部バーが案内する");
    }
```

- [ ] **Step 8: XAML を左 ContentControl ＋ 右キューに組み替える**

`src/MoTask.App/Views/MorningPlanView.xaml` の `<Grid Margin="{StaticResource Gap.Top.4}">` から
`</Grid>` までを丸ごと次に置き換える（上部バーとバナーはそのまま）:

```xml
    <Grid Margin="{StaticResource Gap.Top.4}">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="360" />
        <ColumnDefinition Width="*" />
      </Grid.ColumnDefinitions>

      <!--
        左の固定パネル。候補が残っている間は仕分け、片づいたら「最初にやる1件」に、同じ場所が
        切り替わる（ワイヤー 4a / 4b）。別パネルの表示/非表示ではなく、LeftPanel の型で
        DataTemplate を選ぶ（仕様 §6）。実行前・実行中・失敗のときは null で、上部バーが案内する。
      -->
      <ContentControl Grid.Column="0" Content="{Binding LeftPanel}" VerticalAlignment="Top">
        <ContentControl.Resources>
          <DataTemplate DataType="{x:Type vm:TriagePanelViewModel}">
            <ScrollViewer VerticalScrollBarVisibility="Auto">
              <StackPanel>
                <TextBlock Text="{Binding HeadingText}" Style="{StaticResource Text.Label}" />
                <TextBlock Text="{Binding PositionText}" Style="{StaticResource Text.Caption}" />

                <StackPanel Orientation="Horizontal" Margin="{StaticResource Gap.Top.4}">
                  <Border Background="{StaticResource Brush.AccentSubtle}" Padding="{StaticResource Pad.Chip}" CornerRadius="3">
                    <TextBlock Text="{Binding Selected.Source}" Style="{StaticResource Text.Caption}" Foreground="{StaticResource Brush.Accent}" />
                  </Border>
                  <TextBlock Text="{Binding Selected.From}" Style="{StaticResource Text.Caption}"
                             VerticalAlignment="Center" Margin="{StaticResource Gap.Left.2}" />
                  <TextBlock Text="{Binding Selected.ReceivedText}" Style="{StaticResource Text.Caption}"
                             VerticalAlignment="Center" Margin="{StaticResource Gap.Left.2}" />
                </StackPanel>
                <TextBlock Text="{Binding Selected.Title}" Style="{StaticResource Text.Heading}" TextWrapping="Wrap"
                           Margin="{StaticResource Gap.Top.2}" />

                <TextBlock Text="{x:Static res:Strings.MorningEvidence}" Style="{StaticResource Text.Label}"
                           Margin="{StaticResource Gap.Top.4}" />
                <TextBlock Text="{Binding Selected.Evidence}" TextWrapping="Wrap" />
                <Button Content="{x:Static res:Strings.MorningOpenSource}" Style="{StaticResource Btn.Link}"
                        HorizontalAlignment="Left" Command="{Binding OpenLinkCommand}"
                        Visibility="{Binding Selected.HasLink, Converter={StaticResource BoolToVisibility}}" />

                <TextBlock Text="{x:Static res:Strings.MorningReasoning}" Style="{StaticResource Text.Label}"
                           Margin="{StaticResource Gap.Top.4}" />
                <TextBlock Text="{Binding Selected.Reasoning}" TextWrapping="Wrap" />

                <TextBlock Text="{x:Static res:Strings.FieldTitle}" Style="{StaticResource Text.Label}"
                           Margin="{StaticResource Gap.Top.4}" />
                <TextBox Text="{Binding EditTitle, UpdateSourceTrigger=PropertyChanged}" />

                <TextBlock Text="{x:Static res:Strings.DueDate}" Style="{StaticResource Text.Label}" />
                <DatePicker SelectedDate="{Binding EditDueDate}" />

                <TextBlock Text="{x:Static res:Strings.Project}" Style="{StaticResource Text.Label}" />
                <TextBox Text="{Binding EditProjectName, UpdateSourceTrigger=PropertyChanged}" />

                <TextBlock Text="{x:Static res:Strings.Column}" Style="{StaticResource Text.Label}" />
                <ComboBox ItemsSource="{Binding ColumnChoices}" DisplayMemberPath="Name"
                          SelectedValuePath="Id" SelectedValue="{Binding EditColumnId}" />

                <TextBlock Text="{x:Static res:Strings.MorningMergeTarget}" Style="{StaticResource Text.Label}" />
                <ComboBox ItemsSource="{Binding MergeTargets}" DisplayMemberPath="Display"
                          SelectedValuePath="Id" SelectedValue="{Binding EditMergeTargetId}" />

                <Button Content="{x:Static res:Strings.MorningRegister}" Style="{StaticResource Btn.Primary}"
                        Command="{Binding RegisterCommand}" Margin="{StaticResource Gap.Top.4}" HorizontalAlignment="Stretch" />
                <StackPanel Orientation="Horizontal" Margin="{StaticResource Gap.Top.2}">
                  <Button Content="{x:Static res:Strings.MorningMerge}" Style="{StaticResource Btn.Ghost}"
                          Command="{Binding MergeCommand}" />
                  <Button Content="{x:Static res:Strings.MorningPostpone}" Style="{StaticResource Btn.Ghost}"
                          Command="{Binding PostponeCommand}" Margin="{StaticResource Gap.Left.2}" />
                  <Button Content="{x:Static res:Strings.MorningReject}" Style="{StaticResource Btn.Ghost}"
                          Command="{Binding RejectCommand}" Margin="{StaticResource Gap.Left.2}" />
                </StackPanel>
                <TextBlock Text="{Binding KeyHint}" Style="{StaticResource Text.Hint}" Margin="{StaticResource Gap.Top.2}" />
              </StackPanel>
            </ScrollViewer>
          </DataTemplate>
        </ContentControl.Resources>
      </ContentControl>

      <!-- 右カラム。このタスクでは候補キューだけ。プランの 4 区分は Task 5 で足す。 -->
      <ScrollViewer Grid.Column="1" VerticalScrollBarVisibility="Auto" Margin="{StaticResource Gap.Left.4}">
        <StackPanel>
          <!--
            候補キュー。ListBox には暗黙スタイルが無い（Controls.xaml は Button / TextBox /
            ComboBox / DatePicker などを持つが ListBox は持たない）ので、既定のままだと
            白地・黒文字のまま暗い画面に浮く。ColumnView の CardList と同じく、背景と枠を
            自分で消し、項目の見た目もここで与える。
          -->
          <ListBox ItemsSource="{Binding Candidates}" SelectedItem="{Binding Selected}"
                   Background="Transparent" BorderThickness="0"
                   ScrollViewer.HorizontalScrollBarVisibility="Disabled" FocusVisualStyle="{x:Null}">
            <ListBox.ItemContainerStyle>
              <Style TargetType="ListBoxItem">
                <Setter Property="Foreground" Value="{StaticResource Brush.Text}" />
                <Setter Property="Margin" Value="{StaticResource Gap.Bottom.2}" />
                <Setter Property="Padding" Value="0" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                      <Border x:Name="Frame" Background="{StaticResource Brush.SurfaceRaised}"
                              BorderBrush="{StaticResource Brush.Divider}" BorderThickness="1"
                              Padding="{StaticResource Pad.Box}" CornerRadius="8" SnapsToDevicePixels="True">
                        <ContentPresenter />
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Frame" Property="Background" Value="{StaticResource Brush.SurfaceHover}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource Brush.Accent}" />
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ListBox.ItemContainerStyle>
            <ListBox.ItemTemplate>
              <DataTemplate>
                <StackPanel Margin="0,4">
                  <TextBlock Text="{Binding Source}" Style="{StaticResource Text.Caption}" />
                  <TextBlock Text="{Binding Title}" TextWrapping="Wrap" />
                </StackPanel>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
        </StackPanel>
      </ScrollViewer>
    </Grid>
```

`Btn.Link` / `Text.Heading` / `Text.Hint` / `Pad.Chip` / `Gap.Left.4` はすべて `Themes/Industry.xaml` / `Controls.xaml` に既にあるキー。

- [ ] **Step 9: 全テストが通り、警告ゼロでビルドできることを確認する**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true` → 0 エラー・0 警告（`MoTask.exe` 起動中なら出力コピーの MSB3021/MSB3027 だけは無視してよい。コンパイルエラーは不可）
Run: `dotnet test MoTask.sln -nologo -v q` → 全件 PASS（`TriagePanelViewModelTests` 8 件を含む）

- [ ] **Step 10: コミット**

```bash
git add src/MoTask.App/ViewModels/TriagePanelViewModel.cs src/MoTask.App/ViewModels/MorningPlanViewModel.cs src/MoTask.App/ViewModels/CandidateItemViewModel.cs src/MoTask.App/Views/MorningPlanView.xaml src/MoTask.App/Resources/Strings.resx src/MoTask.App/Resources/Strings.cs tests/MoTask.App.Tests/TriagePanelViewModelTests.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit -m "refactor(app): split the triage form into its own panel and let the user pick a merge target

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: プランの解決、最初にやる1件（左・状態2）、右カラムの4区分

仕様 §6・§7。`PlanJson` を `MorningPlanResolver` に通して右カラムに描き、候補が片づいたら左を
`FirstThingViewModel` に切り替える。仕分けの1件ごとに解決し直すので、登録した候補の行はその場で
実タスクに変わり、却下した行は消える。

**Files:**
- Create: `src/MoTask.App/ViewModels/FirstThingViewModel.cs`
- Create: `src/MoTask.App/ViewModels/PlanSectionViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/MorningPlanViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/CandidateItemViewModel.cs`
- Modify: `src/MoTask.App/Views/MorningPlanView.xaml`
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（追記・1件修正）

**Interfaces:**
- Consumes: Task 1 の `MorningPlanResolver` / `ResolvedPlan` / `PlanGroup` / `TaskRow` / `CandidateRow`、Task 2 の `GetCandidatesOfRunAsync`、Task 4 の `TriagePanelViewModel`
- Produces:
  - `FirstThingViewModel(Action<int> openOnBoard)`: `Update(ResolvedPlan)`、`Title` / `Reason` / `IsFallback` / `HasFirstThing` / `TaskId` / `Heading` / `EmptyText`、`OpenOnBoardCommand`
  - `PlanSectionViewModel(PlanGroupKey key, Action<PlanRowViewModel> open)`: `Update(PlanGroup)`、`Heading` / `CountText` / `IsEmpty` / `Rows`
  - `PlanRowViewModel(PlanRow row, Action<PlanRowViewModel> open)`: `Title` / `Caption` / `BadgeText` / `HasBadge` / `IsDone` / `TaskId` / `CandidateId`、`OpenCommand`
  - `MorningPlanViewModel`: `FirstThing` / `Sections`（4件固定）/ `IsTriaging` / `IsPlanReady` / `DateHeading` / `StatusLine` / `FirstThingHeadingText` / `PendingCount`、`event EventHandler<int>? NavigateToTask`
  - `CandidateItemViewModel.SuggestionText` / `Caption`

- [ ] **Step 1: 文言を足す**

`src/MoTask.App/Resources/Strings.resx` の `</root>` の直前に:

```xml
  <data name="MorningFirstThingHeading" xml:space="preserve"><value>01 ／ 最初にやる1件</value></data>
  <data name="MorningFirstThingProvisional" xml:space="preserve"><value>（暫定）</value></data>
  <data name="MorningNoFirstThing" xml:space="preserve"><value>最初にやる1件はありません</value></data>
  <data name="MorningOpenOnBoard" xml:space="preserve"><value>ボードで開く</value></data>
  <data name="MorningDateHeadingFormat" xml:space="preserve"><value>{0} の実行プラン</value></data>
  <data name="MorningTriagingFormat" xml:space="preserve"><value>候補 {0} 件を仕分け中 — 終わるとプランが確定します</value></data>
  <data name="MorningTriageDoneFormat" xml:space="preserve"><value>仕分け完了　候補 {0} 件 → 登録 {1}・統合 {2}・却下 {3}・あとで {4}</value></data>
  <data name="MorningQueueHeading" xml:space="preserve"><value>候補キュー</value></data>
  <data name="MorningPlanHeading" xml:space="preserve"><value>プラン</value></data>
  <data name="MorningGroupToday" xml:space="preserve"><value>02 ／ 今日中</value></data>
  <data name="MorningGroupIfTime" xml:space="preserve"><value>03 ／ 余裕があれば</value></data>
  <data name="MorningGroupAiReady" xml:space="preserve"><value>04 ／ AI 準備完了</value></data>
  <data name="MorningGroupWaiting" xml:space="preserve"><value>05 ／ 待ち</value></data>
  <data name="MorningGroupCountFormat" xml:space="preserve"><value>{0} 件</value></data>
  <data name="MorningGroupCountWithCandidatesFormat" xml:space="preserve"><value>{0} 件（+候補 {1} 件）</value></data>
  <data name="MorningGroupEmpty" xml:space="preserve"><value>なし</value></data>
  <data name="MorningRowNew" xml:space="preserve"><value>新規</value></data>
  <data name="MorningRowMerged" xml:space="preserve"><value>統合</value></data>
  <data name="MorningRowPendingCandidate" xml:space="preserve"><value>仕分け待ち</value></data>
  <data name="MorningSuggestRegister" xml:space="preserve"><value>登録が推奨</value></data>
  <data name="MorningSuggestMerge" xml:space="preserve"><value>統合が推奨</value></data>
  <data name="MorningSuggestLater" xml:space="preserve"><value>あとでが推奨</value></data>
  <data name="MorningSuggestReject" xml:space="preserve"><value>却下が推奨</value></data>
```

`src/MoTask.App/Resources/Strings.cs` の `MorningKeyHint` の下に、上の 23 キーと同名のプロパティを同じ形で足す:

```csharp
    public static string MorningFirstThingHeading => Get(nameof(MorningFirstThingHeading));
    public static string MorningFirstThingProvisional => Get(nameof(MorningFirstThingProvisional));
    public static string MorningNoFirstThing => Get(nameof(MorningNoFirstThing));
    public static string MorningOpenOnBoard => Get(nameof(MorningOpenOnBoard));
    public static string MorningDateHeadingFormat => Get(nameof(MorningDateHeadingFormat));
    public static string MorningTriagingFormat => Get(nameof(MorningTriagingFormat));
    public static string MorningTriageDoneFormat => Get(nameof(MorningTriageDoneFormat));
    public static string MorningQueueHeading => Get(nameof(MorningQueueHeading));
    public static string MorningPlanHeading => Get(nameof(MorningPlanHeading));
    public static string MorningGroupToday => Get(nameof(MorningGroupToday));
    public static string MorningGroupIfTime => Get(nameof(MorningGroupIfTime));
    public static string MorningGroupAiReady => Get(nameof(MorningGroupAiReady));
    public static string MorningGroupWaiting => Get(nameof(MorningGroupWaiting));
    public static string MorningGroupCountFormat => Get(nameof(MorningGroupCountFormat));
    public static string MorningGroupCountWithCandidatesFormat => Get(nameof(MorningGroupCountWithCandidatesFormat));
    public static string MorningGroupEmpty => Get(nameof(MorningGroupEmpty));
    public static string MorningRowNew => Get(nameof(MorningRowNew));
    public static string MorningRowMerged => Get(nameof(MorningRowMerged));
    public static string MorningRowPendingCandidate => Get(nameof(MorningRowPendingCandidate));
    public static string MorningSuggestRegister => Get(nameof(MorningSuggestRegister));
    public static string MorningSuggestMerge => Get(nameof(MorningSuggestMerge));
    public static string MorningSuggestLater => Get(nameof(MorningSuggestLater));
    public static string MorningSuggestReject => Get(nameof(MorningSuggestReject));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` に追加。まず `IngestedRun()` の下にプラン付きのヘルパー:

```csharp
    /// <summary>TestBoards.Sample のタスク 10 / 12 と、候補 outlook:001 を指すプラン。</summary>
    private static MorningRun IngestedRunWithPlan(string firstThing = "{\"taskId\":10,\"reason\":\"期限が一番近い\"}",
        string today = "{\"taskId\":10},{\"externalId\":\"outlook:001\"},{\"taskId\":12}")
    {
        var run = IngestedRun();
        run.PlanJson = $"{{\"date\":\"2026-09-07\",\"firstThing\":{firstThing},\"groups\":[{{\"key\":\"today\",\"items\":[{today}]}}]}}";
        return run;
    }
```

`BoardViewModelTests` と同じく `_boards.GetProjectsAsync()` も返すようにコンストラクタへ 1 行足す:

```csharp
        _boards.GetProjectsAsync().Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
```

テスト本体:

```csharp
    [Fact]
    public async Task Load_WithAnIngestedRunAndNoQueue_ShowsTheFirstThingAndTheGroups()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();

        _vm.IsPlanReady.Should().BeTrue();
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing, "候補が無ければ左は『最初にやる1件』（ワイヤー 4b）");
        _vm.FirstThing.HasFirstThing.Should().BeTrue();
        _vm.FirstThing.Title.Should().Be("請求先情報を更新する");
        _vm.FirstThing.Reason.Should().Be("期限が一番近い");
        _vm.FirstThing.IsFallback.Should().BeFalse();
        _vm.Sections.Select(s => s.Key).Should().Equal(
            PlanGroupKey.Today, PlanGroupKey.IfTime, PlanGroupKey.AiReady, PlanGroupKey.Waiting);
        _vm.Sections[0].Rows.Select(r => r.TaskId).Should().Equal(10, 12, "候補 outlook:001 は取り込まれていないので落ちる");
        _vm.Sections[0].Rows[0].Caption.Should().Be("顧客A対応 / " + string.Format(Strings.CardDueFormat, 9, 8));
        _vm.Sections[0].CountText.Should().Be(string.Format(Strings.MorningGroupCountFormat, 2));
        _vm.Sections[1].IsEmpty.Should().BeTrue();
        _vm.StatusLine.Should().Be(string.Format(Strings.MorningTriageDoneFormat, 0, 0, 0, 0, 0));
        _vm.DateHeading.Should().Be(string.Format(Strings.MorningDateHeadingFormat, "9月7日（月）"));
    }

    [Fact]
    public async Task Load_WithCandidates_ShowsTheProvisionalPlan()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.IsTriaging.Should().BeTrue();
        _vm.StatusLine.Should().Be(string.Format(Strings.MorningTriagingFormat, 1));
        _vm.FirstThingHeadingText.Should().Be(Strings.MorningFirstThingHeading + Strings.MorningFirstThingProvisional);
        var row = _vm.Sections[0].Rows.Should().HaveCount(3).And.Subject.ElementAt(1);
        row.CandidateId.Should().Be(1);
        row.BadgeText.Should().Be(Strings.MorningRowPendingCandidate);
        _vm.Sections[0].CountText.Should().Be(string.Format(Strings.MorningGroupCountWithCandidatesFormat, 2, 1));
        _vm.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Register_TurnsTheCandidateRowIntoATaskRow_AndSwitchesTheLeftPanel()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.RegisterResult = Result.Ok(new TaskItem { Id = 11 });
        await _vm.LoadAsync();

        await _vm.Triage.RegisterCommand.ExecuteAsync(null);

        var row = _vm.Sections[0].Rows.Should().HaveCount(3).And.Subject.ElementAt(1);
        row.TaskId.Should().Be(11, "登録した候補の行はその場で実タスクに解決する（親仕様 §8）");
        row.BadgeText.Should().Be(Strings.MorningRowNew);
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing, "最後の 1 件を片づけたので切り替わる");
        _vm.PendingCount.Should().Be(0);
        _vm.HasNoCandidates.Should().BeFalse("候補はあった。『候補はありませんでした』は 0 件の朝だけ");
    }

    [Fact]
    public async Task Reject_RemovesTheRowFromThePlan()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.Sections[0].Rows.Select(r => r.TaskId).Should().Equal(10, 12);
        _vm.StatusLine.Should().Be(string.Format(Strings.MorningTriageDoneFormat, 1, 0, 0, 1, 0));
    }

    [Fact]
    public async Task FirstThing_FallsBackToTheFirstTodayRow_WhenItPointedAtARejectedCandidate()
    {
        _service.Current = IngestedRunWithPlan(
            firstThing: "{\"externalId\":\"outlook:001\",\"reason\":\"今朝の依頼\"}",
            today: "{\"externalId\":\"outlook:001\"},{\"taskId\":12}");
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.FirstThing.Title.Should().Be("週次レポートを作成する");
        _vm.FirstThing.IsFallback.Should().BeTrue();
        _vm.FirstThingHeadingText.Should().Be(Strings.MorningFirstThingHeading + Strings.MorningFirstThingProvisional,
            "繰り下げたら状態 2 でも（暫定）を付ける");
    }

    [Fact]
    public async Task FirstThing_SaysSo_WhenNothingIsLeft()
    {
        _service.Current = IngestedRunWithPlan(firstThing: "{\"taskId\":999,\"reason\":\"消えた\"}", today: "");
        await _vm.LoadAsync();

        _vm.FirstThing.HasFirstThing.Should().BeFalse();
        _vm.FirstThing.OpenOnBoardCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task RunChanged_ToIngestedWithoutCandidates_StillLoadsThePlan()
    {
        await _vm.LoadAsync();
        var run = IngestedRunWithPlan();
        _service.Current = run;

        _service.Raise(new MorningRun { Id = run.Id, Date = run.Date, Status = MorningRunStatus.Running });
        _service.Raise(run, candidates: false);
        await _vm.PendingLoad;

        _vm.Sections[0].Rows.Should().NotBeEmpty("候補 0 件の朝でもプランはある（親仕様 §8）");
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing);
    }

    [Fact]
    public async Task OpeningATaskRow_AsksTheWindowToShowItOnTheBoard()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();
        var navigated = new List<int>();
        _vm.NavigateToTask += (_, id) => navigated.Add(id);

        _vm.Sections[0].Rows[1].OpenCommand.Execute(null);
        _vm.FirstThing.OpenOnBoardCommand.Execute(null);

        navigated.Should().Equal(12, 10);
    }

    [Fact]
    public async Task OpeningACandidateRow_SelectsThatCandidate()
    {
        _service.Current = IngestedRunWithPlan(today: "{\"externalId\":\"outlook:002\"},{\"externalId\":\"outlook:001\"}");
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.Selected!.CandidateId.Should().Be(1);

        _vm.Sections[0].Rows[0].OpenCommand.Execute(null);

        _vm.Selected!.CandidateId.Should().Be(2);
        _vm.Triage.Selected!.CandidateId.Should().Be(2);
    }

    [Fact]
    public async Task Load_SurvivesABoardFailure_WithAnEmptyPlanAndAWarning()
    {
        _boards.GetBoardAsync().Returns(Task.FromResult(Result.Fail<Board>("接続できません")));
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();

        _vm.WarningMessage.Should().Be("接続できません");
        _vm.Sections[0].Rows.Should().ContainSingle().Which.CandidateId.Should().Be(1, "候補行は盤面が無くても解決できる");
        _vm.LeftPanel.Should().BeSameAs(_vm.Triage, "仕分けは盤面が無くても動く（仕様 §8）");
    }

    [Fact]
    public void CandidateItem_MapsTheSuggestionToABadge()
    {
        new CandidateItemViewModel(Candidate(suggested: TriageAction.Merge)).SuggestionText.Should().Be(Strings.MorningSuggestMerge);
        new CandidateItemViewModel(Candidate(suggested: TriageAction.Reject)).SuggestionText.Should().Be(Strings.MorningSuggestReject);
    }
```

既存の `PostponeAndReject_TakeTheCandidateOutOfTheQueue` の最後の行

```csharp
        _vm.HasNoCandidates.Should().BeTrue();
```

を次に置き換える（意味が変わる: 「候補はありませんでした」は候補が 1 件も無かった朝だけ）:

```csharp
        _vm.IsPlanReady.Should().BeTrue();
        _vm.HasNoCandidates.Should().BeFalse("候補はあった。案内文は 0 件の朝だけ");
```

`using MoTask.Core.Morning;` をファイル先頭に足す。

- [ ] **Step 3: 失敗を確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~MorningPlanViewModelTests" -nologo -v q`
Expected: ビルドエラー（`FirstThing` / `Sections` / `IsTriaging` が無い）

- [ ] **Step 4: FirstThingViewModel を書く**

`src/MoTask.App/ViewModels/FirstThingViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Morning;

namespace MoTask.App.ViewModels;

/// <summary>
/// 左パネル・状態 2「最初にやる1件」（仕様 §6、ワイヤー 4b）。plan.json にあるのは taskId / externalId と
/// reason だけなので、出すのもタイトル・選定理由・「ボードで開く」の 3 つだけ（仕様 §3）。
/// </summary>
public sealed partial class FirstThingViewModel : ObservableObject
{
    private readonly Action<int> _openOnBoard;

    public FirstThingViewModel(Action<int> openOnBoard)
    {
        _openOnBoard = openOnBoard;
    }

    public string Heading => Strings.MorningFirstThingHeading;
    public string EmptyText => Strings.MorningNoFirstThing;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _reason = "";
    /// <summary>firstThing が解決できず today の先頭に繰り下げた（仕様 §4）。見出しに「（暫定）」を付ける。</summary>
    [ObservableProperty] private bool _isFallback;
    [ObservableProperty] private bool _hasFirstThing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenOnBoard))]
    [NotifyCanExecuteChangedFor(nameof(OpenOnBoardCommand))]
    private int? _taskId;

    /// <summary>候補行（仕分け待ち）が最初の 1 件になっている間はボードに無いので開けない。</summary>
    public bool CanOpenOnBoard => TaskId is not null;

    public void Update(ResolvedPlan plan)
    {
        HasFirstThing = plan.FirstThing is not null;
        Title = plan.FirstThing?.Title ?? "";
        Reason = plan.FirstThingReason;
        IsFallback = plan.FirstThingIsFallback && plan.FirstThing is not null;
        TaskId = (plan.FirstThing as TaskRow)?.TaskId;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOnBoard))]
    private void OpenOnBoard()
    {
        if (TaskId is int id) _openOnBoard(id);
    }
}
```

- [ ] **Step 5: PlanSectionViewModel / PlanRowViewModel を書く**

`src/MoTask.App/ViewModels/PlanSectionViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Morning;

namespace MoTask.App.ViewModels;

/// <summary>右カラムの区分 1 つ（仕様 §7）。4 つを画面が固定で持ち、Update で中身だけ差し替える。</summary>
public sealed partial class PlanSectionViewModel : ObservableObject
{
    private readonly Action<PlanRowViewModel> _open;

    public PlanSectionViewModel(PlanGroupKey key, Action<PlanRowViewModel> open)
    {
        Key = key;
        _open = open;
    }

    public PlanGroupKey Key { get; }

    public string Heading => Key switch
    {
        PlanGroupKey.Today => Strings.MorningGroupToday,
        PlanGroupKey.IfTime => Strings.MorningGroupIfTime,
        PlanGroupKey.AiReady => Strings.MorningGroupAiReady,
        _ => Strings.MorningGroupWaiting,
    };

    public string EmptyText => Strings.MorningGroupEmpty;
    public ObservableCollection<PlanRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty = true;

    public void Update(PlanGroup group)
    {
        Rows.Clear();
        foreach (var row in group.Rows) Rows.Add(new PlanRowViewModel(row, _open));
        IsEmpty = Rows.Count == 0;
        CountText = group.CandidateCount > 0
            ? string.Format(Strings.MorningGroupCountWithCandidatesFormat, group.TaskCount, group.CandidateCount)
            : string.Format(Strings.MorningGroupCountFormat, group.TaskCount);
    }
}

/// <summary>プランの 1 行。タスク行はクリックでボードへ、候補行はクリックで候補キューの選択になる。</summary>
public sealed partial class PlanRowViewModel
{
    private readonly Action<PlanRowViewModel> _open;

    public PlanRowViewModel(PlanRow row, Action<PlanRowViewModel> open)
    {
        Row = row;
        _open = open;
    }

    public PlanRow Row { get; }
    public string Title => Row.Title;
    public int? TaskId => (Row as TaskRow)?.TaskId;
    public int? CandidateId => (Row as CandidateRow)?.CandidateId;
    public bool IsDone => Row is TaskRow { IsDone: true };

    /// <summary>プロジェクト／期限の小さな 1 行。候補行はソースも前に付ける。</summary>
    public string Caption => Row switch
    {
        TaskRow task => Join(task.ProjectName, Due(task.DueDate)),
        CandidateRow candidate => Join(candidate.Source, candidate.SuggestedProject, Due(candidate.SuggestedDueDate)),
        _ => "",
    };

    public string? BadgeText => Row switch
    {
        TaskRow { Origin: TaskRowOrigin.RegisteredThisMorning } => Strings.MorningRowNew,
        TaskRow { Origin: TaskRowOrigin.MergedThisMorning } => Strings.MorningRowMerged,
        CandidateRow => Strings.MorningRowPendingCandidate,
        _ => null,
    };

    public bool HasBadge => BadgeText is not null;

    [RelayCommand]
    private void Open() => _open(this);

    private static string Due(DateOnly? date)
        => date is DateOnly d ? string.Format(CultureInfo.CurrentCulture, Strings.CardDueFormat, d.Month, d.Day) : "";

    private static string Join(params string?[] parts)
        => string.Join(" / ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
```

- [ ] **Step 6: CandidateItemViewModel に推奨バッジと 1 行の説明を足す**

`src/MoTask.App/ViewModels/CandidateItemViewModel.cs` の `DueText` の下に:

```csharp
    /// <summary>候補キューの推奨バッジ（仕様 §7）。SuggestedAction を文言に写すだけ。</summary>
    public string SuggestionText => _candidate.SuggestedAction switch
    {
        TriageAction.Merge => Strings.MorningSuggestMerge,
        TriageAction.Later => Strings.MorningSuggestLater,
        TriageAction.Reject => Strings.MorningSuggestReject,
        _ => Strings.MorningSuggestRegister,
    };

    /// <summary>差出人／期限の小さな 1 行。</summary>
    public string Caption => string.Join(" / ", new[] { From, DueText }.Where(s => s.Length > 0));
```

- [ ] **Step 7: MorningPlanViewModel にプランの解決を足す**

`src/MoTask.App/ViewModels/MorningPlanViewModel.cs` を次のとおり変更する。

(a) `using MoTask.Core.Morning;` を足す。

(b) `RunState` に `PlanJson` を足す（スナップショットには無いので空。取り込み後は `RefreshAsync` が実体から読み直す）:

```csharp
    private sealed record RunState(
        int Id, DateOnly Date, MorningRunStatus Status, string? ErrorMessage, string JobFolder, bool HasPlan, string PlanJson)
    {
        public static RunState From(MorningRun run) => new(
            run.Id, run.Date, run.Status, run.ErrorMessage, run.JobFolder,
            run.Status == MorningRunStatus.Ingested, run.PlanJson);

        public static RunState From(MorningRunSnapshot snapshot) => new(
            snapshot.RunId, snapshot.Date, snapshot.Status, snapshot.ErrorMessage, snapshot.JobFolder,
            snapshot.Status == MorningRunStatus.Ingested, "");
    }
```

(c) フィールドとプロパティ。`Triage` の下に:

```csharp
    /// <summary>左パネル・状態 2。</summary>
    public FirstThingViewModel FirstThing { get; }

    /// <summary>右カラムの 4 区分。PlanGroupKey の順で固定。</summary>
    public IReadOnlyList<PlanSectionViewModel> Sections { get; }

    /// <summary>ボードへ飛ぶ（「ボードで開く」とタスク行のクリック）。MainWindow が購読する。</summary>
    public event EventHandler<int>? NavigateToTask;

    private Board? _board;
    private IReadOnlyList<Project> _projects = Array.Empty<Project>();
    private TriageSummary _summary = TriageSummary.None;

    [ObservableProperty] private bool _isTriaging;
    [ObservableProperty] private bool _isPlanReady;
    [ObservableProperty] private string _dateHeading = "";
    [ObservableProperty] private string _statusLine = "";
    /// <summary>右カラムの「01 ／ 最初にやる1件」。仕分け中と繰り下げは「（暫定）」を付ける。</summary>
    [ObservableProperty] private string _firstThingHeadingText = "";
    /// <summary>タブのバッジ。候補キューの件数。</summary>
    [ObservableProperty] private int _pendingCount;
```

コンストラクタの `Triage = ...` の下に:

```csharp
        FirstThing = new FirstThingViewModel(id => NavigateToTask?.Invoke(this, id));
        Sections = Enum.GetValues<PlanGroupKey>().Select(key => new PlanSectionViewModel(key, OpenRow)).ToList();
```

(d) `LoadBoardChoicesAsync` を `LoadBoardAsync` に改名し、盤面とプロジェクトを覚える。失敗は警告バナーへ（仕様 §8）:

```csharp
    /// <summary>
    /// 盤面とプロジェクトを読み、登録先の列（完了以外）と統合先のタスク（完了列と論理削除済み以外）を
    /// Triage に渡す。盤面の取得に失敗したら理由を警告に出し、盤面なしで続ける（候補の仕分けは
    /// 盤面が無くても動く・仕様 §8）。
    /// </summary>
    private async Task LoadBoardAsync()
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        if (!board.IsSuccess)
        {
            _board = null;
            WarningMessage = board.Error;
            Triage.SetChoices(Array.Empty<ColumnChoice>(), Array.Empty<TaskChoice>());
            return;
        }
        _board = board.Value!;
        _projects = await _boardService.GetProjectsAsync().ConfigureAwait(true);
        var columns = _board.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order).ToList();
        Triage.SetChoices(
            columns.Select(c => new ColumnChoice(c.Id, c.Name)),
            columns.SelectMany(c => c.Tasks.Where(t => !t.IsDeleted).OrderBy(t => t.Position)
                .Select(t => new TaskChoice(t.Id, t.Title, c.Name))));
    }

    private string? ProjectName(int? projectId)
        => projectId is int id ? _projects.FirstOrDefault(p => p.Id == id)?.Name : null;
```

(e) `ReloadQueueAsync` でキューの後にプランを解決する:

```csharp
    private async Task ReloadQueueAsync()
    {
        await LoadBoardAsync().ConfigureAwait(true);
        var previous = Selected?.CandidateId;
        Candidates.Clear();
        if (_run is not null)
        {
            foreach (var candidate in await _service.GetQueueAsync(_run.Id).ConfigureAwait(true))
                Candidates.Add(new CandidateItemViewModel(candidate));
        }
        await ResolvePlanAsync().ConfigureAwait(true);
        Select(Candidates.FirstOrDefault(c => c.CandidateId == previous) ?? Candidates.FirstOrDefault());
    }

    /// <summary>
    /// PlanJson を行に解決する（仕様 §4・§6）。仕分けの 1 件ごとに呼ばれるので、登録した候補の行は
    /// その場で実タスクに変わり、却下した行は消える。取り込み前は空のプラン。
    /// </summary>
    private async Task ResolvePlanAsync()
    {
        ResolvedPlan plan;
        if (_run is { Status: MorningRunStatus.Ingested } run)
        {
            var candidates = await _service.GetCandidatesOfRunAsync(run.Id).ConfigureAwait(true);
            plan = MorningPlanResolver.Resolve(run.PlanJson, candidates, _board, ProjectName);
        }
        else
        {
            plan = ResolvedPlan.Empty(TriageSummary.None);
        }
        _summary = plan.Summary;
        FirstThing.Update(plan);
        for (var i = 0; i < Sections.Count; i++) Sections[i].Update(plan.Groups[i]);
    }

    /// <summary>タスク行はボードへ、候補行は候補キューの選択へ。</summary>
    private void OpenRow(PlanRowViewModel row)
    {
        if (row.TaskId is int taskId) NavigateToTask?.Invoke(this, taskId);
        else if (row.CandidateId is int candidateId)
            Select(Candidates.FirstOrDefault(c => c.CandidateId == candidateId) ?? Selected);
    }
```

(f) `UpdateCounters` の末尾（`LeftPanel = ...` の行）を次に置き換える:

```csharp
        HasNoCandidates = ingested && _summary.Total == 0 && Candidates.Count == 0;
        IsTriaging = ingested && Candidates.Count > 0;
        IsPlanReady = ingested && Candidates.Count == 0;
        LeftPanel = IsTriaging ? Triage : IsPlanReady ? FirstThing : null;
        PendingCount = Candidates.Count;
        DateHeading = _run is null
            ? ""
            : string.Format(Strings.MorningDateHeadingFormat, _run.Date.ToString("M月d日（ddd）", new CultureInfo("ja-JP")));
        StatusLine = IsTriaging
            ? string.Format(Strings.MorningTriagingFormat, Candidates.Count)
            : IsPlanReady
                ? string.Format(Strings.MorningTriageDoneFormat,
                    _summary.Total, _summary.Registered, _summary.Merged, _summary.Rejected, _summary.Later)
                : "";
        FirstThingHeadingText = Strings.MorningFirstThingHeading
            + (IsTriaging || FirstThing.IsFallback ? Strings.MorningFirstThingProvisional : "");
```

`UpdateCounters` の先頭にあった `HasNoCandidates = Candidates.Count == 0 && ingested;` は削除する（上で置き換わる）。

(g) `OnRunChanged` の最後の 1 行を、取り込み完了でも読み直すようにする（候補 0 件の朝は `CandidatesChanged` が偽で来る）:

```csharp
        var wasTerminal = _run is not null && _run.Status.IsTerminal();
```

を `if (_run is not null)` ブロックの**前**に置き、末尾の

```csharp
        if (e.CandidatesChanged) PendingLoad = RefreshAsync();
```

を

```csharp
        var becameIngested = !wasTerminal && e.Run.Status == MorningRunStatus.Ingested;
        if (e.CandidatesChanged || becameIngested) PendingLoad = RefreshAsync();
```

に置き換える。

- [ ] **Step 8: XAML に左の状態 2 と右カラムを足す**

`src/MoTask.App/Views/MorningPlanView.xaml`。

(a) 左 `ContentControl.Resources` に 2 つ目の `DataTemplate` を足す（`TriagePanelViewModel` のテンプレートの下）:

```xml
          <DataTemplate DataType="{x:Type vm:FirstThingViewModel}">
            <StackPanel>
              <TextBlock Text="{Binding Heading}" Style="{StaticResource Text.Label}" />
              <TextBlock Text="{Binding EmptyText}" Style="{StaticResource Text.Caption}" Margin="{StaticResource Gap.Top.4}"
                         Visibility="{Binding HasFirstThing, Converter={StaticResource BoolToVisibilityInverse}}" />
              <StackPanel Visibility="{Binding HasFirstThing, Converter={StaticResource BoolToVisibility}}">
                <TextBlock Text="{Binding Title}" Style="{StaticResource Text.Heading}" TextWrapping="Wrap"
                           Margin="{StaticResource Gap.Top.4}" />
                <TextBlock Text="{Binding Reason}" TextWrapping="Wrap" Margin="{StaticResource Gap.Top.2}"
                           Visibility="{Binding Reason, Converter={StaticResource NullToVisibility}}" />
                <Button Content="{x:Static res:Strings.MorningOpenOnBoard}" Style="{StaticResource Btn.Primary}"
                        Command="{Binding OpenOnBoardCommand}" Margin="{StaticResource Gap.Top.4}" HorizontalAlignment="Left" />
              </StackPanel>
            </StackPanel>
          </DataTemplate>
```

`NullToVisibility` は null と空文字の両方を Collapsed にする（`NullToVisibilityConverter.cs`）ので、`Reason` が空なら行ごと消える。

(b) 右カラムの `<StackPanel>`（`ScrollViewer` の中）を次に置き換える。候補キューの `ListBox` は Task 4 のものをそのまま使い、`Visibility` と `ItemTemplate` だけ変える:

```xml
        <StackPanel Visibility="{Binding HasNoPlanYet, Converter={StaticResource BoolToVisibilityInverse}}">
          <TextBlock Text="{Binding DateHeading}" Style="{StaticResource Text.Section}" />
          <TextBlock Text="{Binding StatusLine}" Style="{StaticResource Text.Caption}" Margin="{StaticResource Gap.Top.1}" />

          <!-- 候補キュー（状態 1 のみ） -->
          <StackPanel Visibility="{Binding IsTriaging, Converter={StaticResource BoolToVisibility}}"
                      Margin="{StaticResource Gap.Top.4}">
            <TextBlock Text="{x:Static res:Strings.MorningQueueHeading}" Style="{StaticResource Text.Label}" />
            <ListBox ItemsSource="{Binding Candidates}" SelectedItem="{Binding Selected}"
                     Background="Transparent" BorderThickness="0" Margin="{StaticResource Gap.Top.2}"
                     ScrollViewer.HorizontalScrollBarVisibility="Disabled" FocusVisualStyle="{x:Null}">
              <ListBox.ItemContainerStyle>
                <!-- Task 4 の ItemContainerStyle をそのまま -->
              </ListBox.ItemContainerStyle>
              <ListBox.ItemTemplate>
                <DataTemplate>
                  <DockPanel Margin="0,2">
                    <Border DockPanel.Dock="Right" Background="{StaticResource Brush.AccentSubtle}" Padding="{StaticResource Pad.Chip}"
                            CornerRadius="3" VerticalAlignment="Top">
                      <TextBlock Text="{Binding SuggestionText}" Style="{StaticResource Text.Caption}" Foreground="{StaticResource Brush.Accent}" />
                    </Border>
                    <StackPanel>
                      <TextBlock Text="{Binding Source}" Style="{StaticResource Text.Caption}" />
                      <TextBlock Text="{Binding Title}" TextWrapping="Wrap" />
                      <TextBlock Text="{Binding Caption}" Style="{StaticResource Text.Caption}" />
                    </StackPanel>
                  </DockPanel>
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
          </StackPanel>
          <TextBlock Text="{x:Static res:Strings.MorningNoCandidates}" Style="{StaticResource Text.Caption}"
                     Margin="{StaticResource Gap.Top.4}"
                     Visibility="{Binding HasNoCandidates, Converter={StaticResource BoolToVisibility}}" />

          <!-- プラン: 01 最初にやる1件 ＋ 02〜05 の区分 -->
          <TextBlock Text="{x:Static res:Strings.MorningPlanHeading}" Style="{StaticResource Text.Label}"
                     Margin="{StaticResource Gap.Section}" />
          <Border Background="{StaticResource Brush.SurfaceRaised}" BorderBrush="{StaticResource Brush.Divider}"
                  BorderThickness="1" CornerRadius="8" Padding="{StaticResource Pad.Box}" Margin="{StaticResource Gap.Top.2}">
            <StackPanel DataContext="{Binding}">
              <TextBlock Text="{Binding FirstThingHeadingText}" Style="{StaticResource Text.Caption}" />
              <TextBlock Text="{Binding FirstThing.Title}" FontWeight="Medium" TextWrapping="Wrap"
                         Visibility="{Binding FirstThing.HasFirstThing, Converter={StaticResource BoolToVisibility}}" />
              <TextBlock Text="{Binding FirstThing.EmptyText}" Style="{StaticResource Text.Caption}"
                         Visibility="{Binding FirstThing.HasFirstThing, Converter={StaticResource BoolToVisibilityInverse}}" />
            </StackPanel>
          </Border>

          <ItemsControl ItemsSource="{Binding Sections}" Margin="{StaticResource Gap.Top.2}">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <StackPanel Margin="{StaticResource Gap.Top.4}">
                  <DockPanel>
                    <TextBlock DockPanel.Dock="Right" Text="{Binding CountText}" Style="{StaticResource Text.Caption}" />
                    <TextBlock Text="{Binding Heading}" Style="{StaticResource Text.Label}" />
                  </DockPanel>
                  <TextBlock Text="{Binding EmptyText}" Style="{StaticResource Text.Caption}"
                             Visibility="{Binding IsEmpty, Converter={StaticResource BoolToVisibility}}" />
                  <ItemsControl ItemsSource="{Binding Rows}">
                    <ItemsControl.ItemTemplate>
                      <DataTemplate>
                        <Button Style="{StaticResource Btn.Ghost}" Command="{Binding OpenCommand}"
                                HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"
                                Padding="{StaticResource Pad.Box}" Margin="{StaticResource Gap.Top.1}">
                          <DockPanel>
                            <Border DockPanel.Dock="Right" Background="{StaticResource Brush.AccentSubtle}"
                                    Padding="{StaticResource Pad.Chip}" CornerRadius="3" VerticalAlignment="Top"
                                    Visibility="{Binding HasBadge, Converter={StaticResource BoolToVisibility}}">
                              <TextBlock Text="{Binding BadgeText}" Style="{StaticResource Text.Caption}"
                                         Foreground="{StaticResource Brush.Accent}" />
                            </Border>
                            <StackPanel>
                              <TextBlock Text="{Binding Title}" TextWrapping="Wrap">
                                <TextBlock.Style>
                                  <Style TargetType="TextBlock">
                                    <Setter Property="Foreground" Value="{StaticResource Brush.Text}" />
                                    <Style.Triggers>
                                      <DataTrigger Binding="{Binding IsDone}" Value="True">
                                        <Setter Property="TextDecorations" Value="Strikethrough" />
                                        <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
                                      </DataTrigger>
                                    </Style.Triggers>
                                  </Style>
                                </TextBlock.Style>
                              </TextBlock>
                              <TextBlock Text="{Binding Caption}" Style="{StaticResource Text.Caption}"
                                         Visibility="{Binding Caption, Converter={StaticResource NullToVisibility}}" />
                            </StackPanel>
                          </DockPanel>
                        </Button>
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
                </StackPanel>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </StackPanel>
```

`Text.Section` / `Gap.Section` / `Gap.Top.1` / `Brush.TextMuted` は既存キー。`ListBox.ItemContainerStyle` の中身は Task 4 で書いたものをコピーする（コメントのままにしない）。

- [ ] **Step 9: 全テストと警告ゼロビルドを確認する**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 10: 実機で見た目を確認する**

Run: `dotnet run --project src/MoTask.App`（起動中の MoTask.exe があれば先に閉じる）
「朝の実行プラン」タブを開き、実行が無ければ「今日のプランはまだありません」と開始ボタンだけが出て、
右カラムが空であること。例外なく閉じられること。

- [ ] **Step 11: コミット**

```bash
git add src/MoTask.App/ViewModels/FirstThingViewModel.cs src/MoTask.App/ViewModels/PlanSectionViewModel.cs src/MoTask.App/ViewModels/MorningPlanViewModel.cs src/MoTask.App/ViewModels/CandidateItemViewModel.cs src/MoTask.App/Views/MorningPlanView.xaml src/MoTask.App/Resources/Strings.resx src/MoTask.App/Resources/Strings.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit -m "feat(app): show the resolved plan and switch the left panel to the first thing

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: 一括操作の画面（推奨をまとめて適用・すべて後で）

仕様 §6「一括操作」。「推奨をまとめて適用」は確認ダイアログを 1 枚挟む（複数件が一度に動き、
統合はタスク本文を書き換える）。「すべて後で」は取り消しが容易なので確認なし。

**Files:**
- Modify: `src/MoTask.App/ViewModels/MorningPlanViewModel.cs`
- Modify: `src/MoTask.App/Views/MorningPlanView.xaml`
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（追記）

**Interfaces:**
- Consumes: Task 3 の `ApplySuggestionsAsync` / `PostponeAllAsync` / `BulkOutcome`、Task 5 の `IsTriaging`
- Produces: `MorningPlanViewModel.Confirm`（`Func<string, bool>`）、`ApplySuggestionsCommand` / `PostponeAllCommand`

- [ ] **Step 1: 文言を足す**

`Strings.resx` の `</root>` の直前に:

```xml
  <data name="MorningApplySuggestions" xml:space="preserve"><value>推奨をまとめて適用</value></data>
  <data name="MorningPostponeAll" xml:space="preserve"><value>すべて後で</value></data>
  <data name="MorningApplyConfirmFormat" xml:space="preserve"><value>候補 {0} 件を推奨どおりに処理します。登録は「{1}」へ入ります。統合は対象タスクの説明を書き換えます。よろしいですか？</value></data>
  <data name="MorningBulkAppliedFormat" xml:space="preserve"><value>{0} 件を処理しました</value></data>
  <data name="MorningBulkResultFormat" xml:space="preserve"><value>{0} 件を処理、{1} 件を見送り: {2}</value></data>
  <data name="MorningBulkNoColumn" xml:space="preserve"><value>登録先の列がありません</value></data>
```

`Strings.cs` に同名のプロパティ 6 つ:

```csharp
    public static string MorningApplySuggestions => Get(nameof(MorningApplySuggestions));
    public static string MorningPostponeAll => Get(nameof(MorningPostponeAll));
    public static string MorningApplyConfirmFormat => Get(nameof(MorningApplyConfirmFormat));
    public static string MorningBulkAppliedFormat => Get(nameof(MorningBulkAppliedFormat));
    public static string MorningBulkResultFormat => Get(nameof(MorningBulkResultFormat));
    public static string MorningBulkNoColumn => Get(nameof(MorningBulkNoColumn));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` に追加:

```csharp
    [Fact]
    public async Task ApplySuggestions_DoesNothing_WhenThePersonSaysNo()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        await _vm.LoadAsync();
        var asked = new List<string>();
        _vm.Confirm = message => { asked.Add(message); return false; };

        await _vm.ApplySuggestionsCommand.ExecuteAsync(null);

        asked.Should().ContainSingle().Which.Should().Be(string.Format(Strings.MorningApplyConfirmFormat, 1, "未着手"),
            "登録先の列は完了以外の先頭で、文言に明記する");
        _service.Calls.Should().NotContain(c => c.StartsWith("ApplySuggestions"));
        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplySuggestions_AppliesIntoTheFirstColumn_AndReportsTheOutcome()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2, TriageAction.Reject));
        _service.BulkResult = Result.Ok(new BulkOutcome(1, new[] { "候補 2: 列が見つかりません" }));
        await _vm.LoadAsync();
        _vm.Confirm = _ => true;

        await _vm.ApplySuggestionsCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("ApplySuggestions:1");
        _vm.WarningMessage.Should().Be(string.Format(Strings.MorningBulkResultFormat, 1, 1, "候補 2: 列が見つかりません"));
        _vm.Candidates.Should().BeEmpty("偽サービスが全件を決着させたのでキューは読み直しで空になる");
        _vm.LeftPanel.Should().BeSameAs(_vm.FirstThing);
    }

    [Fact]
    public async Task PostponeAll_NeedsNoConfirmation()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.BulkResult = Result.Ok(new BulkOutcome(1, Array.Empty<string>()));
        await _vm.LoadAsync();
        _vm.Confirm = _ => throw new InvalidOperationException("確認は出さない");

        await _vm.PostponeAllCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("PostponeAll");
        _vm.WarningMessage.Should().Be(string.Format(Strings.MorningBulkAppliedFormat, 1));
        _vm.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkCommands_AreDisabled_WhenNothingIsQueued()
    {
        _service.Current = IngestedRunWithPlan();
        await _vm.LoadAsync();

        _vm.ApplySuggestionsCommand.CanExecute(null).Should().BeFalse();
        _vm.PostponeAllCommand.CanExecute(null).Should().BeFalse();
    }
```

- [ ] **Step 3: 失敗を確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~MorningPlanViewModelTests" -nologo -v q`
Expected: ビルドエラー（`Confirm` / `ApplySuggestionsCommand` が無い）

- [ ] **Step 4: VM に足す**

`MorningPlanViewModel.cs`:

(a) `_isTriaging` の属性に `NotifyCanExecuteChangedFor` を足す:

```csharp
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PostponeAllCommand))]
    private bool _isTriaging;
```

(b) `OpenPath` の下に:

```csharp
    /// <summary>「推奨をまとめて適用」の確認。既定は MessageBox、テストでは差し替える（OpenPath と同じ流儀）。</summary>
    public Func<string, bool> Confirm { get; set; } = message =>
        MessageBox.Show(message, Strings.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
```

(c) `OpenJobFolder` の前に:

```csharp
    // ---------- 一括（仕様 §6） ----------

    /// <summary>登録先は完了以外の先頭の列に固定し、確認の文言に明記する（仕様 §3）。</summary>
    [RelayCommand(CanExecute = nameof(IsTriaging))]
    private async Task ApplySuggestionsAsync()
    {
        if (_run is null) return;
        var column = Triage.ColumnChoices.FirstOrDefault();
        if (column is null)
        {
            ErrorMessage = Strings.MorningBulkNoColumn;
            return;
        }
        if (!Confirm(string.Format(Strings.MorningApplyConfirmFormat, Candidates.Count, column.Name))) return;
        ClearBanners();
        var outcome = await _service.ApplySuggestionsAsync(_run.Id, column.Id).ConfigureAwait(true);
        await AfterBulkAsync(outcome).ConfigureAwait(true);
    }

    /// <summary>取り消しが容易なので確認なし（親仕様 §11）。</summary>
    [RelayCommand(CanExecute = nameof(IsTriaging))]
    private async Task PostponeAllAsync()
    {
        if (_run is null) return;
        ClearBanners();
        var outcome = await _service.PostponeAllAsync(_run.Id).ConfigureAwait(true);
        await AfterBulkAsync(outcome).ConfigureAwait(true);
    }

    /// <summary>件数と見送り理由を 1 行で警告バナーへ。成功の警告（WIP 超過など）はその後ろに続ける。</summary>
    private async Task AfterBulkAsync(Result<BulkOutcome> result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        var outcome = result.Value!;
        var summary = outcome.Skipped.Count == 0
            ? string.Format(Strings.MorningBulkAppliedFormat, outcome.Applied)
            : string.Format(Strings.MorningBulkResultFormat, outcome.Applied, outcome.Skipped.Count, string.Join(" / ", outcome.Skipped));
        WarningMessage = result.Warnings.Count > 0 ? summary + " / " + string.Join(" / ", result.Warnings) : summary;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }
```

- [ ] **Step 5: XAML にボタンを足す**

`MorningPlanView.xaml` の右カラムで、`StatusLine` の `TextBlock` の直下に:

```xml
          <StackPanel Orientation="Horizontal" Margin="{StaticResource Gap.Top.2}"
                      Visibility="{Binding IsTriaging, Converter={StaticResource BoolToVisibility}}">
            <Button Content="{x:Static res:Strings.MorningApplySuggestions}" Style="{StaticResource Btn.Ghost}"
                    Command="{Binding ApplySuggestionsCommand}" />
            <Button Content="{x:Static res:Strings.MorningPostponeAll}" Style="{StaticResource Btn.Ghost}"
                    Command="{Binding PostponeAllCommand}" Margin="{StaticResource Gap.Left.2}" />
          </StackPanel>
```

- [ ] **Step 6: 全テストと警告ゼロビルドを確認する**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true` / `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.App/ViewModels/MorningPlanViewModel.cs src/MoTask.App/Views/MorningPlanView.xaml src/MoTask.App/Resources/Strings.resx src/MoTask.App/Resources/Strings.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit -m "feat(app): apply every suggestion after one confirmation, or postpone them all

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: ボードとの往復（ボードで開く・タブのバッジ・起動時の読み込み）

仕様 §6「ボードとの往復」「タブのバッジ」。

**Files:**
- Modify: `src/MoTask.App/ViewModels/BoardViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/MorningPlanViewModel.cs`（`HasPendingCandidates`）
- Modify: `src/MoTask.App/Views/MainWindow.xaml` / `MainWindow.xaml.cs`
- Test: `tests/MoTask.App.Tests/BoardViewModelTests.cs`（追記）
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`（追記）

**Interfaces:**
- Consumes: Task 5 の `NavigateToTask` / `PendingCount`
- Produces: `BoardViewModel.SelectTask(int taskId)`、`MorningPlanViewModel.HasPendingCandidates`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/BoardViewModelTests.cs` に追加:

```csharp
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
```

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` に追加:

```csharp
    [Fact]
    public async Task PendingBadge_FollowsTheQueue()
    {
        _service.Current = IngestedRunWithPlan();
        _service.Candidates.Add(Candidate());
        _service.Candidates.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.PendingCount.Should().Be(2);
        _vm.HasPendingCandidates.Should().BeTrue();

        await _vm.Triage.RejectCommand.ExecuteAsync(null);
        await _vm.Triage.RejectCommand.ExecuteAsync(null);

        _vm.PendingCount.Should().Be(0);
        _vm.HasPendingCandidates.Should().BeFalse("0 のときはバッジを出さない");
    }
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~SelectTask|FullyQualifiedName~PendingBadge" -nologo -v q`
Expected: ビルドエラー（`SelectTask` / `HasPendingCandidates` が無い）

- [ ] **Step 3: VM を書く**

`BoardViewModel.cs` の `SelectCard` の下に:

```csharp
    /// <summary>
    /// 朝のプランの「ボードで開く」とタスク行のクリックから（仕様 §6）。フィルタで隠れていても
    /// 選択（と詳細パネル）は開く。盤面に無ければ何もしない（仕様 §8）。
    /// </summary>
    public void SelectTask(int taskId)
    {
        var card = Columns.SelectMany(c => c.AllCards).FirstOrDefault(c => c.Id == taskId);
        if (card is not null) SelectCard(card);
    }
```

`MorningPlanViewModel.cs` の `_pendingCount` の下に:

```csharp
    /// <summary>タブのバッジを出すか。0 件のときは出さない。</summary>
    [ObservableProperty] private bool _hasPendingCandidates;
```

`UpdateCounters` の `PendingCount = Candidates.Count;` の下に `HasPendingCandidates = Candidates.Count > 0;`。

- [ ] **Step 4: MainWindow を配線する**

`MainWindow.xaml` の `MorningTabButton` を次に置き換える（DataContext は BoardViewModel なので、
`MorningHost` の DataContext を `ElementName` で引く）:

```xml
          <Button x:Name="MorningTabButton" Style="{StaticResource Btn.Ghost}" Click="OnShowMorningClick"
                  Padding="{StaticResource Pad.Tab}" Margin="{StaticResource Gap.Left.2}">
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="{x:Static res:Strings.ViewMorningPlan}" FontWeight="Medium" VerticalAlignment="Center" />
              <!-- 候補件数のバッジ（ワイヤー 4c）。同じ画面への導線を 2 つ作らず、タブに付ける（仕様 §3） -->
              <Border Background="{StaticResource Brush.Accent}" CornerRadius="8" Padding="{StaticResource Pad.Chip}"
                      Margin="{StaticResource Gap.Left.1}" VerticalAlignment="Center"
                      Visibility="{Binding DataContext.HasPendingCandidates, ElementName=MorningHost, Converter={StaticResource BoolToVisibility}}">
                <TextBlock Text="{Binding DataContext.PendingCount, ElementName=MorningHost}"
                           Style="{StaticResource Text.Caption}" Foreground="{StaticResource Brush.OnAccent}" />
              </Border>
            </StackPanel>
          </Button>
```

`MainWindow.xaml.cs`:

(a) コンストラクタの `MorningHost.DataContext = morning;` の下に:

```csharp
        morning.NavigateToTask += OnNavigateToTask;
```

(b) `OnLoaded` の `await _vm.LoadAsync();` の下に（同じ try の中）:

```csharp
            // タブのバッジを起動直後から出す（仕様 §6）。タブを押したときも LoadAsync で読み直す。
            await _morning.LoadAsync();
```

(c) `OnShowBoardClick` の下に:

```csharp
    /// <summary>朝のプランの「ボードで開く」／タスク行のクリック。ボードへ切り替えてそのタスクを選ぶ。</summary>
    private void OnNavigateToTask(object? sender, int taskId)
    {
        ShowBoard(true);
        _vm.SelectTask(taskId);
    }
```

- [ ] **Step 5: 全テストと警告ゼロビルドを確認し、実機で往復を見る**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true` / `dotnet test MoTask.sln -nologo -v q`
Run: `dotnet run --project src/MoTask.App` → 起動直後にタブのバッジが（候補があれば）出る。無ければ出ない。

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.App/ViewModels/BoardViewModel.cs src/MoTask.App/ViewModels/MorningPlanViewModel.cs src/MoTask.App/Views/MainWindow.xaml src/MoTask.App/Views/MainWindow.xaml.cs tests/MoTask.App.Tests/BoardViewModelTests.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit -m "feat(app): jump from the morning plan to the board and show the pending badge on the tab

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: キーボード（T / E / X / L）

仕様 §6「キーボード」。対応は純関数に切り出してテストし、MainWindow は「入力中は奪わない」判定の後ろで呼ぶ。

**Files:**
- Create: `src/MoTask.App/MorningKeyMap.cs`
- Modify: `src/MoTask.App/ViewModels/TriagePanelViewModel.cs`（`RunAsync`）
- Modify: `src/MoTask.App/Views/MainWindow.xaml.cs`
- Test: `tests/MoTask.App.Tests/MorningKeyMapTests.cs`（新規）
- Test: `tests/MoTask.App.Tests/TriagePanelViewModelTests.cs`（追記）

**Interfaces:**
- Produces: `enum TriageKeyAction { Register, Merge, Reject, Postpone }`、`MorningKeyMap.Resolve(Key key, ModifierKeys modifiers) : TriageKeyAction?`、`TriagePanelViewModel.RunAsync(TriageKeyAction) : Task`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/MorningKeyMapTests.cs`:

```csharp
using System.Windows.Input;
using FluentAssertions;
using MoTask.App;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>仕様 §6 のキー割当。修飾キー付きは対象外。</summary>
public class MorningKeyMapTests
{
    [Theory]
    [InlineData(Key.T, TriageKeyAction.Register)]
    [InlineData(Key.E, TriageKeyAction.Merge)]
    [InlineData(Key.X, TriageKeyAction.Reject)]
    [InlineData(Key.L, TriageKeyAction.Postpone)]
    public void MapsTheFourKeys(Key key, TriageKeyAction expected)
        => MorningKeyMap.Resolve(key, ModifierKeys.None).Should().Be(expected);

    [Theory]
    [InlineData(Key.T, ModifierKeys.Control)]
    [InlineData(Key.E, ModifierKeys.Alt)]
    [InlineData(Key.N, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.None)]
    public void IgnoresModifiersAndOtherKeys(Key key, ModifierKeys modifiers)
        => MorningKeyMap.Resolve(key, modifiers).Should().BeNull();
}
```

`tests/MoTask.App.Tests/TriagePanelViewModelTests.cs` に追加:

```csharp
    [Fact]
    public async Task RunAsync_Merge_DoesNothingWithoutATarget()
    {
        _panel.Show(Candidate(), 0, 1);

        await _panel.RunAsync(TriageKeyAction.Merge);

        await _service.DidNotReceive().MergeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_Reject_CallsTheService()
    {
        _service.RejectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _panel.Show(Candidate(), 0, 1);

        await _panel.RunAsync(TriageKeyAction.Reject);

        await _service.Received(1).RejectAsync(1, Arg.Any<CancellationToken>());
        _decisions.Should().ContainSingle();
    }
```

（`using MoTask.App;` を足す。）

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~MorningKeyMapTests|FullyQualifiedName~RunAsync" -nologo -v q`
Expected: ビルドエラー

- [ ] **Step 3: 対応表と RunAsync を書く**

`src/MoTask.App/MorningKeyMap.cs`:

```csharp
using System.Windows.Input;

namespace MoTask.App;

/// <summary>仕分けの 4 操作。キーから引く。</summary>
public enum TriageKeyAction
{
    Register,
    Merge,
    Reject,
    Postpone,
}

/// <summary>仕様 §6 のキー割当（T 登録 / E 統合 / X 却下 / L あとで）。修飾キー付きは対象外。</summary>
public static class MorningKeyMap
{
    public static TriageKeyAction? Resolve(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None) return null;
        return key switch
        {
            Key.T => TriageKeyAction.Register,
            Key.E => TriageKeyAction.Merge,
            Key.X => TriageKeyAction.Reject,
            Key.L => TriageKeyAction.Postpone,
            _ => null,
        };
    }
}
```

`TriagePanelViewModel.cs` の `OpenLink` の上に（`MoTask.App.ViewModels` は `MoTask.App` の子名前空間なので `using` は要らない）:

```csharp
    /// <summary>キーからの操作。統合は統合先が選ばれているときだけ（ボタンと同じ）。</summary>
    public Task RunAsync(TriageKeyAction action) => action switch
    {
        TriageKeyAction.Register => RegisterCommand.ExecuteAsync(null),
        TriageKeyAction.Merge => CanMerge ? MergeCommand.ExecuteAsync(null) : Task.CompletedTask,
        TriageKeyAction.Reject => RejectCommand.ExecuteAsync(null),
        TriageKeyAction.Postpone => PostponeCommand.ExecuteAsync(null),
        _ => Task.CompletedTask,
    };
```

- [ ] **Step 4: MainWindow のキー処理に足す**

`MainWindow.xaml.cs` の `OnPreviewKeyDown` を次に置き換える（Ctrl+F → 入力中の判定 → 朝の画面 → ボードの順）:

```csharp
    /// <summary>
    /// 仕様 §6 キーボード: N=新規、Delete=論理削除、Esc=詳細を閉じる、Ctrl+F=検索。文字入力中は奪わない。
    /// 朝の画面では T/E/X/L（仕分け）だけを受け、ボードのキーは渡さない（候補の仕分け中に Delete を
    /// 押しただけでボードのタスクが消えないように）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBar.FocusSearch();
            e.Handled = true;
            return;
        }

        // 非編集の ComboBox も文字キーで項目を選ぶ。列追加の種別選択で Esc を奪わないよう、
        // IsEditable を問わず ComboBox は「入力中」として扱う。
        var typing = Keyboard.FocusedElement is TextBoxBase or ComboBox or DatePicker;
        if (typing) return; // インライン編集中の Enter/Esc は各入力欄が処理する

        if (MorningHost.Visibility == Visibility.Visible)
        {
            if (_morning.LeftPanel is TriagePanelViewModel triage
                && MorningKeyMap.Resolve(e.Key, Keyboard.Modifiers) is { } action)
            {
                e.Handled = true;
                _ = RunTriageKeyAsync(triage, action);
            }
            return;
        }

        switch (e.Key)
        {
            case Key.N when Keyboard.Modifiers == ModifierKeys.None:
                _vm.NewTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                _vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.CloseDetailCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>誰も待たない Task なので、例外はここで捕まえてバナーへ回す（OnLoaded と同じ考え方）。</summary>
    private async Task RunTriageKeyAsync(TriagePanelViewModel triage, TriageKeyAction action)
    {
        try
        {
            await triage.RunAsync(action);
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }
```

`switch` の中身は今のファイルにある case をそのまま残す（上に写したのは既存の 3 つ。他にあればそれも残す）。

- [ ] **Step 5: 全テストと警告ゼロビルドを確認し、実機でキーを試す**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true` / `dotnet test MoTask.sln -nologo -v q`
Run: `dotnet run --project src/MoTask.App` → 朝の画面で候補があれば `L` で「あとで」になる。タイトル欄にカーソルを置いて `L` を押すと文字が入るだけ。ボードに戻って `N` で新規タスクが作れる。

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.App/MorningKeyMap.cs src/MoTask.App/ViewModels/TriagePanelViewModel.cs src/MoTask.App/Views/MainWindow.xaml.cs tests/MoTask.App.Tests/MorningKeyMapTests.cs tests/MoTask.App.Tests/TriagePanelViewModelTests.cs
git commit -m "feat(app): triage with T / E / X / L on the morning plan screen

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: 仕上げ（古いコメントの掃除・完了条件の確認）

**Files:**
- Modify: `src/MoTask.App/ViewModels/CandidateItemViewModel.cs` / `MorningPlanViewModel.cs` / `MorningPlanView.xaml`（残っていれば）
- Modify: `docs/superpowers/plans/2026-09-09-motask-morning-plan-view.md`（チェックボックス）

- [ ] **Step 1: 「2 本目の計画で」の類の古いコメントが残っていないか調べる**

Run: `grep -rn "2 本目\|2本目\|Task 5 で足す" src/ | grep -v "/obj/"`
Expected: 0 件。残っていれば、その行を現状に合った説明に直す（説明が要らなければ削除）。

- [ ] **Step 2: 完了条件を全部確認する**

```bash
dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true
dotnet test MoTask.sln -nologo -v q
git status --short
```

- 警告 0・エラー 0（`MoTask.exe` が起動中なら出力コピーの MSB3021/MSB3027 だけ。それも避けたければ先に閉じる）
- 全テスト PASS。`AiJob*` のテストが 1 件も減っていない（`dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~AiJobService" -nologo -v q` で件数を見る）
- `git status --short` が空（コミット漏れなし）

- [ ] **Step 3: 実機で仕様 §11 の手動確認を通せるところまで通す**

`claude` を実際に走らせる項目は人が実施する。ここでは次だけ確認する:

- `dotnet run --project src/MoTask.App` で両方の画面が開き、例外なく閉じられる
- 過去の実行が DB にあれば、「朝の実行プラン」で左右が仕様 §7 の表のとおりに出る

確認できた項目は `docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md` §11 のチェックボックスに印を付けてコミットする:

```bash
git add docs/superpowers/specs/2026-09-09-motask-morning-plan-view-design.md
git commit -m "docs(spec): record the manual checks done for the morning plan view

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

- [ ] **Step 4: マージへ**

`superpowers:finishing-a-development-branch` に進む。マージ先は `master`。マージ直前に必ず
`git log --oneline -3 master` と `git merge-base --is-ancestor e902c92 master` を確認する
（master は作業中に動くし、巻き戻ることもある）。`rebase` / `reset --hard` は使わない。

---

## 完了の条件

- [ ] `dotnet test MoTask.sln` が全部緑
- [ ] `dotnet build MoTask.sln -p:TreatWarningsAsErrors=true` が警告 0
- [ ] `grep -rn "2 本目\|2本目" src/` が空
- [ ] 既存の AI 遂行（`AiJob*`）のテストとコードが 1 行も壊れていない
- [ ] 契約（`MorningResultReader` / `MorningInstruction` / `BoardSnapshot`）に diff が無い: `git diff e902c92 -- src/MoTask.Core/Morning/MorningResultReader.cs src/MoTask.Core/Morning/MorningInstruction.cs src/MoTask.Core/Morning/BoardSnapshot.cs` が空
- [ ] マイグレーションが増えていない: `git diff e902c92 --stat -- src/MoTask.Data/Migrations` が空
- [ ] `dotnet run --project src/MoTask.App` で両方の画面が開き、例外なく閉じられる

## この計画に入れないもの

仕様 §2「含まない」のとおり。ワイヤーの「所要」「次の一手」「必要情報」「着手する」「Claude に相談」
「今日じゃない」「再計算」「共有」「履歴を見る」「AI に準備を依頼する」、プランの手編集、契約の変更。
