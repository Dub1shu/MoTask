# 朝の実行プラン（1/2）取り込みと仕分け 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 朝ボタンを1つ押すと Claude が受信箱を回ってタスク候補とプランを書き、MoTask がそれを取り込んで、人が候補を1件ずつ「登録・統合・あとで・却下」で片づけられるようにする。

**Architecture:** 既存の AI 遂行基盤（`IJobFolder` / `ISessionLauncher` / `IJobEventSource` / `HookEventParser` / `OperationGate`）だけを共有し、`AiJob` / `AiJobService` には一切触らない。新設の `MorningRun` と `TriageCandidate` の2テーブルを持ち、`MorningService` がジョブフォルダを作って端末を開き、`events.jsonl` を追い、`Stop` が来るたび `result/` を読んで取り込む。`result/` の形を知るコードは `MorningResultReader` 1クラスに閉じる。

**Tech Stack:** .NET 10 / WPF / SQLite / EF Core 10.0.11 / CommunityToolkit.Mvvm 8.4.2 / xunit 2.9.3 + FluentAssertions 7.2.2

**Spec:** `docs/superpowers/specs/2026-09-07-motask-morning-plan-triage-design.md`

**この計画の範囲:** 仕様 §15 の分割のうち **1本目（取り込みと仕分け）** だけ。プランの解決（`MorningPlanResolver`）、ワイヤー 4a / 4b の左パネルのモード切替、右カラムの4区分、ボード画面との導線、キーボード、一括操作は **2本目**の計画に入る。この計画が終わった時点で「候補を取り込んでタスクにする」までが動く。

---

## Global Constraints

このプランのすべてのタスクに、暗黙にこの節の要求が含まれる。

- **`AiJob` / `AiJobService` / `AiJobStatus` / `AiJobs` テーブルは一切変更しない。** 相乗りしない理由は仕様 §5。`Tasks` テーブルにも列を足さない
- **`MorningService` は `BoardService` / `AiJobService` と同じ singleton の `OperationGate` を共有する。** ゲートの中から `IBoardService` を呼ぶとデッドロックする（`AiJobService` のコメントに残っている既知の事故）。登録・統合はゲートの外で呼ぶ
- **イベントを DB に持つテーブルは作らない。** 記録は `events.jsonl`、表示は `IJobFolder.ReadTail`、再開位置は `MorningRun.ProcessedLines`
- **取り込み元を列挙しない。** `TriageCandidate.Source` は enum ではなく自由文字列。認証済みコネクタが0でも候補0件は失敗ではない
- **Core は UI 非依存・EF 非依存**を維持する。`MoTask.Core.csproj` に参照を足さない
- 利用者向け文言はハードコードしない。Core は `src/MoTask.Core/Resources/Messages.resx`（アクセサは `Messages.cs`）、App は `src/MoTask.App/Resources/Strings.resx`（アクセサは `Strings.cs`）。**resx に値を足したら同じ名前のプロパティを .cs に足す**
- enum の永続化は既存の流儀どおり `.HasConversion<string>()`
- パッケージのバージョンは `Directory.Packages.props` にあるので `PackageReference` に `Version` を書かない
- テストは xunit + FluentAssertions。アサーションは `Should()` 形式
- 各タスクの最後に1コミット。コミットメッセージは日本語または英語の慣用形（既存ログは両方ある）で、末尾に必ず次の行を付ける:

  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

### 仕様からの意図的な逸脱（3件）

実装可能にするために必要な差分。レビュー時にここを見ること。

1. **`TriageCandidate` に `SuggestedMergeTaskId`（`int?`）を足す。** 仕様 §9 の列表には無いが、§8 の `mergeTargetTaskId` を保存しないと「統合が推奨」の表示も 2本目の一括適用も成立しない
2. **`IJobFolder` に `ResolveRoot` / `WriteText` / `ReadText` を足し、`JobFolderRequest` に `Category` / `OutputDirectoryName` を足す。** 仕様 §5 は「`IJobFolder` を共有する」と言うが、現状の `Create` は置き場所が `jobs/` 固定で `result/` を作れない（Task 5）
3. **`AiSettings` に `MorningInstruction`（`string?`）を1つ足す。** 仕様 §6 の「`instruction.md` は AI 設定ダイアログで編集可能」を満たすため（Task 4）

### fixture について（正直な但し書き）

仕様 §13 は `MorningResultReader` を「実キャプチャの fixture でピン留めする」と言う。実キャプチャは実際に Claude を走らせないと採れない。**この計画では仕様 §8 の例文から手で書いた fixture を置き、初回の実機確認のあとで本物のキャプチャに差し替える**。差し替えは README の手動確認チェックリストに項目として残す（Task 10）。

---

## File Structure

### 新規（Core）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.Core/Model/MorningRun.cs` | 朝の実行1回。永続化される状態だけ |
| `src/MoTask.Core/Model/MorningRunStatus.cs` | 状態と `IsTerminal` / `IsActive` |
| `src/MoTask.Core/Model/TriageCandidate.cs` | 候補1件 |
| `src/MoTask.Core/Model/TriageAction.cs` | Claude の推薦（4値） |
| `src/MoTask.Core/Model/TriageStatus.cs` | 人の判断の結果（5値） |
| `src/MoTask.Core/Model/TriageHistoryDetail.cs` | 履歴の `Detail`（`AiJobHistoryDetail` と同じ形） |
| `src/MoTask.Core/Morning/CandidateRecord.cs` | `candidates.jsonl` の1行を検証したもの |
| `src/MoTask.Core/Morning/MorningResult.cs` | 読み取り結果（候補・捨てた行数・生の `PlanJson`） |
| `src/MoTask.Core/Morning/MorningResultReader.cs` | **`result/` の形を知る唯一の場所。** 純関数 |
| `src/MoTask.Core/Morning/BoardSnapshot.cs` | `board.json` の組み立て。純関数 |
| `src/MoTask.Core/Morning/MorningInstruction.cs` | `instruction.md` の組み立て。純関数 |
| `src/MoTask.Core/Morning/MorningRunDescriptor.cs` | `run.json` の中身 |
| `src/MoTask.Core/Morning/CandidateNote.cs` | 候補の根拠をタスクの説明に残す文面。登録と統合で共有 |
| `src/MoTask.Core/Abstractions/IMorningRepository.cs` | 永続化の口 |
| `src/MoTask.Core/Services/IMorningService.cs` | 操作の口 ＋ `CandidateDecision` |
| `src/MoTask.Core/Services/MorningService.cs` | 起動・追従・取り込み・4アクション |
| `src/MoTask.Core/Services/MorningRunChangedEventArgs.cs` | 画面へ渡す1回分の変化 |

### 新規（Data / App）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.Data/Repositories/MorningRepository.cs` | `IMorningRepository` の EF 実装 |
| `src/MoTask.Data/Migrations/<stamp>_AddMorningRuns.cs` | 2テーブル追加（`dotnet ef` が生成） |
| `src/MoTask.App/ViewModels/MorningPlanViewModel.cs` | 朝の実行プラン画面（最小） |
| `src/MoTask.App/ViewModels/CandidateItemViewModel.cs` | 候補1件の表示と編集欄 |
| `src/MoTask.App/Views/MorningPlanView.xaml` (+ `.cs`) | 上と対になるビュー |

### 変更

| ファイル | 変更 |
| --- | --- |
| `src/MoTask.Core/Model/HistoryKind.cs` | `CandidateRegistered = 7` / `CandidateMerged = 8` を足す |
| `src/MoTask.Core/Ai/JobFolderPaths.cs` | `morning` / `result` / `board.json` / `run.json` のパスと定数 |
| `src/MoTask.Core/Ai/JobFolderRequest.cs` | `Category` / `OutputDirectoryName`（init プロパティ、既定は現状維持） |
| `src/MoTask.Core/Ai/IJobFolder.cs` | `ResolveRoot` / `WriteText` / `ReadText` |
| `src/MoTask.Core/Ai/AiSettings.cs` | `MorningInstruction`（末尾に既定値付きで追加） |
| `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs` | 新しい文言 |
| `src/MoTask.App/Ai/JobFolder.cs` | 上の3メソッドと `Category` 対応 |
| `src/MoTask.Data/MoTaskDbContext.cs` | 2エンティティの設定 |
| `src/MoTask.Data/JsonAiSettingsStore.cs` | `MorningInstruction` の往復 |
| `src/MoTask.Data/ServiceCollectionExtensions.cs` | `IMorningRepository` を登録 |
| `src/MoTask.App/App.xaml.cs` | `IMorningService` / VM を登録し、起動時に復旧を呼ぶ |
| `src/MoTask.App/Views/MainWindow.xaml` (+ `.cs`) | 「ボード」⇄「朝の実行プラン」の切替 |
| `src/MoTask.App/ViewModels/AiSettingsViewModel.cs` / `Views/AiSettingsDialog.xaml` | 指示文の編集欄 |
| `src/MoTask.App/ViewModels/HistoryFormatter.cs` | 新しい2つの `HistoryKind` |
| `src/MoTask.App/Resources/Strings.resx` / `Strings.cs` | 新しい文言 |
| `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs` | `IMorningRepository` を実装 |
| `tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs` | 新しい3メソッド |
| `README.md` | 手動確認チェックリスト |

---

### Task 1: ドメインモデルと履歴の種別

**Files:**
- Create: `src/MoTask.Core/Model/MorningRunStatus.cs`
- Create: `src/MoTask.Core/Model/MorningRun.cs`
- Create: `src/MoTask.Core/Model/TriageAction.cs`
- Create: `src/MoTask.Core/Model/TriageStatus.cs`
- Create: `src/MoTask.Core/Model/TriageCandidate.cs`
- Create: `src/MoTask.Core/Model/TriageHistoryDetail.cs`
- Modify: `src/MoTask.Core/Model/HistoryKind.cs`
- Test: `tests/MoTask.Core.Tests/MorningModelTests.cs`

