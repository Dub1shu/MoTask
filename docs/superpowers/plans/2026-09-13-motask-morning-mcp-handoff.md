# 朝の実行（2/2）MCP への移行 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 朝の実行の成果（盤面の取得・候補・プラン・完了）をファイルから MCP ツール 4 本に移し、`result/candidates.jsonl` / `result/plan.json` / `board.json` のファイル契約を廃止する。候補が 1 件ずつ画面に現れ、不備はその場で Claude に返り、完了は `morning_complete` が宣言して次の `Stop` で端末が閉じる。

**Architecture:** `MorningResultReader` を `CandidateValidator`（1 件分）と `MorningPlanValidator`（プラン 1 本）の純関数 2 つに解体し、`MorningService` に受け口 4 本（`GetContextAsync` / `AddCandidateAsync` / `SubmitPlanAsync` / `CompleteRunAsync`）を足す。App 側に `BoardToolHost` と同じ構えの `MorningToolHost` を新設して `MoTaskMcpServer` に載せ、朝の実行の起動には `--mcp-config` でジョブフォルダの `mcp.json` を渡す。経路は 2 本に分かれたままで、**成果は MCP・営み（フック）はそのまま**である。

**Tech Stack:** .NET 10 / WPF / SQLite / EF Core / CommunityToolkit.Mvvm / xunit + FluentAssertions + NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md`（この計画は §6・§8・§9 と §13「2 本目」を実装する）

**前提:** `docs/superpowers/plans/2026-09-13-motask-morning-terminal-ownership.md`（1 本目）が**マージ済みであること**。この計画は `ISessionLauncher.LaunchOwned` / `CloseOwned` / `TryReattach` / `OwnedSessionExited`、`SessionLaunchRequest.CloseOnExit`、`MorningRunDescriptor.ProcessId`、`MorningService.PendingTerminalExit` に依存する。

**作業場所:** ワークツリー `.claude/worktrees/morning-mcp-handoff`、ブランチ `worktree-morning-mcp-handoff`。すべてのコマンドはこのディレクトリで実行する（`superpowers:using-git-worktrees` を先に使うこと）。

---

## Global Constraints

このプランのすべてのタスクに、暗黙にこの節の要求が含まれる。

- **完了条件: `MorningPlanResolver` とそのテストが 1 行も壊れないこと**（仕様 §13）。`PlanJson` に入る文字列の形は今までと同じである
- **`AiJob*` 一式とそのテストも 1 行も変えない。** AI 遂行の起動は `Launch`（所有しない）・`cmd.exe /k`・`artifacts/`・`TerminalStartPromptFormat` のままで、`--mcp-config` も渡さない
- **`MoTask.Hooks` / `HookEventParser` / `JobEventWatcher` / `events.jsonl` の契約は変えない**（仕様 §2）
- **`BoardToolHost` の 6 本は変えない**（仕様 §2）。`MoTaskMcpServer` は「ツールを 2 つのホストから集める」以外は変えない
- **`TriageCandidate` / `MorningRun` に列を足さない。マイグレーションは作らない**（仕様 §2）。`PlanJson` は今までどおり生 JSON を 1 カラムに持つ
- **朝の画面のレイアウトと `MorningPlanViewModel` は変えない。** 候補が 1 件ずつ現れるのは `RunChanged(candidatesChanged: true)` を受け取る既存の経路でそのまま実現する
- **新規 NuGet パッケージを足さない。P/Invoke も使わない**（仕様 §4）。依存方向（App → Core、Core は UI 非依存・EF 非依存）も変えない
- **`MorningToolHost` は `IMorningService` だけを呼ぶ**（仕様 §9）。検証は Core の純関数が持ち、ホストは「JSON を読んで渡し、結果を JSON にする」だけにする。HTTP も JSON-RPC も知らない
- **ツールの説明文は resx に置かずコードに直書きする**（`2026-09-05-motask-mcp-interface-design.md` §3 の決定）。一方で**利用者と Claude に返す理由（reason）は resx に置く**。Core は `src/MoTask.Core/Resources/Messages.resx`（アクセサ `Messages.cs`）、App は `src/MoTask.App/Resources/Strings.resx`（アクセサ `Strings.cs`）。**resx に値を足したら同じ名前のプロパティを .cs に足す**
- パッケージのバージョンは `Directory.Packages.props` にあるので `PackageReference` に `Version` を書かない
- テストは xunit + FluentAssertions（App 側のモックは NSubstitute）。ビルドは `-p:TreatWarningsAsErrors=true` で警告ゼロ
- **git の扱い:** `git rebase` / `git reset --hard` / 素の `git stash` は使わない。loose object の書き込みが `Permission denied` で落ちたら**同じコマンドをそのまま再実行する**
- 各タスクの最後に 1 コミット。コミットメッセージの末尾に必ず次の行を付ける:

  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

### 仕様からの意図的な補足（4 件）

1. **`runId` は DB の採番なので、指示文は行を保存してから組み立てる。** 仕様 §8 は契約文に `runId` を書くと決めているが、現行の `StartAsync` はフォルダ（と `instruction.md`）を DB 保存より先に作る。Task 6 で「フォルダは先に作る（`instruction.md` は空）→ 行を保存して `Id` を得る → 指示文を組み立てて `instruction.md` を書き直し、`Instruction` 列も埋める」の順に変える。`SaveChangesAsync` が同じゲートの中で 2 回走るが、ゲートの取得は 1 回のままである
2. **「不備」と「宛先違い」を `Result` の成否で分ける。** 仕様 §6 は「不備は通常の結果、`runId` 不一致はツールエラー」と決めている。`MorningService` はこれを `Result.Ok(outcome)`（outcome の中に `accepted:false` と理由）と `Result.Fail(理由)` の違いで表し、`MorningToolHost` はそれを `McpToolResult.Ok` / `McpToolResult.Error` へ機械的に写す
3. **`--mcp-config` のための `MoTask.Mcp.exe` は、フックと同じやり方でアプリ出力へ運ぶ。** 仕様 §5.4 は「絶対パスを書いた `mcp.json`」と言うだけで在り処を決めていない。`MoTask.App.csproj` が `hooks\MoTask.Hooks.exe` を運んでいるのと同じ形で `mcp\MoTask.Mcp.exe` を運び、`JobFolder` が `HooksExecutable` と並べて `McpExecutable` を持つ
4. **`SessionLaunchRequest.OutputDirectoryName` は `string?` にする。** 仕様 §5.4 は「AI 遂行の `artifacts` 固定に戻り、朝の実行から渡さなくなる」と言う。null を「成果物の案内を出さない」の意味にし、朝の実行だけが null を渡す

---

## File Structure

### 新規（Core）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.Core/Morning/CandidateInput.cs` | ツールから渡る候補 1 件の生の値（`suggestedAction` は文字列のまま） |
| `src/MoTask.Core/Morning/CandidateValidator.cs` | `CandidateInput` → `Result<CandidateRecord>`。純関数 |
| `src/MoTask.Core/Morning/MorningPlanValidator.cs` | プラン 1 本の JSON → `Result<string>`（原文そのまま）。純関数。`PlanGroupKeys` もここへ移る |
| `src/MoTask.Core/Morning/MorningOutcomes.cs` | `CandidateOutcome` / `MorningOutcome`（受理したか・理由） |

### 新規（App）

| ファイル | 責務 |
| --- | --- |
| `src/MoTask.App/Ai/MorningTools/MorningToolHost.cs` | ツール 4 本の定義・引数の JSON ↔ ドメイン変換・`IMorningService` 呼び出し・結果の JSON 整形 |
| `src/MoTask.App/Ai/MorningTools/MorningArgs.cs` | 候補 1 件分の引数読み取り（`BoardArgs` を土台に、ISO8601 の `receivedAt` だけ足す） |
| `src/MoTask.App/Ai/McpConfigJson.cs` | `--mcp-config` に渡す `mcp.json` の組み立て（`HooksJson` と同じ構え） |

### 新規（テスト）

| ファイル | 責務 |
| --- | --- |
| `tests/MoTask.Core.Tests/CandidateValidatorTests.cs` | `MorningResultReaderTests` の候補まわりを移植 |
| `tests/MoTask.Core.Tests/MorningPlanValidatorTests.cs` | 同じくプランまわりを移植し、`items[]` の形の検証を足す |
| `tests/MoTask.Core.Tests/MorningServiceMcpTests.cs` | 候補・プラン・完了の受け口と、閉じる予約 |
| `tests/MoTask.App.Tests/MorningToolHostTests.cs` | ツール 4 本を「引数 JSON → `McpToolResult`」で |
| `tests/MoTask.App.Tests/McpConfigJsonTests.cs` | `mcp.json` の形 |

### 変更

| ファイル | 変更の中身 |
| --- | --- |
| `src/MoTask.Core/Services/IMorningService.cs` / `MorningService.cs` | 受け口 4 本、`Stop` での `result/` 読みの撤去、閉じる予約 |
| `src/MoTask.Core/Morning/MorningInstruction.cs` | 引数を（テンプレート・日付・runId）に変え、契約節を差し替え |
| `src/MoTask.Core/Ai/JobFolderPaths.cs` | `McpJsonName` / `McpJson` を足し、`result/` と `board.json` の定数を落とす |
| `src/MoTask.Core/Ai/JobFolderRequest.cs` / `SessionLaunchRequest.cs` | `OutputDirectoryName` を「空 / null で出さない」に |
| `src/MoTask.App/Ai/JobFolder.cs` | `McpExecutable` と `mcp.json` の書き出し、出力フォルダを作らない道 |
| `src/MoTask.App/Ai/TerminalLauncher.cs` | `--mcp-config` の受け渡しと朝用の起動プロンプト |
| `src/MoTask.App/Ai/MoTaskMcpServer.cs` | ツールを 2 つのホストから集める |
| `src/MoTask.App/App.xaml.cs` | `MorningToolHost` の DI 登録 |
| `src/MoTask.App/MoTask.App.csproj` | `mcp\MoTask.Mcp.exe` を出力へ運ぶ |
| `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs` | 契約文の差し替えと、受理しない理由の文言 |

### 削除

`src/MoTask.Core/Morning/MorningResultReader.cs`、`src/MoTask.Core/Morning/MorningResult.cs`、`tests/MoTask.Core.Tests/MorningResultReaderTests.cs`、`tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl`、`morning-candidates-broken.jsonl`、`JobFolderPaths.CandidatesRelativePath` / `PlanRelativePath` / `ResultDirectoryName` / `BoardJsonName` とその派生プロパティ、`Messages.MorningResultUnreadable` / `MorningCandidatesDiscardedFormat`。

### 触らない

`src/MoTask.Core/Morning/MorningPlanResolver.cs`・`ResolvedPlan.cs`・`BoardSnapshot.cs`・`CandidateNote.cs`、`src/MoTask.App/Ai/BoardTools/BoardToolHost.cs`、`src/MoTask.App/ViewModels/` 一式、`src/MoTask.App/Views/` 一式、`src/MoTask.Data/` 一式、`src/MoTask.Hooks/`、`src/MoTask.Mcp/`。

---

## 受け口の契約（全タスク共通の参照表）

以降のタスクはこの名前と型に依存する。Task 2 と Task 3 が実装し、Task 4 が呼ぶ。

```csharp
// src/MoTask.Core/Morning/CandidateInput.cs
/// <summary>
/// morning_add_candidate から渡る 1 件（仕様 §6）。SuggestedAction は 4 値の検証が
/// CandidateValidator の仕事なので、ここでは文字列のまま持つ。
/// </summary>
public sealed record CandidateInput(
    string ExternalId, string Source, string Title, string Evidence,
    string From, string Link, string Reasoning, DateTime? ReceivedAt,
    DateOnly? SuggestedDueDate, string SuggestedProject,
    string SuggestedAction, int? MergeTargetTaskId);

// src/MoTask.Core/Morning/MorningOutcomes.cs
/// <summary>候補 1 件の受け口の返事。Accepted が false でもツールエラーにはしない（仕様 §3）。</summary>
public sealed record CandidateOutcome(bool Accepted, string? Reason, int CandidateId, int Total);

/// <summary>プラン提出・完了宣言の返事。同じく false でもツールエラーにはしない。</summary>
public sealed record MorningOutcome(bool Accepted, string? Reason);

// src/MoTask.Core/Services/IMorningService.cs（追加分）
/// <summary>対象日と盤面（BoardSnapshot.Build の出力そのまま）。Fail は宛先違い＝ツールエラー。</summary>
Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default);

/// <summary>候補を 1 件積む。受理のたびに RunChanged(candidatesChanged: true) が上がる。</summary>
Task<Result<CandidateOutcome>> AddCandidateAsync(int runId, CandidateInput input, CancellationToken ct = default);

/// <summary>プランを出す。何度でも呼べて、最後に受理されたものが残る。</summary>
Task<Result<MorningOutcome>> SubmitPlanAsync(int runId, string planJson, CancellationToken ct = default);

/// <summary>
/// この朝の実行を終える。closeNow が false なら閉じるのを予約し、次の Stop（か 60 秒の保険）で閉じる。
/// 人の「完了にする」は closeNow: true でその場で閉じる（仕様 §7）。
/// </summary>
Task<Result<MorningOutcome>> CompleteRunAsync(int runId, bool closeNow, CancellationToken ct = default);
```

**`Result` の意味づけ（仕様 §3・§6）**

| 返り | 意味 | `MorningToolHost` の写し方 |
| --- | --- | --- |
| `Result.Fail(理由)` | 宛先違い（`runId` が未完了の実行と一致しない）・盤面が無い | `McpToolResult.Error(理由)` |
| `Result.Ok(outcome)` で `Accepted: false` | 受け取ったが積まなかった（不備・重複） | `McpToolResult.Ok({"accepted":false,"reason":…})` |
| `Result.Ok(outcome)` で `Accepted: true` | 受理 | `McpToolResult.Ok({"accepted":true,…})` |

---

## Task 1: `MorningResultReader` を 2 つの純関数に解体する

行単位の JSON Lines 読みが要らなくなるので、残るのは「1 件分の検証」と「プラン 1 本の検証」である。**このタスクでは `MorningResultReader` をまだ消さない**（`MorningService` が使っている）。消すのは Task 3。

**Files:**
- Create: `src/MoTask.Core/Morning/CandidateInput.cs`
- Create: `src/MoTask.Core/Morning/CandidateValidator.cs`
- Create: `src/MoTask.Core/Morning/MorningPlanValidator.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx`, `src/MoTask.Core/Resources/Messages.cs`
- Modify: `tests/MoTask.Core.Tests/MorningInstructionTests.cs:64`
- Test: `tests/MoTask.Core.Tests/CandidateValidatorTests.cs`（新規）
- Test: `tests/MoTask.Core.Tests/MorningPlanValidatorTests.cs`（新規）

**Interfaces:**
- Consumes: 既存の `CandidateRecord`（無変更）、`TriageAction`、`Result<T>`
- Produces: 参照表の `CandidateInput`、`CandidateValidator.Validate` / `.Actions`、`MorningPlanValidator.Validate` / `.PlanGroupKeys`

- [ ] **Step 1: 理由の文言を resx とアクセサに足す**

`src/MoTask.Core/Resources/Messages.resx` の `MorningCandidatesDiscardedFormat` の直後に足す:

```xml
  <data name="CandidateFieldRequiredFormat" xml:space="preserve"><value>{0} が空です。必須の項目です</value></data>
  <data name="CandidateEvidenceRequired" xml:space="preserve"><value>evidence が空です。元の文面から引用してください</value></data>
  <data name="CandidateActionInvalid" xml:space="preserve"><value>suggestedAction は register / merge / later / reject のどれかにしてください</value></data>
  <data name="CandidateMergeTargetMissing" xml:space="preserve"><value>mergeTargetTaskId が盤面にありません</value></data>
  <data name="PlanNotAnObject" xml:space="preserve"><value>plan は JSON オブジェクトで渡してください</value></data>
  <data name="PlanGroupsInvalid" xml:space="preserve"><value>groups が配列ではありません</value></data>
  <data name="PlanGroupKeyInvalidFormat" xml:space="preserve"><value>groups[{0}].key が '{1}' です。today / ifTime / aiReady / waiting のどれかにしてください</value></data>
  <data name="PlanItemsInvalidFormat" xml:space="preserve"><value>groups[{0}].items が配列ではありません</value></data>
  <data name="PlanItemNeedsIdFormat" xml:space="preserve"><value>groups[{0}].items[{1}] は taskId か externalId のどちらかを持ってください</value></data>
  <data name="PlanFirstThingNeedsId" xml:space="preserve"><value>firstThing は taskId か externalId のどちらかを持ってください</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の `MorningCandidatesDiscardedFormat` のプロパティの直後に足す:

```csharp
    public static string CandidateFieldRequiredFormat => Get(nameof(CandidateFieldRequiredFormat));
    public static string CandidateEvidenceRequired => Get(nameof(CandidateEvidenceRequired));
    public static string CandidateActionInvalid => Get(nameof(CandidateActionInvalid));
    public static string CandidateMergeTargetMissing => Get(nameof(CandidateMergeTargetMissing));
    public static string PlanNotAnObject => Get(nameof(PlanNotAnObject));
    public static string PlanGroupsInvalid => Get(nameof(PlanGroupsInvalid));
    public static string PlanGroupKeyInvalidFormat => Get(nameof(PlanGroupKeyInvalidFormat));
    public static string PlanItemsInvalidFormat => Get(nameof(PlanItemsInvalidFormat));
    public static string PlanItemNeedsIdFormat => Get(nameof(PlanItemNeedsIdFormat));
    public static string PlanFirstThingNeedsId => Get(nameof(PlanFirstThingNeedsId));