**Interfaces:**
- Consumes: なし（最初のタスク）
- Produces: `MorningRun`, `MorningRunStatus`（`.IsTerminal()` / `.IsActive()`）, `TriageCandidate`, `TriageAction`（`Register` / `Merge` / `Later` / `Reject`）, `TriageStatus`（`Pending` / `Registered` / `Merged` / `Later` / `Rejected`）, `TriageHistoryDetail(string Source, string ExternalId)` と `Serialize` / `Deserialize`, `HistoryKind.CandidateRegistered` / `HistoryKind.CandidateMerged`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningModelTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class MorningModelTests
{
    [Fact]
    public void NewRun_StartsPending_AndIsNotTerminal()
    {
        var run = new MorningRun { Date = new DateOnly(2026, 9, 7) };

        run.Status.Should().Be(MorningRunStatus.Pending);
        run.Status.IsActive().Should().BeTrue();
        run.PlanJson.Should().BeEmpty("取り込み前のプランは空文字。null にはしない");
        run.JobFolder.Should().BeEmpty();
        run.Instruction.Should().BeEmpty();
        run.ProcessedLines.Should().Be(0);
    }

    [Theory]
    [InlineData(MorningRunStatus.Ingested)]
    [InlineData(MorningRunStatus.Failed)]
    [InlineData(MorningRunStatus.Cancelled)]
    public void FinishedStatuses_AreTerminal(MorningRunStatus status)
        => status.IsTerminal().Should().BeTrue();

    [Theory]
    [InlineData(MorningRunStatus.Pending)]
    [InlineData(MorningRunStatus.Running)]
    public void UnfinishedStatuses_AreNotTerminal(MorningRunStatus status)
        => status.IsTerminal().Should().BeFalse();

    [Fact]
    public void NewCandidate_StartsPending()
    {
        var candidate = new TriageCandidate { ExternalId = "outlook:AAMkAD" };

        candidate.Status.Should().Be(TriageStatus.Pending);
        candidate.SuggestedAction.Should().Be(TriageAction.Register);
        candidate.Source.Should().BeEmpty();
        candidate.Evidence.Should().BeEmpty();
        candidate.ResultTaskId.Should().BeNull();
        candidate.SuggestedMergeTaskId.Should().BeNull();
        candidate.DecidedAt.Should().BeNull();
    }

    [Fact]
    public void HistoryKind_GainsTheTwoTriageMembers_WithoutRenumberingTheOldOnes()
    {
        ((int)HistoryKind.AiJobFinished).Should().Be(6, "既存行の値を動かさない");
        ((int)HistoryKind.CandidateRegistered).Should().Be(7);
        ((int)HistoryKind.CandidateMerged).Should().Be(8);
    }

    [Fact]
    public void TriageHistoryDetail_RoundTrips()
    {
        var text = TriageHistoryDetail.Serialize(new TriageHistoryDetail("Outlook", "outlook:AAMkAD"));

        var back = TriageHistoryDetail.Deserialize(text);

        back!.Source.Should().Be("Outlook");
        back.ExternalId.Should().Be("outlook:AAMkAD");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    public void TriageHistoryDetail_ReturnsNull_ForUnusableText(string text)
        => TriageHistoryDetail.Deserialize(text).Should().BeNull();
}
```

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningModelTests`
Expected: コンパイルエラー（`MorningRun` / `TriageCandidate` / `TriageHistoryDetail` などが存在しない）

- [ ] **Step 3: モデルを書く**

`src/MoTask.Core/Model/MorningRunStatus.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>朝の実行1回の状態（仕様 §9）。</summary>
public enum MorningRunStatus
{
    /// <summary>行は作ったが、まだ SessionStart フックが来ていない。</summary>
    Pending = 0,
    Running = 1,
    /// <summary>result/ を読んで候補とプランを保存し終えた。</summary>
    Ingested = 2,
    Failed = 3,
    Cancelled = 4,
}

public static class MorningRunStatusExtensions
{
    /// <summary>もう追いかけない実行。端末が生きているかどうかは MoTask には分からない。</summary>
    public static bool IsTerminal(this MorningRunStatus status)
        => status is MorningRunStatus.Ingested or MorningRunStatus.Failed or MorningRunStatus.Cancelled;

    public static bool IsActive(this MorningRunStatus status) => !status.IsTerminal();
}
```

`src/MoTask.Core/Model/MorningRun.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>
/// 朝の実行1回（仕様 §9）。AiJob には相乗りしない（AiJob.TaskId は必須で、朝の実行には対象タスクが無い）。
/// イベントは DB に持たず、events.jsonl と ProcessedLines だけで足りるようにする。
/// </summary>
public sealed class MorningRun
{
    public int Id { get; set; }
    /// <summary>対象日（ローカル）。</summary>
    public DateOnly Date { get; set; }
    public MorningRunStatus Status { get; set; } = MorningRunStatus.Pending;
    /// <summary>MoTask が採番して --session-id に渡す。</summary>
    public Guid SessionId { get; set; }
    /// <summary>実行時に組み立てた指示文のスナップショット。</summary>
    public string Instruction { get; set; } = "";
    /// <summary>ジョブフォルダの絶対パス。DB に行が出来る時点で必ず埋まっている（仕様 §12）。</summary>
    public string JobFolder { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>events.jsonl から読んだ行数。追従を張り直すときの読み飛ばし数になる。</summary>
    public int ProcessedLines { get; set; }
    /// <summary>検証済み plan.json の生データ。未取り込みは空文字（仕様 §9）。</summary>
    public string PlanJson { get; set; } = "";
}
```

`src/MoTask.Core/Model/TriageAction.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>
/// Claude の推薦（仕様 §8）。<b>推薦であって実行ではない。</b>決めるのは常に人。
/// </summary>
public enum TriageAction
{
    Register = 0,
    Merge = 1,
    Later = 2,
    Reject = 3,
}
```

`src/MoTask.Core/Model/TriageStatus.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>候補に対する人の判断の結果（仕様 §9）。</summary>
public enum TriageStatus
{
    Pending = 0,
    Registered = 1,
    Merged = 2,
    /// <summary>翌朝の候補キューに残る唯一の状態。</summary>
    Later = 3,
    Rejected = 4,
}
```

`src/MoTask.Core/Model/TriageCandidate.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>
/// 受信箱から拾ったタスク候補1件（仕様 §9）。ExternalId が一意なので、
/// 一度片づけた候補は翌朝 Claude が再提出しても取り込み時に黙って捨てられる。
/// </summary>
public sealed class TriageCandidate
{
    public int Id { get; set; }
    public int MorningRunId { get; set; }
    /// <summary>重複排除の鍵。一意インデックスを張る。</summary>
    public string ExternalId { get; set; } = "";
    /// <summary>取り込み元。enum ではなく自由文字列（仕様 §4）。</summary>
    public string Source { get; set; } = "";
    /// <summary>差出人。SQLite の予約語と同名だが、EF は識別子を必ず引用符で囲むので列名は "From" のままでよい。</summary>
    public string From { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>元の文面からの引用。これが無い候補はそもそも取り込まない（仕様 §8）。</summary>
    public string Evidence { get; set; } = "";
    public string Link { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public DateTime? ReceivedAt { get; set; }
    public DateOnly? SuggestedDueDate { get; set; }
    public string SuggestedProject { get; set; } = "";
    public TriageAction SuggestedAction { get; set; } = TriageAction.Register;
    /// <summary>SuggestedAction が Merge のときの統合先。仕様 §8 の mergeTargetTaskId。</summary>
    public int? SuggestedMergeTaskId { get; set; }
    public TriageStatus Status { get; set; } = TriageStatus.Pending;
    /// <summary>登録先または統合先のタスク。</summary>
    public int? ResultTaskId { get; set; }
    public DateTime? DecidedAt { get; set; }
}
```

`src/MoTask.Core/Model/TriageHistoryDetail.cs`:

```csharp
using System.Text.Json;

namespace MoTask.Core.Model;

/// <summary>HistoryKind.CandidateRegistered / CandidateMerged の Detail。</summary>
public sealed record TriageHistoryDetail(string Source, string ExternalId)
{
    public static string Serialize(TriageHistoryDetail detail) => JsonSerializer.Serialize(detail);

    /// <summary>空・壊れた JSON は null（1 行の壊れた履歴で呼び出し元を落とさない）。</summary>
    public static TriageHistoryDetail? Deserialize(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        try
        {
            return JsonSerializer.Deserialize<TriageHistoryDetail>(detail);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

`src/MoTask.Core/Model/HistoryKind.cs` の末尾（`AiJobFinished = 6,` の直後）に足す:

```csharp
    /// <summary>Detail は TriageHistoryDetail。候補から新しいタスクを作った。</summary>
    CandidateRegistered = 7,
    /// <summary>Detail は TriageHistoryDetail。候補を既存タスクへ統合した。</summary>
    CandidateMerged = 8,
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningModelTests`
Expected: PASS（10 件）

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core/Model tests/MoTask.Core.Tests/MorningModelTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add MorningRun and TriageCandidate models

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: MorningResultReader（`result/` の形を知る唯一の場所）

**Files:**
- Create: `src/MoTask.Core/Morning/CandidateRecord.cs`
- Create: `src/MoTask.Core/Morning/MorningResult.cs`
- Create: `src/MoTask.Core/Morning/MorningResultReader.cs`
- Create: `tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl`
- Create: `tests/MoTask.Core.Tests/Fixtures/morning-candidates-broken.jsonl`
- Create: `tests/MoTask.Core.Tests/Fixtures/morning-plan.json`
- Create: `tests/MoTask.Core.Tests/Fixtures/morning-plan-broken.json`
- Test: `tests/MoTask.Core.Tests/MorningResultReaderTests.cs`

`tests/MoTask.Core.Tests/MoTask.Core.Tests.csproj` は既に `<None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />` を持つので、csproj の変更は不要。

**Interfaces:**
- Consumes: Task 1 の `TriageAction`
- Produces:
  - `MoTask.Core.Morning.CandidateRecord(string ExternalId, string Source, string Title, string Evidence, string From, string Link, string Reasoning, DateTime? ReceivedAt, DateOnly? SuggestedDueDate, string SuggestedProject, TriageAction SuggestedAction, int? MergeTargetTaskId)`
  - `MoTask.Core.Morning.MorningResult(IReadOnlyList<CandidateRecord> Candidates, int DiscardedLines, string PlanJson)` と `bool IsUsable => PlanJson.Length > 0`
  - `static MorningResult MorningResultReader.Read(string? candidatesJsonl, string? planJson)`
  - `static IReadOnlyList<string> MorningResultReader.PlanGroupKeys`（`today` / `ifTime` / `aiReady` / `waiting`）

- [ ] **Step 1: fixture を置く**

`tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl`（3行。正常系。**改行を含めず1件1行**）:

```
{"externalId":"outlook:AAMkAD001","source":"Outlook","receivedAt":"2026-09-07T07:42:00+09:00","from":"顧客A 山本さん","title":"請求先情報を更新する","evidence":"「9月8日までに新しい請求先へ変更をお願いします」","link":"https://outlook.office.com/mail/id/AAMkAD001","reasoning":"依頼が明確で期限の記述あり。既存タスクとの重複なし。","suggestedDueDate":"2026-09-08","suggestedProject":"顧客A","suggestedAction":"register","mergeTargetTaskId":null}
{"externalId":"teams:19:meeting@thread.v2#1725","source":"Teams","receivedAt":"2026-09-07T08:05:00+09:00","from":"部長","title":"Q4企画書の数値を差し替える","evidence":"「先週の速報値に差し替えてから出してください」","link":"https://teams.microsoft.com/l/message/1725","reasoning":"既存タスク『Q4企画書の内容を確定する』と同じ成果物への追加指示。","suggestedProject":"プロジェクトQ4","suggestedAction":"merge","mergeTargetTaskId":45}
{"externalId":"gmail:18f2c9","source":"Gmail","receivedAt":"2026-09-06T21:10:00+09:00","from":"採用サービス","title":"求人票の掲載内容を確認する","evidence":"「掲載期限は今月末です。内容をご確認ください」","link":"https://mail.google.com/mail/u/0/#inbox/18f2c9","reasoning":"期限は月末で今日でなくてよい。","suggestedAction":"later","mergeTargetTaskId":null}
```

`tests/MoTask.Core.Tests/Fixtures/morning-candidates-broken.jsonl`（5行。良い行1つと、捨てられるべき行4つ）:

```
{"externalId":"outlook:AAMkAD100","source":"Outlook","title":"見積書を送る","evidence":"「金曜までに見積を」","suggestedAction":"register"}
{"externalId":"outlook:AAMkAD101","source":"Outlook","title":"途中で切れた
{"externalId":"outlook:AAMkAD102","source":"Outlook","title":"根拠が無い","suggestedAction":"register"}
{"externalId":"outlook:AAMkAD103","source":"Outlook","title":"統合先が無い","evidence":"「前のとまとめて」","suggestedAction":"merge"}
{"externalId":"outlook:AAMkAD100","source":"Outlook","title":"同じ ExternalId の2件目","evidence":"「金曜までに見積を」","suggestedAction":"register"}
```

`tests/MoTask.Core.Tests/Fixtures/morning-plan.json`:

```json
{"date":"2026-09-07",
 "firstThing":{"taskId":45,"reason":"送付前に部長の確認が必要。今朝の候補『数値の差し替え』を統合したため確認範囲が1つ増えた"},
 "groups":[{"key":"today","items":[{"taskId":45},{"externalId":"outlook:AAMkAD001"}]},
           {"key":"ifTime","items":[{"taskId":52}]},
           {"key":"aiReady","items":[{"taskId":61}]},
           {"key":"waiting","items":[{"taskId":70}]}]}
```

`tests/MoTask.Core.Tests/Fixtures/morning-plan-broken.json`（`groups[].key` に4値以外が混ざっている）:

```json
{"date":"2026-09-07",
 "groups":[{"key":"today","items":[{"taskId":45}]},
           {"key":"someday","items":[{"taskId":52}]}]}
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningResultReaderTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// MoTask と Claude の唯一の接点（仕様 §8）。CLI や指示文が形を変えたらここが赤くなる。
/// fixture は初回の実機確認のあと、本物の result/ に差し替えること（README のチェックリスト参照）。
/// </summary>
public class MorningResultReaderTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MorningResult ReadFixtures(string candidates, string plan)
        => MorningResultReader.Read(Fixture(candidates), Fixture(plan));

    [Fact]
    public void Read_TakesEveryWellFormedCandidate()
    {
        var result = ReadFixtures("morning-candidates.jsonl", "morning-plan.json");

        result.Candidates.Should().HaveCount(3);
        result.DiscardedLines.Should().Be(0);
        result.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void Read_MapsEveryFieldOfACandidate()
    {
        var first = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[0];

        first.ExternalId.Should().Be("outlook:AAMkAD001");
        first.Source.Should().Be("Outlook");
        first.From.Should().Be("顧客A 山本さん");
        first.Title.Should().Be("請求先情報を更新する");
        first.Evidence.Should().Contain("9月8日までに");
        first.Link.Should().Be("https://outlook.office.com/mail/id/AAMkAD001");
        first.Reasoning.Should().Contain("依頼が明確");
        first.SuggestedProject.Should().Be("顧客A");
        first.SuggestedAction.Should().Be(TriageAction.Register);
        first.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        first.MergeTargetTaskId.Should().BeNull();
        // +09:00 の 07:42 は UTC の前日 22:42
        first.ReceivedAt.Should().Be(new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Read_KeepsTheMergeTarget()
    {
        var merge = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[1];

        merge.SuggestedAction.Should().Be(TriageAction.Merge);
        merge.MergeTargetTaskId.Should().Be(45);
    }

    [Fact]
    public void Read_AcceptsAnySourceString()
    {
        var gmail = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[2];

        gmail.Source.Should().Be("Gmail", "取り込み元は列挙しない（仕様 §4）");
        gmail.SuggestedAction.Should().Be(TriageAction.Later);
        gmail.SuggestedProject.Should().BeEmpty("欠けている項目は空扱い");
        gmail.SuggestedDueDate.Should().BeNull();
    }

    [Fact]
    public void Read_DropsBadLinesAndCountsThem()
    {
        var result = ReadFixtures("morning-candidates-broken.jsonl", "morning-plan.json");

        result.Candidates.Should().ContainSingle()
            .Which.ExternalId.Should().Be("outlook:AAMkAD100");
        result.DiscardedLines.Should().Be(4, "JSON 崩れ・根拠なし・統合先なし・重複の 4 行");
        result.IsUsable.Should().BeTrue("1 行の崩れで朝が全滅しない");
    }

    [Fact]
    public void Read_DoesNotCountBlankLinesAsDiscarded()
    {
        var result = MorningResultReader.Read("\n\n   \n", Fixture("morning-plan.json"));

        result.Candidates.Should().BeEmpty();
        result.DiscardedLines.Should().Be(0);
    }

    [Fact]
    public void Read_KeepsThePlanVerbatimWhenItIsValid()
    {
        var raw = Fixture("morning-plan.json");

        var result = MorningResultReader.Read(null, raw);

        result.PlanJson.Should().Be(raw.Trim(), "検証済みの生 JSON を 1 カラムに持つ（仕様 §9）");
        JsonDocument.Parse(result.PlanJson).RootElement
            .GetProperty("groups").GetArrayLength().Should().Be(4);
    }

    [Theory]
    [InlineData("morning-plan-broken.json")]
    public void Read_RejectsAPlanWithAnUnknownGroupKey(string fixture)
        => MorningResultReader.Read(null, Fixture(fixture)).PlanJson.Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    [InlineData("{\"date\":\"2026-09-07\"}")]
    [InlineData("{\"groups\":{}}")]
    [InlineData("{\"groups\":[{\"key\":\"today\"}]}")]
    public void Read_RejectsAnUnusablePlan(string? plan)
    {
        var result = MorningResultReader.Read(null, plan);

        result.PlanJson.Should().BeEmpty();
        result.IsUsable.Should().BeFalse("プランが読めない実行は Failed（仕様 §8）");
    }

    [Fact]
    public void Read_SucceedsWithZeroCandidates_WhenThePlanIsValid()
    {
        var result = MorningResultReader.Read("", Fixture("morning-plan.json"));

        result.Candidates.Should().BeEmpty();
        result.IsUsable.Should().BeTrue("コネクタ未認証や候補が無い朝は失敗ではない（仕様 §8）");
    }

    [Fact]
    public void PlanGroupKeys_AreTheFourFixedOnes()
        => MorningResultReader.PlanGroupKeys.Should().Equal("today", "ifTime", "aiReady", "waiting");
}
```

- [ ] **Step 3: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningResultReaderTests`
Expected: コンパイルエラー（`MoTask.Core.Morning` 名前空間が存在しない）

- [ ] **Step 4: リーダーを書く**

`src/MoTask.Core/Morning/CandidateRecord.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// candidates.jsonl の 1 行を検証したもの（仕様 §8）。ここから先は DB のエンティティに写すだけ。
/// </summary>
public sealed record CandidateRecord(
    string ExternalId,
    string Source,
    string Title,
    string Evidence,
    string From,
    string Link,
    string Reasoning,
    DateTime? ReceivedAt,
    DateOnly? SuggestedDueDate,
    string SuggestedProject,
    TriageAction SuggestedAction,
    int? MergeTargetTaskId);
```

`src/MoTask.Core/Morning/MorningResult.cs`:

```csharp
namespace MoTask.Core.Morning;

/// <summary>
/// result/ を 1 回読んだ結果。DiscardedLines は画面に出す（黙って減らさない・仕様 §8）。
/// </summary>
public sealed record MorningResult(
    IReadOnlyList<CandidateRecord> Candidates,
    int DiscardedLines,
    string PlanJson)
{
    /// <summary>取り込んでよいか。候補 0 件でもプランが妥当なら成功（仕様 §8）。</summary>
    public bool IsUsable => PlanJson.Length > 0;
}
```

`src/MoTask.Core/Morning/MorningResultReader.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// result/ の形を知る唯一の場所（仕様 §8）。HookEventParser が CLI 出力への依存を 1 点に
/// 閉じているのと同じ役割で、同じく純関数にして fixture から固定する。
/// 行単位で検証し、読めない行は捨てて残りを返す。1 行の JSON 崩れで朝を全滅させない。
/// </summary>
public static class MorningResultReader
{
    /// <summary>plan.json の groups[].key に許す 4 値（仕様 §8）。</summary>
    public static readonly IReadOnlyList<string> PlanGroupKeys =
        new[] { "today", "ifTime", "aiReady", "waiting" };

    public static MorningResult Read(string? candidatesJsonl, string? planJson)
    {
        var candidates = ReadCandidates(candidatesJsonl, out var discarded);
        return new MorningResult(candidates, discarded, ReadPlan(planJson));
    }

    private static IReadOnlyList<CandidateRecord> ReadCandidates(string? text, out int discarded)
    {
        discarded = 0;
        var records = new List<CandidateRecord>();
        if (string.IsNullOrWhiteSpace(text)) return records;

        // 同じファイルに同じ ExternalId が 2 度出てきたら 2 件目以降は捨てる。
        // DB の一意インデックスに任せると SaveChanges ごと落ちるので、ここで先に落とす。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue; // 空行は「読めなかった行」に数えない
            var record = Parse(trimmed);
            if (record is null || !seen.Add(record.ExternalId))
            {
                discarded++;
                continue;
            }
            records.Add(record);
        }
        return records;
    }

    private static CandidateRecord? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            var externalId = Text(root, "externalId");
            var source = Text(root, "source");
            var title = Text(root, "title");
            // 根拠の無い候補は人が判断できないので捨てる（仕様 §8）
            var evidence = Text(root, "evidence");
            if (externalId.Length == 0 || source.Length == 0 || title.Length == 0 || evidence.Length == 0) return null;

            if (!TryAction(Text(root, "suggestedAction"), out var action)) return null;
            var mergeTarget = Int(root, "mergeTargetTaskId");
            if (action == TriageAction.Merge && mergeTarget is null) return null;

            return new CandidateRecord(
                externalId, source, title, evidence,
                Text(root, "from"), Text(root, "link"), Text(root, "reasoning"),
                Instant(root, "receivedAt"), Date(root, "suggestedDueDate"),
                Text(root, "suggestedProject"), action, mergeTarget);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>妥当なら原文をそのまま返す（生 JSON を 1 カラムに持つ・仕様 §9）。壊れていれば空文字。</summary>
    private static string ReadPlan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array) return "";

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) return "";
                if (!PlanGroupKeys.Contains(Text(group, "key"))) return "";
                if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return "";
            }
            return text.Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static bool TryAction(string value, out TriageAction action)
    {
        switch (value)
        {
            case "register": action = TriageAction.Register; return true;
            case "merge": action = TriageAction.Merge; return true;
            case "later": action = TriageAction.Later; return true;
            case "reject": action = TriageAction.Reject; return true;
            default: action = TriageAction.Register; return false;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>オフセット付き ISO8601 を UTC へ寄せる。読めなければ null（行そのものは捨てない）。</summary>
    private static DateTime? Instant(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text.Length > 0
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.UtcDateTime
            : null;
    }

    private static DateOnly? Date(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text.Length > 0
               && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }
}
```

- [ ] **Step 5: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningResultReaderTests`
Expected: PASS（20 件前後）

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.Core/Morning tests/MoTask.Core.Tests/Fixtures tests/MoTask.Core.Tests/MorningResultReaderTests.cs
git commit -m "$(cat <<'EOF'
feat(core): read result/ into candidates and a validated plan

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: BoardSnapshot（Claude に渡す盤面）

**Files:**
- Create: `src/MoTask.Core/Morning/BoardSnapshot.cs`
- Test: `tests/MoTask.Core.Tests/BoardSnapshotTests.cs`

**Interfaces:**
- Consumes: 既存の `Board` / `Column` / `TaskItem` / `ColumnRole`
- Produces: `static string BoardSnapshot.Build(Board board, DateOnly date, IReadOnlyDictionary<int, string> projectNames, IReadOnlyCollection<int> taskIdsWithActiveAiJob)`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardSnapshotTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// board.json（仕様 §7）。文字列比較ではなく JSON として読み直して見る
/// （整形を変えてもテストが割れないように）。
/// </summary>
public class BoardSnapshotTests
{
    private static readonly DateTime Updated = new(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

    private static Board Sample()
    {
        var backlog = new Column { Id = 1, Name = "やること", Order = 0, Role = ColumnRole.Backlog };
        var active = new Column { Id = 2, Name = "今日中", Order = 1, Role = ColumnRole.Active };
        var done = new Column { Id = 3, Name = "完了", Order = 2, Role = ColumnRole.Done };

        active.Tasks.Add(new TaskItem
        {
            Id = 45, Title = "Q4企画書の内容を確定する", ColumnId = 2, Position = 0, ProjectId = 100,
            DueDate = new DateOnly(2026, 9, 8), CreatedAt = Updated, UpdatedAt = Updated,
            Labels = { new Label { Id = 200, Name = "社内", Color = "accent-500" } },
        });
        backlog.Tasks.Add(new TaskItem
        {
            Id = 46, Title = "消したもの", ColumnId = 1, Position = 0,
            CreatedAt = Updated, UpdatedAt = Updated, DeletedAt = Updated,
        });
        done.Tasks.Add(new TaskItem
        {
            Id = 47, Title = "終わったもの", ColumnId = 3, Position = 0,
            CreatedAt = Updated, UpdatedAt = Updated, CompletedAt = Updated,
        });

        var board = new Board { Id = 1, Name = "テスト" };
        board.Columns.AddRange(new[] { backlog, active, done });
        return board;
    }

    private static readonly Dictionary<int, string> Projects = new() { [100] = "プロジェクトQ4" };

    private static JsonElement Build(Board? board = null, IReadOnlyCollection<int>? busy = null)
        => JsonDocument.Parse(BoardSnapshot.Build(
                board ?? Sample(), new DateOnly(2026, 9, 7), Projects, busy ?? Array.Empty<int>()))
            .RootElement.Clone();

    [Fact]
    public void Build_WritesTheDateAndEveryColumn()
    {
        var root = Build();

        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Equal("やること", "今日中", "完了");
        root.GetProperty("columns")[1].GetProperty("id").GetInt32().Should().Be(2);
        root.GetProperty("columns")[1].GetProperty("role").GetString().Should().Be("Active");
    }

    [Fact]
    public void Build_WritesEveryFieldOfATask()
    {
        var task = Build().GetProperty("tasks").EnumerateArray().Single();

        task.GetProperty("id").GetInt32().Should().Be(45);
        task.GetProperty("title").GetString().Should().Be("Q4企画書の内容を確定する");
        task.GetProperty("columnId").GetInt32().Should().Be(2);
        task.GetProperty("columnRole").GetString().Should().Be("Active");
        task.GetProperty("project").GetString().Should().Be("プロジェクトQ4");
        task.GetProperty("due").GetString().Should().Be("2026-09-08");
        task.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Should().Equal("社内");
        task.GetProperty("updatedAt").GetString().Should().Be("2026-09-05T10:00:00Z");
        task.GetProperty("hasActiveAiJob").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Build_LeavesOutDeletedAndDoneTasks()
    {
        var ids = Build().GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("id").GetInt32()).ToList();

        ids.Should().Equal(45);
    }

    [Fact]
    public void Build_MarksTasksThatAlreadyHaveAnAiJob()
        => Build(busy: new[] { 45 }).GetProperty("tasks").EnumerateArray().Single()
            .GetProperty("hasActiveAiJob").GetBoolean().Should()
            .BeTrue("プランの『AI 準備完了』区分に要る（仕様 §7）");

    [Fact]
    public void Build_WritesNullForAMissingProjectAndDueDate()
    {
        var board = Sample();
        board.Columns[1].Tasks[0].ProjectId = null;
        board.Columns[1].Tasks[0].DueDate = null;

        var task = Build(board).GetProperty("tasks").EnumerateArray().Single();

        task.GetProperty("project").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("due").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Build_DoesNotEscapeJapaneseText()
        => BoardSnapshot.Build(Sample(), new DateOnly(2026, 9, 7), Projects, Array.Empty<int>())
            .Should().Contain("今日中", "人が開いて読めるファイルにする");
}
```

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~BoardSnapshotTests`
Expected: コンパイルエラー（`BoardSnapshot` が存在しない）

- [ ] **Step 3: 実装する**

`src/MoTask.Core/Morning/BoardSnapshot.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// board.json（仕様 §7）。統合先の推薦とプラン作成には現在の盤面が要るが、
/// SQLite を直接読ませず MoTask がスナップショットを書く。
/// </summary>
public static class BoardSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 人が開いて読めるように、日本語やスラッシュを \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(
        Board board,
        DateOnly date,
        IReadOnlyDictionary<int, string> projectNames,
        IReadOnlyCollection<int> taskIdsWithActiveAiJob)
    {
        var ordered = board.Columns.OrderBy(c => c.Order).ToList();
        var columns = ordered.Select(c => new ColumnDto(c.Id, c.Name, c.Role.ToString())).ToList();

        var tasks = new List<TaskDto>();
        foreach (var column in ordered)
        {
            // 未完了タスクだけを渡す（仕様 §6）。論理削除済みも含めない（仕様 §7）。
            if (column.Role == ColumnRole.Done) continue;
            foreach (var task in column.Tasks.Where(t => t.DeletedAt is null).OrderBy(t => t.Position))
            {
                tasks.Add(new TaskDto(
                    task.Id,
                    task.Title,
                    column.Id,
                    column.Role.ToString(),
                    task.ProjectId is int id && projectNames.TryGetValue(id, out var name) ? name : null,
                    task.DueDate?.ToString("yyyy-MM-dd"),
                    task.Labels.Select(l => l.Name).ToList(),
                    DateTime.SpecifyKind(task.UpdatedAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    taskIdsWithActiveAiJob.Contains(task.Id)));
            }
        }

        return JsonSerializer.Serialize(
            new SnapshotDto(date.ToString("yyyy-MM-dd"), columns, tasks), Options);
    }

    private sealed record SnapshotDto(string Date, IReadOnlyList<ColumnDto> Columns, IReadOnlyList<TaskDto> Tasks);

    private sealed record ColumnDto(int Id, string Name, string Role);

    private sealed record TaskDto(
        int Id, string Title, int ColumnId, string ColumnRole, string? Project,
        string? Due, IReadOnlyList<string> Labels, string UpdatedAt, bool HasActiveAiJob);
}
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~BoardSnapshotTests`
Expected: PASS（6 件）

- [ ] **Step 5: コミット**

```
git add src/MoTask.Core/Morning/BoardSnapshot.cs tests/MoTask.Core.Tests/BoardSnapshotTests.cs
git commit -F- <<'MSG'
feat(core): snapshot the board for the morning session

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 4: ジョブフォルダの共有部を朝の実行でも使えるようにする

仕様 §5 は「`IJobFolder` を共有する」と言うが、現状の `Create` は置き場所が `jobs/` 固定、
作る出力フォルダも `artifacts/` 固定で、任意ファイルの読み書きができない。
**既存の AI 遂行の振る舞いを一切変えない**（既定値が現状と同じ）ことが受け入れ条件。

**Files:**
- Modify: `src/MoTask.Core/Ai/JobFolderPaths.cs`
- Modify: `src/MoTask.Core/Ai/JobFolderRequest.cs`
- Modify: `src/MoTask.Core/Ai/IJobFolder.cs`
- Modify: `src/MoTask.App/Ai/JobFolder.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs`
- Test: `tests/MoTask.Core.Tests/JobFolderPathsTests.cs`（追記）
- Test: `tests/MoTask.App.Tests/JobFolderTests.cs`（追記）

**Interfaces:**
- Consumes: 既存の `IJobFolder` / `JobFolderPaths` / `JobFolderRequest`
- Produces:
  - `JobFolderPaths` の定数 `MorningDirectoryName`(`"morning"`) / `ArtifactsDirectoryName`(`"artifacts"`) / `ResultDirectoryName`(`"result"`) / `JobJsonName` / `InstructionMarkdownName` / `HooksJsonName` / `EventsJsonlName` / `BoardJsonName` / `RunJsonName`、および `static readonly string CandidatesRelativePath` / `PlanRelativePath`
  - `JobFolderPaths` のプロパティ `ResultDirectory` / `BoardJson` / `RunJson` / `CandidatesJsonl` / `PlanJson`
  - `JobFolderRequest` の init プロパティ `Category`（既定 `"jobs"`）/ `OutputDirectoryName`（既定 `"artifacts"`）
  - `string IJobFolder.ResolveRoot(JobFolderRequest request)`
  - `Result IJobFolder.WriteText(string root, string relativePath, string content)`
  - `string? IJobFolder.ReadText(string root, string relativePath)`
  - `FakeJobFolder.Put(string root, string relativePath, string content)`（テストが Claude の出力を置く）

- [ ] **Step 1: 失敗するテストを書く（Core・パスの組み立て）**

`tests/MoTask.Core.Tests/JobFolderPathsTests.cs` のクラス内に足す（`using System.IO;` が無ければ足す）:

```csharp
    [Fact]
    public void MorningFolder_IsNumberedAndDated()
        => JobFolderPaths.FolderName(7, "2026-09-07").Should().Be("0007-2026-09-07",
            "仕様 §6 の morning/0007-2026-09-07");

    [Fact]
    public void MorningPaths_PointIntoTheResultFolder()
    {
        var paths = JobFolderPaths.For(@"C:\work\morning\0007-2026-09-07");

        paths.BoardJson.Should().Be(@"C:\work\morning\0007-2026-09-07\board.json");
        paths.RunJson.Should().Be(@"C:\work\morning\0007-2026-09-07\run.json");
        paths.ResultDirectory.Should().Be(@"C:\work\morning\0007-2026-09-07\result");
        paths.CandidatesJsonl.Should().Be(@"C:\work\morning\0007-2026-09-07\result\candidates.jsonl");
        paths.PlanJson.Should().Be(@"C:\work\morning\0007-2026-09-07\result\plan.json");
    }

    [Fact]
    public void RelativePaths_MatchTheAbsoluteOnes()
    {
        var root = @"C:\work\morning\0007-2026-09-07";
        var paths = JobFolderPaths.For(root);

        Path.Combine(root, JobFolderPaths.CandidatesRelativePath).Should().Be(paths.CandidatesJsonl);
        Path.Combine(root, JobFolderPaths.PlanRelativePath).Should().Be(paths.PlanJson);
        Path.Combine(root, JobFolderPaths.BoardJsonName).Should().Be(paths.BoardJson);
        Path.Combine(root, JobFolderPaths.RunJsonName).Should().Be(paths.RunJson);
    }

    [Fact]
    public void JobFolderRequest_DefaultsToTheExistingLayout()
    {
        var request = new JobFolderRequest(1, "見積り", "やること");

        request.Category.Should().Be("jobs", "既存の AI 遂行の置き場所を変えない");
        request.OutputDirectoryName.Should().Be("artifacts");
    }
```

- [ ] **Step 2: 失敗するテストを書く（App・実ファイル）**

`tests/MoTask.App.Tests/JobFolderTests.cs` のクラス内に足す。既存テストの下ごしらえ
（一時フォルダを `DefaultWorkingDirectory` に向け、`HooksExecutable` をダミーに差し替える）を
private ヘルパー `NewFolder()` に切り出してから使う。既に相当するヘルパーがあればそれを使う:

```csharp
    private static JobFolderRequest MorningRequest() =>
        new(7, "2026-09-07", "指示")
        {
            Category = JobFolderPaths.MorningDirectoryName,
            OutputDirectoryName = JobFolderPaths.ResultDirectoryName,
        };

    [Fact]
    public void Create_ForTheMorning_PutsTheFolderUnderMorningAndMakesResult()
    {
        var folder = NewFolder();

        var root = folder.Create(MorningRequest());

        root.IsSuccess.Should().BeTrue(root.Error);
        root.Value!.Should().EndWith(Path.Combine("morning", "0007-2026-09-07"));
        var paths = JobFolderPaths.For(root.Value!);
        Directory.Exists(paths.ResultDirectory).Should().BeTrue();
        Directory.Exists(paths.ArtifactsDirectory).Should().BeFalse("朝の実行に artifacts/ は要らない");
        File.ReadAllText(paths.InstructionMarkdown).Should().Be("指示");
        File.Exists(paths.HooksJson).Should().BeTrue();
    }

    [Fact]
    public void ResolveRoot_GivesTheSamePathWithoutTouchingTheDisk()
    {
        var folder = NewFolder();
        var request = MorningRequest();

        var resolved = folder.ResolveRoot(request);

        Directory.Exists(resolved).Should().BeFalse("指示文にパスを埋めるために先に知りたいだけ");
        folder.Create(request).Value.Should().Be(resolved);
    }

    [Fact]
    public void WriteText_AndReadText_RoundTripThroughSubfolders()
    {
        var folder = NewFolder();
        var root = folder.Create(MorningRequest()).Value!;

        folder.WriteText(root, JobFolderPaths.BoardJsonName, "{\"date\":\"2026-09-07\"}").IsSuccess.Should().BeTrue();
        folder.WriteText(root, JobFolderPaths.CandidatesRelativePath, "1行目\n2行目").IsSuccess.Should().BeTrue();

        folder.ReadText(root, JobFolderPaths.BoardJsonName).Should().Be("{\"date\":\"2026-09-07\"}");
        folder.ReadText(root, JobFolderPaths.CandidatesRelativePath).Should().Be("1行目\n2行目");
    }

    [Fact]
    public void ReadText_ReturnsNull_WhenTheFileIsNotThereYet()
    {
        var folder = NewFolder();
        var root = folder.Create(MorningRequest()).Value!;

        folder.ReadText(root, JobFolderPaths.PlanRelativePath).Should()
            .BeNull("Claude がまだ書いていないだけで、失敗ではない");
        folder.ReadText("", JobFolderPaths.PlanRelativePath).Should().BeNull();
    }

    [Fact]
    public void Create_ForAJob_StillMakesArtifactsUnderJobs()
    {
        var folder = NewFolder();

        var root = folder.Create(new JobFolderRequest(1, "見積り", "やること"));

        root.Value!.Should().EndWith(Path.Combine("jobs", "0001-見積り"));
        Directory.Exists(JobFolderPaths.For(root.Value!).ArtifactsDirectory).Should().BeTrue();
    }
```

- [ ] **Step 3: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~JobFolderPathsTests`
Expected: コンパイルエラー（`MorningDirectoryName` などが無い）

- [ ] **Step 4: パスと要求を広げる**

`src/MoTask.Core/Ai/JobFolderPaths.cs` の定数・プロパティ部分を次で置き換える
（`MaxSlugLength` / `For` / `FolderName` / `Slug` は既存のまま残す）:

```csharp
    /// <summary>既定ワークフォルダ直下の、ジョブフォルダを集める場所。</summary>
    public const string JobsDirectoryName = "jobs";

    /// <summary>朝の実行のフォルダを集める場所（仕様 §6）。</summary>
    public const string MorningDirectoryName = "morning";

    /// <summary>AI 遂行の成果物。Create が併せて作る。</summary>
    public const string ArtifactsDirectoryName = "artifacts";

    /// <summary>朝の実行の成果物。Create が併せて作る。</summary>
    public const string ResultDirectoryName = "result";

    public const string JobJsonName = "job.json";
    public const string InstructionMarkdownName = "instruction.md";
    public const string HooksJsonName = "hooks.json";
    public const string EventsJsonlName = "events.jsonl";
    public const string BoardJsonName = "board.json";
    public const string RunJsonName = "run.json";

    /// <summary>ルートからの相対パス。IJobFolder.WriteText / ReadText に渡す。</summary>
    public static readonly string CandidatesRelativePath = Path.Combine(ResultDirectoryName, "candidates.jsonl");

    public static readonly string PlanRelativePath = Path.Combine(ResultDirectoryName, "plan.json");

    public string JobJson => Path.Combine(Root, JobJsonName);
    public string InstructionMarkdown => Path.Combine(Root, InstructionMarkdownName);
    public string HooksJson => Path.Combine(Root, HooksJsonName);
    public string EventsJsonl => Path.Combine(Root, EventsJsonlName);
    public string ArtifactsDirectory => Path.Combine(Root, ArtifactsDirectoryName);
    public string ResultDirectory => Path.Combine(Root, ResultDirectoryName);
    public string BoardJson => Path.Combine(Root, BoardJsonName);
    public string RunJson => Path.Combine(Root, RunJsonName);
    public string CandidatesJsonl => Path.Combine(Root, CandidatesRelativePath);
    public string PlanJson => Path.Combine(Root, PlanRelativePath);
```

`src/MoTask.Core/Ai/JobFolderRequest.cs` を丸ごと置き換える:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// ジョブフォルダを作るのに要る材料。親フォルダは実装が設定から決める。
/// Category / OutputDirectoryName の既定は AI 遂行の現状と同じなので、既存の呼び出しは変わらない。
/// </summary>
public sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction)
{
    /// <summary>既定ワークフォルダ直下のどこに置くか（jobs / morning）。</summary>
    public string Category { get; init; } = JobFolderPaths.JobsDirectoryName;

    /// <summary>Create が併せて作る出力フォルダ（artifacts / result）。</summary>
    public string OutputDirectoryName { get; init; } = JobFolderPaths.ArtifactsDirectoryName;
}
```

`src/MoTask.Core/Ai/IJobFolder.cs` に3つ足す（既存の4メンバーはそのまま）:

```csharp
    /// <summary>
    /// フォルダを作らずにルートのパスだけ決める。指示文がフォルダ内のパスを含む朝の実行で、
    /// Create に渡す前に知る必要がある。
    /// </summary>
    string ResolveRoot(JobFolderRequest request);

    /// <summary>ルートからの相対パスにテキストを書く（途中のフォルダは作る）。</summary>
    Result WriteText(string root, string relativePath, string content);

    /// <summary>
    /// ルートからの相対パスを読む。フォルダやファイルが無ければ null。
    /// 読めなくても投げない（Claude がまだ書いていないだけのことが多い）。
    /// </summary>
    string? ReadText(string root, string relativePath);
```

- [ ] **Step 5: App の実装を合わせる**

`src/MoTask.App/Ai/JobFolder.cs`。`Create` の頭を差し替え、3メソッドを足す:

```csharp
    public string ResolveRoot(JobFolderRequest request)
        => Path.Combine(
            _settings.Load().DefaultWorkingDirectory,
            request.Category,
            JobFolderPaths.FolderName(request.JobId, request.TaskTitle));

    public Result<string> Create(JobFolderRequest request)
    {
        // フックが無いと端末は動くが盤面が一切追従しない。黙って走らせず、開始時に止める。
        if (!File.Exists(HooksExecutable)) return Result.Fail<string>(Messages.HooksExecutableNotFound);

        var root = ResolveRoot(request);
        var paths = JobFolderPaths.For(root);
        try
        {
            // 既にあっても作り直さない（--resume で開き直すときに同じフォルダへ戻る）
            Directory.CreateDirectory(Path.Combine(root, request.OutputDirectoryName));
            File.WriteAllText(paths.InstructionMarkdown, request.Instruction, Utf8);
            File.WriteAllText(paths.HooksJson, HooksJson.Build(HooksExecutable, paths.EventsJsonl), Utf8);
            return Result.Ok(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail<string>(string.Format(Messages.JobFolderFailedFormat, root, ex.Message));
        }
    }

    public Result WriteText(string root, string relativePath, string content)
    {
        try
        {
            var path = Path.Combine(root, relativePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, content, Utf8);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail(string.Format(Messages.JobFolderFailedFormat, root, ex.Message));
        }
    }

    public string? ReadText(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var path = Path.Combine(root, relativePath);
            if (!File.Exists(path)) return null;
            // Claude が書いている最中でも読めるように共有を広く取る（ReadTail と同じ理由）。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 読めないだけ。呼び出し側は「まだ書かれていない」と同じに扱う。
            return null;
        }
    }
```

- [ ] **Step 6: テスト用の偽物を合わせる**

`tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs`。`Create` を差し替え、以下を足す:

```csharp
    /// <summary>WriteText / Put で置かれたもの。キーは「ルート|相対パス」。</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Result? WriteFailure { get; set; }

    public string ResolveRoot(JobFolderRequest request)
        => $@"C:\work\{request.Category}\{request.JobId:0000}-{request.TaskTitle}";

    public Result<string> Create(JobFolderRequest request)
    {
        Created.Add(request);
        if (CreateFailure is { } failure) return failure;
        return Result.Ok(ResolveRoot(request));
    }

    public Result WriteText(string root, string relativePath, string content)
    {
        if (WriteFailure is { } failure) return failure;
        Files[Key(root, relativePath)] = content;
        return Result.Ok();
    }

    public string? ReadText(string root, string relativePath)
        => Files.TryGetValue(Key(root, relativePath), out var content) ? content : null;

    /// <summary>テストが Claude の書いたファイルを置く。</summary>
    public void Put(string root, string relativePath, string content)
        => Files[Key(root, relativePath)] = content;

    private static string Key(string root, string relativePath)
        => root + "|" + relativePath.Replace('/', '\\');
```

既存の `AiJobServiceLifecycleTests` は `C:\work\jobs\0001-見積り` を期待している。
`Category` の既定が `"jobs"` なので `ResolveRoot` の結果は今までと同じ文字列になる。

- [ ] **Step 7: すべてのテストが通ることを確認する**

Run: `dotnet test MoTask.sln`
Expected: PASS。**既存の AI 遂行のテストが1件も落ちないこと**が受け入れ条件

- [ ] **Step 8: コミット**

```
git add src/MoTask.Core/Ai src/MoTask.App/Ai/JobFolder.cs tests/MoTask.Core.Tests tests/MoTask.App.Tests/JobFolderTests.cs
git commit -F- <<'MSG'
refactor(ai): let the job folder host the morning run too

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 5: instruction.md の組み立てと、AI 設定での編集

`instruction.md` は2つの部分でできている。**前半**は「どう集めるか」の方針で、人が AI 設定で
書き換えられる。**後半**は出力先の実パスと JSON の形、つまり MoTask と Claude の契約で、
こちらは MoTask が必ず付ける（書き換えさせない）。契約を人に編集させると、
`MorningResultReader` が読める形との対応が黙って壊れるため。

**Files:**
- Create: `src/MoTask.Core/Morning/MorningInstruction.cs`
- Modify: `src/MoTask.Core/Ai/AiSettings.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs`
- Modify: `src/MoTask.Data/JsonAiSettingsStore.cs`
- Modify: `src/MoTask.App/ViewModels/AiSettingsViewModel.cs`
- Modify: `src/MoTask.App/Views/AiSettingsDialog.xaml`
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Test: `tests/MoTask.Core.Tests/MorningInstructionTests.cs`
- Test: `tests/MoTask.Data.Tests/SettingsStoreTests.cs`（追記）
- Test: `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`（追記）

**Interfaces:**
- Consumes: Task 4 の `JobFolderPaths`（`BoardJson` / `CandidatesJsonl` / `PlanJson`）
- Produces:
  - `AiSettings` の末尾パラメータ `string? MorningInstruction = null`
  - `static string MorningInstruction.DefaultTemplate`
  - `static string MorningInstruction.Build(string? template, JobFolderPaths paths, DateOnly date)`
  - `Messages.MorningInstructionDefault` / `Messages.MorningInstructionContractFormat`
  - `Strings.SettingsMorningInstruction`
  - `AiSettingsViewModel.MorningInstruction`（`[ObservableProperty]`）

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningInstructionTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// instruction.md（仕様 §6）。前半は人が編集できる収集方針、後半は MoTask が必ず付ける契約。
/// </summary>
public class MorningInstructionTests
{
    private static readonly JobFolderPaths Paths = JobFolderPaths.For(@"C:\work\morning\0007-2026-09-07");
    private static readonly DateOnly Date = new(2026, 9, 7);

    [Fact]
    public void Build_UsesTheDefaultTemplate_WhenNothingIsConfigured()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        text.Should().StartWith(MorningInstruction.DefaultTemplate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Build_FallsBackToTheDefault_ForABlankTemplate(string template)
        => MorningInstruction.Build(template, Paths, Date).Should().StartWith(MorningInstruction.DefaultTemplate);

    [Fact]
    public void Build_UsesTheConfiguredTemplate_WhenThereIsOne()
    {
        var text = MorningInstruction.Build("  自分で書いた方針  ", Paths, Date);

        text.Should().StartWith("自分で書いた方針");
        text.Should().NotContain(MorningInstruction.DefaultTemplate);
    }

    [Fact]
    public void Build_AlwaysAppendsTheContract_EvenWithACustomTemplate()
    {
        var text = MorningInstruction.Build("自分で書いた方針", Paths, Date);

        text.Should().Contain(Paths.CandidatesJsonl, "出力先は MoTask が決める");
        text.Should().Contain(Paths.PlanJson);
        text.Should().Contain(Paths.BoardJson);
        text.Should().Contain("2026-09-07");
    }

    [Fact]
    public void Build_SpellsOutTheFourSuggestedActions()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        foreach (var action in new[] { "register", "merge", "later", "reject" })
            text.Should().Contain(action);
    }

    [Fact]
    public void Build_SpellsOutTheFourPlanGroupKeys()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        foreach (var key in MorningResultReader.PlanGroupKeys) text.Should().Contain(key);
    }

    [Fact]
    public void Build_DoesNotNameAnySpecificConnector()
    {
        var text = MorningInstruction.Build(null, Paths, Date);

        // 取り込み元は列挙しない（仕様 §4）。例として出す JSON の中の値は別（そこは形の説明）。
        MorningInstruction.DefaultTemplate.Should().NotContain("Outlook");
        MorningInstruction.DefaultTemplate.Should().NotContain("Gmail");
        MorningInstruction.DefaultTemplate.Should().NotContain("Teams");
        text.Should().Contain("認証済み", "コネクタが 0 でも候補 0 件は失敗ではないと伝える");
    }

    [Fact]
    public void Build_EndsWithASingleNewline()
        => MorningInstruction.Build(null, Paths, Date).Should().EndWith("\n").And.NotEndWith("\n\n");
}
```

`tests/MoTask.Data.Tests/SettingsStoreTests.cs` のクラス内に足す（既存テストの一時ファイルの
作り方に合わせる。既存が `NewStore()` 相当のヘルパーを持っていればそれを使う）:

```csharp
    [Fact]
    public void MorningInstruction_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonAiSettingsStore(path);

        store.Save(AiSettings.Default() with { MorningInstruction = "私の方針\n2行目" });

        new JsonAiSettingsStore(path).Load().MorningInstruction.Should().Be("私の方針\n2行目");
    }

    [Fact]
    public void MorningInstruction_IsNull_WhenTheFileDoesNotHaveIt()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"DefaultWorkingDirectory\":\"C:\\\\work\"}");

        new JsonAiSettingsStore(path).Load().MorningInstruction.Should().BeNull("既定のテンプレートを使う");
    }
```

`tests/MoTask.App.Tests/AiSettingsViewModelTests.cs` のクラス内に足す:

```csharp
    [Fact]
    public void Save_KeepsTheMorningInstruction()
    {
        var store = new InMemorySettingsStore();
        var vm = new AiSettingsViewModel(store) { MorningInstruction = "  私の方針  " };

        vm.SaveCommand.Execute(null);

        store.Settings.MorningInstruction.Should().Be("私の方針");
    }

    [Fact]
    public void Save_ClearsTheMorningInstruction_WhenTheBoxIsEmptied()
    {
        var store = new InMemorySettingsStore { Settings = AiSettings.Default() with { MorningInstruction = "前の方針" } };
        var vm = new AiSettingsViewModel(store) { MorningInstruction = "" };

        vm.SaveCommand.Execute(null);

        store.Settings.MorningInstruction.Should().BeNull("空にしたら既定のテンプレートへ戻る");
    }
```

`InMemorySettingsStore` は `MoTask.Core.Tests.Fakes` にあるので、App のテストで使えなければ
同じ内容の小さな偽物を `tests/MoTask.App.Tests` に置く（既存の `AiSettingsViewModelTests` が
すでに何か使っているはずなので、まずそれに合わせること）。

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningInstructionTests`
Expected: コンパイルエラー（`MorningInstruction` が存在しない）

- [ ] **Step 3: 文言を resx に足す**

`src/MoTask.Core/Resources/Messages.resx` の `</root>` の直前に足す。
**`{{` と `}}` は `string.Format` が `{` `}` に戻す。JSON の例をそのまま出すために必要**:

```xml
  <data name="MorningInstructionDefault" xml:space="preserve"><value># 朝のタスク候補の収集

あなたが今アクセスできる受信箱・チャット・カレンダーを見て、今日のタスク候補を出してください。
どのサービスから取るかはこちらでは指定しません。認証済みのコネクタで届く範囲を自分で決めて集めてください。
認証済みのコネクタが 1 つも無ければ、候補は 0 件で構いません。それは失敗ではありません。

候補にするのは「人が何かをする必要があるもの」だけです。通知・広告・自動送信のものは候補にしないでください。
1 件ごとに、元の文面から根拠となる一文を引用してください。引用できないものは候補にしないでください。</value></data>
  <data name="MorningInstructionContractFormat" xml:space="preserve"><value>## 出力の契約（この節は MoTask が書いています。変更しないでください）

対象日は {0} です。
現在の盤面は {1} にあります。統合先の推薦と今日のプランは、必ずこのファイルの内容に基づいてください。

### 1. 候補: {2}

1 行 1 件の JSON Lines で書いてください。1 行は次の形です（改行を含めないこと）。

{{"externalId":"outlook:AAMkAD001","source":"Outlook","receivedAt":"2026-09-07T07:42:00+09:00","from":"顧客A 山本さん","title":"請求先情報を更新する","evidence":"「9月8日までに新しい請求先へ…」","link":"https://outlook.office.com/...","reasoning":"依頼が明確で期限の記述あり。既存タスクとの重複なし。","suggestedDueDate":"2026-09-08","suggestedProject":"顧客A","suggestedAction":"register","mergeTargetTaskId":null}}

- externalId / source / title / evidence / suggestedAction は必須です。欠けている行は捨てられます。
- externalId は再実行しても同じ値になるようにしてください（例: サービス名:元のID）。一度片づけた候補を二度出さないための鍵です。
- suggestedAction は register / merge / later / reject の 4 つだけです。
- suggestedAction が merge のときは、mergeTargetTaskId に {1} の tasks[].id を必ず入れてください。
- 決めるのは人です。suggestedAction は推薦であって実行ではありません。

### 2. プラン: {3}

次の形の JSON を 1 つ書いてください。

{{"date":"{0}","firstThing":{{"taskId":45,"reason":"送付前に部長の確認が必要"}},"groups":[{{"key":"today","items":[{{"taskId":45}},{{"externalId":"outlook:AAMkAD001"}}]}},{{"key":"ifTime","items":[]}},{{"key":"aiReady","items":[]}},{{"key":"waiting","items":[]}}]}}

- groups[].key は today / ifTime / aiReady / waiting の 4 つに固定です。
- items の各要素は taskId（{1} にあるタスク）か externalId（今朝の候補）のどちらか一方を持ちます。
- firstThing も taskId か externalId のどちらかを指し、reason に理由を 1 文で書いてください。
- aiReady には、{1} の hasActiveAiJob が false で、AI に任せられるものを入れてください。
- 候補が 0 件でも、このファイルは必ず書いてください。

両方のファイルを書き終えたら、何を何件書いたかを 1 行で報告してください。</value></data>
  <data name="MorningRunAlreadyRunning" xml:space="preserve"><value>朝の実行がまだ終わっていません。先に取り込むか、追跡をやめてください</value></data>
  <data name="MorningRunNotFound" xml:space="preserve"><value>朝の実行が見つかりません</value></data>
  <data name="MorningRunAlreadyFinished" xml:space="preserve"><value>この朝の実行はもう終わっています</value></data>
  <data name="MorningResultUnreadable" xml:space="preserve"><value>Claude の出力を読み取れませんでした（result\plan.json が無いか壊れています）</value></data>
  <data name="MorningCandidatesDiscardedFormat" xml:space="preserve"><value>{0} 件のうち {1} 件は読み取れませんでした</value></data>
  <data name="CandidateNotFound" xml:space="preserve"><value>候補が見つかりません</value></data>
  <data name="CandidateAlreadyDecided" xml:space="preserve"><value>この候補はもう片づいています</value></data>
  <data name="CandidateNoteHeaderFormat" xml:space="preserve"><value>【{0} から取り込み】</value></data>
  <data name="CandidateNoteFromFormat" xml:space="preserve"><value>差出人: {0}</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の最後のプロパティの下に足す:

```csharp
    public static string MorningInstructionDefault => Get(nameof(MorningInstructionDefault));
    public static string MorningInstructionContractFormat => Get(nameof(MorningInstructionContractFormat));
    public static string MorningRunAlreadyRunning => Get(nameof(MorningRunAlreadyRunning));
    public static string MorningRunNotFound => Get(nameof(MorningRunNotFound));
    public static string MorningRunAlreadyFinished => Get(nameof(MorningRunAlreadyFinished));
    public static string MorningResultUnreadable => Get(nameof(MorningResultUnreadable));
    public static string MorningCandidatesDiscardedFormat => Get(nameof(MorningCandidatesDiscardedFormat));
    public static string CandidateNotFound => Get(nameof(CandidateNotFound));
    public static string CandidateAlreadyDecided => Get(nameof(CandidateAlreadyDecided));
    public static string CandidateNoteHeaderFormat => Get(nameof(CandidateNoteHeaderFormat));
    public static string CandidateNoteFromFormat => Get(nameof(CandidateNoteFromFormat));
```

- [ ] **Step 4: MorningInstruction を書く**

`src/MoTask.Core/Morning/MorningInstruction.cs`:

```csharp
using MoTask.Core.Ai;

namespace MoTask.Core.Morning;

/// <summary>
/// instruction.md（仕様 §6）。前半は人が AI 設定で書き換えられる収集方針、
/// 後半は出力先と形の契約で、MoTask が必ず付ける。
/// 契約を人に編集させると MorningResultReader が読める形との対応が黙って壊れる。
/// </summary>
public static class MorningInstruction
{
    public static string DefaultTemplate => Messages.MorningInstructionDefault;

    public static string Build(string? template, JobFolderPaths paths, DateOnly date)
    {
        var head = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        var contract = string.Format(
            Messages.MorningInstructionContractFormat,
            date.ToString("yyyy-MM-dd"), paths.BoardJson, paths.CandidatesJsonl, paths.PlanJson);
        return head + "\n\n" + contract + "\n";
    }
}
```

- [ ] **Step 5: AiSettings に1つ足して往復させる**

`src/MoTask.Core/Ai/AiSettings.cs` のレコード宣言の末尾にパラメータを足す
（**既定値を付けるので既存の呼び出しは全部そのまま通る**）:

```csharp
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    string? ClaudeExecutablePath,
    string? Model,
    string PermissionMode,
    string? TerminalCommandTemplate,
    // 朝の実行の指示文（仕様 §6）。null なら MorningInstruction.DefaultTemplate。
    // XML doc コメントはパラメータリストの中に置けない（CS1587）ので行コメントにする。
    string? MorningInstruction = null)
```

`src/MoTask.Data/JsonAiSettingsStore.cs`:
- `Dto` に `public string? MorningInstruction { get; set; }` を足す
- `Load` の `new AiSettings(...)` の末尾に
  `string.IsNullOrWhiteSpace(dto.MorningInstruction) ? null : dto.MorningInstruction` を足す
- `Save` の `dto` 初期化に `MorningInstruction = settings.MorningInstruction,` を足す

- [ ] **Step 6: 設定ダイアログに欄を足す**

`src/MoTask.App/Resources/Strings.resx` に足す:

```xml
  <data name="SettingsMorningInstruction" xml:space="preserve"><value>朝の実行の指示文（空なら既定の文面。出力先と JSON の形は MoTask が自動で付け足します）</value></data>
```

`src/MoTask.App/Resources/Strings.cs` に足す:

```csharp
    public static string SettingsMorningInstruction => Get(nameof(SettingsMorningInstruction));
```

`src/MoTask.App/ViewModels/AiSettingsViewModel.cs`:
- フィールドを足す: `[ObservableProperty] private string _morningInstruction = "";`
- コンストラクタに `_morningInstruction = s.MorningInstruction ?? "";`
- `Save` の `_store.Save(...)` を差し替える:

```csharp
        _store.Save(new AiSettings(dir, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model),
            mode, NullIfBlank(TerminalCommandTemplate), NullIfBlank(MorningInstruction)));
```

`src/MoTask.App/Views/AiSettingsDialog.xaml` の `TerminalCommandTemplate` の `TextBox` の下に足す:

```xml
        <TextBlock Text="{x:Static res:Strings.SettingsMorningInstruction}" Style="{StaticResource Settings.Label}" TextWrapping="Wrap" />
        <TextBox Text="{Binding MorningInstruction, UpdateSourceTrigger=PropertyChanged}"
                 AcceptsReturn="True" TextWrapping="Wrap" Height="160"
                 VerticalScrollBarVisibility="Auto" />
```

- [ ] **Step 7: テストが通ることを確認する**

Run: `dotnet test MoTask.sln`
Expected: PASS

- [ ] **Step 8: コミット**

```
git add src/MoTask.Core src/MoTask.Data/JsonAiSettingsStore.cs src/MoTask.App tests
git commit -F- <<'MSG'
feat(core): build instruction.md and let the user edit its collection policy

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 6: 永続化（IMorningRepository / エンティティ / マイグレーション）

**Files:**
- Create: `src/MoTask.Core/Abstractions/IMorningRepository.cs`
- Create: `src/MoTask.Data/Repositories/MorningRepository.cs`
- Modify: `src/MoTask.Data/MoTaskDbContext.cs`
- Modify: `src/MoTask.Data/ServiceCollectionExtensions.cs`
- Create: `src/MoTask.Data/Migrations/<stamp>_AddMorningRuns.cs`（`dotnet ef` が生成）
- Test: `tests/MoTask.Data.Tests/MorningRepositoryTests.cs`
- Test: `tests/MoTask.Data.Tests/MigrationTests.cs`（`Migrate_OnEmptyFile_CreatesSchema` のテーブル一覧に追記）

**Interfaces:**
- Consumes: Task 1 の `MorningRun` / `TriageCandidate` / `MorningRunStatus` / `TriageStatus`
- Produces: `MoTask.Core.Abstractions.IMorningRepository`

```csharp
public interface IMorningRepository
{
    void Add(MorningRun run);
    Task<MorningRun?> GetRunAsync(int runId, CancellationToken ct = default);
    /// <summary>未完了（Pending / Running）の実行。二重起動の判定に使う。無ければ null。</summary>
    Task<MorningRun?> GetUnfinishedRunAsync(CancellationToken ct = default);
    /// <summary>Id 降順の先頭。画面が「前回」を出すのに使う。無ければ null。</summary>
    Task<MorningRun?> GetLatestRunAsync(CancellationToken ct = default);
    /// <summary>これまでの実行の件数。ジョブフォルダの連番に使う。</summary>
    Task<int> CountRunsAsync(CancellationToken ct = default);

    void AddCandidate(TriageCandidate candidate);
    Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default);
    /// <summary>渡した中で既に DB に居る ExternalId だけを返す（取り込み時の重複排除）。</summary>
    Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct = default);
    /// <summary>候補キュー: この実行の Pending ＋ 過去の実行の Later（Id 昇順・仕様 §9）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);
}
```

**注意:** メソッド名を `GetRunAsync` / `GetLatestRunAsync` にしているのは、テスト用の
`InMemoryStore` が `IAiJobRepository.GetAsync(int, ct)` を既に持っており、
戻り値だけ違う同名同引数のメソッドを C# が許さないため（既存の `GetForTaskAsync` と同じ問題）。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/MorningRepositoryTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class MorningRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    private async Task InitAsync()
    {
        await using var ctx = _db.CreateContext();
        await new DatabaseInitializer(ctx).InitializeAsync();
    }

    private static MorningRun NewRun(DateOnly date, MorningRunStatus status = MorningRunStatus.Pending)
        => new()
        {
            Date = date, Status = status, SessionId = Guid.NewGuid(),
            Instruction = "指示", JobFolder = @"C:\work\morning\0001-2026-09-07",
            StartedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
        };

    private static TriageCandidate NewCandidate(int runId, string externalId, TriageStatus status = TriageStatus.Pending)
        => new()
        {
            MorningRunId = runId, ExternalId = externalId, Source = "Outlook",
            From = "顧客A 山本さん", Title = "請求先情報を更新する",
            Evidence = "「9月8日までに」", Link = "https://outlook.office.com/x",
            Reasoning = "依頼が明確", ReceivedAt = new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc),
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
            SuggestedAction = TriageAction.Register, Status = status,
        };

    [Fact]
    public async Task MorningRun_RoundTrips()
    {
        await InitAsync();
        int id;
        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);
            var run = NewRun(new DateOnly(2026, 9, 7));
            run.PlanJson = "{\"groups\":[]}";
            run.ProcessedLines = 12;
            repo.Add(run);
            await ctx.SaveChangesAsync();
            id = run.Id;
        }

        await using (var ctx = _db.CreateContext())
        {
            var run = await new MorningRepository(ctx).GetRunAsync(id);

            run!.Date.Should().Be(new DateOnly(2026, 9, 7));
            run.Status.Should().Be(MorningRunStatus.Pending);
            run.Instruction.Should().Be("指示");
            run.JobFolder.Should().Be(@"C:\work\morning\0001-2026-09-07");
            run.PlanJson.Should().Be("{\"groups\":[]}");
            run.ProcessedLines.Should().Be(12);
        }
    }

    [Fact]
    public async Task Status_IsStoredAsText()
    {
        await InitAsync();
        await using (var ctx = _db.CreateContext())
        {
            new MorningRepository(ctx).Add(NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var stored = await ctx.Database
                .SqlQueryRaw<string>("SELECT Status AS Value FROM MorningRuns").ToListAsync();

            stored.Should().Equal("Ingested", "enum は既存の流儀どおり文字列で持つ");
        }
    }

    [Fact]
    public async Task TriageCandidate_RoundTrips_IncludingTheFromColumn()
    {
        await InitAsync();
        int runId;
        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);
            var run = NewRun(new DateOnly(2026, 9, 7));
            repo.Add(run);
            await ctx.SaveChangesAsync();
            runId = run.Id;
            repo.AddCandidate(NewCandidate(runId, "outlook:AAMkAD001"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var candidate = (await new MorningRepository(ctx).GetQueueAsync(runId)).Single();

            candidate.From.Should().Be("顧客A 山本さん", "From は SQLite の予約語だが EF が引用符で囲む");
            candidate.Evidence.Should().Be("「9月8日までに」");
            candidate.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
            candidate.SuggestedAction.Should().Be(TriageAction.Register);
            candidate.Status.Should().Be(TriageStatus.Pending);
        }
    }

    [Fact]
    public async Task ExternalId_IsUnique()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var run = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(run);
        await ctx.SaveChangesAsync();

        repo.AddCandidate(NewCandidate(run.Id, "outlook:same"));
        repo.AddCandidate(NewCandidate(run.Id, "outlook:same"));

        var save = async () => await ctx.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>("却下した候補を二度と拾わないための鍵");
    }

    [Fact]
    public async Task GetKnownExternalIds_ReturnsOnlyTheOnesAlreadyStored()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var run = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(run);
        await ctx.SaveChangesAsync();
        repo.AddCandidate(NewCandidate(run.Id, "outlook:known"));
        await ctx.SaveChangesAsync();

        var known = await repo.GetKnownExternalIdsAsync(new[] { "outlook:known", "outlook:new" });

        known.Should().Equal("outlook:known");
    }

    [Fact]
    public async Task GetQueue_IsTodaysPendingPlusOlderLaters()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var yesterday = NewRun(new DateOnly(2026, 9, 6));
        var today = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(yesterday);
        repo.Add(today);
        await ctx.SaveChangesAsync();

        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:later", TriageStatus.Later));
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:rejected", TriageStatus.Rejected));
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:registered", TriageStatus.Registered));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:fresh"));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:decided-today", TriageStatus.Later));
        await ctx.SaveChangesAsync();

        var queue = await repo.GetQueueAsync(today.Id);

        queue.Select(c => c.ExternalId).Should().Equal("outlook:later", "outlook:fresh");
    }

    [Fact]
    public async Task GetUnfinishedRun_FindsOnlyPendingOrRunning()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        repo.Add(NewRun(new DateOnly(2026, 9, 5), MorningRunStatus.Ingested));
        repo.Add(NewRun(new DateOnly(2026, 9, 6), MorningRunStatus.Cancelled));
        await ctx.SaveChangesAsync();

        (await repo.GetUnfinishedRunAsync()).Should().BeNull();
        (await repo.CountRunsAsync()).Should().Be(2);

        var running = NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        repo.Add(running);
        await ctx.SaveChangesAsync();

        (await repo.GetUnfinishedRunAsync())!.Id.Should().Be(running.Id);
        (await repo.GetLatestRunAsync())!.Id.Should().Be(running.Id);
    }

    public void Dispose() => _db.Dispose();
}
```

`tests/MoTask.Data.Tests/MigrationTests.cs` の `Migrate_OnEmptyFile_CreatesSchema` の
`Should().Contain(new[] { ... })` に `"MorningRuns", "TriageCandidates"` を足す。

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests --filter FullyQualifiedName~MorningRepositoryTests`
Expected: コンパイルエラー（`MorningRepository` が存在しない）

- [ ] **Step 3: 口を書く**

`src/MoTask.Core/Abstractions/IMorningRepository.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

/// <summary>読み取りは追跡された同一インスタンスを返す（IBoardRepository と同じ契約）。</summary>
public interface IMorningRepository
{
    void Add(MorningRun run);
    Task<MorningRun?> GetRunAsync(int runId, CancellationToken ct = default);

    /// <summary>未完了（Pending / Running）の実行。二重起動の判定に使う。無ければ null。</summary>
    Task<MorningRun?> GetUnfinishedRunAsync(CancellationToken ct = default);

    /// <summary>Id 降順の先頭。画面が「前回」を出すのに使う。無ければ null。</summary>
    Task<MorningRun?> GetLatestRunAsync(CancellationToken ct = default);

    /// <summary>これまでの実行の件数。ジョブフォルダの連番に使う。</summary>
    Task<int> CountRunsAsync(CancellationToken ct = default);

    void AddCandidate(TriageCandidate candidate);
    Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default);

    /// <summary>渡した中で既に DB に居る ExternalId だけを返す（取り込み時の重複排除）。</summary>
    Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(
        IReadOnlyCollection<string> externalIds, CancellationToken ct = default);

    /// <summary>候補キュー: この実行の Pending ＋ 過去の実行の Later（Id 昇順・仕様 §9）。</summary>
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);
}
```

- [ ] **Step 4: EF 実装とエンティティ設定を書く**

`src/MoTask.Data/Repositories/MorningRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class MorningRepository : IMorningRepository
{
    private static readonly MorningRunStatus[] Unfinished = { MorningRunStatus.Pending, MorningRunStatus.Running };

    private readonly MoTaskDbContext _db;

    public MorningRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(MorningRun run) => _db.MorningRuns.Add(run);

    public Task<MorningRun?> GetRunAsync(int runId, CancellationToken ct = default)
        => _db.MorningRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    public Task<MorningRun?> GetUnfinishedRunAsync(CancellationToken ct = default)
        // 値変換(enum → 文字列)付きの列に対する Contains は EF Core 8 以降で IN 句に翻訳される
        => _db.MorningRuns.Where(r => Unfinished.Contains(r.Status))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<MorningRun?> GetLatestRunAsync(CancellationToken ct = default)
        => _db.MorningRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<int> CountRunsAsync(CancellationToken ct = default) => _db.MorningRuns.CountAsync(ct);

    public void AddCandidate(TriageCandidate candidate) => _db.TriageCandidates.Add(candidate);

    public Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default)
        => _db.TriageCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct);

    public async Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(
        IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        if (externalIds.Count == 0) return Array.Empty<string>();
        var wanted = externalIds.ToList();
        return await _db.TriageCandidates
            .Where(c => wanted.Contains(c.ExternalId))
            .Select(c => c.ExternalId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => await _db.TriageCandidates
            .Where(c => (c.MorningRunId == runId && c.Status == TriageStatus.Pending)
                        || (c.MorningRunId != runId && c.Status == TriageStatus.Later))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
}
```

`src/MoTask.Data/MoTaskDbContext.cs`:
- `DbSet` を2つ足す:

```csharp
    public DbSet<MorningRun> MorningRuns => Set<MorningRun>();
    public DbSet<TriageCandidate> TriageCandidates => Set<TriageCandidate>();
```

- `OnModelCreating` の末尾（`AiJob` の設定の後）に足す:

```csharp
        b.Entity<MorningRun>(e =>
        {
            e.ToTable("MorningRuns");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Instruction).IsRequired().HasDefaultValue("");
            e.Property(x => x.JobFolder).IsRequired().HasDefaultValue("");
            e.Property(x => x.PlanJson).IsRequired().HasDefaultValue("");
            e.Property(x => x.ProcessedLines).HasDefaultValue(0);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Date);
        });

        b.Entity<TriageCandidate>(e =>
        {
            e.ToTable("TriageCandidates");
            e.Property(x => x.ExternalId).IsRequired();
            e.Property(x => x.Source).IsRequired().HasDefaultValue("");
            // From は SQLite の予約語だが、EF は識別子を必ず引用符で囲むので列名はこのままでよい。
            e.Property(x => x.From).IsRequired().HasDefaultValue("");
            e.Property(x => x.Title).IsRequired().HasDefaultValue("");
            e.Property(x => x.Evidence).IsRequired().HasDefaultValue("");
            e.Property(x => x.Link).IsRequired().HasDefaultValue("");
            e.Property(x => x.Reasoning).IsRequired().HasDefaultValue("");
            e.Property(x => x.SuggestedProject).IsRequired().HasDefaultValue("");
            e.Property(x => x.SuggestedAction).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // 却下した候補を翌朝また拾わないための鍵（仕様 §9）
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.MorningRunId);
            e.HasOne<MorningRun>().WithMany().HasForeignKey(x => x.MorningRunId).OnDelete(DeleteBehavior.Cascade);
            // タスクが消えても候補の記録は残す
            e.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.ResultTaskId).OnDelete(DeleteBehavior.SetNull);
        });
```

`src/MoTask.Data/ServiceCollectionExtensions.cs` の `AddMoTaskData` に足す:

```csharp
        services.AddSingleton<IMorningRepository, MorningRepository>();
```

- [ ] **Step 5: マイグレーションを作る**

```
dotnet tool restore
dotnet ef migrations add AddMorningRuns --project src/MoTask.Data --output-dir Migrations
```

生成された `Up` を目で確認する。`MorningRuns` と `TriageCandidates` の `CREATE TABLE` と、
`TriageCandidates.ExternalId` の `CREATE UNIQUE INDEX` があること。既存テーブルに `ALTER` が
入っていたら設定を書き間違えているので直す（**既存テーブルには一切触らない**）。

- [ ] **Step 6: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: PASS（既存の `MigrationTests` / `RepositoryTests` も含めて全部）

- [ ] **Step 7: コミット**

```
git add src/MoTask.Core/Abstractions/IMorningRepository.cs src/MoTask.Data tests/MoTask.Data.Tests
git commit -F- <<'MSG'
feat(data): store morning runs and triage candidates

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 7: MorningService — 朝の実行を始める

**Files:**
- Create: `src/MoTask.Core/Morning/MorningRunDescriptor.cs`
- Create: `src/MoTask.Core/Services/MorningRunChangedEventArgs.cs`
- Create: `src/MoTask.Core/Services/IMorningService.cs`
- Create: `src/MoTask.Core/Services/MorningService.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceStartTests.cs`