```

- [ ] **Step 2: 失敗するテストを書く（候補 1 件）**

`tests/MoTask.Core.Tests/CandidateValidatorTests.cs` を新規に作る:

```csharp
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 候補 1 件の検証（仕様 §6）。MoTask と Claude の接点なので、通る形と落ちる理由を固定する。
/// MorningResultReaderTests の候補まわりをここへ移植したもの（JSON Lines 読みの分だけ落ちている）。
/// </summary>
public class CandidateValidatorTests
{
    private static CandidateInput Input(
        string externalId = "outlook:AAMkAD001", string source = "Outlook",
        string title = "請求先情報を更新する", string evidence = "「9月8日までに」",
        string action = "register", int? mergeTarget = null)
        => new(externalId, source, title, evidence,
            From: "顧客A 山本さん", Link: "https://outlook.office.com/mail/id/AAMkAD001",
            Reasoning: "依頼が明確で期限の記述あり",
            ReceivedAt: new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc),
            SuggestedDueDate: new DateOnly(2026, 9, 8), SuggestedProject: "顧客A",
            SuggestedAction: action, MergeTargetTaskId: mergeTarget);

    [Fact]
    public void Validate_MapsEveryField()
    {
        var result = CandidateValidator.Validate(Input());

        result.IsSuccess.Should().BeTrue(result.Error);
        var record = result.Value!;
        record.ExternalId.Should().Be("outlook:AAMkAD001");
        record.Source.Should().Be("Outlook");
        record.Title.Should().Be("請求先情報を更新する");
        record.Evidence.Should().Be("「9月8日までに」");
        record.From.Should().Be("顧客A 山本さん");
        record.Link.Should().Be("https://outlook.office.com/mail/id/AAMkAD001");
        record.Reasoning.Should().Be("依頼が明確で期限の記述あり");
        record.ReceivedAt.Should().Be(new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc));
        record.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        record.SuggestedProject.Should().Be("顧客A");
        record.SuggestedAction.Should().Be(TriageAction.Register);
        record.MergeTargetTaskId.Should().BeNull();
    }

    [Theory]
    [InlineData("register", TriageAction.Register)]
    [InlineData("later", TriageAction.Later)]
    [InlineData("reject", TriageAction.Reject)]
    public void Validate_AcceptsTheActionsThatNeedNoTarget(string action, TriageAction expected)
    {
        CandidateValidator.Validate(Input(action: action)).Value!.SuggestedAction.Should().Be(expected);
    }

    [Fact]
    public void Validate_KeepsTheMergeTarget()
    {
        var record = CandidateValidator.Validate(Input(action: "merge", mergeTarget: 45)).Value!;

        record.SuggestedAction.Should().Be(TriageAction.Merge);
        record.MergeTargetTaskId.Should().Be(45);
    }

    [Fact]
    public void Validate_AcceptsAnySourceString()
    {
        CandidateValidator.Validate(Input(source: "社内ポータル")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_TrimsTheTextFields()
    {
        CandidateValidator.Validate(Input(title: "  余白つき  ")).Value!.Title.Should().Be("余白つき");
    }

    [Theory]
    [InlineData("externalId")]
    [InlineData("source")]
    [InlineData("title")]
    public void Validate_NamesTheMissingRequiredField(string field)
    {
        var input = field switch
        {
            "externalId" => Input(externalId: "  "),
            "source" => Input(source: ""),
            _ => Input(title: ""),
        };

        var result = CandidateValidator.Validate(input);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.CandidateFieldRequiredFormat, field));
    }

    /// <summary>根拠の無い候補は人が判断できない。理由は「引用してください」まで言う（仕様 §6）。</summary>
    [Fact]
    public void Validate_RefusesACandidateWithoutEvidence()
    {
        var result = CandidateValidator.Validate(Input(evidence: ""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateEvidenceRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REGISTER")]
    [InlineData("archive")]
    public void Validate_RefusesAnActionOutsideTheFour(string action)
    {
        var result = CandidateValidator.Validate(Input(action: action));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateActionInvalid);
    }

    [Fact]
    public void Validate_RefusesMergeWithoutATarget()
    {
        var result = CandidateValidator.Validate(Input(action: "merge"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateMergeTargetMissing);
    }

    [Fact]
    public void Actions_AreTheFourFixedOnes()
        => CandidateValidator.Actions.Should().Equal("register", "merge", "later", "reject");
}
```

- [ ] **Step 3: 失敗するテストを書く（プラン 1 本）**

`tests/MoTask.Core.Tests/MorningPlanValidatorTests.cs` を新規に作る:

```csharp
using System.IO;
using FluentAssertions;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// プラン 1 本の検証（仕様 §6）。通れば原文をそのまま残すので、MorningPlanResolver は
/// 今までと同じ文字列を読む。
/// </summary>
public class MorningPlanValidatorTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Validate_KeepsThePlanVerbatimWhenItIsValid()
    {
        var raw = Fixture("morning-plan.json");

        var result = MorningPlanValidator.Validate(raw);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Be(raw.Trim(), "生 JSON を 1 カラムにそのまま持つ（親仕様 §9）");
    }

    [Fact]
    public void Validate_NamesTheGroupWithABadKey()
    {
        var plan = """{"groups":[{"key":"today","items":[]},{"key":"later","items":[]}]}""";

        var result = MorningPlanValidator.Validate(plan);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.PlanGroupKeyInvalidFormat, 1, "later"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    [InlineData("\"ただの文字列\"")]
    public void Validate_RefusesAnythingThatIsNotAnObject(string? plan)
    {
        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanNotAnObject);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"groups":{}}""")]
    public void Validate_RefusesAPlanWithoutAGroupsArray(string plan)
    {
        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanGroupsInvalid);
    }

    [Fact]
    public void Validate_RefusesAGroupWithoutAnItemsArray()
    {
        var result = MorningPlanValidator.Validate("""{"groups":[{"key":"today"}]}""");

        result.Error.Should().Be(string.Format(Messages.PlanItemsInvalidFormat, 0));
    }

    /// <summary>items[] の各要素は taskId か externalId のどちらかを持つ（仕様 §6）。</summary>
    [Fact]
    public void Validate_RefusesAnItemWithNeitherTaskIdNorExternalId()
    {
        var plan = """{"groups":[{"key":"today","items":[{"taskId":45},{"note":"あとで"}]}]}""";

        var result = MorningPlanValidator.Validate(plan);

        result.Error.Should().Be(string.Format(Messages.PlanItemNeedsIdFormat, 0, 1));
    }

    [Fact]
    public void Validate_AcceptsAnItemIdentifiedByExternalId()
    {
        var plan = """{"groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

        MorningPlanValidator.Validate(plan).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_RefusesAFirstThingWithNoIdentity()
    {
        var plan = """{"firstThing":{"reason":"なんとなく"},"groups":[{"key":"today","items":[]}]}""";

        MorningPlanValidator.Validate(plan).Error.Should().Be(Messages.PlanFirstThingNeedsId);
    }

    [Fact]
    public void Validate_AcceptsAPlanWithNoFirstThing()
    {
        MorningPlanValidator.Validate("""{"groups":[{"key":"today","items":[]}]}""")
            .IsSuccess.Should().BeTrue("最初の 1 件を決められない朝もある");
    }

    [Fact]
    public void PlanGroupKeys_AreTheFourFixedOnes()
        => MorningPlanValidator.PlanGroupKeys.Should().Equal("today", "ifTime", "aiReady", "waiting");
}
```

- [ ] **Step 4: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~CandidateValidatorTests|FullyQualifiedName~MorningPlanValidatorTests" -nologo -v q`
Expected: コンパイルエラー（`CandidateInput` / `CandidateValidator` / `MorningPlanValidator` が無い）で FAIL

- [ ] **Step 5: `CandidateInput` を作る**

`src/MoTask.Core/Morning/CandidateInput.cs`:

```csharp
namespace MoTask.Core.Morning;

/// <summary>
/// morning_add_candidate から渡る 1 件（仕様 §6）。SuggestedAction は 4 値の検証が
/// CandidateValidator の仕事なので、ここでは文字列のまま持つ。
/// 省略された文字列項目は空文字で渡ってくる（ツール側が JSON の欠けを空文字に寄せる）。
/// </summary>
public sealed record CandidateInput(
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
    string SuggestedAction,
    int? MergeTargetTaskId);
```

- [ ] **Step 6: `CandidateValidator` を作る**

`src/MoTask.Core/Morning/CandidateValidator.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Morning;

/// <summary>
/// 候補 1 件の検証（仕様 §6）。MoTask と Claude の接点なので純関数にして固定する。
/// 落ちたときの理由は Claude がそのまま読んで直せる文にする。
/// 「mergeTargetTaskId が盤面に在るか」は盤面が要るので MorningService が見る（理由は同じ文言）。
/// </summary>
public static class CandidateValidator
{
    /// <summary>suggestedAction に許す 4 値（仕様 §6）。</summary>
    public static readonly IReadOnlyList<string> Actions = new[] { "register", "merge", "later", "reject" };

    public static Result<CandidateRecord> Validate(CandidateInput input)
    {
        var externalId = input.ExternalId.Trim();
        if (externalId.Length == 0) return Missing("externalId");

        var source = input.Source.Trim();
        if (source.Length == 0) return Missing("source");

        var title = input.Title.Trim();
        if (title.Length == 0) return Missing("title");

        // 根拠の無い候補は人が判断できないので受け取らない（仕様 §6）
        var evidence = input.Evidence.Trim();
        if (evidence.Length == 0) return Result.Fail<CandidateRecord>(Messages.CandidateEvidenceRequired);

        if (!TryAction(input.SuggestedAction.Trim(), out var action))
        {
            return Result.Fail<CandidateRecord>(Messages.CandidateActionInvalid);
        }
        if (action == TriageAction.Merge && input.MergeTargetTaskId is null)
        {
            return Result.Fail<CandidateRecord>(Messages.CandidateMergeTargetMissing);
        }

        return Result.Ok(new CandidateRecord(
            externalId, source, title, evidence,
            input.From.Trim(), input.Link.Trim(), input.Reasoning.Trim(),
            input.ReceivedAt, input.SuggestedDueDate, input.SuggestedProject.Trim(),
            action, input.MergeTargetTaskId));
    }

    private static Result<CandidateRecord> Missing(string field)
        => Result.Fail<CandidateRecord>(string.Format(Messages.CandidateFieldRequiredFormat, field));

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
}
```

- [ ] **Step 7: `MorningPlanValidator` を作る**

`src/MoTask.Core/Morning/MorningPlanValidator.cs`:

```csharp
using System.Text.Json;

namespace MoTask.Core.Morning;

/// <summary>
/// プラン 1 本の検証（仕様 §6）。妥当なら原文をそのまま返す（生 JSON を 1 カラムに持つ・親仕様 §9）ので、
/// MorningPlanResolver が読む文字列の形は今までと変わらない。
/// </summary>
public static class MorningPlanValidator
{
    /// <summary>groups[].key に許す 4 値（親仕様 §8）。</summary>
    public static readonly IReadOnlyList<string> PlanGroupKeys =
        new[] { "today", "ifTime", "aiReady", "waiting" };

    public static Result<string> Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Result.Fail<string>(Messages.PlanNotAnObject);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result.Fail<string>(Messages.PlanNotAnObject);

            if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return Result.Fail<string>(Messages.PlanGroupsInvalid);
            }

            var groupIndex = 0;
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    return Result.Fail<string>(string.Format(Messages.PlanItemsInvalidFormat, groupIndex));
                }
                var key = Text(group, "key");
                if (!PlanGroupKeys.Contains(key))
                {
                    return Result.Fail<string>(string.Format(Messages.PlanGroupKeyInvalidFormat, groupIndex, key));
                }
                if (!group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return Result.Fail<string>(string.Format(Messages.PlanItemsInvalidFormat, groupIndex));
                }

                var itemIndex = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (!HasIdentity(item))
                    {
                        return Result.Fail<string>(
                            string.Format(Messages.PlanItemNeedsIdFormat, groupIndex, itemIndex));
                    }
                    itemIndex++;
                }
                groupIndex++;
            }

            // firstThing は無くてもよい（最初の 1 件を決められない朝もある）。あるなら items と同じ形。
            if (root.TryGetProperty("firstThing", out var first)
                && first.ValueKind != JsonValueKind.Null
                && !HasIdentity(first))
            {
                return Result.Fail<string>(Messages.PlanFirstThingNeedsId);
            }

            return Result.Ok(json.Trim());
        }
        catch (JsonException)
        {
            return Result.Fail<string>(Messages.PlanNotAnObject);
        }
    }

    private static bool HasIdentity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty("taskId", out var taskId)
            && taskId.ValueKind == JsonValueKind.Number
            && taskId.TryGetInt32(out _))
        {
            return true;
        }
        return element.TryGetProperty("externalId", out var externalId)
               && externalId.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(externalId.GetString());
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
```

- [ ] **Step 8: `MorningInstructionTests` の参照先を移す**

`tests/MoTask.Core.Tests/MorningInstructionTests.cs:64` を置き換える:

```csharp
        foreach (var key in MorningPlanValidator.PlanGroupKeys) text.Should().Contain(key);
```

- [ ] **Step 9: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~CandidateValidatorTests|FullyQualifiedName~MorningPlanValidatorTests|FullyQualifiedName~MorningInstructionTests" -nologo -v q`
Expected: PASS

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS（`MorningResultReader` はまだ生きているので既存の挙動は変わらない）

- [ ] **Step 10: コミット**

```bash
git add src/MoTask.Core/Morning/CandidateInput.cs src/MoTask.Core/Morning/CandidateValidator.cs src/MoTask.Core/Morning/MorningPlanValidator.cs src/MoTask.Core/Resources/Messages.resx src/MoTask.Core/Resources/Messages.cs tests/MoTask.Core.Tests/CandidateValidatorTests.cs tests/MoTask.Core.Tests/MorningPlanValidatorTests.cs tests/MoTask.Core.Tests/MorningInstructionTests.cs
git commit -m "feat(core): 朝の成果の検証を候補 1 件分とプラン 1 本の純関数に分ける

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `MorningService` が盤面・候補・プランを受ける

**Files:**
- Create: `src/MoTask.Core/Morning/MorningOutcomes.cs`
- Modify: `src/MoTask.Core/Services/IMorningService.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx`, `src/MoTask.Core/Resources/Messages.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceMcpTests.cs`（新規）

**Interfaces:**
- Consumes: Task 1 の `CandidateValidator` / `MorningPlanValidator` / `CandidateInput`
- Produces: 参照表の `CandidateOutcome` / `MorningOutcome` / `GetContextAsync` / `AddCandidateAsync` / `SubmitPlanAsync`、`Messages.MorningRunNotRunningFormat` / `CandidateAlreadyDecidedElsewhere` / `CandidateAlreadyInThisRun`

- [ ] **Step 1: 理由の文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の Task 1 で足した塊の直後に足す:

```xml
  <data name="MorningRunNotRunningFormat" xml:space="preserve"><value>runId {0} の朝の実行は動いていません</value></data>
  <data name="CandidateAlreadyDecidedElsewhere" xml:space="preserve"><value>この externalId は過去に処理済みです</value></data>
  <data name="CandidateAlreadyInThisRun" xml:space="preserve"><value>今朝すでに積んだ externalId です</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` に足す:

```csharp
    public static string MorningRunNotRunningFormat => Get(nameof(MorningRunNotRunningFormat));
    public static string CandidateAlreadyDecidedElsewhere => Get(nameof(CandidateAlreadyDecidedElsewhere));
    public static string CandidateAlreadyInThisRun => Get(nameof(CandidateAlreadyInThisRun));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceMcpTests.cs` を新規に作る:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// MCP 経由の受け口（仕様 §6）。「不備」は通常の結果で理由を返し、「宛先違い」だけが Fail になる。
/// </summary>
public class MorningServiceMcpTests
{
    private const string Plan =
        """{"date":"2026-09-07","groups":[{"key":"today","items":[{"externalId":"outlook:001"}]}]}""";

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly List<MorningRunChangedEventArgs> _changes = new();
    private readonly TaskItem _existing;

    public MorningServiceMcpTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _existing = _store.SeedTask(active, "Q4企画書の内容を確定する");
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

    private static CandidateInput Candidate(
        string externalId = "outlook:001", string evidence = "「9月8日までに」",
        string action = "register", int? mergeTarget = null)
        => new(externalId, "Outlook", "請求先情報を更新する", evidence,
            From: "山本さん", Link: "", Reasoning: "依頼が明確", ReceivedAt: null,
            SuggestedDueDate: null, SuggestedProject: "", SuggestedAction: action,
            MergeTargetTaskId: mergeTarget);

    // ---- morning_get_context ----

    [Fact]
    public async Task GetContext_ReturnsTheDateAndTheUnfinishedTasks()
    {
        var run = await StartAsync();

        var context = await _service.GetContextAsync(run.Id);

        context.IsSuccess.Should().BeTrue(context.Error);
        var root = JsonDocument.Parse(context.Value!).RootElement;
        root.GetProperty("date").GetString().Should().Be("2026-09-07");
        root.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString()).Should().Equal("Q4企画書の内容を確定する");
    }

    /// <summary>
    /// 利用者が普段使っている Claude Code も同じ MCP サーバに繋がる。宛先違いはツールエラーにして、
    /// そちらが誤って朝の実行を動かす事故を防ぐ（仕様 §6）。
    /// </summary>
    [Fact]
    public async Task GetContext_FailsForARunIdThatIsNotRunning()
    {
        await StartAsync();

        var context = await _service.GetContextAsync(9999);

        context.IsSuccess.Should().BeFalse();
        context.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, 9999));
    }

    // ---- morning_add_candidate ----

    [Fact]
    public async Task AddCandidate_AcceptsOneAndTellsHowManyAreStacked()
    {
        var run = await StartAsync();

        var first = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        var second = await _service.AddCandidateAsync(run.Id, Candidate("teams:002"));

        first.Value!.Accepted.Should().BeTrue();
        first.Value.Total.Should().Be(1);
        second.Value!.Total.Should().Be(2);
        _store.Candidates.Select(c => c.ExternalId).Should().Equal("outlook:001", "teams:002");
        _store.Candidates[0].Id.Should().Be(first.Value.CandidateId);
        _store.Candidates[0].Status.Should().Be(TriageStatus.Pending);
    }

    /// <summary>受理のたびに知らせる。候補が画面に 1 件ずつ現れる（仕様 §6）。</summary>
    [Fact]
    public async Task AddCandidate_RaisesRunChangedForEachAcceptedCandidate()
    {
        var run = await StartAsync();
        _changes.Clear();

        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        await _service.AddCandidateAsync(run.Id, Candidate("teams:002"));

        _changes.Where(c => c.CandidatesChanged).Should().HaveCount(2);
    }

    [Fact]
    public async Task AddCandidate_CopiesEveryFieldOntoTheCandidate()
    {
        var run = await StartAsync();

        await _service.AddCandidateAsync(run.Id, Candidate(action: "merge", mergeTarget: _existing.Id));

        var candidate = _store.Candidates.Should().ContainSingle().Subject;
        candidate.MorningRunId.Should().Be(run.Id);
        candidate.Source.Should().Be("Outlook");
        candidate.Evidence.Should().Be("「9月8日までに」");
        candidate.SuggestedAction.Should().Be(TriageAction.Merge);
        candidate.SuggestedMergeTaskId.Should().Be(_existing.Id);
        candidate.DecidedAt.Should().BeNull();
    }

    /// <summary>不備はツールエラーにしない。Claude は 1 件諦めて次へ進めばよい（仕様 §3）。</summary>
    [Fact]
    public async Task AddCandidate_RefusesWithoutEvidence_AsAnOrdinaryResult()
    {
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate(evidence: ""));

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateEvidenceRequired);
        _store.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AddCandidate_RefusesAMergeTargetThatIsNotOnTheBoard()
    {
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate(action: "merge", mergeTarget: 9999));

        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateMergeTargetMissing);
    }

    /// <summary>現行は取り込み時に黙って捨てていた。ここでは理由を返す（仕様 §6）。</summary>
    [Fact]
    public async Task AddCandidate_RefusesAnExternalIdDecidedInAnEarlierRun()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:001", TriageStatus.Rejected);
        var run = await StartAsync();

        var result = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.CandidateAlreadyDecidedElsewhere);
    }

    [Fact]
    public async Task AddCandidate_RefusesTheSameExternalIdTwiceInOneRun()
    {
        var run = await StartAsync();
        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        var again = await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));

        again.Value!.Accepted.Should().BeFalse();
        again.Value.Reason.Should().Be(Messages.CandidateAlreadyInThisRun);
        _store.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task AddCandidate_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.AddCandidateAsync(9999, Candidate());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, 9999));
    }

    // ---- morning_submit_plan ----

    [Fact]
    public async Task SubmitPlan_SavesThePlanVerbatim()
    {
        var run = await StartAsync();

        var result = await _service.SubmitPlanAsync(run.Id, Plan);

        result.Value!.Accepted.Should().BeTrue();
        run.PlanJson.Should().Be(Plan);
        run.Status.Should().Be(MorningRunStatus.Running, "提出は実行の終わりではない");
    }

    [Fact]
    public async Task SubmitPlan_KeepsTheLastAcceptedOne()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        var better = """{"groups":[{"key":"ifTime","items":[{"taskId":45}]}]}""";

        await _service.SubmitPlanAsync(run.Id, better);

        run.PlanJson.Should().Be(better, "何度でも呼べて、最後に受理されたものが残る（仕様 §6）");
    }

    [Fact]
    public async Task SubmitPlan_RefusesABadGroupKeyWithoutLosingTheOldPlan()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var result = await _service.SubmitPlanAsync(
            run.Id, """{"groups":[{"key":"someday","items":[]}]}""");

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(string.Format(Messages.PlanGroupKeyInvalidFormat, 0, "someday"));
        run.PlanJson.Should().Be(Plan, "受理しなかったプランで上書きしない");
    }

    [Fact]
    public async Task SubmitPlan_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.SubmitPlanAsync(9999, Plan);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, 9999));
    }
}
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceMcpTests" -nologo -v q`
Expected: コンパイルエラー（`GetContextAsync` などが無い）で FAIL

- [ ] **Step 4: `MorningOutcomes` を作る**

`src/MoTask.Core/Morning/MorningOutcomes.cs`:

```csharp
namespace MoTask.Core.Morning;

/// <summary>
/// 候補 1 件の受け口の返事（仕様 §6）。Accepted が false でもツールエラーにはしない。
/// Claude は「1 件弾かれた」だけを受け取って次へ進めばよい（仕様 §3）。
/// Total は受理後にこの実行へ積まれている候補の件数。
/// </summary>
public sealed record CandidateOutcome(bool Accepted, string? Reason, int CandidateId, int Total);

/// <summary>プラン提出・完了宣言の返事。同じく Accepted が false でもツールエラーにはしない。</summary>
public sealed record MorningOutcome(bool Accepted, string? Reason);
```

- [ ] **Step 5: `IMorningService` に 3 本を足す**

`src/MoTask.Core/Services/IMorningService.cs` の `using` に `using MoTask.Core.Morning;` を足し、`// 実行` の節（`RecoverOnStartupAsync` の直後）に足す:

```csharp
    // MCP 経由の受け口（仕様 §6）。Fail は宛先違い＝ツールエラー、Ok(outcome) は通常の結果。

    /// <summary>対象日と盤面（BoardSnapshot.Build の出力そのまま）を返す。</summary>
    Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default);

    /// <summary>候補を 1 件積む。受理のたびに RunChanged(candidatesChanged: true) が上がる。</summary>
    Task<Result<CandidateOutcome>> AddCandidateAsync(
        int runId, CandidateInput input, CancellationToken ct = default);

    /// <summary>プランを出す。何度でも呼べて、最後に受理されたものが残る。</summary>
    Task<Result<MorningOutcome>> SubmitPlanAsync(int runId, string planJson, CancellationToken ct = default);
```

- [ ] **Step 6: `MorningService` に盤面づくりの共通部を切り出す**

`src/MoTask.Core/Services/MorningService.cs` の `StartAsync` の中、`var snapshot = BoardSnapshot.Build(...)` の 4 行を次の 1 行で置き換える:

```csharp
                var snapshot = await BuildSnapshotAsync(date, ct).ConfigureAwait(false);
```

`StartAsync` の直前（`private sealed record Prepared` の直後）に足す:

```csharp
    /// <summary>
    /// 盤面のスナップショット。ゲートの中から呼ぶこと（リポジトリを 3 つ引く）。
    /// 開始時の board.json と morning_get_context の両方がこれを使う（形は 1 つ）。
    /// 盤面が無ければ null。
    /// </summary>
    private async Task<string?> BuildSnapshotAsync(DateOnly date, CancellationToken ct)
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return null;

        var projects = await _boards.GetProjectsAsync(ct).ConfigureAwait(false);
        var busy = await _jobs.GetByStatusAsync(
            new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct)
            .ConfigureAwait(false);

        return BoardSnapshot.Build(
            board, date,
            projects.ToDictionary(p => p.Id, p => p.Name),
            busy.Select(j => j.TaskId).ToHashSet());
    }
```

`StartAsync` の中の `var board = await _boards.GetBoardAsync(ct)...; if (board is null) return Result.Fail<Prepared>(Messages.BoardNotFound);` と、それに続く `projects` / `busy` の取得を削り、盤面の有無は `snapshot` で見るようにする:

```csharp
                var snapshot = await BuildSnapshotAsync(date, ct).ConfigureAwait(false);
                if (snapshot is null) return Result.Fail<Prepared>(Messages.BoardNotFound);
```

- [ ] **Step 7: `MorningService` に受け口 3 本を実装する**

`src/MoTask.Core/Services/MorningService.cs` の `// ---------- 人の操作 ----------` の直前に足す:

```csharp
    // ---------- MCP 経由の受け口（仕様 §6） ----------

    public Task<Result<string>> GetContextAsync(int runId, CancellationToken ct = default)
        => _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<string>(runId);

            var snapshot = await BuildSnapshotAsync(run.Date, ct).ConfigureAwait(false);
            return snapshot is null ? Result.Fail<string>(Messages.BoardNotFound) : Result.Ok(snapshot);
        }, ct);

    public async Task<Result<CandidateOutcome>> AddCandidateAsync(
        int runId, CandidateInput input, CancellationToken ct = default)
    {
        // 形の検証はゲートの外（純関数なので DB を待たせない）
        var validated = CandidateValidator.Validate(input);

        MorningRun? accepted = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<CandidateOutcome>(runId);

            var mine = await _runs.GetCandidatesOfRunAsync(runId, ct).ConfigureAwait(false);
            var total = mine.Count;
            if (!validated.IsSuccess) return Refused(validated.Error!, total);

            var record = validated.Value!;
            if (record.SuggestedAction == TriageAction.Merge)
            {
                var target = await _boards.GetTaskAsync(record.MergeTargetTaskId!.Value, ct).ConfigureAwait(false);
                if (target is null || target.IsDeleted) return Refused(Messages.CandidateMergeTargetMissing, total);
            }

            // 却下・登録済みの ExternalId は翌朝また出てきても積まない（親仕様 §9）。
            // 現行は黙って捨てていたが、ここでは理由を返す（仕様 §6）。
            var known = await _runs.GetKnownExternalIdsAsync(new[] { record.ExternalId }, ct).ConfigureAwait(false);
            if (known.Count > 0)
            {
                var inThisRun = mine.Any(c => string.Equals(c.ExternalId, record.ExternalId, StringComparison.Ordinal));
                return Refused(
                    inThisRun ? Messages.CandidateAlreadyInThisRun : Messages.CandidateAlreadyDecidedElsewhere, total);
            }

            var candidate = new TriageCandidate
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
            };
            _runs.AddCandidate(candidate);
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            accepted = run;
            return Result.Ok(new CandidateOutcome(true, null, candidate.Id, total + 1));
        }, ct).ConfigureAwait(false);

        // 受理したときだけ知らせる。候補キューが 1 件ずつ増える（仕様 §6）。
        if (accepted is not null) Raise(accepted, warning, candidatesChanged: true);
        return result;
    }

    public async Task<Result<MorningOutcome>> SubmitPlanAsync(
        int runId, string planJson, CancellationToken ct = default)
    {
        var validated = MorningPlanValidator.Validate(planJson);

        MorningRun? saved = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<MorningOutcome>(runId);
            // 受理しなかったプランで、前に受理したものを上書きしない
            if (!validated.IsSuccess) return Result.Ok(new MorningOutcome(false, validated.Error!));

            run.PlanJson = validated.Value!;
            run.Status = MorningRunStatus.Running;
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            saved = run;
            return Result.Ok(new MorningOutcome(true, null));
        }, ct).ConfigureAwait(false);

        if (saved is not null) Raise(saved, warning, candidatesChanged: false);
        return result;
    }

    /// <summary>宛先違い。利用者の普段使いの Claude Code が誤って動かす事故を防ぐ（仕様 §6）。</summary>
    private static Result<T> NotRunning<T>(int runId)
        => Result.Fail<T>(string.Format(Messages.MorningRunNotRunningFormat, runId));

    /// <summary>受け取ったが積まなかった。ツールエラーではない（仕様 §3）。</summary>
    private static Result<CandidateOutcome> Refused(string reason, int total)
        => Result.Ok(new CandidateOutcome(false, reason, 0, total));
```

- [ ] **Step 8: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceMcpTests" -nologo -v q`
Expected: PASS

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS（`result/` 経路はまだ生きているので既存のテストも通る）

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.Core/Morning/MorningOutcomes.cs src/MoTask.Core/Services/IMorningService.cs src/MoTask.Core/Services/MorningService.cs src/MoTask.Core/Resources/Messages.resx src/MoTask.Core/Resources/Messages.cs tests/MoTask.Core.Tests/MorningServiceMcpTests.cs
git commit -m "feat(core): 朝の実行が盤面・候補・プランを MCP から受け取れるようにする

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: 完了を受け、閉じるのを予約し、`result/` 読みを撤去する

閉じる契機が「`Ingested` になったら」から「`morning_complete` の予約 ＋ 次の `Stop`」に移る。これで `MorningResultReader` の出番が無くなるので、ここで消す。

**Files:**
- Modify: `src/MoTask.Core/Services/IMorningService.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`
- Modify: `src/MoTask.Core/Model/MorningRunStatus.cs`（`Ingested` のコメントだけ。値は変えない）
- Modify: `src/MoTask.Core/Resources/Messages.resx`, `src/MoTask.Core/Resources/Messages.cs`
- Delete: `src/MoTask.Core/Morning/MorningResultReader.cs`, `src/MoTask.Core/Morning/MorningResult.cs`
- Delete: `tests/MoTask.Core.Tests/MorningResultReaderTests.cs`, `tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl`, `tests/MoTask.Core.Tests/Fixtures/morning-candidates-broken.jsonl`, `tests/MoTask.Core.Tests/Fixtures/morning-plan-broken.json`
- Modify: `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceMcpTests.cs`

**Interfaces:**
- Consumes: Task 2 の `SubmitPlanAsync` / `NotRunning` / `MorningOutcome`、1 本目の `ISessionLauncher.CloseOwned`
- Produces:
  - `Task<Result<MorningOutcome>> IMorningService.CompleteRunAsync(int runId, bool closeNow, CancellationToken ct = default)`
  - `MorningService.CloseGrace`（`TimeSpan`。既定 60 秒。テストだけが短くする）
  - `MorningService.PendingClose`（`Task`。保険の待ち合わせをテストが待つ口）
  - `Messages.MorningPlanNotSubmitted` / `Messages.MorningCompleteMissing`

### この計画で変わる人向けの振る舞い（1 件）

**「完了にする」はプラン未提出の実行を `Failed` にしない。** 現行は `result/` が読めなければその場で `Failed` にしていた。これからはプランが無ければ受理せず理由を返すだけで、実行は動いたまま残る。詰まった実行を片づける道は「追跡をやめる」（`Cancelled`）である。仕様 §6 が `morning_complete` について定めた振る舞いを、同じ状態遷移を共有する人の操作にもそのまま当てる。

- [ ] **Step 1: 文言を入れ替える**

`src/MoTask.Core/Resources/Messages.resx` から次の 2 行を**削除する**:

```xml
  <data name="MorningResultUnreadable" xml:space="preserve">…</data>
  <data name="MorningCandidatesDiscardedFormat" xml:space="preserve">…</data>
```

同じ場所に足す:

```xml
  <data name="MorningPlanNotSubmitted" xml:space="preserve"><value>先に morning_submit_plan を呼んでください</value></data>
  <data name="MorningCompleteMissing" xml:space="preserve"><value>Claude が完了を通知しないままセッションを終えました</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` から `MorningResultUnreadable` と `MorningCandidatesDiscardedFormat` のプロパティを削除し、次を足す:

```csharp
    public static string MorningPlanNotSubmitted => Get(nameof(MorningPlanNotSubmitted));
    public static string MorningCompleteMissing => Get(nameof(MorningCompleteMissing));
```

- [ ] **Step 2: 失敗するテストを書く（完了と閉じる予約）**

`tests/MoTask.Core.Tests/MorningServiceMcpTests.cs` の末尾に足す:

```csharp
    // ---- morning_complete ----

    [Fact]
    public async Task Complete_RefusesUntilAPlanHasBeenSubmitted()
    {
        var run = await StartAsync();

        var result = await _service.CompleteRunAsync(run.Id, closeNow: false);

        result.IsSuccess.Should().BeTrue("ツールエラーではない");
        result.Value!.Accepted.Should().BeFalse();
        result.Value.Reason.Should().Be(Messages.MorningPlanNotSubmitted);
        run.Status.Should().Be(MorningRunStatus.Pending, "受理しなかっただけ。実行は動いたまま");
        _launcher.Closed.Should().BeEmpty();
    }

    /// <summary>候補 0 件の朝は失敗ではない（親仕様 §8 の約束を引き継ぐ）。</summary>
    [Fact]
    public async Task Complete_AcceptsARunWithNoCandidates()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var result = await _service.CompleteRunAsync(run.Id, closeNow: false);

        result.Value!.Accepted.Should().BeTrue();
        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty();
    }

    /// <summary>
    /// ツール結果を返した直後に殺すと Claude の最後の一言が切れる。Stop はそのターンが
    /// 終わった合図なので、言い終えてから消す（仕様 §7）。
    /// </summary>
    [Fact]
    public async Task Complete_DoesNotCloseUntilTheNextStop()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10); // 保険は今回は効かせない
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteRunAsync(run.Id, closeNow: false);
        _launcher.Closed.Should().BeEmpty("まだ喋っている最中");
        _events.IsFollowing(run.Id).Should().BeTrue("Stop を受け取る必要があるので追従は続ける");

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().Equal(run.Id);
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_ClosesOnlyOnce_EvenIfMoreStopsArrive()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>Stop が来ないまま座り込んだときの保険（仕様 §7）。</summary>
    [Fact]
    public async Task Complete_ClosesAfterTheGraceEvenWithoutAStop()
    {
        _service.CloseGrace = TimeSpan.FromMilliseconds(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteRunAsync(run.Id, closeNow: false);
        await _service.PendingClose;

        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>人の「完了にする」は待つべき Stop が来る保証が無いのでその場で閉じる（仕様 §7）。</summary>
    [Fact]
    public async Task CompleteByHand_ClosesRightAway()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        var done = await _service.CompleteAsync(run.Id);

        done.IsSuccess.Should().BeTrue(done.Error);
        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteByHand_ReportsWhyItRefusedWithoutAPlan()
    {
        var run = await StartAsync();

        var done = await _service.CompleteAsync(run.Id);

        done.IsSuccess.Should().BeFalse();
        done.Error.Should().Be(Messages.MorningPlanNotSubmitted);
        run.Status.Should().Be(MorningRunStatus.Pending);
    }

    /// <summary>候補もプランも MCP から来たものがそのまま画面へ回る。</summary>
    [Fact]
    public async Task AFullMorning_EndsWithThePlanAndTheCandidatesInPlace()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.AddCandidateAsync(run.Id, Candidate("outlook:001"));
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.PlanJson.Should().Be(Plan);
        (await _service.GetQueueAsync(run.Id)).Select(c => c.ExternalId).Should().Equal("outlook:001");
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>complete が来ないままセッションが終わった（仕様 §7）。</summary>
    [Fact]
    public async Task SessionEnd_WithoutComplete_FailsTheRunAndClosesTheTerminal()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningCompleteMissing);
        _events.IsFollowing(run.Id).Should().BeFalse();
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>complete の後に来た SessionEnd は、予約が残っていればそれを果たすだけ。</summary>
    [Fact]
    public async Task SessionEnd_AfterComplete_ClosesWithoutFailingTheRun()
    {
        _service.CloseGrace = TimeSpan.FromMinutes(10);
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteRunAsync(run.Id, closeNow: false);

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Ingested, "終わった実行を蘇らせない");
        _launcher.Closed.Should().Equal(run.Id);
    }

    [Fact]
    public async Task Complete_FailsForARunIdThatIsNotRunning()
    {
        var result = await _service.CompleteRunAsync(9999, closeNow: false);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, 9999));
    }
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceMcpTests" -nologo -v q`
Expected: コンパイルエラー（`CompleteRunAsync` / `CloseGrace` / `PendingClose` が無い）で FAIL

- [ ] **Step 4: `IMorningService` を更新する**

`src/MoTask.Core/Services/IMorningService.cs` の `CompleteAsync` の宣言を次で置き換える:

```csharp
    /// <summary>
    /// 人の「完了にする」。morning_complete と同じ状態遷移を共有し、閉じ方だけが違う（その場で閉じる）。
    /// プランが未提出なら受理せず理由を返す。
    /// </summary>
    Task<Result> CompleteAsync(int runId, CancellationToken ct = default);
```

Task 2 で足した MCP の節に足す:

```csharp
    /// <summary>
    /// この朝の実行を終える。closeNow が false なら閉じるのを予約し、次の Stop（か 60 秒の保険）で
    /// 端末を閉じる。true ならその場で閉じる（仕様 §7）。
    /// </summary>
    Task<Result<MorningOutcome>> CompleteRunAsync(
        int runId, bool closeNow, CancellationToken ct = default);
```

- [ ] **Step 5: `MorningService` の追従を書き換える**

`src/MoTask.Core/Services/MorningService.cs` の `OnHookLineAsync` を丸ごと次で置き換える:

```csharp
    /// <summary>フックが 1 行書くたびに呼ばれる(行の順序どおり、直列)。</summary>
    private async Task OnHookLineAsync(int runId, string line)
    {
        var parsed = HookEventParser.Parse(line);

        // 取り込んだ実行は終端なので普段は行を捨てるが、閉じる予約がある間だけはターンの
        // 終わりを見る。Claude が最後の一言を言い終えてから窓を消すため(仕様 §7)。
        if (_closePending.ContainsKey(runId)
            && parsed.Kind is AiJobEventKind.TurnEnded or AiJobEventKind.SessionEnded)
        {
            CloseIfPending(runId);
            return;
        }

        MorningRun? run = null;
        var abandoned = false;

        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            // 追跡をやめた後・取り込んだ後に届いた行は捨てる(終わった実行を蘇らせない)
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
                    // Stop のたびに result/ を見に行くのはやめた。成果は MCP で届く(仕様 §4)。
                    current.Status = MorningRunStatus.Running;
                    _turns.AddOrUpdate(runId, 1, (_, turns) => turns + 1);
                    break;
                case AiJobEventKind.SessionEnded:
                    // morning_complete が来ないまま終わった(仕様 §7)
                    current.Status = MorningRunStatus.Failed;
                    current.ErrorMessage = Messages.MorningCompleteMissing;
                    current.EndedAt = _clock.UtcNow;
                    abandoned = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        if (abandoned)
        {
            _events.StopFollowing(run.Id);
            _turns.TryRemove(run.Id, out _);
            _launcher.CloseOwned(run.Id);
        }
        Raise(run, warning, candidatesChanged: false);
    }
```

- [ ] **Step 6: 取り込みを完了宣言に置き換える**

`src/MoTask.Core/Services/MorningService.cs` の `// ---------- 取り込み ----------` の節（`private sealed record IngestOutcome` と `IngestAsync` メソッド全体）を**丸ごと削除**し、`// ---------- 人の操作 ----------` の直下の `CompleteAsync` を次で置き換える:

```csharp
    public async Task<Result> CompleteAsync(int runId, CancellationToken ct = default)
    {
        // 人の「完了にする」は、端末が詰まっているか既に閉じられている場面で押される。
        // 待つべき Stop が来る保証が無いのでその場で閉じる(仕様 §7)。
        var result = await CompleteRunAsync(runId, closeNow: true, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return Result.Fail(result.Error!);
        return result.Value!.Accepted ? Result.Ok() : Result.Fail(result.Value.Reason!);
    }

    public async Task<Result<MorningOutcome>> CompleteRunAsync(
        int runId, bool closeNow, CancellationToken ct = default)
    {
        MorningRun? finished = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var run = await _runs.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status.IsTerminal()) return NotRunning<MorningOutcome>(runId);
            // プランの無い実行は終わらせない(仕様 §6)。候補 0 件は失敗ではない(親仕様 §8)。
            if (run.PlanJson is not { Length: > 0 })
            {
                return Result.Ok(new MorningOutcome(false, Messages.MorningPlanNotSubmitted));
            }

            run.Status = MorningRunStatus.Ingested;
            run.EndedAt = _clock.UtcNow;
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            finished = run;
            return Result.Ok(new MorningOutcome(true, null));
        }, ct).ConfigureAwait(false);

        if (finished is null) return result;

        _turns.TryRemove(finished.Id, out _);
        if (closeNow)
        {
            _events.StopFollowing(finished.Id);
            _launcher.CloseOwned(finished.Id);
        }
        else
        {
            // ツール結果を返した直後に殺すと Claude の最後の一言が切れる。追従は予約が
            // 解けるまで続ける(Stop を受け取る必要がある)。
            ReserveClose(finished.Id);
        }
        Raise(finished, warning, candidatesChanged: false);
        return result;
    }
```

`src/MoTask.Core/Model/MorningRunStatus.cs` の `Ingested` の XML コメントを、意味が変わったので置き換える（値そのものは変えない）:

```csharp
    /// <summary>Claude が morning_complete を呼び、候補とプランが揃った。</summary>
    Ingested = 2,
```

`FindActiveRunAsync` が誰からも呼ばれなくなっていないか確認する（`RegisterAsync` などが使う `FindQueuedCandidateAsync` とは別物）。呼び手が無くなっていれば削除する。

- [ ] **Step 7: 閉じる予約の仕掛けを足す**

`src/MoTask.Core/Services/MorningService.cs` の `_startLock` の宣言の直後に足す:

```csharp
    /// <summary>
    /// 閉じるのを予約した実行(仕様 §7)。morning_complete の直後に殺すと Claude の最後の
    /// 一言が切れるので、次に来る Stop まで待つ。
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _closePending = new();
```

`RecoverOnStartupAsync` の直後（1 本目で足した `PendingTerminalExit` の隣）に足す:

```csharp
    /// <summary>
    /// 予約から Stop が来ないまま閉じるまでの保険(仕様 §7)。既定 60 秒。テストだけが短くする。
    /// </summary>
    public TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>保険の待ち合わせ。イベントと同じくテストが待てるように直近の 1 本を残す。</summary>
    public Task PendingClose { get; private set; } = Task.CompletedTask;

    private void ReserveClose(int runId)
    {
        _closePending[runId] = 0;
        PendingClose = CloseAfterGraceAsync(runId);
    }

    private async Task CloseAfterGraceAsync(int runId)
    {
        await Task.Delay(CloseGrace).ConfigureAwait(false);
        CloseIfPending(runId);
    }

    /// <summary>予約が残っていれば閉じる。Stop と保険のどちらが先でも 1 度しか閉じない。</summary>
    private void CloseIfPending(int runId)
    {
        if (!_closePending.TryRemove(runId, out _)) return;
        _events.StopFollowing(runId);
        _launcher.CloseOwned(runId);
    }
```

- [ ] **Step 8: `MorningResultReader` とその一族を消す**

```bash
git rm src/MoTask.Core/Morning/MorningResultReader.cs src/MoTask.Core/Morning/MorningResult.cs
git rm tests/MoTask.Core.Tests/MorningResultReaderTests.cs
git rm tests/MoTask.Core.Tests/Fixtures/morning-candidates.jsonl tests/MoTask.Core.Tests/Fixtures/morning-candidates-broken.jsonl tests/MoTask.Core.Tests/Fixtures/morning-plan-broken.json
```

`tests/MoTask.Core.Tests/Fixtures/morning-plan.json` は `MorningPlanValidatorTests` が使うので残す。

- [ ] **Step 9: `MorningServiceIngestTests` を新しい契約に合わせる**

`tests/MoTask.Core.Tests/MorningServiceIngestTests.cs` に対して次を行う。

(a) `TwoCandidates` 定数と `PutResult` ヘルパーを**削除する**。

(b) 次のテストを**削除する**（成果の受け渡しが MCP に移ったので、`MorningServiceMcpTests` が同じことを固定している）:

- `Stop_IngestsOnceTheResultIsComplete`
- `Ingest_CopiesEveryFieldOntoTheCandidate`
- `Stop_TwiceIngestsOnlyOnce`
- `SessionEnd_WithABrokenPlan_FailsTheRun`
- `Ingest_SucceedsWithNoCandidates_WhenThePlanIsValid`
- `Ingest_ReportsHowManyLinesItThrewAway`
- `Ingest_SkipsCandidatesAlreadyDecidedInAnEarlierRun`
- `SessionEnd_AfterIngesting_ChangesNothing`
- `SessionEnd_WithoutAPlan_FailsTheRunWithAReason`
- `SessionEnd_WithoutAPlan_ClosesTheTerminalToo`
- `Complete_IngestsForARunWhoseTerminalWasClosed`
- `Complete_FailsTheRun_WhenThereIsNothingToIngest`
- `Complete_ClosesTheTerminal`
- `Stop_ClosesTheTerminalOnceTheResultIsIngested`
- `StopTracking_RefusesARunThatAlreadyFinishedIngesting`

(c) 次のテストを書き換える:

```csharp
    [Fact]
    public async Task Stop_LeavesTheRunAlone_UntilCompleteArrives()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Running, "Stop は何度でも来る");
        _store.Candidates.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
        _service.TurnCountOf(run.Id).Should().Be(1);
        _launcher.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_RefusesARunThatIsAlreadyFinished()
    {
        var run = await StartAsync();
        await _service.StopTrackingAsync(run.Id);

        var again = await _service.CompleteAsync(run.Id);

        again.IsSuccess.Should().BeFalse();
        again.Error.Should().Be(string.Format(Messages.MorningRunNotRunningFormat, run.Id));
    }

    [Fact]
    public async Task StopTracking_CancelsWithoutFinishingTheRun()
    {
        var run = await StartAsync();

        var stopped = await _service.StopTrackingAsync(run.Id);

        stopped.IsSuccess.Should().BeTrue(stopped.Error);
        run.Status.Should().Be(MorningRunStatus.Cancelled);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _store.Candidates.Should().BeEmpty("追跡をやめただけ。端末は殺さないし取り込みもしない");
        _events.IsFollowing(run.Id).Should().BeFalse();
        _launcher.Closed.Should().BeEmpty();
    }

    /// <summary>掛け直せた実行は、その後の完了でちゃんと閉じられる（仕様 §7）。</summary>
    [Fact]
    public async Task Recover_ThenComplete_StillClosesTheTerminal()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);
        await _service.RecoverOnStartupAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);

        await _service.CompleteAsync(run.Id);

        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }
```

`Plan` 定数（`MorningServiceMcpTests` と同じ文字列）をこのクラスの先頭に残す。`Recover_ThenIngest_StillClosesTheTerminal` は上の `Recover_ThenComplete_StillClosesTheTerminal` で置き換える。

(d) 残りのテスト（`SessionStart_MovesTheRunToRunning`・`Stop_LeavesTheTerminalOpen_WhileTheResultIsNotThereYet`・`LinesArrivingAfterTheRunFinished_AreDropped`・`Recover_*` の 4 本・`AProblemFollowingTheFile_*`・`GetLogTail_*`・`TheTerminalDyingFirst_*`・`TheTerminalDyingAfterIngesting_*`・`AnExitForARunWeDoNotKnow_IsIgnored`）はそのまま残す。ただし `TheTerminalDyingAfterIngesting_ChangesNothing` の前提は「`PutResult` ＋ `Stop`」から「`SubmitPlanAsync` ＋ `CompleteAsync`」に書き換える:

```csharp
    [Fact]
    public async Task TheTerminalDyingAfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        await _service.SubmitPlanAsync(run.Id, Plan);
        await _service.CompleteAsync(run.Id);

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.ErrorMessage.Should().BeNull();
    }
```

ファイル名が中身と合わなくなるので、ファイルを `tests/MoTask.Core.Tests/MorningServiceLifecycleTests.cs` に改名し、クラス名も `MorningServiceLifecycleTests` にする:

```bash
git mv tests/MoTask.Core.Tests/MorningServiceIngestTests.cs tests/MoTask.Core.Tests/MorningServiceLifecycleTests.cs
```

- [ ] **Step 10: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests -nologo -v q`
Expected: PASS

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 11: コミット**

```bash
git add -A
git commit -m "feat(core): 完了宣言を MCP で受け、次の Stop で端末を閉じる

result/ の読み取りをやめ MorningResultReader を削除する。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `MorningToolHost` とツール 4 本

**Files:**
- Create: `src/MoTask.App/Ai/MorningTools/MorningArgs.cs`
- Create: `src/MoTask.App/Ai/MorningTools/MorningToolHost.cs`
- Modify: `src/MoTask.App/Ai/MoTaskMcpServer.cs`
- Modify: `src/MoTask.App/App.xaml.cs`
- Modify: `src/MoTask.App/Resources/Strings.resx`, `src/MoTask.App/Resources/Strings.cs`
- Modify: `tests/MoTask.App.Tests/MoTaskMcpServerTests.cs:34,134`
- Test: `tests/MoTask.App.Tests/MorningToolHostTests.cs`（新規）

**Interfaces:**
- Consumes: Task 2・Task 3 の `IMorningService.GetContextAsync` / `AddCandidateAsync` / `SubmitPlanAsync` / `CompleteRunAsync`、既存の `McpTool` / `McpToolResult` / `BoardArgs`
- Produces:
  - `MorningToolHost.Tools`（4 本）と定数 `GetContext` / `AddCandidate` / `SubmitPlan` / `Complete`
  - `MoTaskMcpServer(BoardToolHost boardTools, MorningToolHost morningTools)`
  - `Strings.McpMorningRunIdRequired` / `Strings.McpMorningPlanRequired`

- [ ] **Step 1: 文言を足す**

`src/MoTask.App/Resources/Strings.resx` の末尾（`</root>` の直前）に足す:

```xml
  <data name="McpMorningRunIdRequired" xml:space="preserve"><value>runId（整数）を渡してください。instruction.md に書いてある値です</value></data>
  <data name="McpMorningPlanRequired" xml:space="preserve"><value>plan（JSON オブジェクト）を渡してください</value></data>
```

`src/MoTask.App/Resources/Strings.cs` の末尾に足す:

```csharp
    public static string McpMorningRunIdRequired => Get(nameof(McpMorningRunIdRequired));
    public static string McpMorningPlanRequired => Get(nameof(McpMorningPlanRequired));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/MorningToolHostTests.cs` を新規に作る:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Ai.MorningTools;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Morning;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// morning ツール 4 本（仕様 §6）。BoardToolHost のテストと同じ構えで、
/// 「引数 JSON → McpToolResult」だけを見る。
/// </summary>
public class MorningToolHostTests
{
    private readonly IMorningService _service = Substitute.For<IMorningService>();
    private readonly MorningToolHost _host;

    public MorningToolHostTests()
    {
        _host = new MorningToolHost(_service);
    }

    private async Task<(JsonElement Json, bool IsError, string Text)> CallAsync(string tool, string argumentsJson)
    {
        var target = _host.Tools.Single(t => t.Name == tool);
        using var doc = JsonDocument.Parse(argumentsJson);
        var result = await target.InvokeAsync(doc.RootElement, CancellationToken.None);
        return (result.IsError ? default : JsonDocument.Parse(result.Text).RootElement, result.IsError, result.Text);
    }

    [Fact]
    public void Tools_ExposesTheFourMorningTools_WithObjectSchemas()
    {
        _host.Tools.Select(t => t.Name).Should()
            .Equal("morning_get_context", "morning_add_candidate", "morning_submit_plan", "morning_complete");
        _host.Tools.Should().OnlyContain(t => t.Description.Length > 0);
        foreach (var tool in _host.Tools)
        {
            var schema = JsonSerializer.SerializeToElement(tool.InputSchema);
            schema.GetProperty("type").GetString().Should().Be("object");
            schema.GetProperty("required").EnumerateArray().Select(r => r.GetString())
                .Should().Contain("runId", "すべてのツールが runId を必須にする（仕様 §6）");
        }
    }

    [Fact]
    public async Task GetContext_ReturnsTheSnapshotVerbatim()
    {
        _service.GetContextAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok("""{"date":"2026-09-13","tasks":[]}""")));

        var (json, isError, _) = await CallAsync(MorningToolHost.GetContext, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("date").GetString().Should().Be("2026-09-13");
    }

    /// <summary>宛先違いはツールエラー。普段使いの Claude Code の誤爆を防ぐ（仕様 §6）。</summary>
    [Fact]
    public async Task GetContext_ReturnsAToolError_WhenTheRunIsNotRunning()
    {
        _service.GetContextAsync(9999, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail<string>("runId 9999 の朝の実行は動いていません")));

        var (_, isError, text) = await CallAsync(MorningToolHost.GetContext, """{"runId":9999}""");

        isError.Should().BeTrue();
        text.Should().Be("runId 9999 の朝の実行は動いていません");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runId":"7"}""")]
    public async Task EveryTool_ReturnsAToolError_WhenRunIdIsMissingOrNotAnInteger(string arguments)
    {
        foreach (var tool in _host.Tools.Select(t => t.Name))
        {
            var (_, isError, text) = await CallAsync(tool, arguments);

            isError.Should().BeTrue(tool);
            text.Should().Be(Strings.McpMorningRunIdRequired, tool);
        }
    }

    [Fact]
    public async Task AddCandidate_PassesEveryFieldToTheService()
    {
        _service.AddCandidateAsync(7, Arg.Any<CandidateInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new CandidateOutcome(true, null, 12, 3))));

        var (json, isError, _) = await CallAsync(MorningToolHost.AddCandidate, """
            {"runId":7,"externalId":"outlook:001","source":"Outlook","title":"請求先情報を更新する",
             "evidence":"「9月8日までに」","suggestedAction":"merge","mergeTargetTaskId":45,
             "from":"山本さん","link":"https://x","reasoning":"依頼が明確",
             "receivedAt":"2026-09-13T07:42:00+09:00","suggestedDueDate":"2026-09-14",
             "suggestedProject":"顧客A"}
            """);

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        json.GetProperty("candidateId").GetInt32().Should().Be(12);
        json.GetProperty("total").GetInt32().Should().Be(3);

        var input = (CandidateInput)_service.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMorningService.AddCandidateAsync))
            .GetArguments()[1]!;
        input.ExternalId.Should().Be("outlook:001");
        input.Source.Should().Be("Outlook");
        input.Evidence.Should().Be("「9月8日までに」");
        input.SuggestedAction.Should().Be("merge");
        input.MergeTargetTaskId.Should().Be(45);
        input.From.Should().Be("山本さん");
        input.Link.Should().Be("https://x");
        input.Reasoning.Should().Be("依頼が明確");
        input.ReceivedAt.Should().Be(new DateTime(2026, 9, 12, 22, 42, 0, DateTimeKind.Utc));
        input.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 14));
        input.SuggestedProject.Should().Be("顧客A");
    }

    [Fact]
    public async Task AddCandidate_TurnsMissingOptionalFieldsIntoEmptyStrings()
    {
        _service.AddCandidateAsync(7, Arg.Any<CandidateInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new CandidateOutcome(true, null, 1, 1))));

        await CallAsync(MorningToolHost.AddCandidate,
            """{"runId":7,"externalId":"x","source":"S","title":"T","evidence":"E","suggestedAction":"register"}""");

        var input = (CandidateInput)_service.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMorningService.AddCandidateAsync))
            .GetArguments()[1]!;
        input.From.Should().BeEmpty();
        input.Link.Should().BeEmpty();
        input.Reasoning.Should().BeEmpty();
        input.SuggestedProject.Should().BeEmpty();
        input.ReceivedAt.Should().BeNull();
        input.SuggestedDueDate.Should().BeNull();
        input.MergeTargetTaskId.Should().BeNull();
    }

    /// <summary>不備はツールエラーにせず、理由を通常の結果で返す（仕様 §3）。</summary>
    [Fact]
    public async Task AddCandidate_ReportsARefusalAsAnOrdinaryResult()
    {
        _service.AddCandidateAsync(7, Arg.Any<CandidateInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(
                new CandidateOutcome(false, "evidence が空です。元の文面から引用してください", 0, 2))));

        var (json, isError, _) = await CallAsync(MorningToolHost.AddCandidate,
            """{"runId":7,"externalId":"x","source":"S","title":"T","evidence":"","suggestedAction":"register"}""");

        isError.Should().BeFalse("セッションを失敗させる話ではない");
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("evidence が空です。元の文面から引用してください");
    }

    [Fact]
    public async Task SubmitPlan_PassesTheRawPlanObject()
    {
        _service.SubmitPlanAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new MorningOutcome(true, null))));

        var (json, isError, _) = await CallAsync(MorningToolHost.SubmitPlan,
            """{"runId":7,"plan":{"groups":[{"key":"today","items":[{"taskId":45}]}]}}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        await _service.Received(1).SubmitPlanAsync(
            7,
            Arg.Is<string>(p => p.Contains("\"key\":\"today\"") && p.Contains("\"taskId\":45")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitPlan_ReturnsAToolError_WhenThePlanIsMissing()
    {
        var (_, isError, text) = await CallAsync(MorningToolHost.SubmitPlan, """{"runId":7}""");

        isError.Should().BeTrue();
        text.Should().Be(Strings.McpMorningPlanRequired);
    }

    [Fact]
    public async Task SubmitPlan_ReportsARefusalAsAnOrdinaryResult()
    {
        _service.SubmitPlanAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new MorningOutcome(false, "groups が配列ではありません"))));

        var (json, isError, _) = await CallAsync(MorningToolHost.SubmitPlan, """{"runId":7,"plan":{}}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("groups が配列ではありません");
    }

    /// <summary>ツールの直後には閉じない。次の Stop まで待つ（仕様 §7）。</summary>
    [Fact]
    public async Task Complete_AsksTheServiceToReserveTheClose()
    {
        _service.CompleteRunAsync(7, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new MorningOutcome(true, null))));

        var (json, isError, _) = await CallAsync(MorningToolHost.Complete, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        await _service.Received(1).CompleteRunAsync(7, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_ReportsAMissingPlanAsAnOrdinaryResult()
    {
        _service.CompleteRunAsync(7, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(
                new MorningOutcome(false, "先に morning_submit_plan を呼んでください"))));

        var (json, isError, _) = await CallAsync(MorningToolHost.Complete, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("先に morning_submit_plan を呼んでください");
    }
}
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~MorningToolHostTests" -nologo -v q`
Expected: コンパイルエラー（`MorningToolHost` が無い）で FAIL

- [ ] **Step 4: `MorningArgs` を作る**

`src/MoTask.App/Ai/MorningTools/MorningArgs.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using MoTask.App.Ai.BoardTools;
using MoTask.Core.Morning;

namespace MoTask.App.Ai.MorningTools;

/// <summary>
/// morning_* の arguments を読む。BoardArgs に無いのは ISO8601 の receivedAt と
/// 生 JSON の取り出しだけなので、それだけを足して残りは BoardArgs に任せる
/// （仕様 §9: ホストは JSON ↔ ドメインの変換だけを持つ）。
/// </summary>
public readonly struct MorningArgs
{
    private readonly JsonElement _element;
    private readonly BoardArgs _args;

    public MorningArgs(JsonElement arguments)
    {
        _element = arguments;
        _args = new BoardArgs(arguments);
    }

    /// <summary>この朝の実行の runId。整数で来ていなければ null。</summary>
    public int? RunId => _args.Int("runId");

    /// <summary>オブジェクト／配列をそのままの文字列で取り出す（plan 用）。</summary>
    public string? Raw(string name)
        => _element.ValueKind == JsonValueKind.Object
           && _element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? value.GetRawText()
            : null;

    /// <summary>候補 1 件分。欠けた文字列は空文字にする（何が必須かは Core の純関数が決める）。</summary>
    public CandidateInput ToCandidateInput()
    {
        _args.TryDate("suggestedDueDate", out var due);
        return new CandidateInput(
            Text("externalId"), Text("source"), Text("title"), Text("evidence"),
            Text("from"), Text("link"), Text("reasoning"), Instant("receivedAt"),
            due, Text("suggestedProject"), Text("suggestedAction"), _args.Int("mergeTargetTaskId"));
    }

    private string Text(string name) => _args.String(name) ?? "";

    /// <summary>オフセット付き ISO8601 を UTC へ寄せる。読めなければ null（候補ごと捨てはしない）。</summary>
    private DateTime? Instant(string name)
    {
        var text = Text(name);
        return text.Length > 0
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.UtcDateTime
            : null;
    }
}
```

- [ ] **Step 5: `MorningToolHost` を作る**

`src/MoTask.App/Ai/MorningTools/MorningToolHost.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Morning;
using MoTask.Core.Services;

namespace MoTask.App.Ai.MorningTools;

/// <summary>
/// MCP の morning ツール 4 本（仕様 §6・§9）。IMorningService だけを呼び、HTTP も JSON-RPC も
/// 知らない。検証は Core の純関数が持つので、ここは「JSON を読んで渡し、結果を JSON にする」だけ。
/// 説明文は resx に置かずここへ直書きする（MCP I/F 仕様 §3 の決定）。一方で利用者と Claude に
/// 返す理由は resx に置く。
/// </summary>
public sealed class MorningToolHost
{
    public const string GetContext = "morning_get_context";
    public const string AddCandidate = "morning_add_candidate";
    public const string SubmitPlan = "morning_submit_plan";
    public const string Complete = "morning_complete";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IMorningService _service;

    public MorningToolHost(IMorningService service)
    {
        _service = service;
        Tools = BuildTools();
    }

    public IReadOnlyList<McpTool> Tools { get; }

    // ---------- ツール定義 ----------

    private IReadOnlyList<McpTool> BuildTools() => new[]
    {
        new McpTool(GetContext,
            "今朝の対象日と現在の盤面（列・未完了タスク・プロジェクト・期日・hasActiveAiJob）を返す。"
            + "統合先の推薦と今日のプランは、必ずこの結果に基づくこと。",
            new
            {
                type = "object",
                properties = new { runId = RunId() },
                required = new[] { "runId" },
            },
            GetContextAsync),

        new McpTool(AddCandidate,
            "今朝の候補を 1 件積む。1 件ずつ呼ぶこと。accepted が false で返ったら reason を読み、"
            + "直せるなら直して呼び直す。直せないなら、その候補は諦めて次へ進んでよい。",
            new
            {
                type = "object",
                properties = new
                {
                    runId = RunId(),
                    externalId = new
                    {
                        type = "string",
                        description = "再実行しても同じ値になる元の ID（例: outlook:AAMkAD001）。"
                            + "一度片づけた候補を二度出さないための鍵である。",
                    },
                    source = new { type = "string", description = "どこから拾ったか（Outlook / Teams / Gmail など）。" },
                    title = new { type = "string", description = "タスクにしたときの題名。" },
                    evidence = new { type = "string", description = "元の文面からの引用。根拠の無い候補は受け取らない。" },
                    suggestedAction = new
                    {
                        type = "string",
                        description = "推薦。決めるのは人なので、これは実行ではない。",
                        @enum = new[] { "register", "merge", "later", "reject" },
                    },
                    mergeTargetTaskId = new
                    {
                        type = "integer",
                        description = "suggestedAction が merge のときの統合先タスク id（morning_get_context の盤面にあるもの）。",
                    },
                    from = new { type = "string", description = "差出人。" },
                    link = new { type = "string", description = "元のメッセージへの URL。" },
                    reasoning = new { type = "string", description = "なぜ候補にしたか。" },
                    receivedAt = new { type = "string", description = "受信日時。ISO8601（オフセット付きでよい）。" },
                    suggestedDueDate = new { type = "string", description = "推薦する期日。YYYY-MM-DD。" },
                    suggestedProject = new { type = "string", description = "推薦するプロジェクト名。" },
                },
                required = new[] { "runId", "externalId", "source", "title", "evidence", "suggestedAction" },
            },
            AddCandidateAsync),

        new McpTool(SubmitPlan,
            "今日のプランを出す。何度でも呼べて、最後に受理されたものが残る。"
            + "accepted が false なら reason を読んで直し、呼び直すこと。",
            new
            {
                type = "object",
                properties = new
                {
                    runId = RunId(),
                    plan = new
                    {
                        type = "object",
                        description = "groups[] は key が today / ifTime / aiReady / waiting のいずれかで、"
                            + "items[] の各要素は taskId か externalId のどちらかを持つ。firstThing を置くなら同じ形にする。"
                            + "aiReady には morning_get_context の hasActiveAiJob が false で AI に任せられるものを入れる。",
                    },
                },
                required = new[] { "runId", "plan" },
            },
            SubmitPlanAsync),

        new McpTool(Complete,
            "今朝の実行はこれで終わり、と宣言する。先に morning_submit_plan を通しておくこと。"
            + "呼ぶとこの端末は閉じる。候補が 0 件の朝でも必ず呼ぶこと（0 件は失敗ではない）。",
            new
            {
                type = "object",
                properties = new { runId = RunId() },
                required = new[] { "runId" },
            },
            CompleteAsync),
    };

    private static object RunId() => new
    {
        type = "integer",
        description = "この朝の実行の runId。instruction.md に書いてある値をそのまま渡すこと。",
    };

    // ---------- ハンドラ ----------

    private async Task<McpToolResult> GetContextAsync(JsonElement arguments, CancellationToken ct)
    {
        if (new MorningArgs(arguments).RunId is not int runId) return MissingRunId();

        var context = await _service.GetContextAsync(runId, ct).ConfigureAwait(false);
        return context.IsSuccess ? McpToolResult.Ok(context.Value!) : McpToolResult.Error(context.Error!);
    }

    private async Task<McpToolResult> AddCandidateAsync(JsonElement arguments, CancellationToken ct)
    {
        var args = new MorningArgs(arguments);
        if (args.RunId is not int runId) return MissingRunId();

        var result = await _service.AddCandidateAsync(runId, args.ToCandidateInput(), ct).ConfigureAwait(false);
        if (!result.IsSuccess) return McpToolResult.Error(result.Error!);

        var outcome = result.Value!;
        return McpToolResult.Ok(outcome.Accepted
            ? Serialize(new { accepted = true, candidateId = outcome.CandidateId, total = outcome.Total })
            : Serialize(new { accepted = false, reason = outcome.Reason }));
    }

    private async Task<McpToolResult> SubmitPlanAsync(JsonElement arguments, CancellationToken ct)
    {
        var args = new MorningArgs(arguments);
        if (args.RunId is not int runId) return MissingRunId();
        if (args.Raw("plan") is not { } plan) return McpToolResult.Error(Strings.McpMorningPlanRequired);

        return Answer(await _service.SubmitPlanAsync(runId, plan, ct).ConfigureAwait(false));
    }

    private async Task<McpToolResult> CompleteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (new MorningArgs(arguments).RunId is not int runId) return MissingRunId();

        // その場では閉じない。ツール結果を返した直後に殺すと Claude の最後の一言が切れる（仕様 §7）。
        return Answer(await _service.CompleteRunAsync(runId, closeNow: false, ct).ConfigureAwait(false));
    }

    private static McpToolResult MissingRunId() => McpToolResult.Error(Strings.McpMorningRunIdRequired);

    private static McpToolResult Answer(Result<MorningOutcome> result)
    {
        if (!result.IsSuccess) return McpToolResult.Error(result.Error!);
        var outcome = result.Value!;
        return McpToolResult.Ok(outcome.Accepted
            ? Serialize(new { accepted = true })
            : Serialize(new { accepted = false, reason = outcome.Reason }));
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
```

- [ ] **Step 6: サーバに載せる**

`src/MoTask.App/Ai/MoTaskMcpServer.cs` の `using` に `using MoTask.App.Ai.MorningTools;` を足し、フィールドと構築子を置き換える:

```csharp
    private readonly IReadOnlyList<McpTool> _tools;

    public MoTaskMcpServer(BoardToolHost boardTools, MorningToolHost morningTools)
        => _tools = boardTools.Tools.Concat(morningTools.Tools).ToList();
```

（`private readonly BoardToolHost _boardTools;` は消える。）`HandleAsync` の中の `McpProtocol.HandleAsync(body, _boardTools.Tools, _shutdown.Token)` を `McpProtocol.HandleAsync(body, _tools, _shutdown.Token)` に置き換える。クラスの XML コメントの「提供するのは BoardToolHost の board ツール 6 本のみ。」を次に置き換える:

```csharp
/// 提供するのは <see cref="BoardToolHost"/> の board ツール 6 本と
/// <see cref="MorningToolHost"/> の morning ツール 4 本。
```

- [ ] **Step 7: DI に登録する**

`src/MoTask.App/App.xaml.cs` の `BuildHost` の中、`builder.Services.AddSingleton<MoTaskMcpServer>();` の直前に足す:

```csharp
        // 朝の実行の受け口。IMorningService だけを見るので BoardToolHost とは独立している。
        builder.Services.AddSingleton<Ai.MorningTools.MorningToolHost>();
```

- [ ] **Step 8: 既存のサーバテストを追随させる**

`tests/MoTask.App.Tests/MoTaskMcpServerTests.cs` の 34 行目と 134 行目の `new MoTaskMcpServer(new BoardToolHost(_board, new TestClock()))` を次で置き換える（`using MoTask.App.Ai.MorningTools;` と `using MoTask.Core.Services;`、`using NSubstitute;` を追加する）:

```csharp
        new MoTaskMcpServer(
            new BoardToolHost(_board, new TestClock()),
            new MorningToolHost(Substitute.For<IMorningService>()))
```

- [ ] **Step 9: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~MorningToolHostTests|FullyQualifiedName~MoTaskMcpServerTests|FullyQualifiedName~HostWiringTests|FullyQualifiedName~StringsTests" -nologo -v q`
Expected: PASS

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 10: コミット**

```bash
git add src/MoTask.App/Ai/MorningTools src/MoTask.App/Ai/MoTaskMcpServer.cs src/MoTask.App/App.xaml.cs src/MoTask.App/Resources/Strings.resx src/MoTask.App/Resources/Strings.cs tests/MoTask.App.Tests/MorningToolHostTests.cs tests/MoTask.App.Tests/MoTaskMcpServerTests.cs
git commit -m "feat(app): 朝の実行の MCP ツール 4 本を足してサーバに載せる

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `mcp.json` を生成して `--mcp-config` で渡す

利用者が `claude mcp add motask …` を消しても朝の実行はツールを使える（仕様 §2・§5.4）。`--strict-mcp-config` は渡さないので、利用者のコネクタ（Gmail・カレンダーなど）はそのまま生きる。

**Files:**
- Create: `src/MoTask.App/Ai/McpConfigJson.cs`
- Modify: `src/MoTask.Core/Ai/JobFolderPaths.cs`, `JobFolderRequest.cs`, `SessionLaunchRequest.cs`
- Modify: `src/MoTask.App/Ai/JobFolder.cs`, `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `src/MoTask.App/MoTask.App.csproj`
- Modify: `src/MoTask.Core/Services/MorningService.cs`（`StartAsync` の 2 か所）
- Modify: `src/MoTask.Core/Resources/Messages.resx`, `Messages.cs`
- Test: `tests/MoTask.App.Tests/McpConfigJsonTests.cs`（新規）、`JobFolderTests.cs`、`TerminalLauncherTests.cs`、`tests/MoTask.Core.Tests/MorningServiceStartTests.cs`

**Interfaces:**
- Consumes: 既存の `HooksJson` の構え、`JobFolder.HooksExecutable`
- Produces:
  - `McpConfigJson.Build(string mcpExecutable)` → `{"mcpServers":{"motask":{"command":"…","args":[]}}}`
  - `JobFolderPaths.McpJsonName` / `JobFolderPaths.McpJson`
  - `JobFolderRequest.WithMcpConfig`（`bool`。既定 false）
  - `SessionLaunchRequest.McpConfigPath`（`string?`。既定 null）
  - `JobFolder.McpExecutable`（`internal`。既定 `<出力>\mcp\MoTask.Mcp.exe`）
  - `Messages.McpExecutableNotFound`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/McpConfigJsonTests.cs` を新規に作る:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>--mcp-config に渡す 1 ファイル（仕様 §5.4）。HooksJson と同じ構え。</summary>
public class McpConfigJsonTests
{
    [Fact]
    public void Build_RegistersTheBridgeUnderTheNameMotask()
    {
        var json = McpConfigJson.Build(@"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe");

        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("motask");
        server.GetProperty("command").GetString().Should().Be(@"C:\Program Files\MoTask\mcp\MoTask.Mcp.exe");
        server.GetProperty("args").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Build_KeepsBackslashesReadable()
    {
        // 人が開いて読めるように \uXXXX にしない（HooksJson と同じ理由）
        McpConfigJson.Build(@"C:\x\MoTask.Mcp.exe").Should().Contain(@"C:\\x\\MoTask.Mcp.exe");
    }
}
```

`tests/MoTask.App.Tests/JobFolderTests.cs` の構築子に mcp の exe を足し、テストを 2 本足す:

```csharp
    private readonly string _mcpExe;
```

構築子の `_folder = new JobFolder(_settings) { HooksExecutable = _hooksExe };` を置き換える:

```csharp
        _mcpExe = Path.Combine(_root, "MoTask.Mcp.exe");
        File.WriteAllText(_mcpExe, "");
        _folder = new JobFolder(_settings) { HooksExecutable = _hooksExe, McpExecutable = _mcpExe };
```

`MorningRequest()` を置き換える:

```csharp
    private static JobFolderRequest MorningRequest() =>
        new(7, "2026-09-07", "指示")
        {
            Category = JobFolderPaths.MorningDirectoryName,
            OutputDirectoryName = JobFolderPaths.ResultDirectoryName,
            WithMcpConfig = true,
        };
```

足すテスト:

```csharp
    /// <summary>朝の実行は利用者の手動 MCP 登録に依存しない（仕様 §5.4）。</summary>
    [Fact]
    public void Create_ForTheMorning_WritesTheMcpConfig()
    {
        var root = _folder.Create(MorningRequest()).Value!;

        var json = File.ReadAllText(JobFolderPaths.For(root).McpJson);
        json.Should().Contain("mcpServers").And.Contain("MoTask.Mcp.exe");
    }

    [Fact]
    public void Create_ForAnAiJob_WritesNoMcpConfig()
    {
        var root = _folder.Create(new JobFolderRequest(42, "請求書の突合", "やること")).Value!;

        File.Exists(JobFolderPaths.For(root).McpJson).Should().BeFalse("AI 遂行は利用者の登録に任せる");
    }

    [Fact]
    public void Create_ForTheMorning_StopsWhenTheBridgeExeIsMissing()
    {
        File.Delete(_mcpExe);

        var created = _folder.Create(MorningRequest());

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(Messages.McpExecutableNotFound);
    }
```

（`using MoTask.Core;` がこのファイルに無ければ足す。）

`tests/MoTask.App.Tests/TerminalLauncherTests.cs` に足す:

```csharp
    [Fact]
    public void BuildCommand_PassesTheMcpConfigWhenOneIsGiven()
    {
        var command = Launcher()
            .BuildCommand(_request with { McpConfigPath = @"C:\work\jobs\0042-見積り\mcp.json" }).Value!;

        command.Arguments.Should().Contain(@"--mcp-config"" ""C:\work\jobs\0042-見積り\mcp.json""");
        command.Arguments.Should().NotContain("--strict-mcp-config",
            "利用者のコネクタはそのまま生きる（仕様 §5.4）");
    }
```

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs` に足す:

```csharp
    [Fact]
    public async Task Start_AsksForTheMcpConfigSoTheToolsAreThereWithoutManualRegistration()
    {
        var run = (await _service.StartAsync()).Value!;

        _folder.Created[0].WithMcpConfig.Should().BeTrue();
        _launcher.Requests.Should().ContainSingle().Which.McpConfigPath
            .Should().Be(JobFolderPaths.For(run.JobFolder).McpJson);
    }
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: コンパイルエラー（`McpConfigJson` / `McpJson` / `WithMcpConfig` / `McpConfigPath` / `McpExecutable` が無い）で FAIL

- [ ] **Step 3: Core 側の 3 ファイルに項目を足す**

`src/MoTask.Core/Ai/JobFolderPaths.cs` の `RunJsonName` の直後に足す:

```csharp
    /// <summary>--mcp-config に渡す MCP サーバ登録（仕様 §5.4）。朝の実行だけが持つ。</summary>
    public const string McpJsonName = "mcp.json";
```

`RunJson` プロパティの直後に足す:

```csharp
    public string McpJson => Path.Combine(Root, McpJsonName);
```

`src/MoTask.Core/Ai/JobFolderRequest.cs` の `OutputDirectoryName` プロパティの直後に足す:

```csharp
    /// <summary>
    /// mcp.json を併せて書くか（仕様 §5.4）。朝の実行だけが true。
    /// AI 遂行は利用者の手動登録に任せるので既定は false。
    /// </summary>
    public bool WithMcpConfig { get; init; }
```

`src/MoTask.Core/Ai/SessionLaunchRequest.cs` のレコードを置き換える:

```csharp
public sealed record SessionLaunchRequest(
    Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume,
    string OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName,
    bool CloseOnExit = false,
    string? McpConfigPath = null);
```

XML コメントの末尾に足す:

```
/// McpConfigPath が入っていれば --mcp-config で渡す。朝の実行だけが使う（仕様 §5.4）。
```

- [ ] **Step 4: 文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の `HooksExecutableNotFound` の直後に足す:

```xml
  <data name="McpExecutableNotFound" xml:space="preserve"><value>MoTask.Mcp.exe が見つかりません。MoTask をビルドし直してください</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` に足す:

```csharp
    public static string McpExecutableNotFound => Get(nameof(McpExecutableNotFound));
```

- [ ] **Step 5: `McpConfigJson` を作る**

`src/MoTask.App/Ai/McpConfigJson.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>
/// --mcp-config に渡す 1 ファイル（仕様 §5.4）。ブリッジ（MoTask.Mcp.exe）を motask という名前で
/// 登録するだけで、--strict-mcp-config は渡さないので利用者のコネクタはそのまま生きる。
/// HooksJson と同じ構え。
/// </summary>
public static class McpConfigJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 人が開いて読めるように、バックスラッシュや日本語を \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string mcpExecutable)
    {
        var servers = new Dictionary<string, object>
        {
            ["motask"] = new { command = mcpExecutable, args = Array.Empty<string>() },
        };
        return JsonSerializer.Serialize(new { mcpServers = servers }, Options);
    }
}
```

- [ ] **Step 6: `JobFolder` が `mcp.json` を書く**

`src/MoTask.App/Ai/JobFolder.cs` の `HooksExecutable` プロパティの直後に足す:

```csharp
    /// <summary>MoTask.App のビルドが mcp\ へ運ぶ exe（仕様 §5.4）。テストは差し替える。</summary>
    internal string McpExecutable { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "mcp", "MoTask.Mcp.exe");
```

`Create` の中、`if (!File.Exists(HooksExecutable)) return …;` の直後に足す:

```csharp
        // ブリッジが無いと朝の実行は成果を渡す先を失う。黙って走らせず、開始時に止める。
        if (request.WithMcpConfig && !File.Exists(McpExecutable))
        {
            return Result.Fail<string>(Messages.McpExecutableNotFound);
        }
```

`try` の中、`File.WriteAllText(paths.HooksJson, …);` の直後に足す:

```csharp
            if (request.WithMcpConfig)
            {
                File.WriteAllText(paths.McpJson, McpConfigJson.Build(McpExecutable), Utf8);
            }
```

- [ ] **Step 7: `TerminalLauncher` が `--mcp-config` を渡す**

`src/MoTask.App/Ai/TerminalLauncher.cs` の `BuildCommand` の中、`parts.Add(request.JobFolder);`（`--add-dir` の値）の直後に足す:

```csharp
        if (request.McpConfigPath is { Length: > 0 } mcpConfig)
        {
            // 利用者の手動 MCP 登録に依存しない（仕様 §5.4）。--strict-mcp-config は渡さないので、
            // 利用者のコネクタ（Gmail・カレンダーなど）はそのまま生きる。
            parts.Add("--mcp-config");
            parts.Add(mcpConfig);
        }
```

- [ ] **Step 8: `MorningService` が両方を頼む**

`src/MoTask.Core/Services/MorningService.cs` の `StartAsync` の `JobFolderRequest` を置き換える:

```csharp
            var request = new JobFolderRequest(prepared.Value!.RunNumber, date.ToString("yyyy-MM-dd"), "")
            {
                Category = JobFolderPaths.MorningDirectoryName,
                OutputDirectoryName = JobFolderPaths.ResultDirectoryName,
                WithMcpConfig = true,
            };
```

`BuildCommand` の呼び出しを置き換える:

```csharp
            var command = _launcher.BuildCommand(new SessionLaunchRequest(
                sessionId, root, root, Resume: false,
                OutputDirectoryName: JobFolderPaths.ResultDirectoryName, CloseOnExit: true,
                McpConfigPath: JobFolderPaths.For(root).McpJson));
```

- [ ] **Step 9: `MoTask.Mcp.exe` をアプリ出力へ運ぶ**

`src/MoTask.App/MoTask.App.csproj` の `ProjectReference` の塊に足す:

```xml
    <!-- MCP ブリッジの exe。参照はビルド順のためだけで、アセンブリとしては使わない。 -->
    <ProjectReference Include="..\MoTask.Mcp\MoTask.Mcp.csproj" ReferenceOutputAssembly="false" />
```

`hooks\` を運んでいる `ItemGroup` に足す:

```xml
    <Content Include="..\MoTask.Mcp\bin\$(Configuration)\net10.0\MoTask.Mcp.exe"
             Link="mcp\MoTask.Mcp.exe" CopyToOutputDirectory="PreserveNewest" Visible="false" />
    <Content Include="..\MoTask.Mcp\bin\$(Configuration)\net10.0\MoTask.Mcp.dll"
             Link="mcp\MoTask.Mcp.dll" CopyToOutputDirectory="PreserveNewest" Visible="false" />
    <Content Include="..\MoTask.Mcp\bin\$(Configuration)\net10.0\MoTask.Mcp.runtimeconfig.json"
             Link="mcp\MoTask.Mcp.runtimeconfig.json" CopyToOutputDirectory="PreserveNewest" Visible="false" />
```

`MoTask.Mcp` は `MoTask.Core.dll` を必要とするが、それは `MoTask.App` の出力ルートに既にある。ブリッジは出力ルートではなく `mcp\` に置かれるので、`MoTask.Core.dll` も並べる必要がある。上の塊にもう 1 行足す:

```xml
    <Content Include="..\MoTask.Mcp\bin\$(Configuration)\net10.0\MoTask.Core.dll"
             Link="mcp\MoTask.Core.dll" CopyToOutputDirectory="PreserveNewest" Visible="false" />
```

- [ ] **Step 10: 走らせて通ることを確かめる**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

`mcp\` に実体が並んだことを確かめる:

Run: `ls src/MoTask.App/bin/Debug/net10.0-windows/mcp/`
Expected: `MoTask.Mcp.exe` / `MoTask.Mcp.dll` / `MoTask.Mcp.runtimeconfig.json` / `MoTask.Core.dll` が並ぶ

- [ ] **Step 11: コミット**

```bash
git add src/MoTask.App/Ai/McpConfigJson.cs src/MoTask.App/Ai/JobFolder.cs src/MoTask.App/Ai/TerminalLauncher.cs src/MoTask.App/MoTask.App.csproj src/MoTask.Core/Ai src/MoTask.Core/Services/MorningService.cs src/MoTask.Core/Resources tests/MoTask.App.Tests tests/MoTask.Core.Tests
git commit -m "feat(app): 朝の実行に mcp.json を持たせ --mcp-config で渡す

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `instruction.md` の契約をツールの呼び出し手順に差し替える

書式引数が「日付・board.json・candidates.jsonl・plan.json」から「日付・runId」の 2 つに減る。`runId` は DB の採番なので、指示文は行を保存してから組み立てる（補足 1）。

**Files:**
- Modify: `src/MoTask.Core/Resources/Messages.resx`（`MorningInstructionContractFormat`）
- Modify: `src/MoTask.Core/Morning/MorningInstruction.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`（`StartAsync` の順序）
- Test: `tests/MoTask.Core.Tests/MorningInstructionTests.cs`, `tests/MoTask.Core.Tests/MorningServiceStartTests.cs`

**Interfaces:**
- Consumes: Task 1 の `MorningPlanValidator.PlanGroupKeys`
- Produces: `MorningInstruction.Build(string? template, DateOnly date, int runId)`（`JobFolderPaths` を取らなくなる）

- [ ] **Step 1: 契約文を差し替える**

`src/MoTask.Core/Resources/Messages.resx` の `MorningInstructionContractFormat` の `<value>` を次で丸ごと置き換える（`{{` と `}}` は `string.Format` が出力する `{` と `}` である。そのまま写すこと）:

```
## 出力の契約（この節は MoTask が書いています。変更しないでください）

対象日は {0} です。この朝の実行の runId は {1} です。
成果は MCP ツールで渡してください。ファイルは書かないでください。

1. mcp__motask__morning_get_context({{"runId":{1}}}) で今日の盤面を取る。
   統合先の推薦と今日のプランは、必ずこの結果に基づくこと。
2. 候補 1 件ごとに mcp__motask__morning_add_candidate({{"runId":{1}, ...}}) を呼ぶ。
   externalId は再実行しても同じ値になるようにすること（例: サービス名:元のID）。
   一度片づけた候補を二度出さないための鍵である。
   accepted:false が返ったら reason を読み、直せるなら直して呼び直す。
   直せないなら、その候補は諦めて次へ進んでよい。
3. mcp__motask__morning_submit_plan({{"runId":{1},"plan":{{...}}}}) でプランを出す。
   plan の groups[].key は today / ifTime / aiReady / waiting のいずれかにする。
   aiReady には、morning_get_context の hasActiveAiJob が false で AI に任せられるものを入れる。
4. mcp__motask__morning_complete({{"runId":{1}}}) を呼ぶ。呼ぶとこの端末は閉じる。

決めるのは人である。suggestedAction は推薦であって実行ではない。
候補が 0 件の朝もある。それは失敗ではない。その場合も 3 と 4 は必ず呼ぶこと。
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningInstructionTests.cs` を次の内容で置き換える（`Build` の呼び出しが変わるので、既存のケースもここに畳む）:

```csharp
using FluentAssertions;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// instruction.md（仕様 §8）。前半は人が書き換えられる収集方針、後半は MoTask が必ず付ける契約。
/// 契約を人に編集させるとツールの呼び方との対応が黙って壊れる。
/// </summary>
public class MorningInstructionTests
{
    private static string Build(string? template = null)
        => MorningInstruction.Build(template, new DateOnly(2026, 9, 13), runId: 7);

    [Fact]
    public void Build_PutsTheConfiguredPolicyFirst()
    {
        Build("私の方針").Should().StartWith("私の方針");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_FallsBackToTheDefaultPolicy(string? template)
    {
        Build(template).Should().StartWith(MorningInstruction.DefaultTemplate);
    }

    [Fact]
    public void Build_SpellsOutTheDateAndTheRunId()
    {
        var text = Build();

        text.Should().Contain("2026-09-13");
        text.Should().Contain("runId は 7 です");
    }

    /// <summary>Claude が 4 本を順に呼べるだけの手順が書いてある（仕様 §8）。</summary>
    [Fact]
    public void Build_NamesTheFourToolsInOrder()
    {
        var text = Build();

        text.IndexOf("mcp__motask__morning_get_context", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__morning_add_candidate", StringComparison.Ordinal));
        text.IndexOf("mcp__motask__morning_add_candidate", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__morning_submit_plan", StringComparison.Ordinal));
        text.IndexOf("mcp__motask__morning_submit_plan", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("mcp__motask__morning_complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PassesTheRunIdInEveryCall()
    {
        Build().Should().Contain("""{"runId":7}""");
    }

    [Fact]
    public void Build_SpellsOutTheFourPlanGroupKeys()
    {
        var text = Build();

        foreach (var key in MorningPlanValidator.PlanGroupKeys) text.Should().Contain(key);
    }

    /// <summary>ファイルの契約はもう無い（仕様 §4）。</summary>
    [Fact]
    public void Build_NoLongerMentionsTheOldFileContract()
    {
        var text = Build();

        text.Should().NotContain("candidates.jsonl").And.NotContain("plan.json").And.NotContain("board.json");
    }

    [Fact]
    public void Build_TellsThatAnEmptyMorningIsNotAFailure()
    {
        Build().Should().Contain("候補が 0 件の朝もある");
    }
}
```

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs` の `Start_SavesTheRunOnlyAfterTheFolderIsKnown` の中の 1 行を置き換える:

```csharp
        run.Instruction.Should().Contain($"runId は {run.Id} です", "契約文は runId を名指しする（仕様 §8）");
```

同じファイルに足す:

```csharp
    /// <summary>フォルダの instruction.md と DB の Instruction 列は同じ文言（仕様 §8）。</summary>
    [Fact]
    public async Task Start_WritesTheSameInstructionToTheFolderAndTheRow()
    {
        var run = (await _service.StartAsync()).Value!;

        _folder.ReadText(run.JobFolder, JobFolderPaths.InstructionMarkdownName).Should().Be(run.Instruction);
        run.Instruction.Should().Contain("mcp__motask__morning_complete");
    }
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningInstructionTests|FullyQualifiedName~MorningServiceStartTests" -nologo -v q`
Expected: コンパイルエラー（`Build` の引数が合わない）で FAIL

- [ ] **Step 4: `MorningInstruction` の引数を変える**

`src/MoTask.Core/Morning/MorningInstruction.cs` を丸ごと次に置き換える:

```csharp
namespace MoTask.Core.Morning;

/// <summary>
/// instruction.md（仕様 §8）。前半は人が AI 設定で書き換えられる収集方針、
/// 後半は MoTask が必ず付けるツールの呼び出し手順。
/// 契約を人に編集させると、ツールの呼び方との対応が黙って壊れる。
/// </summary>
public static class MorningInstruction
{
    public static string DefaultTemplate => Messages.MorningInstructionDefault;

    /// <summary>
    /// runId は DB の採番なので、呼び手は行を保存してからここへ来ること
    /// （各引数の形は MCP のスキーマが持つので、契約文に JSON の例は並べない）。
    /// </summary>
    public static string Build(string? template, DateOnly date, int runId)
    {
        var head = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        var contract = string.Format(
            Messages.MorningInstructionContractFormat, date.ToString("yyyy-MM-dd"), runId);
        return head + "\n\n" + contract + "\n";
    }
}
```

- [ ] **Step 5: `StartAsync` の順序を変える**

`src/MoTask.Core/Services/MorningService.cs` の `StartAsync` の中を次のように組み替える。

(a) `var root = _folder.ResolveRoot(request);` の下の 2 行（`var instruction = MorningInstruction.Build(...)` と `var created = _folder.Create(request with { Instruction = instruction });`）を置き換える:

```csharp
            // 指示文は runId（DB の採番）を含むので、行を保存してからでないと組み立てられない
            // （仕様 §8）。フォルダと hooks.json / mcp.json だけを先に作り、instruction.md は後で書く。
            var root = _folder.ResolveRoot(request);
            var created = _folder.Create(request);
            if (!created.IsSuccess) return Result.Fail<MorningRun>(created.Error!);
```

（`request` の `Instruction` は空文字のままでよい。`JobFolder.Create` はそれを `instruction.md` に書くが、下ですぐ上書きする。）

(b) `var run = new MorningRun { … Instruction = instruction, … };` の `Instruction = instruction,` を `Instruction = "",` に変える。

(c) 保存のブロックを置き換える:

```csharp
            var saved = await _gate.RunAsync(async () =>
            {
                try
                {
                    _runs.Add(run);
                    // Id が要るのでいったん保存し、採番してから指示文を作って書き戻す（仕様 §8）。
                    // ゲートの取得は 1 回のままで、SaveChanges が 2 回走るだけ。
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    run.Instruction = MorningInstruction.Build(settings.MorningInstruction, date, run.Id);
                    await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Result.Ok();
                }
                catch (PersistenceException ex)
                {
                    return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result.Fail<MorningRun>(saved.Error!);

            var wroteInstruction = _folder.WriteText(
                root, JobFolderPaths.InstructionMarkdownName, run.Instruction);
            if (!wroteInstruction.IsSuccess)
            {
                return await FailAsync(run, wroteInstruction.Error!).ConfigureAwait(false);
            }
```

- [ ] **Step 6: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningInstruction|FullyQualifiedName~MorningServiceStartTests" -nologo -v q`
Expected: PASS

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core/Resources/Messages.resx src/MoTask.Core/Morning/MorningInstruction.cs src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningInstructionTests.cs tests/MoTask.Core.Tests/MorningServiceStartTests.cs
git commit -m "feat(core): instruction.md の契約をツールの呼び出し手順に差し替える

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: `result/` と `board.json` をやめる

ジョブフォルダの中身が `instruction.md` / `hooks.json` / `events.jsonl` / `run.json` / `mcp.json` になる（仕様 §4）。

**Files:**
- Modify: `src/MoTask.Core/Ai/JobFolderPaths.cs`, `JobFolderRequest.cs`, `SessionLaunchRequest.cs`
- Modify: `src/MoTask.App/Ai/JobFolder.cs`, `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`（`StartAsync`）
- Modify: `src/MoTask.Core/Resources/Messages.resx`, `Messages.cs`
- Test: `tests/MoTask.Core.Tests/JobFolderPathsTests.cs`, `MorningServiceStartTests.cs`, `tests/MoTask.App.Tests/JobFolderTests.cs`, `TerminalLauncherTests.cs`

**Interfaces:**
- Consumes: Task 5・Task 6 のすべて
- Produces:
  - `SessionLaunchRequest.OutputDirectoryName` が `string?` になり、null は「成果物の案内を出さない」
  - `JobFolderRequest.OutputDirectoryName` が空文字なら出力フォルダを作らない
  - `Messages.MorningStartPromptFormat`（書式引数は指示文のパス 1 つ）

- [ ] **Step 1: 朝用の起動プロンプトを足す**

`src/MoTask.Core/Resources/Messages.resx` の `TerminalStartPromptFormat` の直後に足す:

```xml
  <data name="MorningStartPromptFormat" xml:space="preserve"><value>{0} を読んで作業を始めてください。</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` に足す:

```csharp
    public static string MorningStartPromptFormat => Get(nameof(MorningStartPromptFormat));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/TerminalLauncherTests.cs` の `BuildCommand_PointsThePromptAtTheResultFolder_ForAMorningShapedRequest` を次で置き換える:

```csharp
    /// <summary>
    /// 朝の実行は成果をファイルに出さないので、起動プロンプトに出し先を書かない（仕様 §5.4）。
    /// </summary>
    [Fact]
    public void BuildCommand_PointsThePromptOnlyAtTheInstruction_ForAMorningRun()
    {
        var morning = _request with { OutputDirectoryName = null, CloseOnExit = true };

        var command = Launcher().BuildCommand(morning).Value!;

        command.Arguments.Should()
            .Contain(@"C:\work\jobs\0042-見積り\instruction.md")
            .And.NotContain(@"C:\work\jobs\0042-見積り\artifacts")
            .And.NotContain(@"C:\work\jobs\0042-見積り\result");
    }
```

`tests/MoTask.Core.Tests/JobFolderPathsTests.cs` の 95〜97 行（`CandidatesRelativePath` / `PlanRelativePath` / `BoardJsonName` を見ている 3 行）を次で置き換える:

```csharp
        Path.Combine(root, JobFolderPaths.RunJsonName).Should().Be(paths.RunJson);
        Path.Combine(root, JobFolderPaths.McpJsonName).Should().Be(paths.McpJson);
```

`tests/MoTask.App.Tests/JobFolderTests.cs`:

- `MorningRequest()` の `OutputDirectoryName = JobFolderPaths.ResultDirectoryName,` の行を `OutputDirectoryName = "",` に置き換える
- `Create_ForTheMorning_PutsTheFolderUnderMorningAndMakesResult` を次で置き換える:

```csharp
    [Fact]
    public void Create_ForTheMorning_PutsTheFolderUnderMorningWithNoOutputFolder()
    {
        var root = _folder.Create(MorningRequest());

        root.IsSuccess.Should().BeTrue(root.Error);
        root.Value!.Should().EndWith(Path.Combine("morning", "0007-2026-09-07"));
        var paths = JobFolderPaths.For(root.Value!);
        Directory.Exists(paths.ArtifactsDirectory).Should().BeFalse("朝の実行は成果をファイルに出さない");
        File.Exists(paths.HooksJson).Should().BeTrue();
        File.Exists(paths.McpJson).Should().BeTrue();
    }
```

- `WriteText_AndReadText_RoundTripThroughSubfolders` と `ReadText_ReturnsNull_WhenTheFileIsNotThereYet` の `JobFolderPaths.BoardJsonName` / `CandidatesRelativePath` / `PlanRelativePath` を、汎用の相対パス文字列に置き換える:

```csharp
    [Fact]
    public void WriteText_AndReadText_RoundTripThroughSubfolders()
    {
        var root = _folder.Create(MorningRequest()).Value!;

        _folder.WriteText(root, "notes.json", "{\"date\":\"2026-09-07\"}").IsSuccess.Should().BeTrue();
        _folder.WriteText(root, Path.Combine("sub", "lines.txt"), "1行目\n2行目").IsSuccess.Should().BeTrue();

        _folder.ReadText(root, "notes.json").Should().Be("{\"date\":\"2026-09-07\"}");
        _folder.ReadText(root, Path.Combine("sub", "lines.txt")).Should().Be("1行目\n2行目");
    }

    [Fact]
    public void ReadText_ReturnsNull_WhenTheFileIsNotThereYet()
    {
        var root = _folder.Create(MorningRequest()).Value!;

        _folder.ReadText(root, Path.Combine("sub", "not-yet.json")).Should()
            .BeNull("まだ書かれていないだけで、失敗ではない");
        _folder.ReadText("", Path.Combine("sub", "not-yet.json")).Should().BeNull();
    }
```

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs`:

- `Start_WritesBoardJsonWithTheUnfinishedTasks` を**削除する**（盤面は `morning_get_context` が返す。`MorningServiceMcpTests.GetContext_ReturnsTheDateAndTheUnfinishedTasks` が同じことを固定している）
- `Start_PutsTheFolderUnderMorning_NamedByRunNumberAndDate` の `OutputDirectoryName` の行を置き換える:

```csharp
        _folder.Created[0].OutputDirectoryName.Should().BeEmpty("朝の実行に出力フォルダは要らない");
```

- 足す:

```csharp
    [Fact]
    public async Task Start_DoesNotAskThePromptToPointAtAnOutputFolder()
    {
        await _service.StartAsync();

        _launcher.Requests.Should().ContainSingle().Which.OutputDirectoryName.Should().BeNull();
    }
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: コンパイルエラー（`OutputDirectoryName` に null を渡せない／`McpJsonName` 周りの型）で FAIL

- [ ] **Step 4: `JobFolderPaths` から `result/` と `board.json` を落とす**

`src/MoTask.Core/Ai/JobFolderPaths.cs` から次を**削除する**:

```csharp
    public const string ResultDirectoryName = "result";
    public const string BoardJsonName = "board.json";
    public static readonly string CandidatesRelativePath = Path.Combine(ResultDirectoryName, "candidates.jsonl");
    public static readonly string PlanRelativePath = Path.Combine(ResultDirectoryName, "plan.json");
    public string ResultDirectory => Path.Combine(Root, ResultDirectoryName);
    public string BoardJson => Path.Combine(Root, BoardJsonName);
    public string CandidatesJsonl => Path.Combine(Root, CandidatesRelativePath);
    public string PlanJson => Path.Combine(Root, PlanRelativePath);
```

`using System.Text;` が `Slug` でまだ要ることを確かめる（要る）。クラスの XML コメントに 1 行足す:

```csharp
/// 朝の実行のフォルダは instruction.md / hooks.json / events.jsonl / run.json / mcp.json だけ
/// （成果は MCP で渡すので result/ も board.json も作らない・仕様 §4）。
```

- [ ] **Step 5: 出力フォルダを作らない道を通す**

`src/MoTask.Core/Ai/JobFolderRequest.cs` の `OutputDirectoryName` のコメントを置き換える:

```csharp
    /// <summary>
    /// Create が併せて作る出力フォルダ。空文字なら作らない（朝の実行は成果をファイルに出さない）。
    /// </summary>
    public string OutputDirectoryName { get; init; } = JobFolderPaths.ArtifactsDirectoryName;
```

`src/MoTask.App/Ai/JobFolder.cs` の `Create` の中、`Directory.CreateDirectory(Path.Combine(root, request.OutputDirectoryName));` を置き換える:

```csharp
            // 既にあっても作り直さない（--resume で開き直すときに同じフォルダへ戻る）
            Directory.CreateDirectory(request.OutputDirectoryName is { Length: > 0 } output
                ? Path.Combine(root, output)
                : root);
```

`src/MoTask.Core/Ai/SessionLaunchRequest.cs` のレコードを置き換える:

```csharp
public sealed record SessionLaunchRequest(
    Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume,
    string? OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName,
    bool CloseOnExit = false,
    string? McpConfigPath = null);
```

XML コメントの `OutputDirectoryName` の説明を置き換える:

```
/// OutputDirectoryName は起動プロンプトで「成果物をここに出して」と伝える先。AI 遂行は artifacts/。
/// null なら案内そのものを出さない（朝の実行は成果をファイルに出さない・仕様 §5.4）。
```

`src/MoTask.App/Ai/TerminalLauncher.cs` の `BuildCommand` の末尾、`var outputDirectory = …` と `parts.Add(string.Format(Messages.TerminalStartPromptFormat, …));` の 2 行を置き換える:

```csharp
        // 成果物の出し先は AI 遂行だけが伝える。朝の実行は MCP で渡すので、伝えるものが無い（仕様 §5.4）。
        parts.Add(request.OutputDirectoryName is { Length: > 0 } output
            ? string.Format(Messages.TerminalStartPromptFormat,
                paths.InstructionMarkdown, Path.Combine(paths.Root, output))
            : string.Format(Messages.MorningStartPromptFormat, paths.InstructionMarkdown));
```

- [ ] **Step 6: `MorningService` が `result/` を頼まないようにする**

`src/MoTask.Core/Services/MorningService.cs` の `StartAsync`:

(a) `Prepared` レコードを置き換える:

```csharp
    private sealed record Prepared(int RunNumber);
```

(b) ゲートの中の `var snapshot = await BuildSnapshotAsync(date, ct)…;` と `return Result.Ok(new Prepared(runNumber, snapshot));` を置き換える:

```csharp
                // 盤面は morning_get_context が返すので、ここでは「在るか」だけを見る（仕様 §4）
                var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
                if (board is null) return Result.Fail<Prepared>(Messages.BoardNotFound);

                // フォルダ名の連番。DB の採番を待たずに決まる（親仕様 §12）。
                var runNumber = await _runs.CountRunsAsync(ct).ConfigureAwait(false) + 1;
                return Result.Ok(new Prepared(runNumber));
```

(c) `JobFolderRequest` の `OutputDirectoryName` を置き換える:

```csharp
                OutputDirectoryName = "",
```

(d) `var wroteBoard = _folder.WriteText(root, JobFolderPaths.BoardJsonName, prepared.Value!.BoardJson);` と続く `if (!wroteBoard.IsSuccess) …;` の 2 行を**削除する**。

(e) `BuildCommand` の呼び出しを置き換える:

```csharp
            var command = _launcher.BuildCommand(new SessionLaunchRequest(
                sessionId, root, root, Resume: false,
                OutputDirectoryName: null, CloseOnExit: true,
                McpConfigPath: JobFolderPaths.For(root).McpJson));
```

- [ ] **Step 7: 走らせて通ることを確かめる**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

Run: `git grep -n "candidates.jsonl\|plan.json\|board.json\|ResultDirectoryName\|BoardJsonName" -- src tests`
Expected: 出力が空（`morning-plan.json` の fixture 参照は `MorningPlanValidatorTests` の 1 か所だけ残る。それ以外が出たら直す）

- [ ] **Step 8: コミット**

```bash
git add -A
git commit -m "refactor(core): 朝の実行の result/ と board.json をやめる

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: 仕上げ — 無傷の確認と手動確認

**Files:**
- Modify: `docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md`（§11 のチェック）
- 変更なし（確認のみ）: `src/MoTask.Core/Morning/MorningPlanResolver.cs`、`src/MoTask.Core/Services/AiJobService.cs` ほか

**Interfaces:**
- Consumes: Task 1〜7 のすべて
- Produces: なし

- [ ] **Step 1: `MorningPlanResolver` が 1 行も変わっていないことを確かめる**

Run:

```bash
git diff --stat master -- src/MoTask.Core/Morning/MorningPlanResolver.cs src/MoTask.Core/Morning/ResolvedPlan.cs tests/MoTask.Core.Tests/MorningPlanResolverTests.cs
```

Expected: 出力が空（仕様 §13 の完了条件）

- [ ] **Step 2: `AiJob*` 一式も無傷であることを確かめる**

Run:

```bash
git diff --stat master -- src/MoTask.Core/Services/AiJobService.cs src/MoTask.Core/Services/IAiJobService.cs src/MoTask.Core/Model/AiJob.cs src/MoTask.Core/Model/AiJobStatus.cs tests/MoTask.Core.Tests/AiJobServiceStartTests.cs tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs
```

Expected: 出力が空

- [ ] **Step 3: 画面と DB が無傷であることを確かめる**

Run:

```bash
git diff --stat master -- src/MoTask.App/ViewModels src/MoTask.App/Views src/MoTask.Data src/MoTask.Hooks src/MoTask.Mcp src/MoTask.App/Ai/BoardTools
```

Expected: 出力が空（`MorningPlanViewModel` は既存の `RunChanged(candidatesChanged: true)` の経路だけで候補が 1 件ずつ増える）

- [ ] **Step 4: 全ビルド・全テスト**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告（`MoTask.exe` が起動中なら出力コピーの MSB3021/MSB3027 だけは無視してよい）

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 5: 移行の後始末（仕様 §12）**

実機の MoTask を開き、未完了の朝の実行が 1 件でも残っていたら、朝の画面の「追跡をやめる」で片づける。旧契約で走っている claude を新契約へ繋ぎ直す自動処理は作らない。過去のジョブフォルダに残っている `result/` と `board.json` は消さない。

- [ ] **Step 6: 手動確認（仕様 §11 の残り全部）**

`dotnet build` 後に `MoTask.exe` を起こして実施し、仕様 §11 のチェックボックスに印を入れる。

- [ ] 朝の実行を起動すると端末が開き、Windows Terminal の中に出る（conhost の古い窓ではない）
- [ ] 候補が届くたびに、朝の画面の候補キューが 1 件ずつ増える
- [ ] Claude が `morning_complete` を呼ぶと、最後の一言を言い終えてから端末が閉じる
- [ ] 閉じた後、朝の画面にプランと候補が揃っている
- [ ] 候補 0 件の朝でも `complete` が通り、端末が閉じ、画面が「候補なし」になる
- [ ] `evidence` を空にした候補を Claude に投げさせると、理由が返って Claude が次へ進む
- [ ] 実行中に端末を × で閉じると、朝の画面が `Failed`（端末が閉じられました）になる
- [ ] 実行中に MoTask を閉じても端末は残り、MoTask を開き直すと追跡が続く
- [ ] その状態から `morning_complete` まで進めると、掛け直した MoTask が端末を閉じる
- [ ] 「追跡をやめる」では端末が閉じない
- [ ] AI 遂行の端末は従来どおり開いたままで、閉じない
- [ ] AI 設定で `TerminalCommandTemplate` に `wt.exe …` を入れると、朝の実行で注意が出て既定の起動になる
- [ ] 利用者の MCP 登録を外しても、朝の実行は `--mcp-config` のおかげで MoTask のツールを使える

あわせてジョブフォルダの中身が `instruction.md` / `hooks.json` / `events.jsonl` / `run.json` / `mcp.json` の 5 つだけであること（`result/` も `board.json` も無いこと）を目で見る。

- [ ] **Step 7: コミット**

```bash
git add docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md
git commit -m "docs(spec): 朝の実行の MCP 受け渡しの手動確認を実施済みにする

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**仕様の対応（この計画の担当分・§13「2 本目」）**

| 仕様 | 対応するタスク |
| --- | --- |
| §4 ジョブフォルダの中身が 5 ファイルになる | Task 5（`mcp.json` を足す）・Task 7（`result/` と `board.json` を落とす） |
| §4「変更するもの」の表 | Task 1〜7 が 1 行ずつ対応（`MorningToolHost` は Task 4、`MorningResultReader` の解体は Task 1・削除は Task 3） |
| §5.4 `--mcp-config` と朝用の起動プロンプト、`OutputDirectoryName` の扱い | Task 5・Task 7 |
| §6 `morning_get_context` | Task 2（サービス）・Task 4（ツール） |
| §6 `morning_add_candidate` と 6 通りの返し方 | Task 2（受理・不備・重複の全 6 パターンをテストで固定）・Task 4 |
| §6 `morning_submit_plan` | Task 2・Task 4 |
| §6 `morning_complete` | Task 3・Task 4 |
| §6 すべてのツールが `runId` 必須・不一致はツールエラー | Task 2・Task 3（`NotRunning`）・Task 4（`EveryTool_ReturnsAToolError_WhenRunIdIsMissing…`） |
| §7 閉じる瞬間（予約 ＋ 次の `Stop` ＋ 60 秒の保険） | Task 3 |
| §7 `SessionEnd` が complete 無しで来たとき | Task 3 |
| §7 人の操作（`CompleteAsync` は即閉じ・`StopTrackingAsync` は閉じない） | Task 3 |
| §8 `instruction.md` の契約 | Task 6 |
| §9 レイヤの分担 | Task 1（検証は Core の純関数）・Task 4（ホストは JSON ↔ ドメインのみ） |
| §10 テスト方針の 5 行 | Core 純関数=Task 1、Core サービス=Task 2・3、Core 重複=Task 2、App ツール=Task 4、App launcher=Task 5・7 |
| §11 手動確認 | Task 8 Step 6 |
| §12 移行 | Task 8 Step 5 |
| §13 `MorningPlanResolver` 無傷の完了条件 | Task 8 Step 1 |

**1 本目（端末の所有）が担当し、この計画では扱わないもの**

`ISessionLauncher` の所有系 4 つ、`TerminalLauncher` のテンプレート統一と `wt.exe` の落とし込み、`MorningRunDescriptor` の `processId`、`OwnedSessionExited` による `Failed`、`TryReattach` による掛け直し。

**タスク間で使い回す名前（整合の確認）**

`CandidateInput`（Task 1 で定義 → Task 2・Task 4 が使う）、`CandidateOutcome` / `MorningOutcome`（Task 2 で定義 → Task 3・Task 4 が使う）、`NotRunning<T>`（Task 2 で定義 → Task 3 が使う）、`BuildSnapshotAsync`（Task 2 で定義 → Task 7 で `StartAsync` 側の呼び出しだけが消え、`GetContextAsync` が使い続ける）、`MorningPlanValidator.PlanGroupKeys`（Task 1 で定義 → Task 6 のテストが使う）、`JobFolderPaths.McpJson`（Task 5 で定義 → Task 7 のテストが使う）、`CloseGrace` / `PendingClose`（Task 3 で定義 → Task 3 のテストだけが使う）。