**Interfaces:**
- Consumes: Task 1 のモデル、Task 2 の `MorningResultReader`、Task 3 の `BoardSnapshot`、Task 4 の `IJobFolder` 拡張、Task 5 の `MorningInstruction`、Task 6 の `IMorningRepository`
- Produces:
  - `MoTask.Core.Morning.MorningRunDescriptor(int RunId, DateOnly Date, Guid SessionId, string JobFolder, string LaunchCommand, DateTime StartedAt)` と `static string Serialize(MorningRunDescriptor)`
  - `MoTask.Core.Services.MorningRunSnapshot(int RunId, DateOnly Date, MorningRunStatus Status, int Turns, string? ErrorMessage, string JobFolder)`
  - `MoTask.Core.Services.MorningRunChangedEventArgs`（`Run` / `Warning` / `CandidatesChanged`）
  - `MoTask.Core.Services.IMorningService`（このタスクでは `StartAsync` / `GetCurrentRunAsync` / `GetQueueAsync` / `GetLogTailAsync` / `RunChanged` を実装。残りは Task 8 / Task 9 で埋める）
  - `MorningService` のコンストラクタ:
    `MorningService(IMorningRepository runs, IBoardRepository boards, IAiJobRepository jobs, IHistoryRepository history, IUnitOfWork uow, IClock clock, OperationGate gate, ISessionLauncher launcher, IJobFolder folder, IJobEventSource events, IAiSettingsStore settings, IBoardService boardService)`
- `InMemoryStore` に `IMorningRepository` を実装（`Runs` / `Candidates` の公開リスト、`SeedRun` / `SeedCandidate` ヘルパー付き）

- [ ] **Step 1: InMemoryStore に IMorningRepository を実装する**

`tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`。クラス宣言に `, IMorningRepository` を足し、
`IUnitOfWork` の節の手前に足す:

```csharp
    // ---- IMorningRepository ----
    //
    // GetAsync / Add は IAiJobRepository・IHistoryRepository と名前が衝突するので、
    // 衝突する分は名前を変えるか明示的実装にする（既存の GetForTaskAsync と同じ事情）。

    public List<MorningRun> Runs { get; } = new();
    public List<TriageCandidate> Candidates { get; } = new();

    public MorningRun SeedRun(DateOnly date, MorningRunStatus status = MorningRunStatus.Pending,
        string jobFolder = @"C:\work\morning\0001-2026-09-07")
    {
        var run = new MorningRun
        {
            Id = _nextId++, Date = date, Status = status, SessionId = Guid.NewGuid(),
            Instruction = "指示", JobFolder = jobFolder,
        };
        Runs.Add(run);
        return run;
    }

    public TriageCandidate SeedCandidate(MorningRun run, string externalId,
        TriageStatus status = TriageStatus.Pending, TriageAction suggested = TriageAction.Register)
    {
        var candidate = new TriageCandidate
        {
            Id = _nextId++, MorningRunId = run.Id, ExternalId = externalId, Source = "Outlook",
            From = "山本さん", Title = "請求先情報を更新する", Evidence = "「9月8日までに」",
            Link = "https://outlook.office.com/x", Reasoning = "依頼が明確",
            SuggestedAction = suggested, Status = status,
        };
        Candidates.Add(candidate);
        return candidate;
    }

    public void Add(MorningRun run)
    {
        if (run.Id == 0) run.Id = _nextId++;
        Runs.Add(run);
    }

    public Task<MorningRun?> GetRunAsync(int runId, CancellationToken ct = default)
        => Task.FromResult(Runs.FirstOrDefault(r => r.Id == runId));

    public Task<MorningRun?> GetUnfinishedRunAsync(CancellationToken ct = default)
        => Task.FromResult(Runs.Where(r => r.Status.IsActive()).OrderByDescending(r => r.Id).FirstOrDefault());

    public Task<MorningRun?> GetLatestRunAsync(CancellationToken ct = default)
        => Task.FromResult(Runs.OrderByDescending(r => r.Id).FirstOrDefault());

    public Task<int> CountRunsAsync(CancellationToken ct = default) => Task.FromResult(Runs.Count);

    public void AddCandidate(TriageCandidate candidate)
    {
        if (candidate.Id == 0) candidate.Id = _nextId++;
        Candidates.Add(candidate);
    }

    public Task<TriageCandidate?> GetCandidateAsync(int candidateId, CancellationToken ct = default)
        => Task.FromResult(Candidates.FirstOrDefault(c => c.Id == candidateId));

    public Task<IReadOnlyList<string>> GetKnownExternalIdsAsync(
        IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            Candidates.Select(c => c.ExternalId).Where(externalIds.Contains).ToList());

    public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TriageCandidate>>(Candidates
            .Where(c => (c.MorningRunId == runId && c.Status == TriageStatus.Pending)
                        || (c.MorningRunId != runId && c.Status == TriageStatus.Later))
            .OrderBy(c => c.Id).ToList());
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 朝の実行の開始（仕様 §6・§12）。フォルダを先に作り、パスが確定してから DB に保存する。
/// </summary>
public class MorningServiceStartTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly Column _active;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceStartTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        _active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedColumn("完了", ColumnRole.Done);
        _store.SeedTask(_active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    [Fact]
    public async Task Start_PutsTheFolderUnderMorning_NamedByRunNumberAndDate()
    {
        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeTrue(started.Error);
        _folder.Created.Should().ContainSingle();
        _folder.Created[0].Category.Should().Be(JobFolderPaths.MorningDirectoryName);
        _folder.Created[0].OutputDirectoryName.Should().Be(JobFolderPaths.ResultDirectoryName);
        _folder.Created[0].TaskTitle.Should().Be("2026-09-07");
        started.Value!.JobFolder.Should().Be(@"C:\work\morning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_RunsInsideTheJobFolderItself()
    {
        await _service.StartAsync();

        var request = _launcher.Requests.Should().ContainSingle().Subject;
        request.WorkingDirectory.Should().Be(request.JobFolder, "朝の実行はソースツリーに用が無い（仕様 §6）");
        request.Resume.Should().BeFalse();
    }

    [Fact]
    public async Task Start_SavesTheRunOnlyAfterTheFolderIsKnown()
    {
        var run = (await _service.StartAsync()).Value!;

        run.Status.Should().Be(MorningRunStatus.Pending);
        run.JobFolder.Should().NotBeEmpty("JobFolder が空のまま Pending で残る窓を作らない（仕様 §12）");
        run.Instruction.Should().Contain(run.JobFolder, "指示文には出力先の実パスが入る");
        run.SessionId.Should().NotBeEmpty();
        run.StartedAt.Should().Be(_clock.UtcNow);
        _store.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_WritesBoardJsonWithTheUnfinishedTasks()
    {
        var run = (await _service.StartAsync()).Value!;

        var board = _folder.ReadText(run.JobFolder, JobFolderPaths.BoardJsonName);
        board.Should().NotBeNull();
        var root = JsonDocument.Parse(board!).RootElement;
        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()).Should().Equal("Q4企画書の内容を確定する");
    }

    [Fact]
    public async Task Start_WritesRunJsonWithTheLaunchCommand()
    {
        var run = (await _service.StartAsync()).Value!;

        var text = _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName);
        text.Should().NotBeNull("DB が壊れてもフォルダだけで何の実行か分かるようにする（仕様 §6）");
        var root = JsonDocument.Parse(text!).RootElement;
        root.GetProperty("runId").GetInt32().Should().Be(run.Id);
        root.GetProperty("launchCommand").GetString().Should().StartWith("wt.exe ");
    }

    [Fact]
    public async Task Start_FollowsTheEventsFileFromTheTop()
    {
        var run = (await _service.StartAsync()).Value!;

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(0);
        _events.EventsPathOf(run.Id).Should().Be(JobFolderPaths.For(run.JobFolder).EventsJsonl);
    }

    [Fact]
    public async Task Start_RefusesASecondRunWhileOneIsUnfinished()
    {
        await _service.StartAsync();

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeFalse("二重起動の防止（仕様 §12）");
        second.Error.Should().Be(Messages.MorningRunAlreadyRunning);
        _store.Runs.Should().ContainSingle();
        _folder.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_IsAllowedAgainOnceTheLastRunIsFinished()
    {
        var first = (await _service.StartAsync()).Value!;
        first.Status = MorningRunStatus.Ingested;

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.JobFolder.Should().Be(@"C:\work\morning\0002-2026-09-07", "連番は実行の件数で決まる");
    }

    [Fact]
    public async Task Start_StopsBeforeDoingAnything_WhenClaudeIsMissing()
    {
        _launcher.Availability = Result.Fail(Messages.ClaudeNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Runs.Should().BeEmpty("黙って走らせず、開始時点で止める（仕様 §12）");
        _folder.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_StopsBeforeSaving_WhenTheHooksExeIsMissing()
    {
        _folder.CreateFailure = Result.Fail<string>(Messages.HooksExecutableNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.HooksExecutableNotFound);
        _store.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_MarksTheRunFailed_WhenTheTerminalWillNotOpen()
    {
        _launcher.LaunchFailure = Result.Fail("端末を起動できませんでした");

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        var run = _store.Runs.Should().ContainSingle().Subject;
        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be("端末を起動できませんでした");
        run.JobFolder.Should().NotBeEmpty("失敗した実行でもフォルダは開ける");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Start_UsesTheConfiguredInstructionTemplate()
    {
        _settings.Settings = AiSettings.Default() with { MorningInstruction = "私の方針" };

        var run = (await _service.StartAsync()).Value!;

        run.Instruction.Should().StartWith("私の方針");
        _folder.Created[0].Instruction.Should().Be(run.Instruction);
    }

    [Fact]
    public async Task Start_RaisesRunChangedOnce()
    {
        var run = (await _service.StartAsync()).Value!;

        _changes.Should().ContainSingle();
        _changes[0].Run.RunId.Should().Be(run.Id);
        _changes[0].Run.Status.Should().Be(MorningRunStatus.Pending);
        _changes[0].CandidatesChanged.Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentRun_IsTheLatestOne()
    {
        (await _service.GetCurrentRunAsync()).Should().BeNull("まだ一度も走らせていない");

        var run = (await _service.StartAsync()).Value!;

        (await _service.GetCurrentRunAsync())!.Id.Should().Be(run.Id);
    }
}
```

- [ ] **Step 3: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningServiceStartTests`
Expected: コンパイルエラー（`MorningService` が存在しない）

- [ ] **Step 4: run.json とイベント引数を書く**

`src/MoTask.Core/Morning/MorningRunDescriptor.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Morning;

/// <summary>
/// run.json の中身（仕様 §6）。job.json 相当で、DB が壊れてもフォルダだけで何の実行か分かるように残す。
/// </summary>
public sealed record MorningRunDescriptor(
    int RunId,
    DateOnly Date,
    Guid SessionId,
    string JobFolder,
    string LaunchCommand,
    DateTime StartedAt)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(MorningRunDescriptor descriptor)
        => JsonSerializer.Serialize(descriptor, Options);
}
```

`src/MoTask.Core/Services/MorningRunChangedEventArgs.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// 画面に渡す 1 回分の変化。MorningRun そのものを渡さないのは、追従スレッドから
/// 追跡中のエンティティを UI に触らせないため（AiJobSnapshot と同じ理由）。
/// </summary>
public sealed record MorningRunSnapshot(
    int RunId,
    DateOnly Date,
    MorningRunStatus Status,
    int Turns,
    string? ErrorMessage,
    string JobFolder);

public sealed class MorningRunChangedEventArgs : EventArgs
{
    public MorningRunChangedEventArgs(MorningRunSnapshot run, string? warning, bool candidatesChanged)
    {
        Run = run;
        Warning = warning;
        CandidatesChanged = candidatesChanged;
    }

    public MorningRunSnapshot Run { get; }

    /// <summary>バナーに出す注意（保存失敗・読み捨てた行数など）。無ければ null。</summary>
    public string? Warning { get; }

    /// <summary>候補キューを読み直す必要があるか。</summary>
    public bool CandidatesChanged { get; }
}
```

- [ ] **Step 5: IMorningService を書く**

`src/MoTask.Core/Services/IMorningService.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>候補を登録するときに人が確定した内容（編集後の値）。</summary>
public sealed record CandidateDecision(
    int CandidateId,
    string Title,
    DateOnly? DueDate,
    string ProjectName,
    int ColumnId);

public interface IMorningService
{
    event EventHandler<MorningRunChangedEventArgs>? RunChanged;

    // 照会
    Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default);
    /// <summary>events.jsonl の末尾。進行の表示に使う（DB には持たない）。</summary>
    Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default);
    /// <summary>記憶しているターン数。追跡していなければ 0。</summary>
    int TurnCountOf(int runId);

    // 実行
    Task<Result<MorningRun>> StartAsync(CancellationToken ct = default);
    /// <summary>端末を × で閉じられた後の「完了にする」。取り込みを走らせる。</summary>
    Task<Result> CompleteAsync(int runId, CancellationToken ct = default);
    /// <summary>追跡をやめる。端末は殺さない。</summary>
    Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default);

    // 仕分け
    Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default);
    Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default);
    Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default);
    Task<Result> RejectAsync(int candidateId, CancellationToken ct = default);
}
```

- [ ] **Step 6: MorningService の骨と StartAsync を書く**

`src/MoTask.Core/Services/MorningService.cs`。このタスクでは `CompleteAsync` /
`StopTrackingAsync` / `RecoverOnStartupAsync` / 4アクションを
`throw new NotImplementedException()` にせず、**Task 8 / Task 9 で埋めるまでは
`Task.FromResult(Result.Fail(Messages.MorningRunNotFound))` のような無害な形にもしない**。
順序どおりに進めるなら、この Step ではコンパイルを通すために空実装が要る。
`// Task 8 で実装する` のコメント付きで `Result.Fail(Messages.MorningRunNotFound)` を返しておき、
**Task 8 / Task 9 の最初のステップで必ず置き換える**（置き換え漏れは各タスクのテストが捕まえる）。

```csharp
using System.Collections.Concurrent;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Morning;

namespace MoTask.Core.Services;

/// <summary>
/// 朝の実行のライフサイクル（仕様 §6・§10）。AiJobService と同じ構えだが、終了時の副作用が違う
/// （あちらはタスクを確認待ちへ動かし、こちらは候補とプランを取り込む）ので共通化しない。
/// DB は BoardService / AiJobService と共有の OperationGate で直列化する。
/// <b>ゲートの中から IBoardService を呼ぶとデッドロックする</b>ので、登録・統合はゲートの外で呼ぶ。
/// </summary>
public sealed class MorningService : IMorningService
{
    private readonly IMorningRepository _runs;
    private readonly IBoardRepository _boards;
    private readonly IAiJobRepository _jobs;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly OperationGate _gate;
    private readonly ISessionLauncher _launcher;
    private readonly IJobFolder _folder;
    private readonly IJobEventSource _events;
    private readonly IAiSettingsStore _settings;
    private readonly IBoardService _boardService;

    /// <summary>追跡中の実行のターン数。DB には持たない（仕様 §9）。</summary>
    private readonly ConcurrentDictionary<int, int> _turns = new();

    /// <summary>
    /// 開始は 1 本ずつ。ゲートを 2 回に分けて取る（フォルダ作成と端末起動はゲートの外）ので、
    /// 2 つの StartAsync が同じ連番のフォルダを作らないようにここで直列化する。
    /// </summary>
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public event EventHandler<MorningRunChangedEventArgs>? RunChanged;

    public MorningService(
        IMorningRepository runs, IBoardRepository boards, IAiJobRepository jobs, IHistoryRepository history,
        IUnitOfWork uow, IClock clock, OperationGate gate, ISessionLauncher launcher, IJobFolder folder,
        IJobEventSource events, IAiSettingsStore settings, IBoardService boardService)
    {
        _runs = runs;
        _boards = boards;
        _jobs = jobs;
        _history = history;
        _uow = uow;
        _clock = clock;
        _gate = gate;
        _launcher = launcher;
        _folder = folder;
        _events = events;
        _settings = settings;
        _boardService = boardService;
    }

    // ---------- 照会 ----------

    public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetLatestRunAsync(ct), ct);

    public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(() => _runs.GetQueueAsync(runId, ct), ct);

    public async Task<IReadOnlyList<string>> GetLogTailAsync(int runId, int lines, CancellationToken ct = default)
    {
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId, ct), ct).ConfigureAwait(false);
        return run is null || run.JobFolder.Length == 0
            ? Array.Empty<string>()
            : _folder.ReadTail(run.JobFolder, lines);
    }

    public int TurnCountOf(int runId) => _turns.TryGetValue(runId, out var turns) ? turns : 0;

    // ---------- 開始 ----------

    private sealed record Prepared(int RunNumber, string BoardJson);

    public async Task<Result<MorningRun>> StartAsync(CancellationToken ct = default)
    {
        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<MorningRun>(available.Error!);

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings.Load();
            var date = _clock.Today;

            var prepared = await _gate.RunAsync(async () =>
            {
                // 二重起動の防止（仕様 §12）。追跡中かどうかは DB で数える。
                var unfinished = await _runs.GetUnfinishedRunAsync(ct).ConfigureAwait(false);
                if (unfinished is not null) return Result.Fail<Prepared>(Messages.MorningRunAlreadyRunning);

                var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
                if (board is null) return Result.Fail<Prepared>(Messages.BoardNotFound);

                var projects = await _boards.GetProjectsAsync(ct).ConfigureAwait(false);
                var busy = await _jobs.GetByStatusAsync(
                    new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct)
                    .ConfigureAwait(false);

                var snapshot = BoardSnapshot.Build(
                    board, date,
                    projects.ToDictionary(p => p.Id, p => p.Name),
                    busy.Select(j => j.TaskId).ToHashSet());

                // フォルダ名の連番。DB の採番を待たずに決まるので、行の保存を後ろへ回せる（仕様 §12）。
                var runNumber = await _runs.CountRunsAsync(ct).ConfigureAwait(false) + 1;
                return Result.Ok(new Prepared(runNumber, snapshot));
            }, ct).ConfigureAwait(false);
            if (!prepared.IsSuccess) return Result.Fail<MorningRun>(prepared.Error!);

            // ここから先はファイル操作と端末の起動なので、ゲートの外でやる。
            var request = new JobFolderRequest(prepared.Value!.RunNumber, date.ToString("yyyy-MM-dd"), "")
            {
                Category = JobFolderPaths.MorningDirectoryName,
                OutputDirectoryName = JobFolderPaths.ResultDirectoryName,
            };
            // 指示文は出力先の実パスを含むので、フォルダのパスが決まってから組み立てる。
            var root = _folder.ResolveRoot(request);
            var instruction = MorningInstruction.Build(settings.MorningInstruction, JobFolderPaths.For(root), date);

            var created = _folder.Create(request with { Instruction = instruction });
            if (!created.IsSuccess) return Result.Fail<MorningRun>(created.Error!);

            var wroteBoard = _folder.WriteText(root, JobFolderPaths.BoardJsonName, prepared.Value!.BoardJson);
            if (!wroteBoard.IsSuccess) return Result.Fail<MorningRun>(wroteBoard.Error!);

            var sessionId = Guid.NewGuid();
            // cwd はジョブフォルダ自身。朝の実行はソースツリーに用が無い（仕様 §6）。
            var command = _launcher.BuildCommand(new SessionLaunchRequest(sessionId, root, root, Resume: false));
            if (!command.IsSuccess) return Result.Fail<MorningRun>(command.Error!);

            // フォルダとコマンドが確定してから DB に書く。
            // AiJobService の「JobFolder が空のまま Pending で残る」窓をこちらでは作らない（仕様 §12）。
            var now = _clock.UtcNow;
            var run = new MorningRun
            {
                Date = date, Status = MorningRunStatus.Pending, SessionId = sessionId,
                Instruction = instruction, JobFolder = root, StartedAt = now,
            };
            var saved = await _gate.RunAsync(async () =>
            {
                try
                {
                    _runs.Add(run);
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Result.Ok();
                }
                catch (PersistenceException ex)
                {
                    return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result.Fail<MorningRun>(saved.Error!);

            _folder.WriteText(root, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
                new MorningRunDescriptor(run.Id, date, sessionId, root, command.Value!.Display, now)));

            var launched = _launcher.Launch(command.Value!);
            if (!launched.IsSuccess) return await FailAsync(run, launched.Error!).ConfigureAwait(false);

            _turns[run.Id] = 0;
            Follow(run.Id, root, skipLines: 0);
            Raise(run, null, candidatesChanged: false);
            return Result.Ok(run);
        }
        finally
        {
            _startLock.Release();
        }
    }

    // ---------- 追従（Task 8 で埋める） ----------

    private void Follow(int runId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            runId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(runId, line),
            message => OnProblemAsync(runId, message)));
    }

    private Task OnHookLineAsync(int runId, string line) => Task.CompletedTask; // Task 8 で実装する

    private Task OnProblemAsync(int runId, string message) => Task.CompletedTask; // Task 8 で実装する

    public Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.MorningRunNotFound)); // Task 8 で実装する

    public Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.MorningRunNotFound)); // Task 8 で実装する

    public Task RecoverOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask; // Task 8 で実装する

    // ---------- 仕分け（Task 9 で埋める） ----------

    public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
        => Task.FromResult(Result.Fail<TaskItem>(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        => Task.FromResult(Result.Fail(Messages.CandidateNotFound)); // Task 9 で実装する

    // ---------- 補助 ----------

    /// <summary>ゲートの外から呼ぶ。実行を終了状態にして通知する。</summary>
    private async Task<Result<MorningRun>> FailAsync(MorningRun run, string error)
    {
        await _gate.RunAsync(async () =>
        {
            run.ErrorMessage = error;
            run.Status = MorningRunStatus.Failed;
            run.EndedAt = _clock.UtcNow;
            await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        Raise(run, null, candidatesChanged: false);
        return Result.Fail<MorningRun>(error);
    }

    /// <summary>追従スレッドからの保存失敗で実行を殺さない。理由は警告として UI へ回す。</summary>
    private async Task<string?> SaveQuietlyAsync()
    {
        try
        {
            await _uow.SaveChangesAsync().ConfigureAwait(false);
            return null;
        }
        catch (PersistenceException ex)
        {
            return $"{Messages.SaveFailed}: {ex.Message}";
        }
    }

    private void Raise(MorningRun run, string? warning, bool candidatesChanged)
        => RunChanged?.Invoke(this, new MorningRunChangedEventArgs(
            new MorningRunSnapshot(run.Id, run.Date, run.Status, TurnCountOf(run.Id), run.ErrorMessage, run.JobFolder),
            warning, candidatesChanged));
}
```

- [ ] **Step 7: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningServiceStartTests`
Expected: PASS（14 件）

- [ ] **Step 8: コミット**

```
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -F- <<'MSG'
feat(core): start a morning run and hand the terminal off

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 8: MorningService — events.jsonl を追い、result/ を取り込む

**Files:**
- Modify: `src/MoTask.Core/Services/MorningService.cs`（Task 7 で置いた空実装を全部置き換える）
- Test: `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`

**Interfaces:**
- Consumes: Task 7 の `MorningService` の骨、Task 2 の `MorningResultReader` / `MorningResult`
- Produces: `MorningService` の `CompleteAsync` / `StopTrackingAsync` / `RecoverOnStartupAsync` と、フック行の処理
- **このタスクの終わりに `// Task 8 で実装する` のコメントが 1 つも残っていないこと**

**取り込みの規則（仕様 §6・§8・§14）:**

| 来た合図 | やること |
| --- | --- |
| `SessionStart` / `PostToolUse` | `Running` にする |
| `Stop` | ターン数を1つ増やし、`result/` を見に行く。揃っていなければ何もしない |
| `SessionEnd` | `result/` を見に行く。**ここで揃っていなければ `Failed`** |
| 人が「完了にする」 | `SessionEnd` と同じ扱い |
| 人が「追跡をやめる」 | `Cancelled`。取り込まない。端末は殺さない |

取り込みは1度だけ。成功すると `Ingested`（終了状態）になるので、後から届いた行は
終了状態の門番が落とす。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 追従と取り込み（仕様 §6・§8）。Stop は何度でも来るので、揃った時点で 1 度だけ取り込む。
/// </summary>
public class MorningServiceIngestTests
{
    private const string Plan =
        """{"date":"2026-09-07","groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

    private const string TwoCandidates =
        """{"externalId":"outlook:001","source":"Outlook","title":"請求先情報を更新する","evidence":"「9月8日までに」","suggestedAction":"register"}
{"externalId":"teams:002","source":"Teams","title":"数値を差し替える","evidence":"「速報値に」","suggestedAction":"merge","mergeTargetTaskId":45}""";

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceIngestTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    private async Task<MorningRun> StartAsync()
    {
        var started = await _service.StartAsync();
        started.IsSuccess.Should().BeTrue(started.Error);
        return started.Value!;
    }

    private void PutResult(MorningRun run, string? candidates, string? plan)
    {
        if (candidates is not null) _folder.Put(run.JobFolder, JobFolderPaths.CandidatesRelativePath, candidates);
        if (plan is not null) _folder.Put(run.JobFolder, JobFolderPaths.PlanRelativePath, plan);
    }

    [Fact]
    public async Task SessionStart_MovesTheRunToRunning()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        run.Status.Should().Be(MorningRunStatus.Running);
        run.ProcessedLines.Should().Be(1);
    }

    [Fact]
    public async Task Stop_LeavesTheRunAlone_WhenResultIsNotThereYet()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Running, "Stop は何度でも来る");
        _store.Candidates.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
        _service.TurnCountOf(run.Id).Should().Be(1);
    }

    [Fact]
    public async Task Stop_IngestsOnceTheResultIsComplete()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.PlanJson.Should().Be(Plan);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Select(c => c.ExternalId).Should().Equal("outlook:001", "teams:002");
        _events.IsFollowing(run.Id).Should().BeFalse();
        _changes.Last().CandidatesChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Ingest_CopiesEveryFieldOntoTheCandidate()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        var merge = _store.Candidates.Single(c => c.ExternalId == "teams:002");
        merge.MorningRunId.Should().Be(run.Id);
        merge.Source.Should().Be("Teams");
        merge.Title.Should().Be("数値を差し替える");
        merge.Evidence.Should().Be("「速報値に」");
        merge.SuggestedAction.Should().Be(TriageAction.Merge);
        merge.SuggestedMergeTaskId.Should().Be(45);
        merge.Status.Should().Be(TriageStatus.Pending);
        merge.DecidedAt.Should().BeNull();
    }

    [Fact]
    public async Task Stop_TwiceIngestsOnlyOnce()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Should().HaveCount(2, "result が揃った時点で 1 度だけ取り込む（仕様 §14）");
    }

    [Fact]
    public async Task SessionEnd_AfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Ingested, "終わった実行を蘇らせない");
        _store.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task SessionEnd_WithoutAPlan_FailsTheRunWithAReason()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, plan: null);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningResultUnreadable);
        _store.Candidates.Should().BeEmpty("プランが読めない実行は取り込まない（仕様 §8）");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task SessionEnd_WithABrokenPlan_FailsTheRun()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, """{"groups":[{"key":"someday","items":[]}]}""");

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
    }

    [Fact]
    public async Task Ingest_SucceedsWithNoCandidates_WhenThePlanIsValid()
    {
        var run = await StartAsync();
        PutResult(run, "", Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Ingested,
            "コネクタ未認証や候補が無い朝は失敗ではない（仕様 §8・§14）");
        _store.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_ReportsHowManyLinesItThrewAway()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates + "\n{壊れた行\n{\"externalId\":\"x\",\"source\":\"S\",\"title\":\"根拠なし\",\"suggestedAction\":\"register\"}", Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Should().HaveCount(2);
        _changes.Last().Warning.Should()
            .Be(string.Format(Messages.MorningCandidatesDiscardedFormat, 4, 2), "黙って減らさない（仕様 §8）");
    }

    [Fact]
    public async Task Ingest_SkipsCandidatesAlreadyDecidedInAnEarlierRun()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:001", TriageStatus.Rejected);
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _store.Candidates.Where(c => c.MorningRunId == run.Id).Select(c => c.ExternalId)
            .Should().Equal("teams:002", "却下した候補は翌朝また出てきても取り込まない（仕様 §9）");
    }

    [Fact]
    public async Task Complete_IngestsForARunWhoseTerminalWasClosed()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        PutResult(run, TwoCandidates, Plan);

        var completed = await _service.CompleteAsync(run.Id);

        completed.IsSuccess.Should().BeTrue(completed.Error);
        run.Status.Should().Be(MorningRunStatus.Ingested);
        _store.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task Complete_FailsTheRun_WhenThereIsNothingToIngest()
    {
        var run = await StartAsync();

        var completed = await _service.CompleteAsync(run.Id);

        completed.IsSuccess.Should().BeFalse();
        completed.Error.Should().Be(Messages.MorningResultUnreadable);
        run.Status.Should().Be(MorningRunStatus.Failed);
    }

    [Fact]
    public async Task Complete_RefusesARunThatIsAlreadyFinished()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        var again = await _service.CompleteAsync(run.Id);

        again.IsSuccess.Should().BeFalse();
        again.Error.Should().Be(Messages.MorningRunAlreadyFinished);
    }

    [Fact]
    public async Task StopTracking_CancelsWithoutIngesting()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeTrue(stopped.Error);
        run.Status.Should().Be(MorningRunStatus.Cancelled);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty("追跡をやめただけ。端末は殺さないし取り込みもしない");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task LinesArrivingAfterTheRunFinished_AreDropped()
    {
        var run = await StartAsync();
        await _service.StopTrackingAsync(run.Id);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        run.Status.Should().Be(MorningRunStatus.Cancelled);
    }

    [Fact]
    public async Task Recover_ResumesFollowingFromTheProcessedLineCount()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        run.ProcessedLines = 12;

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(12);
    }

    [Fact]
    public async Task Recover_IgnoresFinishedRuns()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task AProblemFollowingTheFile_RaisesAWarningWithoutChangingTheStatus()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.ProblemAsync(run.Id, "イベントログを追えなくなりました");

        run.Status.Should().Be(MorningRunStatus.Running);
        _changes.Last().Warning.Should().Be("イベントログを追えなくなりました");
    }

    [Fact]
    public async Task GetLogTail_ReadsTheEndOfTheEventsFile()
    {
        var run = await StartAsync();
        _folder.Lines.AddRange(new[] { "1", "2", "3", "4" });

        var tail = await _service.GetLogTailAsync(run.Id, 3);

        tail.Should().Equal("2", "3", "4");
    }
}
```

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningServiceIngestTests`
Expected: 多数 FAIL（`CompleteAsync` などが空実装なので）

- [ ] **Step 3: 追従と取り込みを書く**

`src/MoTask.Core/Services/MorningService.cs`。「追従（Task 8 で埋める）」の節を丸ごと置き換える:

```csharp
    // ---------- 追従 ----------

    private void Follow(int runId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            runId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(runId, line),
            message => OnProblemAsync(runId, message)));
    }

    /// <summary>フックが 1 行書くたびに呼ばれる（行の順序どおり、直列）。</summary>
    private async Task OnHookLineAsync(int runId, string line)
    {
        var parsed = HookEventParser.Parse(line);
        MorningRun? run = null;
        var lookAtResult = false;
        var lastChance = false;

        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            // 追跡をやめた後・取り込んだ後に届いた行は捨てる（終わった実行を蘇らせない）
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            run = current;
            current.ProcessedLines++;

            switch (parsed.Kind)
            {
                case AiJobEventKind.SessionStarted:
                case AiJobEventKind.ToolUse:
                    current.Status = MorningRunStatus.Running;
                    break;
                case AiJobEventKind.TurnEnded:
                    current.Status = MorningRunStatus.Running;
                    _turns.AddOrUpdate(runId, 1, (_, turns) => turns + 1);
                    // Stop のたびに result/ を見に行く。揃っていなければ何もしない（仕様 §6）。
                    lookAtResult = true;
                    break;
                case AiJobEventKind.SessionEnded:
                    lookAtResult = true;
                    lastChance = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        if (!lookAtResult)
        {
            Raise(run, warning, candidatesChanged: false);
            return;
        }

        var ingest = await IngestAsync(run, lastChance).ConfigureAwait(false);
        Raise(run, warning ?? ingest.Warning, ingest.CandidatesChanged);
    }

    /// <summary>events.jsonl が消えた／作り直された（仕様 §12）。状態は変えず、注意だけ出す。</summary>
    private async Task OnProblemAsync(int runId, string message)
    {
        _events.StopFollowing(runId);
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId)).ConfigureAwait(false);
        if (run is null) return;
        Raise(run, message, candidatesChanged: false);
    }

    // ---------- 取り込み ----------

    private sealed record IngestOutcome(bool CandidatesChanged, string? Warning);

    /// <summary>
    /// result/ を読んで取り込む。プランがまだ書かれていなければ何もしない（Stop は何度でも来る）。
    /// lastChance が true のときだけ、揃っていない実行を Failed にする（仕様 §8）。
    /// </summary>
    private async Task<IngestOutcome> IngestAsync(MorningRun run, bool lastChance)
    {
        // ファイル読みはゲートの外
        var result = MorningResultReader.Read(
            _folder.ReadText(run.JobFolder, JobFolderPaths.CandidatesRelativePath),
            _folder.ReadText(run.JobFolder, JobFolderPaths.PlanRelativePath));

        if (!result.IsUsable)
        {
            if (!lastChance) return new IngestOutcome(false, null);
            await FinishAsync(run, MorningRunStatus.Failed, Messages.MorningResultUnreadable).ConfigureAwait(false);
            return new IngestOutcome(false, Messages.MorningResultUnreadable);
        }

        var added = 0;
        var warning = await _gate.RunAsync(async () =>
        {
            // 取り込みは 1 度だけ。Stop が複数回来ても 2 度目はここで降りる（仕様 §14）。
            if (run.Status.IsTerminal()) return (string?)null;

            var known = (await _runs.GetKnownExternalIdsAsync(
                result.Candidates.Select(c => c.ExternalId).ToList()).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

            foreach (var record in result.Candidates)
            {
                // 却下・登録済みの ExternalId は翌朝また出てきても黙って捨てる（仕様 §9）
                if (!known.Add(record.ExternalId)) continue;
                _runs.AddCandidate(new TriageCandidate
                {
                    MorningRunId = run.Id,
                    ExternalId = record.ExternalId,
                    Source = record.Source,
                    From = record.From,
                    Title = record.Title,
                    Evidence = record.Evidence,
                    Link = record.Link,
                    Reasoning = record.Reasoning,
                    ReceivedAt = record.ReceivedAt,
                    SuggestedDueDate = record.SuggestedDueDate,
                    SuggestedProject = record.SuggestedProject,
                    SuggestedAction = record.SuggestedAction,
                    SuggestedMergeTaskId = record.MergeTargetTaskId,
                    Status = TriageStatus.Pending,
                });
                added++;
            }

            run.PlanJson = result.PlanJson;
            run.Status = MorningRunStatus.Ingested;
            run.EndedAt = _clock.UtcNow;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);

        // 保存失敗のほうが重い。バナーは 1 本なのでそちらを優先する。
        warning ??= result.DiscardedLines > 0
            ? string.Format(Messages.MorningCandidatesDiscardedFormat,
                result.Candidates.Count + result.DiscardedLines, result.DiscardedLines)
            : null;
        return new IngestOutcome(added > 0, warning);
    }

    // ---------- 人の操作 ----------

    public async Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
    {
        var found = await FindActiveRunAsync(runId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);

        var run = found.Value!;
        // 「完了にする」は SessionEnd と同じ扱い（仕様 §12）
        var ingest = await IngestAsync(run, lastChance: true).ConfigureAwait(false);
        Raise(run, ingest.Warning, ingest.CandidatesChanged);
        return run.Status == MorningRunStatus.Ingested
            ? Result.Ok()
            : Result.Fail(Messages.MorningResultUnreadable);
    }

    public async Task<Result> StopTrackingAsync(int runId, CancellationToken ct = default)
    {
        var found = await FindActiveRunAsync(runId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);

        var run = found.Value!;
        // 仕事が終わったわけではないので取り込まない。端末も殺さない（仕様 §12）。
        var warning = await FinishAsync(run, MorningRunStatus.Cancelled, null).ConfigureAwait(false);
        Raise(run, warning, candidatesChanged: false);
        return Result.Ok();
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var run = await _gate.RunAsync(() => _runs.GetUnfinishedRunAsync(ct), ct).ConfigureAwait(false);
        if (run is null || run.JobFolder.Length == 0) return;
        Follow(run.Id, run.JobFolder, run.ProcessedLines);
    }

    private Task<Result<MorningRun>> FindActiveRunAsync(int runId, CancellationToken ct)
        => _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return Result.Fail<MorningRun>(Messages.MorningRunNotFound);
            if (run.Status.IsTerminal()) return Result.Fail<MorningRun>(Messages.MorningRunAlreadyFinished);
            return Result.Ok(run);
        }, ct);

    /// <summary>ゲートの外から呼ぶ。終了状態にして追従を降りる。戻り値はバナー向けの警告。</summary>
    private async Task<string?> FinishAsync(MorningRun run, MorningRunStatus status, string? error)
    {
        var warning = await _gate.RunAsync(async () =>
        {
            run.Status = status;
            run.EndedAt = _clock.UtcNow;
            if (error is not null) run.ErrorMessage = error;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        return warning;
    }
```

`FailAsync`（Task 7 で書いたもの）は `FinishAsync` を使う形に寄せる:

```csharp
    private async Task<Result<MorningRun>> FailAsync(MorningRun run, string error)
    {
        await FinishAsync(run, MorningRunStatus.Failed, error).ConfigureAwait(false);
        Raise(run, null, candidatesChanged: false);
        return Result.Fail<MorningRun>(error);
    }
```

`using MoTask.Core.Morning;` は Task 7 で入っている。`HookEventParser` / `AiJobEventKind` のため
`using MoTask.Core.Ai;` と `using MoTask.Core.Model;` も入っている。

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningService`
Expected: PASS（Start と Ingest の両方）

- [ ] **Step 5: `Task 8 で実装する` が残っていないことを確認する**

Run: `grep -rn "Task 8 で実装する" src/`
Expected: 出力なし

- [ ] **Step 6: コミット**

```
git add src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningServiceIngestTests.cs
git commit -F- <<'MSG'
feat(core): ingest candidates and the plan when result/ is complete

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 9: MorningService — 仕分けの4アクション

**Files:**
- Create: `src/MoTask.Core/Morning/CandidateNote.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`（Task 7 で置いた空実装を全部置き換える）
- Modify: `src/MoTask.App/ViewModels/HistoryFormatter.cs`
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceTriageTests.cs`
- Test: `tests/MoTask.App.Tests/HistoryFormatterTests.cs`（追記）

**Interfaces:**
- Consumes: Task 7 の `CandidateDecision`、Task 8 の `MorningService`
- Produces:
  - `static string CandidateNote.Format(TriageCandidate candidate)`
  - `MorningService.RegisterAsync` / `MergeAsync` / `PostponeAsync` / `RejectAsync`
  - `Strings.HistoryCandidateRegisteredFormat` / `Strings.HistoryCandidateMergedFormat`
- **このタスクの終わりに `// Task 9 で実装する` のコメントが 1 つも残っていないこと**

**ゲートの規律（Global Constraints の再掲。ここが本タスクの一番の落とし穴）:**
`_boardService` は `MorningService` と**同じ `OperationGate`** を取る。ゲートの中から呼ぶと
即デッドロックする。したがって各アクションは必ず「ゲート内で読む → ゲート外で `IBoardService`
を呼ぶ → ゲート内で候補の状態と履歴を書く」の3段構えにする。
下のテストは実物の `BoardService` を同じゲートで組むので、間違えると**テストが返ってこない**。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceTriageTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 仕分けの 4 アクション（仕様 §10）。BoardService は実物を同じ OperationGate で組むので、
/// ゲートの中から IBoardService を呼ぶ実装にするとこのクラスのテストが返ってこなくなる。
/// </summary>
public class MorningServiceTriageTests
{
    /// <summary>デッドロックを「失敗」として見えるようにする上限。</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly Column _backlog;
    private readonly TaskItem _target;
    private readonly MorningRun _run;

    public MorningServiceTriageTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _target = _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
    }

    private TriageCandidate Candidate(TriageAction suggested = TriageAction.Register)
    {
        var candidate = _store.SeedCandidate(_run, "outlook:001", suggested: suggested);
        candidate.SuggestedDueDate = new DateOnly(2026, 9, 8);
        candidate.SuggestedProject = "顧客A";
        return candidate;
    }

    private static async Task<T> WithinLimitAsync<T>(Task<T> work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(Limit));
        finished.Should().BeSameAs(work, "ゲートの中から IBoardService を呼ぶとデッドロックする");
        return await work;
    }

    // ---- 登録 ----

    [Fact]
    public async Task Register_CreatesTheTaskWithTheEditedValues()
    {
        var candidate = Candidate();

        var created = await WithinLimitAsync(_service.RegisterAsync(new CandidateDecision(
            candidate.Id, "  請求先情報を更新する  ", new DateOnly(2026, 9, 9), "顧客A", _backlog.Id)));

        created.IsSuccess.Should().BeTrue(created.Error);
        var task = created.Value!;
        task.Title.Should().Be("請求先情報を更新する", "人が編集した値を使う");
        task.ColumnId.Should().Be(_backlog.Id);
        task.DueDate.Should().Be(new DateOnly(2026, 9, 9));
        _store.Projects.Should().ContainSingle().Which.Name.Should().Be("顧客A");
        task.ProjectId.Should().Be(_store.Projects[0].Id);
    }

    [Fact]
    public async Task Register_PutsTheEvidenceIntoTheDescription()
    {
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)))).Value!;

        task.Description.Should().Contain("Outlook");
        task.Description.Should().Contain("山本さん");
        task.Description.Should().Contain("「9月8日までに」");
        task.Description.Should().Contain("https://outlook.office.com/x");
    }

    [Fact]
    public async Task Register_MarksTheCandidateAndLeavesOneHistoryEntry()
    {
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)))).Value!;

        candidate.Status.Should().Be(TriageStatus.Registered);
        candidate.ResultTaskId.Should().Be(task.Id);
        candidate.DecidedAt.Should().Be(_clock.UtcNow);
        _store.History.Where(h => h.Kind == HistoryKind.CandidateRegistered).Should().ContainSingle()
            .Which.TaskId.Should().Be(task.Id);
    }

    [Fact]
    public async Task Register_ReusesAnExistingProjectByName()
    {
        var existing = _store.SeedProject("顧客A");
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "  顧客A  ", _backlog.Id)))).Value!;

        task.ProjectId.Should().Be(existing.Id);
        _store.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task Register_RefusesABlankTitle()
    {
        var candidate = Candidate();

        var created = await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "   ", null, "", _backlog.Id)));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(Messages.TitleRequired);
        candidate.Status.Should().Be(TriageStatus.Pending);
    }

    [Fact]
    public async Task Register_RefusesACandidateThatIsAlreadyDecided()
    {
        var candidate = Candidate();
        candidate.Status = TriageStatus.Rejected;

        var created = await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(Messages.CandidateAlreadyDecided);
    }

    // ---- 統合 ----

    [Fact]
    public async Task Merge_AppendsTheEvidenceToTheTargetDescription()
    {
        _target.Description = "前からある説明";
        var candidate = Candidate(TriageAction.Merge);

        var merged = await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        merged.IsSuccess.Should().BeTrue(merged.Error);
        _target.Description.Should().StartWith("前からある説明");
        _target.Description.Should().Contain("「9月8日までに」");
    }

    [Fact]
    public async Task Merge_FillsTheDueDateOnlyWhenTheTaskHasNone()
    {
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        _target.DueDate.Should().Be(new DateOnly(2026, 9, 8), "候補側にだけ期限があるとき（仕様 §10）");
    }

    [Fact]
    public async Task Merge_KeepsAnExistingDueDate()
    {
        _target.DueDate = new DateOnly(2026, 9, 30);
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        _target.DueDate.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public async Task Merge_MarksTheCandidateAndLeavesOneHistoryEntry()
    {
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        candidate.Status.Should().Be(TriageStatus.Merged);
        candidate.ResultTaskId.Should().Be(_target.Id);
        _store.History.Where(h => h.Kind == HistoryKind.CandidateMerged).Should().ContainSingle()
            .Which.TaskId.Should().Be(_target.Id);
    }

    [Fact]
    public async Task Merge_RefusesADeletedTarget()
    {
        _target.DeletedAt = _clock.UtcNow;
        var candidate = Candidate(TriageAction.Merge);

        var merged = await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        merged.IsSuccess.Should().BeFalse();
        merged.Error.Should().Be(Messages.TaskNotFound);
        candidate.Status.Should().Be(TriageStatus.Pending);
    }

    // ---- あとで / 却下 ----

    [Fact]
    public async Task Postpone_OnlyChangesTheStatus()
    {
        var candidate = Candidate();

        var postponed = await WithinLimitAsync(_service.PostponeAsync(candidate.Id));

        postponed.IsSuccess.Should().BeTrue(postponed.Error);
        candidate.Status.Should().Be(TriageStatus.Later);
        candidate.DecidedAt.Should().Be(_clock.UtcNow);
        candidate.ResultTaskId.Should().BeNull();
        _store.AllTasks.Should().ContainSingle("タスクは作らない");
        _store.History.Should().NotContain(h => h.Kind == HistoryKind.CandidateRegistered);
    }

    [Fact]
    public async Task Reject_OnlyChangesTheStatus()
    {
        var candidate = Candidate();

        var rejected = await WithinLimitAsync(_service.RejectAsync(candidate.Id));

        rejected.IsSuccess.Should().BeTrue(rejected.Error);
        candidate.Status.Should().Be(TriageStatus.Rejected);
        _store.AllTasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Reject_RefusesAnUnknownCandidate()
    {
        var rejected = await WithinLimitAsync(_service.RejectAsync(9999));

        rejected.IsSuccess.Should().BeFalse();
        rejected.Error.Should().Be(Messages.CandidateNotFound);
    }

    // ---- キュー ----

    [Fact]
    public async Task Queue_LosesACandidateAsSoonAsItIsDecided()
    {
        var candidate = Candidate();
        (await _service.GetQueueAsync(_run.Id)).Should().ContainSingle();

        await WithinLimitAsync(_service.PostponeAsync(candidate.Id));

        (await _service.GetQueueAsync(_run.Id)).Should()
            .BeEmpty("『あとで』にした候補は今日のキューから消え、翌朝の実行で戻ってくる（仕様 §9）");
    }

    [Fact]
    public async Task Queue_KeepsLatersFromEarlierRuns()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:old-later", TriageStatus.Later);
        _store.SeedCandidate(yesterday, "outlook:old-rejected", TriageStatus.Rejected);
        Candidate();

        var queue = await _service.GetQueueAsync(_run.Id);

        queue.Select(c => c.ExternalId).Should().Equal("outlook:old-later", "outlook:001");
    }
}
```

`tests/MoTask.App.Tests/HistoryFormatterTests.cs` のクラス内に足す:

```csharp
    [Fact]
    public void Format_RendersACandidateRegistration()
    {
        var entry = new HistoryEntry
        {
            TaskId = 1, At = new DateTime(2026, 9, 7, 0, 40, 0, DateTimeKind.Utc),
            Kind = HistoryKind.CandidateRegistered,
            Detail = TriageHistoryDetail.Serialize(new TriageHistoryDetail("Outlook", "outlook:001")),
        };

        HistoryFormatter.Format(entry, _ => "やること", TimeZoneInfo.Utc)
            .Should().EndWith(string.Format(Strings.HistoryCandidateRegisteredFormat, "Outlook"));
    }

    [Fact]
    public void Format_RendersACandidateMerge()
    {
        var entry = new HistoryEntry
        {
            TaskId = 1, At = new DateTime(2026, 9, 7, 0, 40, 0, DateTimeKind.Utc),
            Kind = HistoryKind.CandidateMerged,
            Detail = TriageHistoryDetail.Serialize(new TriageHistoryDetail("Teams", "teams:002")),
        };

        HistoryFormatter.Format(entry, _ => "やること", TimeZoneInfo.Utc)
            .Should().EndWith(string.Format(Strings.HistoryCandidateMergedFormat, "Teams"));
    }

    [Fact]
    public void Format_FallsBackWhenTheTriageDetailIsBroken()
    {
        var entry = new HistoryEntry
        {
            TaskId = 1, At = new DateTime(2026, 9, 7, 0, 40, 0, DateTimeKind.Utc),
            Kind = HistoryKind.CandidateRegistered, Detail = "{壊れた",
        };

        HistoryFormatter.Format(entry, _ => "やること", TimeZoneInfo.Utc)
            .Should().EndWith(Strings.HistoryUnknown);
    }
```

既存の `HistoryFormatterTests` が `TimeZoneInfo` を渡していない書き方なら、そちらに合わせる。

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter FullyQualifiedName~MorningServiceTriageTests`
Expected: FAIL（4アクションが `Messages.CandidateNotFound` を返す空実装のまま）

- [ ] **Step 3: 根拠の文面を書く**

`src/MoTask.Core/Morning/CandidateNote.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// 候補の根拠をタスクの説明に残す文面（仕様 §10）。登録と統合で同じものを使う。
/// これが無いと、タスクになった後で「なぜこれをやるのか」が辿れなくなる。
/// </summary>
public static class CandidateNote
{
    public static string Format(TriageCandidate candidate)
    {
        var lines = new List<string> { string.Format(Messages.CandidateNoteHeaderFormat, candidate.Source) };
        if (candidate.From.Length > 0) lines.Add(string.Format(Messages.CandidateNoteFromFormat, candidate.From));
        lines.Add(candidate.Evidence);
        if (candidate.Link.Length > 0) lines.Add(candidate.Link);
        return string.Join("\n", lines);
    }
}
```

- [ ] **Step 4: 4アクションを書く**

`src/MoTask.Core/Services/MorningService.cs` の「仕分け（Task 9 で埋める）」の節を置き換える:

```csharp
    // ---------- 仕分け ----------

    public async Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
    {
        var title = decision.Title.Trim();
        if (title.Length == 0) return Result.Fail<TaskItem>(Messages.TitleRequired);

        var found = await FindPendingCandidateAsync(decision.CandidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail<TaskItem>(found.Error!);
        var candidate = found.Value!;

        // ここからゲートの外。IBoardService は同じゲートを取るので、中から呼ぶとデッドロックする。
        var project = await ResolveProjectAsync(decision.ProjectName, ct).ConfigureAwait(false);
        if (!project.IsSuccess) return Result.Fail<TaskItem>(project.Error!);

        var created = await _boardService.CreateTaskAsync(decision.ColumnId, title, ct).ConfigureAwait(false);
        if (!created.IsSuccess) return Result.Fail<TaskItem>(created.Error!);
        var task = created.Value!;

        var updated = await _boardService.UpdateTaskAsync(
            new TaskUpdate(task.Id, title, CandidateNote.Format(candidate), project.Value, decision.DueDate), ct)
            .ConfigureAwait(false);
        if (!updated.IsSuccess) return Result.Fail<TaskItem>(updated.Error!);

        var warning = await DecideAsync(candidate, TriageStatus.Registered, task.Id,
            HistoryKind.CandidateRegistered, ct).ConfigureAwait(false);

        return Result.Ok(task, warning is null ? null : new[] { warning });
    }

    public async Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
    {
        var found = await FindPendingCandidateAsync(candidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);
        var candidate = found.Value!;

        var target = await _gate.RunAsync(async () =>
        {
            var task = await _boards.GetTaskAsync(targetTaskId, ct).ConfigureAwait(false);
            return task is null || task.IsDeleted ? null : task;
        }, ct).ConfigureAwait(false);
        if (target is null) return Result.Fail(Messages.TaskNotFound);

        var note = CandidateNote.Format(candidate);
        var description = target.Description.TrimEnd().Length == 0
            ? note
            : target.Description.TrimEnd() + "\n\n" + note;
        // 候補側にだけ期限があるときだけ入れる。既にある期限は上書きしない（仕様 §10）。
        var due = target.DueDate ?? candidate.SuggestedDueDate;

        var updated = await _boardService.UpdateTaskAsync(
            new TaskUpdate(target.Id, target.Title, description, target.ProjectId, due), ct).ConfigureAwait(false);
        if (!updated.IsSuccess) return updated;

        var warning = await DecideAsync(candidate, TriageStatus.Merged, target.Id,
            HistoryKind.CandidateMerged, ct).ConfigureAwait(false);
        return warning is null ? Result.Ok() : Result.Ok(warning);
    }

    public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        => DecideOnlyAsync(candidateId, TriageStatus.Later, ct);

    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        => DecideOnlyAsync(candidateId, TriageStatus.Rejected, ct);

    /// <summary>「あとで」「却下」は候補の状態を変えるだけ。タスクは作らないし履歴も残さない。</summary>
    private async Task<Result> DecideOnlyAsync(int candidateId, TriageStatus status, CancellationToken ct)
    {
        var found = await FindPendingCandidateAsync(candidateId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return Result.Fail(found.Error!);

        var warning = await DecideAsync(found.Value!, status, resultTaskId: null, kind: null, ct).ConfigureAwait(false);
        return warning is null ? Result.Ok() : Result.Ok(warning);
    }

    private Task<Result<TriageCandidate>> FindPendingCandidateAsync(int candidateId, CancellationToken ct)
        => _gate.RunAsync(async () =>
        {
            var candidate = await _runs.GetCandidateAsync(candidateId, ct).ConfigureAwait(false);
            if (candidate is null) return Result.Fail<TriageCandidate>(Messages.CandidateNotFound);
            if (candidate.Status != TriageStatus.Pending) return Result.Fail<TriageCandidate>(Messages.CandidateAlreadyDecided);
            return Result.Ok(candidate);
        }, ct);

    /// <summary>ゲートの中で候補を片づけ、必要なら履歴を 1 件残す。戻り値はバナー向けの警告。</summary>
    private async Task<string?> DecideAsync(
        TriageCandidate candidate, TriageStatus status, int? resultTaskId, HistoryKind? kind, CancellationToken ct)
    {
        var warning = await _gate.RunAsync(async () =>
        {
            candidate.Status = status;
            candidate.ResultTaskId = resultTaskId;
            candidate.DecidedAt = _clock.UtcNow;
            if (kind is HistoryKind historyKind && resultTaskId is int taskId)
            {
                _history.Add(new HistoryEntry
                {
                    TaskId = taskId, At = _clock.UtcNow, Kind = historyKind,
                    Detail = TriageHistoryDetail.Serialize(
                        new TriageHistoryDetail(candidate.Source, candidate.ExternalId)),
                });
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await RaiseCandidatesChangedAsync(candidate.MorningRunId, warning, ct).ConfigureAwait(false);
        return warning;
    }

    /// <summary>候補が動いたことだけを知らせる。実行そのものの状態は変わらない。</summary>
    private async Task RaiseCandidatesChangedAsync(int runId, string? warning, CancellationToken ct)
    {
        var run = await _gate.RunAsync(() => _runs.GetRunAsync(runId, ct), ct).ConfigureAwait(false);
        if (run is null) return;
        Raise(run, warning, candidatesChanged: true);
    }

    /// <summary>
    /// 名前でプロジェクトを引き、無ければ作る。空白だけなら「プロジェクト無し」。
    /// ゲートの外から呼ぶこと（IBoardService を使う）。
    /// </summary>
    private async Task<Result<int?>> ResolveProjectAsync(string name, CancellationToken ct)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return Result.Ok<int?>(null);

        var projects = await _boardService.GetProjectsAsync(ct).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return Result.Ok<int?>(existing.Id);

        var created = await _boardService.CreateProjectAsync(wanted, ct).ConfigureAwait(false);
        return created.IsSuccess ? Result.Ok<int?>(created.Value!.Id) : Result.Fail<int?>(created.Error!);
    }
```

- [ ] **Step 5: 履歴の表示を足す**

`src/MoTask.App/Resources/Strings.resx` に足す:

```xml
  <data name="HistoryCandidateRegisteredFormat" xml:space="preserve"><value>{0} の候補を登録</value></data>
  <data name="HistoryCandidateMergedFormat" xml:space="preserve"><value>{0} の候補を統合</value></data>
```

`src/MoTask.App/Resources/Strings.cs` に足す:

```csharp
    public static string HistoryCandidateRegisteredFormat => Get(nameof(HistoryCandidateRegisteredFormat));
    public static string HistoryCandidateMergedFormat => Get(nameof(HistoryCandidateMergedFormat));
```

`src/MoTask.App/ViewModels/HistoryFormatter.cs` の `switch` に2つ足す:

```csharp
            HistoryKind.CandidateRegistered => FormatTriage(entry.Detail, Strings.HistoryCandidateRegisteredFormat),
            HistoryKind.CandidateMerged => FormatTriage(entry.Detail, Strings.HistoryCandidateMergedFormat),
```

同じファイルに private メソッドを足す:

```csharp
    /// <summary>壊れた Detail で呼び出し元を落とさない（FormatAi と同じ扱い）。</summary>
    private static string FormatTriage(string detail, string format)
    {
        var triage = TriageHistoryDetail.Deserialize(detail);
        return triage is null ? Strings.HistoryUnknown : string.Format(format, triage.Source);
    }
```

- [ ] **Step 6: テストが通ることを確認する**

Run: `dotnet test MoTask.sln`
Expected: PASS。**`MorningServiceTriageTests` が 10 秒以内に終わること**（返ってこない＝
ゲートの中から `IBoardService` を呼んでいる）

- [ ] **Step 7: `Task 9 で実装する` が残っていないことを確認する**

Run: `grep -rn "Task 9 で実装する" src/`
Expected: 出力なし

- [ ] **Step 8: コミット**

```
git add src/MoTask.Core src/MoTask.App tests
git commit -F- <<'MSG'
feat(core): register, merge, postpone and reject a candidate

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 10: 最小の UI と配線

仕様 §15 の「最小の UI（候補一覧とアクション）」まで。**ワイヤー 4a / 4b の左パネルのモード切替、
右カラムの4区分、候補件数バッジ、キーボード、一括操作は 2本目の計画**なので、ここでは作らない。
この画面は 2本目で作り直される前提の足場である。

**Files:**
- Create: `src/MoTask.App/ViewModels/CandidateItemViewModel.cs`
- Create: `src/MoTask.App/ViewModels/MorningPlanViewModel.cs`
- Create: `src/MoTask.App/Views/MorningPlanView.xaml` (+ `.xaml.cs`)
- Modify: `src/MoTask.App/App.xaml.cs`
- Modify: `src/MoTask.App/Views/MainWindow.xaml` (+ `.xaml.cs`)
- Modify: `src/MoTask.App/Resources/Strings.resx` / `Strings.cs`
- Modify: `README.md`
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`
- Test: `tests/MoTask.App.Tests/HostWiringTests.cs`（追記）

**Interfaces:**
- Consumes: Task 7〜9 の `IMorningService` / `CandidateDecision` / `MorningRunChangedEventArgs`
- Produces: `MorningPlanViewModel`（DI から解決される singleton）、`CandidateItemViewModel`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`。`IMorningService` の偽物は NSubstitute
（`Directory.Packages.props` にある）ではなく、状態を持たせたいので手書きにする:

```csharp
using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class MorningPlanViewModelTests
{
    /// <summary>呼ばれた操作を記録するだけの偽サービス。</summary>
    private sealed class FakeMorningService : IMorningService
    {
        public event EventHandler<MorningRunChangedEventArgs>? RunChanged;

        public MorningRun? Current { get; set; }
        public List<TriageCandidate> Queue { get; } = new();
        public List<string> Calls { get; } = new();
        public Result<MorningRun> StartResult { get; set; } = Result.Ok(new MorningRun());
        public Result<TaskItem> RegisterResult { get; set; } = Result.Ok(new TaskItem { Id = 1 });
        public CandidateDecision? LastDecision { get; private set; }

        public void Raise(MorningRun run, string? warning = null, bool candidates = false)
            => RunChanged?.Invoke(this, new MorningRunChangedEventArgs(
                new MorningRunSnapshot(run.Id, run.Date, run.Status, 0, run.ErrorMessage, run.JobFolder),
                warning, candidates));

        public Task<MorningRun?> GetCurrentRunAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task<IReadOnlyList<TriageCandidate>> GetQueueAsync(int runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TriageCandidate>>(Queue.ToList());

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

        public Task<Result<TaskItem>> RegisterAsync(CandidateDecision decision, CancellationToken ct = default)
        {
            Calls.Add("Register");
            LastDecision = decision;
            if (RegisterResult.IsSuccess) Queue.RemoveAll(c => c.Id == decision.CandidateId);
            return Task.FromResult(RegisterResult);
        }

        public Task<Result> MergeAsync(int candidateId, int targetTaskId, CancellationToken ct = default)
        {
            Calls.Add($"Merge:{targetTaskId}");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> PostponeAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Postpone");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
        {
            Calls.Add("Reject");
            Queue.RemoveAll(c => c.Id == candidateId);
            return Task.FromResult(Result.Ok());
        }
    }

    private readonly FakeMorningService _service = new();
    private readonly IBoardService _boards = Substitute.For<IBoardService>();
    private readonly MorningPlanViewModel _vm;
    private readonly List<string> _opened = new();

    public MorningPlanViewModelTests()
    {
        // 未着手(1) / 進行中(2) / 完了(3)。完了列は登録先に出さない。
        _boards.GetBoardAsync().Returns(Task.FromResult(Result.Ok(TestBoards.Sample())));
        _vm = new MorningPlanViewModel(_service, _boards) { OpenPath = _opened.Add };
    }

    [Fact]
    public async Task Load_OffersEveryColumnExceptDone()
    {
        await _vm.LoadAsync();

        _vm.ColumnChoices.Select(c => c.Id).Should().Equal(1, 2);
        _vm.EditColumnId.Should().Be(1, "既定は先頭の列");
    }

    private TriageCandidate Candidate(int id = 1, TriageAction suggested = TriageAction.Register)
        => new()
        {
            Id = id, MorningRunId = 1, ExternalId = $"outlook:{id:000}", Source = "Outlook",
            From = "山本さん", Title = "請求先情報を更新する", Evidence = "「9月8日までに」",
            Link = "https://outlook.office.com/x", Reasoning = "依頼が明確",
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
            SuggestedAction = suggested, SuggestedMergeTaskId = suggested == TriageAction.Merge ? 12 : null,
        };

    private MorningRun IngestedRun() => new()
    {
        Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Ingested,
        JobFolder = @"C:\work\morning\0001-2026-09-07", PlanJson = "{\"groups\":[]}",
    };

    [Fact]
    public async Task Load_WithNoRun_OffersToStart()
    {
        await _vm.LoadAsync();

        _vm.CanStart.Should().BeTrue();
        _vm.IsRunning.Should().BeFalse();
        _vm.Candidates.Should().BeEmpty();
        _vm.HasNoCandidates.Should().BeFalse("まだ一度も走らせていないので『候補なし』ではない");
    }

    [Fact]
    public async Task Load_WithCandidates_SelectsTheFirstAndFillsTheEditor()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        _service.Queue.Add(Candidate(2));

        await _vm.LoadAsync();

        _vm.Candidates.Should().HaveCount(2);
        _vm.Selected!.CandidateId.Should().Be(1);
        _vm.EditTitle.Should().Be("請求先情報を更新する", "人が編集してから登録できる");
        // 期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）
        _vm.EditDueDate.Should().Be(new DateTime(2026, 9, 8));
        _vm.EditProjectName.Should().Be("顧客A");
        _vm.PositionText.Should().Be("1 / 2");
        _vm.CanStart.Should().BeFalse("片づけ終わるまでは次の実行を始めない");
    }

    [Fact]
    public async Task Load_WithAnIngestedRunAndNoCandidates_SaysSo()
    {
        _service.Current = IngestedRun();

        await _vm.LoadAsync();

        _vm.HasNoCandidates.Should().BeTrue("候補 0 件は失敗ではない（仕様 §11）");
        _vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task Load_WithAFailedRun_ShowsTheReasonAndTheFolder()
    {
        _service.Current = new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Failed,
            ErrorMessage = "読み取れませんでした", JobFolder = @"C:\work\morning\0001-2026-09-07",
        };

        await _vm.LoadAsync();

        _vm.IsFailed.Should().BeTrue();
        _vm.ErrorMessage.Should().Be("読み取れませんでした");
        _vm.OpenJobFolderCommand.Execute(null);
        _opened.Should().Equal(@"C:\work\morning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_ShowsTheError_WhenTheServiceRefuses()
    {
        _service.StartResult = Result.Fail<MorningRun>("claude が見つかりません");

        await _vm.LoadAsync();
        await _vm.StartCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("claude が見つかりません");
        _service.Calls.Should().Equal("Start");
    }

    [Fact]
    public async Task Register_PassesTheEditedValues_AndMovesToTheNextCandidate()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        _service.Queue.Add(Candidate(2));
        await _vm.LoadAsync();
        _vm.EditTitle = "書き換えた題名";
        _vm.EditDueDate = new DateTime(2026, 9, 10);
        _vm.EditProjectName = "別プロジェクト";
        _vm.EditColumnId = 2;

        await _vm.RegisterCommand.ExecuteAsync(null);

        _service.LastDecision.Should().Be(new CandidateDecision(1, "書き換えた題名",
            new DateOnly(2026, 9, 10), "別プロジェクト", 2));
        _vm.Candidates.Should().ContainSingle();
        _vm.Selected!.CandidateId.Should().Be(2);
        _vm.PositionText.Should().Be("1 / 1");
    }

    [Fact]
    public async Task Register_KeepsTheCandidate_WhenTheServiceFails()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();
        _service.RegisterResult = Result.Fail<TaskItem>("列が見つかりません");

        await _vm.RegisterCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().Be("列が見つかりません");
        _vm.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Merge_UsesTheSuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate(suggested: TriageAction.Merge));
        await _vm.LoadAsync();

        _vm.Selected!.CanMerge.Should().BeTrue();
        await _vm.MergeCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain("Merge:12");
    }

    [Fact]
    public async Task Merge_IsNotOfferedWithoutASuggestedTarget()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        _vm.Selected!.CanMerge.Should().BeFalse("統合先が無ければ 2 本目の計画で選ばせる。今は出さない");
    }

    [Theory]
    [InlineData("Postpone")]
    [InlineData("Reject")]
    public async Task PostponeAndReject_TakeTheCandidateOutOfTheQueue(string call)
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        if (call == "Postpone") await _vm.PostponeCommand.ExecuteAsync(null);
        else await _vm.RejectCommand.ExecuteAsync(null);

        _service.Calls.Should().Contain(call);
        _vm.Candidates.Should().BeEmpty();
        _vm.Selected.Should().BeNull();
        _vm.HasNoCandidates.Should().BeTrue();
    }

    [Fact]
    public async Task OpenLink_OpensTheCandidateLink()
    {
        _service.Current = IngestedRun();
        _service.Queue.Add(Candidate());
        await _vm.LoadAsync();

        _vm.OpenLinkCommand.Execute(null);

        _opened.Should().Equal("https://outlook.office.com/x");
    }

    [Fact]
    public async Task Load_WithNoRun_MentionsThePreviousOne()
    {
        await _vm.LoadAsync();
        _vm.LastRunText.Should().BeEmpty("一度も走らせていなければ『前回』は無い");

        _service.Current = new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 5), Status = MorningRunStatus.Ingested,
        };
        await _vm.LoadAsync();

        _vm.LastRunText.Should().Be(string.Format(Strings.MorningLastRunFormat, "9/5"));
    }

    [Fact]
    public async Task RunChanged_ForARunningRun_SwitchesToTheProgressView()
    {
        await _vm.LoadAsync();

        _service.Raise(new MorningRun
        {
            Id = 1, Date = new DateOnly(2026, 9, 7), Status = MorningRunStatus.Running,
        });

        _vm.IsRunning.Should().BeTrue();
        _vm.CanStart.Should().BeFalse();
        _vm.CanControl.Should().BeTrue("『完了にする』『追跡をやめる』を出す");
        _vm.ProgressText.Should().Be(string.Format(Strings.MorningTurnsFormat, 0),
            "仕様 §11『実行中』はターン数を出す");
    }

    [Fact]
    public async Task RunChanged_ShowsTheWarningAboutDiscardedLines()
    {
        await _vm.LoadAsync();

        _service.Raise(IngestedRun(), warning: "5 件のうち 1 件は読み取れませんでした");

        _vm.WarningMessage.Should().Be("5 件のうち 1 件は読み取れませんでした");
    }

    [Fact]
    public async Task RunChanged_WithCandidates_ReloadsTheQueue()
    {
        await _vm.LoadAsync();
        var run = IngestedRun();
        _service.Current = run;
        _service.Queue.Add(Candidate());

        _service.Raise(run, candidates: true);
        await _vm.PendingLoad;

        _vm.Candidates.Should().ContainSingle();
    }
}
```

`tests/MoTask.App.Tests/HostWiringTests.cs` に足す:

```csharp
    [Fact]
    public void BuildHost_GivesMorningServiceTheSameOperationGate()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        GateOf(host.Services.GetRequiredService<IMorningService>())
            .Should().BeSameAs(host.Services.GetRequiredService<OperationGate>());
    }

    [Fact]
    public void BuildHost_ResolvesTheMorningPlanViewModel()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        host.Services.GetRequiredService<ViewModels.MorningPlanViewModel>().Should().NotBeNull();
    }
```

- [ ] **Step 2: 落ちることを確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~MorningPlanViewModel`
Expected: コンパイルエラー（`MorningPlanViewModel` が存在しない）

- [ ] **Step 3: 文言を足す**

`src/MoTask.App/Resources/Strings.resx` に足す:

```xml
  <data name="ViewMorningPlan" xml:space="preserve"><value>朝の実行プラン</value></data>
  <data name="MorningStart" xml:space="preserve"><value>朝のプランを作る</value></data>
  <data name="MorningNoRun" xml:space="preserve"><value>今日のプランはまだありません</value></data>
  <data name="MorningLastRunFormat" xml:space="preserve"><value>前回: {0}</value></data>
  <data name="MorningRunning" xml:space="preserve"><value>Claude が受信箱を見ています</value></data>
  <data name="MorningTurnsFormat" xml:space="preserve"><value>{0} ターン</value></data>
  <data name="MorningComplete" xml:space="preserve"><value>完了にする</value></data>
  <data name="MorningStopTracking" xml:space="preserve"><value>追跡をやめる</value></data>
  <data name="MorningOpenJobFolder" xml:space="preserve"><value>ジョブフォルダを開く</value></data>
  <data name="MorningNoCandidates" xml:space="preserve"><value>候補はありませんでした</value></data>
  <data name="MorningTriageHeading" xml:space="preserve"><value>タスク候補の仕分け</value></data>
  <data name="MorningPositionFormat" xml:space="preserve"><value>{0} / {1}</value></data>
  <data name="MorningEvidence" xml:space="preserve"><value>根拠</value></data>
  <data name="MorningOpenSource" xml:space="preserve"><value>元のメールを開く</value></data>
  <data name="MorningReasoning" xml:space="preserve"><value>AI の判断</value></data>
  <data name="MorningRegister" xml:space="preserve"><value>登録</value></data>
  <data name="MorningMerge" xml:space="preserve"><value>統合</value></data>
  <data name="MorningPostpone" xml:space="preserve"><value>あとで</value></data>
  <data name="MorningReject" xml:space="preserve"><value>却下</value></data>
```

`src/MoTask.App/Resources/Strings.cs` に同名のプロパティを 19 個足す（既存の書き方に合わせて
`public static string Xxx => Get(nameof(Xxx));`）。

- [ ] **Step 4: 候補1件の ViewModel を書く**

`src/MoTask.App/ViewModels/CandidateItemViewModel.cs`:

```csharp
using System.Globalization;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>候補 1 件の読み取り専用の見え方。編集中の値は MorningPlanViewModel が持つ。</summary>
public sealed class CandidateItemViewModel
{
    private readonly TriageCandidate _candidate;

    public CandidateItemViewModel(TriageCandidate candidate)
    {
        _candidate = candidate;
    }

    public int CandidateId => _candidate.Id;
    public string Source => _candidate.Source;
    public string From => _candidate.From;
    public string Title => _candidate.Title;
    public string Evidence => _candidate.Evidence;
    public string Reasoning => _candidate.Reasoning;
    public string Link => _candidate.Link;
    public bool HasLink => _candidate.Link.Length > 0;
    public string SuggestedProject => _candidate.SuggestedProject;
    public DateOnly? SuggestedDueDate => _candidate.SuggestedDueDate;
    public TriageAction SuggestedAction => _candidate.SuggestedAction;
    public int? SuggestedMergeTaskId => _candidate.SuggestedMergeTaskId;

    /// <summary>統合先が推薦されているときだけ「統合」を出す（選ばせるのは 2 本目の計画）。</summary>
    public bool CanMerge => _candidate.SuggestedMergeTaskId is not null;

    /// <summary>受信時刻は DB に UTC で入っているので現地時刻へ直す。</summary>
    public string ReceivedText => _candidate.ReceivedAt is DateTime at
        ? HistoryFormatter.Timestamp(at)
        : "";

    public string DueText => _candidate.SuggestedDueDate?.ToString("M/d", CultureInfo.InvariantCulture) ?? "";
}
```

- [ ] **Step 5: 画面の ViewModel を書く**

`src/MoTask.App/ViewModels/MorningPlanViewModel.cs`:

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
/// 朝の実行プラン画面（仕様 §11）。この計画では候補一覧と 4 アクションまで。
/// ワイヤー 4a / 4b の左パネルのモード切替とプランの 4 区分は 2 本目の計画で作る。
/// RunChanged はワーカースレッドから来るので UI スレッドへ載せ替える（BoardViewModel と同じ）。
/// </summary>
public sealed partial class MorningPlanViewModel : ObservableObject
{
    private readonly IMorningService _service;
    private readonly IBoardService _boardService;
    private readonly SynchronizationContext? _ui;
    private MorningRun? _run;

    /// <summary>リンクや成果物を開く。テストでは差し替える。</summary>
    public Action<string> OpenPath { get; set; } = ShellOpener.Open;

    /// <summary>テストが読み込みの完了を待つためのハンドル。</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();

    /// <summary>登録先に選べる列。完了列は選ばせない。LoadAsync で埋める。</summary>
    public ObservableCollection<ColumnChoice> ColumnChoices { get; } = new();

    [ObservableProperty] private CandidateItemViewModel? _selected;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _hasNoCandidates;
    [ObservableProperty] private string _headingText = "";
    [ObservableProperty] private string _positionText = "";
    /// <summary>プラン未生成のときの「前回: 9/5」（仕様 §11）。無ければ空文字。</summary>
    [ObservableProperty] private string _lastRunText = "";
    /// <summary>実行中の進捗。ターン数と直近のツール使用（仕様 §11）。</summary>
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _warningMessage;

    // 編集フォーム。期限は DatePicker に直接つなぐので DateTime?（TaskDetailViewModel と同じ流儀）。
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editProjectName = "";
    [ObservableProperty] private int _editColumnId;

    public MorningPlanViewModel(IMorningService service, IBoardService boardService)
    {
        _service = service;
        _boardService = boardService;
        _ui = SynchronizationContext.Current;
        Debug.Assert(_ui is not null || Application.Current is null,
            "MorningPlanViewModel は UI スレッドで生成すること。");

        service.RunChanged += (_, e) => Post(() => OnRunChanged(e));
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public async Task LoadAsync()
    {
        await LoadColumnChoicesAsync().ConfigureAwait(true);
        _run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        await ReloadQueueAsync().ConfigureAwait(true);
        ApplyRunState();
    }

    /// <summary>
    /// 登録先の選択肢。完了列は選ばせない（仕様 §11）。
    /// 盤面の取得に失敗したら選択肢は空のままにして、画面そのものは出す。
    /// </summary>
    private async Task LoadColumnChoicesAsync()
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(true);
        ColumnChoices.Clear();
        if (!board.IsSuccess) return;
        foreach (var column in board.Value!.Columns.Where(c => c.Role != ColumnRole.Done).OrderBy(c => c.Order))
            ColumnChoices.Add(new ColumnChoice(column.Id, column.Name));
        if (EditColumnId == 0 && ColumnChoices.Count > 0) EditColumnId = ColumnChoices[0].Id;
    }

    private async Task ReloadQueueAsync()
    {
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
        EditTitle = candidate?.Title ?? "";
        EditDueDate = candidate?.SuggestedDueDate?.ToDateTime(TimeOnly.MinValue);
        EditProjectName = candidate?.SuggestedProject ?? "";
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        HeadingText = Strings.MorningTriageHeading;
        PositionText = Selected is null
            ? ""
            : string.Format(Strings.MorningPositionFormat, Candidates.IndexOf(Selected) + 1, Candidates.Count);
        HasNoCandidates = Candidates.Count == 0 && _run is { Status: MorningRunStatus.Ingested };
        CanStart = _run is null || (_run.Status.IsTerminal() && Candidates.Count == 0);
        IsRunning = _run is not null && _run.Status.IsActive();
        IsFailed = _run is { Status: MorningRunStatus.Failed };
        CanControl = IsRunning;
        ProgressText = IsRunning ? string.Format(Strings.MorningTurnsFormat, _service.TurnCountOf(_run!.Id)) : "";
        // 「今日のプランはまだありません（前回: 9/5）」（仕様 §11）
        LastRunText = _run is null || !CanStart
            ? ""
            : string.Format(Strings.MorningLastRunFormat, _run.Date.ToString("M/d", CultureInfo.InvariantCulture));
        ErrorMessage = IsFailed ? _run!.ErrorMessage : ErrorMessage;
    }

    private void ApplyRunState() => UpdateCounters();

    private void OnRunChanged(MorningRunChangedEventArgs e)
    {
        if (_run is not null && _run.Id != e.Run.RunId) return;
        WarningMessage = e.Warning ?? WarningMessage;
        if (_run is not null)
        {
            _run.Status = e.Run.Status;
            _run.ErrorMessage = e.Run.ErrorMessage;
        }
        else if (e.Run.Status.IsActive())
        {
            // 開始直後の 1 通目より先にフックの行が届くことがある。実行を知らないまま捨てない。
            _run = new MorningRun
            {
                Id = e.Run.RunId, Date = e.Run.Date, Status = e.Run.Status,
                ErrorMessage = e.Run.ErrorMessage, JobFolder = e.Run.JobFolder,
            };
        }
        UpdateCounters();
        if (e.CandidatesChanged) PendingLoad = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _run = await _service.GetCurrentRunAsync().ConfigureAwait(true);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    // ---------- 操作 ----------

    [RelayCommand]
    private async Task StartAsync()
    {
        ErrorMessage = null;
        WarningMessage = null;
        var started = await _service.StartAsync().ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            ErrorMessage = started.Error;
            return;
        }
        _run = started.Value;
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_run is null) return;
        var done = await _service.CompleteAsync(_run.Id).ConfigureAwait(true);
        if (!done.IsSuccess) ErrorMessage = done.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_run is null) return;
        var stopped = await _service.StopTrackingAsync(_run.Id).ConfigureAwait(true);
        if (!stopped.IsSuccess) ErrorMessage = stopped.Error;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        var due = EditDueDate is DateTime date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var registered = await _service.RegisterAsync(new CandidateDecision(
            Selected.CandidateId, EditTitle, due, EditProjectName, EditColumnId)).ConfigureAwait(true);
        await AfterDecisionAsync(registered).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Selected?.SuggestedMergeTaskId is not int target) return;
        ErrorMessage = null;
        var merged = await _service.MergeAsync(Selected.CandidateId, target).ConfigureAwait(true);
        await AfterDecisionAsync(merged).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PostponeAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        await AfterDecisionAsync(await _service.PostponeAsync(Selected.CandidateId).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (Selected is null) return;
        ErrorMessage = null;
        await AfterDecisionAsync(await _service.RejectAsync(Selected.CandidateId).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    /// <summary>片づいたら次の 1 件へ。失敗したらキューはそのままで理由だけ出す。</summary>
    private async Task AfterDecisionAsync(Result result)
    {
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        if (result.Warnings.Count > 0) WarningMessage = string.Join(" / ", result.Warnings);
        await ReloadQueueAsync().ConfigureAwait(true);
        UpdateCounters();
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Selected is { HasLink: true } candidate) OpenPath(candidate.Link);
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

`RegisterAsync` は `Result<TaskItem>` を返すが、`Result` を受ける `AfterDecisionAsync` に
そのまま渡せる（`Result<T> : Result`）。

- [ ] **Step 6: ビューを書く**

`src/MoTask.App/Views/MorningPlanView.xaml`。既存の `BoardView.xaml` / `TaskDetailPanel.xaml` の
`StaticResource` の名前（`Brush.Surface` / `Pad.Board` / `Btn.Primary` / `Btn.Ghost` /
`Text.Label` / `Text.Caption` / `Gap.Top.4` など）に合わせること。**新しいブラシやスタイルは
足さない**（見た目の作り込みは 2 本目の計画）:

```xml
<UserControl x:Class="MoTask.App.Views.MorningPlanView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:res="clr-namespace:MoTask.App.Resources"
             xmlns:vm="clr-namespace:MoTask.App.ViewModels"
             mc:Ignorable="d" d:DataContext="{d:DesignInstance vm:MorningPlanViewModel}">
  <DockPanel Margin="{StaticResource Pad.Board}">

    <!-- バナー -->
    <Border DockPanel.Dock="Top" Background="{StaticResource Brush.AccentSubtle}"
            BorderBrush="{StaticResource Brush.Danger}" BorderThickness="0,0,0,1"
            Padding="{StaticResource Pad.BarTight}"
            Visibility="{Binding ErrorMessage, Converter={StaticResource NullToVisibility}}">
      <DockPanel>
        <Button DockPanel.Dock="Right" Content="×" Style="{StaticResource Btn.Ghost}"
                Command="{Binding DismissBannerCommand}" />
        <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource Brush.Danger}"
                   VerticalAlignment="Center" TextWrapping="Wrap" />
      </DockPanel>
    </Border>
    <TextBlock DockPanel.Dock="Top" Text="{Binding WarningMessage}" Style="{StaticResource Text.Caption}"
               TextWrapping="Wrap" Margin="{StaticResource Gap.Top.4}"
               Visibility="{Binding WarningMessage, Converter={StaticResource NullToVisibility}}" />

    <!-- プラン未生成（仕様 §11） -->
    <StackPanel DockPanel.Dock="Top" Margin="{StaticResource Gap.Top.4}"
                Visibility="{Binding CanStart, Converter={StaticResource BoolToVisibility}}">
      <TextBlock Text="{x:Static res:Strings.MorningNoRun}" />
      <TextBlock Text="{Binding LastRunText}" Style="{StaticResource Text.Caption}" />
    </StackPanel>

    <!-- 実行のボタン -->
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="{StaticResource Gap.Top.4}">
      <Button Content="{x:Static res:Strings.MorningStart}" Style="{StaticResource Btn.Primary}"
              Command="{Binding StartCommand}"
              Visibility="{Binding CanStart, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{x:Static res:Strings.MorningRunning}" VerticalAlignment="Center"
                 Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressText}" Style="{StaticResource Text.Caption}"
                 VerticalAlignment="Center" Margin="{StaticResource Gap.Left.2}"
                 Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}" />
      <Button Content="{x:Static res:Strings.MorningComplete}" Style="{StaticResource Btn.Ghost}"
              Command="{Binding CompleteCommand}" Margin="{StaticResource Gap.Left.2}"
              Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
      <Button Content="{x:Static res:Strings.MorningStopTracking}" Style="{StaticResource Btn.Ghost}"
              Command="{Binding StopTrackingCommand}"
              Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
      <Button Content="{x:Static res:Strings.MorningOpenJobFolder}" Style="{StaticResource Btn.Ghost}"
              Command="{Binding OpenJobFolderCommand}"
              Visibility="{Binding IsFailed, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>

    <TextBlock DockPanel.Dock="Top" Text="{x:Static res:Strings.MorningNoCandidates}"
               Style="{StaticResource Text.Caption}" Margin="{StaticResource Gap.Top.4}"
               Visibility="{Binding HasNoCandidates, Converter={StaticResource BoolToVisibility}}" />

    <Grid Margin="{StaticResource Gap.Top.4}">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="320" />
        <ColumnDefinition Width="*" />
      </Grid.ColumnDefinitions>

      <!-- 候補キュー -->
      <ListBox Grid.Column="0" ItemsSource="{Binding Candidates}" SelectedItem="{Binding Selected}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <StackPanel Margin="0,4">
              <TextBlock Text="{Binding Source}" Style="{StaticResource Text.Caption}" />
              <TextBlock Text="{Binding Title}" TextWrapping="Wrap" />
            </StackPanel>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>

      <!-- 1 件の詳細と編集 -->
      <ScrollViewer Grid.Column="1" VerticalScrollBarVisibility="Auto" Margin="{StaticResource Gap.Left.2}"
                    DataContext="{Binding}"
                    Visibility="{Binding Selected, Converter={StaticResource NullToVisibility}}">
        <StackPanel>
          <TextBlock Text="{Binding HeadingText}" Style="{StaticResource Text.Label}" />
          <TextBlock Text="{Binding PositionText}" Style="{StaticResource Text.Caption}" />

          <TextBlock Text="{Binding Selected.From}" Style="{StaticResource Text.Caption}"
                     Margin="{StaticResource Gap.Top.4}" />
          <TextBlock Text="{Binding Selected.ReceivedText}" Style="{StaticResource Text.Caption}" />

          <TextBlock Text="{x:Static res:Strings.MorningEvidence}" Style="{StaticResource Text.Label}"
                     Margin="{StaticResource Gap.Top.4}" />
          <TextBlock Text="{Binding Selected.Evidence}" TextWrapping="Wrap" />
          <Button Content="{x:Static res:Strings.MorningOpenSource}" Style="{StaticResource Btn.Ghost}"
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

          <StackPanel Orientation="Horizontal" Margin="{StaticResource Gap.Top.4}">
            <Button Content="{x:Static res:Strings.MorningRegister}" Style="{StaticResource Btn.Primary}"
                    Command="{Binding RegisterCommand}" />
            <Button Content="{x:Static res:Strings.MorningMerge}" Style="{StaticResource Btn.Ghost}"
                    Command="{Binding MergeCommand}" Margin="{StaticResource Gap.Left.2}"
                    Visibility="{Binding Selected.CanMerge, Converter={StaticResource BoolToVisibility}}" />
            <Button Content="{x:Static res:Strings.MorningPostpone}" Style="{StaticResource Btn.Ghost}"
                    Command="{Binding PostponeCommand}" />
            <Button Content="{x:Static res:Strings.MorningReject}" Style="{StaticResource Btn.Ghost}"
                    Command="{Binding RejectCommand}" />
          </StackPanel>
        </StackPanel>
      </ScrollViewer>
    </Grid>
  </DockPanel>
</UserControl>
```

期限にコンバーターは使わない。`TaskDetailPanel.xaml` が `<DatePicker SelectedDate="{Binding DueDate}" />`
と書き、`TaskDetailViewModel` 側が `DateTime?` で持って保存時に `DateOnly.FromDateTime` する流儀なので、
`MorningPlanViewModel` もそれに揃えてある（`EditDueDate` は `DateTime?`）。

使っている `StaticResource` はすべて既存（`Themes/Controls.xaml` の `Text.Label` / `Text.Caption` /
`Btn.Primary` / `Btn.Ghost` / `BoolToVisibility` / `NullToVisibility`、`Themes/Industry.xaml` の
`Pad.Board` / `Pad.BarTight` / `Gap.Top.4` / `Gap.Left.2` / `Brush.AccentSubtle` / `Brush.Danger`）。
**新しいキーは足さない。**

`src/MoTask.App/Views/MorningPlanView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace MoTask.App.Views;

public partial class MorningPlanView : UserControl
{
    public MorningPlanView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: 配線する**

`src/MoTask.App/App.xaml.cs` の `BuildHost` に足す（`IAiJobService` の登録の下）:

```csharp
        builder.Services.AddSingleton<IMorningService, MorningService>();
        builder.Services.AddSingleton<ViewModels.MorningPlanViewModel>();
```

`OnStartup` の `RecoverOnStartupAsync` の隣に足す:

```csharp
            // 前回閉じたあとも朝の実行の端末は走り続けている。events.jsonl に追いつく（仕様 §10）
            await _host.Services.GetRequiredService<IMorningService>().RecoverOnStartupAsync();
```

`src/MoTask.App/Views/MainWindow.xaml`。トップバーのタブを2つにし、本文を切り替える。
既存の `<StackPanel Orientation="Horizontal">` の中のタブ 1 枚を2枚にする:

```xml
        <StackPanel Orientation="Horizontal">
          <Button Style="{StaticResource Btn.Ghost}" Click="OnShowBoardClick" Padding="{StaticResource Pad.Tab}">
            <TextBlock Text="{x:Static res:Strings.ViewBoard}" FontWeight="Medium" />
          </Button>
          <Button Style="{StaticResource Btn.Ghost}" Click="OnShowMorningClick" Padding="{StaticResource Pad.Tab}">
            <TextBlock Text="{x:Static res:Strings.ViewMorningPlan}" FontWeight="Medium" />
          </Button>
        </StackPanel>
```

本文の `<Grid>` を2つ並べ、`x:Name` を付けて排他表示にする:

```xml
    <Grid>
      <Grid x:Name="BoardHost">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*" />
          <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <views:BoardView Grid.Column="0" />
        <views:TaskDetailPanel Grid.Column="1" Width="360" DataContext="{Binding Detail}"
                               Visibility="{Binding DataContext.Detail, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource NullToVisibility}}" />
      </Grid>
      <views:MorningPlanView x:Name="MorningHost" Visibility="Collapsed" />
    </Grid>
```

`FilterBar` はボード専用なので、`BoardHost` の切替と一緒に隠す。

`src/MoTask.App/Views/MainWindow.xaml.cs`:
- コンストラクタで `MorningPlanViewModel` を受け取り、`MorningHost.DataContext` に入れる
- 切替のハンドラを足す:

```csharp
    private void OnShowBoardClick(object sender, RoutedEventArgs e) => ShowBoard(true);

    private async void OnShowMorningClick(object sender, RoutedEventArgs e)
    {
        ShowBoard(false);
        try
        {
            await _morning.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    private void ShowBoard(bool board)
    {
        BoardHost.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        MorningHost.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
    }
```

`OnPreviewKeyDown` の `N` / `Delete` / `Esc` は**ボードを表示しているときだけ**効かせる
（朝の画面で `Delete` がボードのタスクを消さないように）。先頭に足す:

```csharp
        if (MorningHost.Visibility == Visibility.Visible && e.Key is Key.N or Key.Delete) return;
```

- [ ] **Step 8: テストが通ることを確認する**

Run: `dotnet test MoTask.sln`
Expected: PASS

- [ ] **Step 9: 実際に起動して確認する**

Run: `dotnet run --project src/MoTask.App`
- 「朝の実行プラン」タブが開くこと
- 「朝のプランを作る」で端末が開くこと（`claude` が入っている環境で）
- 閉じるときに例外で落ちないこと

XAML の実体は起動して初めて検証されるので、**このステップは飛ばさない**。

- [ ] **Step 10: README に手動確認を足す**

`README.md` の「手動確認チェックリスト」の末尾に節を足す:

```markdown
### 7. 朝の実行プランと候補の仕分け

自動テストでは端末を開いて `claude` を走らせるところに一切届かない。仕様 §14 の項目。

- [ ] 「朝のプランを作る」で端末が開き、Claude がコネクタから候補を集めて
      `result/candidates.jsonl` と `result/plan.json` を書く。
- [ ] `instruction.md` の指示が意図どおり効く（根拠の引用が入る、推奨が4値に収まる）。
- [ ] 認証済みコネクタが1つも無いときに、失敗ではなく「候補はありませんでした」として扱われる。
- [ ] `Stop` が複数回来る実行で、result が揃った時点で1度だけ取り込まれる（候補が二重にならない）。
- [ ] 端末を × で閉じたあと「完了にする」で取り込みが走る。
- [ ] 却下した候補が翌朝の実行で再提出されても候補キューに出てこない。
- [ ] 「あとで」にした候補が翌朝の候補キューに残っている。
- [ ] 統合でタスクの説明末尾に根拠が追記され、履歴に1件残る。
- [ ] **上の1つ目が通ったら、実際の `result/candidates.jsonl` と `result/plan.json` を
      `tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl` と `morning-plan.json` へ
      差し替える。** 現在の fixture は仕様の例文から手で書いたもので、実機のキャプチャではない
      （`MorningResultReaderTests` の目的は「CLI と指示文が形を変えたら赤くする」ことなので、
      実物でないとその役目を果たさない）。
```

- [ ] **Step 11: コミット**

```
git add src/MoTask.App tests/MoTask.App.Tests README.md
git commit -F- <<'MSG'
feat(app): show the morning run and let the user triage candidates

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## 完了の条件

- [ ] `dotnet test MoTask.sln` が全部緑
- [ ] `grep -rn "Task 8 で実装する\|Task 9 で実装する" src/` が空
- [ ] `dotnet run --project src/MoTask.App` で両方の画面が開き、例外なく閉じられる
- [ ] 既存の AI 遂行（`AiJob*`）のテストとコードが1行も壊れていない
- [ ] README に §7 のチェックリストが入っている

## 2本目の計画に残すもの（この計画には入れない）

- `MorningPlanResolver`（`PlanJson` の `taskId` / `externalId` を実タスクと候補に解決する）
- ワイヤー 4a / 4b の左パネルのモード切替（`ContentControl` の `DataTemplate` 切替）
- 右カラムのプラン4区分と「最初にやる1件」
- ボード画面の `候補 N` バッジと「朝のプランへ →」（ワイヤー 4c）
- キーボード `T` / `E` / `X` / `L`（編集欄にフォーカスがある間は無効）
- 「推奨をまとめて適用」（確認ダイアログを1枚挟む）と「すべて後で」
- 統合先を人が選ぶ UI（この計画では推薦がある候補にだけ「統合」を出している）
