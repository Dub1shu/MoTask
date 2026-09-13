# 朝の実行（1/2）起動の統一と端末の所有 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 朝の実行の端末を MoTask が `cmd.exe` として直接起こして `Process` ハンドルごと所有し、取り込みが終わった時点でその端末を自分で閉じる。あわせて起動テンプレートの `wt.exe` 分岐を落として既定を `cmd.exe` 系に統一する。

**Architecture:** `ISessionLauncher` に「所有する起動」（`LaunchOwned` / `CloseOwned` / `TryReattach` / `OwnedSessionExited`）を足し、`TerminalLauncher` が `ConcurrentDictionary<int, Process>` でハンドルを保持する。`MorningService` は朝の実行だけを所有し、`run.json` に `processId` / `processStartedAt` を残して再起動後に掛け直す。**成果の受け渡しは現行のファイル契約（`result/candidates.jsonl` / `result/plan.json` / `board.json`）のまま**で、閉じる契機は「`Stop` で `result/` が揃って `Ingested` になったら」である。MCP への移行は 2 本目が引き受ける。

**Tech Stack:** .NET 10 / WPF / SQLite / EF Core / CommunityToolkit.Mvvm / xunit + FluentAssertions + NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md`（この計画は §5・§7・§10・§11 の端末まわり、および §13「1 本目」を実装する）

**親仕様:** `docs/superpowers/specs/2026-09-07-motask-morning-plan-triage-design.md`

**作業場所:** ワークツリー `.claude/worktrees/morning-terminal-ownership`、ブランチ `worktree-morning-terminal-ownership`。すべてのコマンドはこのディレクトリで実行する（`superpowers:using-git-worktrees` を先に使うこと）。

---

## Global Constraints

このプランのすべてのタスクに、暗黙にこの節の要求が含まれる。

- **完了条件: `AiJob*` 一式とそのテストが 1 行も壊れないこと**（仕様 §13）。`AiJob` / `AiJobService` / `IAiJobService` / `AiJobStatus` / `AiJobs` テーブル、および `tests/MoTask.Core.Tests/AiJobService*Tests.cs` は変更しない。AI 遂行の起動は `Launch`（所有しない）のままで、端末は閉じない（仕様 §3）
- **成果の受け渡しは変えない。** `MorningResultReader` / `MorningResult` / `CandidateRecord` / `BoardSnapshot` / `MorningInstruction` / `Messages.MorningInstructionDefault` / `Messages.MorningInstructionContractFormat` には触らない。`result/` と `board.json` は今までどおり作る
- **`MoTask.Hooks` / `HookEventParser` / `JobEventWatcher` / `events.jsonl` の契約は変えない**（仕様 §2「含まない」）
- **`BoardToolHost` の 6 本・`MoTaskMcpServer` は変えない。** MCP は 2 本目の仕事
- **`TriageCandidate` / `MorningRun` に列を足さない。マイグレーションは作らない**（仕様 §2）
- **`MorningPlanResolver` / `ResolvedPlan` / 朝の画面のレイアウトは変えない**（仕様 §2）
- **新規 NuGet パッケージを足さない。P/Invoke も使わない**（仕様 §4）。依存方向（App → Core、Core は UI 非依存・EF 非依存）も変えない。`MoTask.Core.csproj` に参照を足さない
- **`Process` を触るのは `src/MoTask.App/Ai/TerminalLauncher.cs` だけ**（仕様 §9）。Core は `ISessionLauncher` の向こう側を知らない
- 利用者向け文言はハードコードしない。Core は `src/MoTask.Core/Resources/Messages.resx`（アクセサ `src/MoTask.Core/Resources/Messages.cs`）、App は `src/MoTask.App/Resources/Strings.resx`（アクセサ `src/MoTask.App/Resources/Strings.cs`）。**resx に値を足したら同じ名前のプロパティを .cs に足す**（`StringsTests` が守っている）
- パッケージのバージョンは `Directory.Packages.props` にあるので `PackageReference` に `Version` を書かない
- テストは xunit + FluentAssertions。アサーションは `Should()` 形式。ビルドは `-p:TreatWarningsAsErrors=true` で警告ゼロ
- **git の扱い:** `git rebase` / `git reset --hard` / 素の `git stash` は使わない。loose object の書き込みが `Permission denied` で落ちたら（ウイルス対策ソフトのロック）**同じコマンドをそのまま再実行する**
- 各タスクの最後に 1 コミット。コミットメッセージは日本語の慣用形で、末尾に必ず次の行を付ける:

  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

### 仕様からの意図的な補足（3 件）

1. **`SessionLaunchRequest` に `CloseOnExit` を足す。** 仕様 §5.2 は「朝の実行のときは `MorningTemplate`」と言うだけで、`BuildCommand` がそれをどう知るかを書いていない。`OutputDirectoryName` が `result` かどうかで判定すると、2 本目が `OutputDirectoryName` を捨てるときに壊れる。明示のフラグを足す
2. **`processStartedAt` は UTC で持つ。** `Process.StartTime` はローカル時刻なので、JSON を往復したときの `DateTimeKind` の食い違いを避けるために `ToUniversalTime()` して書き、照合時も `ToUniversalTime()` して比べる
3. **`wt.exe` テンプレートの注意は `AiSettingsViewModel` が自分で判定する。** 仕様 §5.2 は「AI 設定画面に注意を出す」と言うだけである。起動時の判定結果を画面まで運ぶ配線を足すより、同じ純関数（`TerminalLauncher.IsWindowsTerminalTemplate`）を画面からも呼ぶほうが安い

---

## File Structure

### 変更（Core）

| ファイル | 変更の中身 |
| --- | --- |
| `src/MoTask.Core/Ai/ISessionLauncher.cs` | `LaunchOwned` / `CloseOwned` / `TryReattach` / `OwnedSessionExited` と `OwnedSession` レコードを追加。既存の `Launch` はそのまま |
| `src/MoTask.Core/Ai/SessionLaunchRequest.cs` | `CloseOnExit`（既定 false）を追加 |
| `src/MoTask.Core/Morning/MorningRunDescriptor.cs` | `ProcessId` / `ProcessStartedAt` を追加。`TryParse` を追加 |
| `src/MoTask.Core/Services/MorningService.cs` | `LaunchOwned` での起動、取り込み後の `CloseOwned`、`OwnedSessionExited` の購読、`RecoverOnStartupAsync` での `TryReattach` |
| `src/MoTask.Core/Resources/Messages.resx` / `Messages.cs` | `MorningTerminalClosed` を追加 |

### 変更（App）

| ファイル | 変更の中身 |
| --- | --- |
| `src/MoTask.App/Ai/TerminalLauncher.cs` | テンプレートの統一（`wt.exe` 分岐の削除）、所有起動の実装、`IsWindowsTerminalTemplate`、`IDisposable` |
| `src/MoTask.App/ViewModels/AiSettingsViewModel.cs` | `TemplateNotice` を追加 |
| `src/MoTask.App/Views/AiSettingsDialog.xaml` | 注意の表示を 1 行追加 |
| `src/MoTask.App/Resources/Strings.resx` / `Strings.cs` | `MorningTemplateFallsBackToDefault` を追加 |

### 変更（テスト）

| ファイル | 変更の中身 |
| --- | --- |
| `tests/MoTask.App.Tests/TerminalLauncherTests.cs` | `wt` 分岐のテストを差し替え、朝用テンプレートと所有の契約を足す |
| `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs` | `TemplateNotice` のテストを足す |
| `tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs` | 所有の 3 メソッドと `OwnedSessionExited` の記録・発火 |
| `tests/MoTask.Core.Tests/MorningServiceStartTests.cs` | `run.json` の pid、所有起動になったこと |
| `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs` | 閉じる／閉じない、端末が先に死んだとき、掛け直し |
| `tests/MoTask.Core.Tests/MorningRunDescriptorTests.cs`（新規） | `run.json` の往復 |

### 触らない

`src/MoTask.Core/Morning/MorningResultReader.cs`、`MorningInstruction.cs`、`BoardSnapshot.cs`、`MorningPlanResolver.cs`、`src/MoTask.App/Ai/BoardTools/`、`src/MoTask.App/Ai/MoTaskMcpServer.cs`、`src/MoTask.App/Ai/JobFolder.cs`、`src/MoTask.Core/Services/AiJobService.cs`、`src/MoTask.Data/` 一式。

---

## Task 1: 起動テンプレートを `cmd.exe` 系に統一する

`wt.exe` を挟むと `Process.Start` が返すのは即座に終了する起動役の pid で、掛けどころが無い（仕様 §5「プロセスの掴み方」）。分岐・`{cwd}` の埋め込み・構築子引数を丸ごと落とし、既定を 2 本（AI 遂行 `/k`・朝の実行 `/c`）にする。

**Files:**
- Modify: `src/MoTask.Core/Ai/SessionLaunchRequest.cs`
- Modify: `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs:141-142`
- Test: `tests/MoTask.App.Tests/TerminalLauncherTests.cs`
- Test: `tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceStartTests.cs:93-99`

**Interfaces:**
- Consumes: 既存の `CommandLine.Quote` / `CommandLine.SplitFirstToken`、`Messages.TerminalStartPromptFormat`
- Produces:
  - `SessionLaunchRequest(Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume, string OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName, bool CloseOnExit = false)`
  - `internal const string TerminalLauncher.DefaultTemplate = "cmd.exe /k {command}"`
  - `internal const string TerminalLauncher.MorningTemplate = "cmd.exe /c {command}"`
  - `internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable)`（3 引数版は消える）

- [ ] **Step 1: `SessionLaunchRequest` に `CloseOnExit` を足す**

`src/MoTask.Core/Ai/SessionLaunchRequest.cs` を丸ごと次に置き換える:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// 1 回の端末起動。Resume が true なら --session-id ではなく --resume を渡す。
/// WorkingDirectory は cwd（プロジェクトの作業フォルダ）で、JobFolder とは別（ターミナル AI 仕様 §6）。
/// OutputDirectoryName は起動プロンプトで「成果物をここに出して」と伝える先。AI 遂行は
/// artifacts/、朝の実行は result/ と、JobFolderRequest.OutputDirectoryName に合わせて呼び分ける
/// （既定を artifacts のままにして、AiJobService の呼び出しを変えずに済ませる）。
/// CloseOnExit は「claude が終わったら窓も畳むか」。朝の実行だけが true で、既定テンプレートが
/// cmd.exe /k ではなく cmd.exe /c になる（MCP 受け渡し仕様 §5.2）。
/// </summary>
public sealed record SessionLaunchRequest(
    Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume,
    string OutputDirectoryName = JobFolderPaths.ArtifactsDirectoryName,
    bool CloseOnExit = false);
```

- [ ] **Step 2: `TerminalLauncherTests` の `wt` 前提を書き換える（失敗するテスト）**

`tests/MoTask.App.Tests/TerminalLauncherTests.cs` に対して次の 4 点を行う。

(a) `Launcher` ヘルパーから `hasWt` を落とす:

```csharp
    private TerminalLauncher Launcher() => new(_store, pathVariable: "");
```

(b) `BuildCommand_UsesWindowsTerminalWithTheProjectAsCwd` を次の 2 本で置き換える:

```csharp
    /// <summary>AI 遂行は cmd.exe /k。claude が終わってもシェルが残る（続けて打てる）。</summary>
    [Fact]
    public void BuildCommand_UsesCmdWithSlashK_ForAnAiJob()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/k ");
        // 既定テンプレートに {cwd} は現れない。cwd は ProcessStartInfo 側で渡す
        command.Arguments.Should().NotContain(@"-d ""D:\repo\sample""");
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
    }

    /// <summary>朝の実行は cmd.exe /c。claude が終われば窓も畳む（仕様 §5.2）。</summary>
    [Fact]
    public void BuildCommand_UsesCmdWithSlashC_ForAMorningRun()
    {
        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/c ");
        command.Arguments.Should().Contain("--session-id");
    }
```

(c) `BuildCommand_FallsBackToCmdWhenWindowsTerminalIsMissing` を**削除する**（逃げ道そのものが既定になったので意味が無い）。

(d) `BuildCommand_DoublesATrailingBackslashInCwdSoTheTemplateQuoteStillCloses` を、既定に `{cwd}` が無くなったぶん**利用者定義テンプレート**で試す形に書き換える:

```csharp
    [Fact]
    public void BuildCommand_DoublesATrailingBackslashInCwdSoTheTemplateQuoteStillCloses()
    {
        // ドライブ直下（D:\）も正当な作業ディレクトリ。末尾の \ をそのまま埋めると
        // テンプレートの閉じ " が \" と解釈され、以降が丸ごと 1 引数に飲み込まれてしまう。
        // 既定テンプレートに {cwd} は無くなったので、置換の規則は利用者定義テンプレートで固定する。
        _store.Save(_store.Load() with { TerminalCommandTemplate = "pwsh.exe -d \"{cwd}\" -Command {command}" });

        var command = Launcher().BuildCommand(_request with { WorkingDirectory = @"D:\" }).Value!;

        command.Arguments.Should().StartWith("-d \"D:\\\\\" -Command ");
        command.Arguments.Should().Contain("--session-id");
    }
```

- [ ] **Step 3: テストを走らせて、狙いどおり落ちることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: コンパイルエラー（`CloseOnExit` がまだ無い／`Launcher()` の引数が合わない）で FAIL

- [ ] **Step 4: `TerminalLauncher` からテンプレート分岐を落とす**

`src/MoTask.App/Ai/TerminalLauncher.cs` の先頭から構築子までを次で置き換える:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 端末で claude を起こす（ターミナル AI 仕様 §7）。AI 遂行は起こして手放し、朝の実行は
/// Process ハンドルごと所有する（MCP 受け渡し仕様 §5）。Process を触るのはこのクラスだけ。
/// </summary>
public sealed class TerminalLauncher : ISessionLauncher
{
    /// <summary>AI 遂行の既定。claude が終わってもシェルを残す（続けて打てる）。</summary>
    internal const string DefaultTemplate = "cmd.exe /k {command}";

    /// <summary>朝の実行の既定。claude が終われば窓も畳む（仕様 §5.2）。</summary>
    internal const string MorningTemplate = "cmd.exe /c {command}";

    private readonly IAiSettingsStore _settings;
    private readonly string? _pathVariable;

    public TerminalLauncher(IAiSettingsStore settings)
        : this(settings, null)
    {
    }

    internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable)
    {
        _settings = settings;
        _pathVariable = pathVariable;
    }
```

`BuildCommand` の中のテンプレート選択（現行の `var template = settings.TerminalCommandTemplate is { Length: > 0 } configured ? configured : _hasWindowsTerminal() ? WindowsTerminalTemplate : FallbackTemplate;`）を次で置き換える:

```csharp
        var template = settings.TerminalCommandTemplate is { Length: > 0 } configured
            ? configured
            : request.CloseOnExit ? MorningTemplate : DefaultTemplate;
```

ファイル末尾の `private static string? FindWindowsTerminal()` メソッドを**丸ごと削除する**。`WindowsTerminalTemplate` と `FallbackTemplate` の 2 つの定数も上の置き換えで消えている。

- [ ] **Step 5: `MorningService` が朝のテンプレートを頼むようにする**

`src/MoTask.Core/Services/MorningService.cs` の `StartAsync` の中、`_launcher.BuildCommand(...)` の呼び出しを次で置き換える:

```csharp
            var command = _launcher.BuildCommand(new SessionLaunchRequest(
                sessionId, root, root, Resume: false,
                OutputDirectoryName: JobFolderPaths.ResultDirectoryName, CloseOnExit: true));
```

- [ ] **Step 6: `FakeSessionLauncher` の組み立て結果を `cmd.exe` にする**

`tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs` の `BuildCommand` を次で置き換える:

```csharp
    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        Requests.Add(request);
        if (BuildFailure is { } failure) return Result.Fail<TerminalCommand>(failure.Error!);
        var switches = request.CloseOnExit ? "/c" : "/k";
        return Result.Ok(new TerminalCommand("cmd.exe", $"{switches} claude", request.WorkingDirectory));
    }
```

- [ ] **Step 7: `run.json` の起動コマンドを見ているテストを直す**

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs` の `Start_WritesRunJsonWithTheLaunchCommand` の最後の行を置き換える:

```csharp
        root.GetProperty("launchCommand").GetString().Should().StartWith("cmd.exe /c ");
```

同じファイルに、朝の実行が「畳む側」で頼んでいることを固定するテストを足す:

```csharp
    /// <summary>朝の実行は claude が終われば窓も畳む形で頼む（仕様 §5.2）。</summary>
    [Fact]
    public async Task Start_AsksForATerminalThatClosesWhenClaudeExits()
    {
        await _service.StartAsync();

        _launcher.Requests.Should().ContainSingle().Which.CloseOnExit.Should().BeTrue();
    }
```

- [ ] **Step 7b: 共有フェイクの既定を見ている AI 遂行のテストを 1 行直す**

`FakeSessionLauncher` は `MorningService*Tests` と `AiJobService*Tests` の共有物なので、Step 6 で既定出力が
`wt.exe` から `cmd.exe` に変わると AI 遂行側の 1 本が割れる。仕様 §5.2 は AI 遂行の既定も `cmd.exe /k` に
すると決めているので、これは仕様どおりの帰結である。`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs`
の `Start_WritesJobJsonWithTheLaunchCommand` の 1 行を置き換える:

```csharp
            .Which.LaunchCommand.Should().StartWith("cmd.exe ");
```

このファイルで直すのはこの 1 行だけである。`tests/MoTask.Core.Tests/AiJobServiceStartTests.cs:159` の
`wt.exe` はテストが自前で失敗文字列を渡しているので触らない。

- [ ] **Step 8: テストを走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: PASS

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceStartTests|FullyQualifiedName~AiJobServiceLifecycleTests" -nologo -v q`
Expected: PASS

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告（`MoTask.exe` が起動中なら出力コピーの MSB3021/MSB3027 だけは無視してよい。コンパイルエラーは不可）

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.Core/Ai/SessionLaunchRequest.cs src/MoTask.App/Ai/TerminalLauncher.cs src/MoTask.Core/Services/MorningService.cs tests/MoTask.App.Tests/TerminalLauncherTests.cs tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs tests/MoTask.Core.Tests/MorningServiceStartTests.cs
git commit -m "refactor(app): 起動テンプレートを cmd.exe 系に統一し wt.exe 分岐を落とす

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: 利用者テンプレートが `wt.exe` 始まりなら朝の実行では既定に落とす

`powershell.exe -NoExit -Command {command}` や WSL のテンプレートは先頭プロセスがウィンドウの持ち主なので朝の実行でもそのまま使える。`wt.exe` だけが使えない（仕様 §5.2）。

**Files:**
- Modify: `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `src/MoTask.App/ViewModels/AiSettingsViewModel.cs`
- Modify: `src/MoTask.App/Views/AiSettingsDialog.xaml:46-47`
- Modify: `src/MoTask.App/Resources/Strings.resx`
- Modify: `src/MoTask.App/Resources/Strings.cs`
- Test: `tests/MoTask.App.Tests/TerminalLauncherTests.cs`
- Test: `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1 の `TerminalLauncher.MorningTemplate` と `SessionLaunchRequest.CloseOnExit`
- Produces:
  - `internal static bool TerminalLauncher.IsWindowsTerminalTemplate(string? template)`
  - `AiSettingsViewModel.TemplateNotice`（`string?`。注意が無ければ null）
  - `Strings.MorningTemplateFallsBackToDefault`

- [ ] **Step 1: 失敗するテストを書く（launcher 側）**

`tests/MoTask.App.Tests/TerminalLauncherTests.cs` に足す:

```csharp
    /// <summary>
    /// wt を経由すると Process.Start が返すのは即座に終了する起動役の pid で、完了時に
    /// 端末を閉じられない。朝の実行のときだけ既定に落とす（仕様 §5.2）。
    /// </summary>
    [Theory]
    [InlineData("wt.exe -d \"{cwd}\" cmd /k {command}")]
    [InlineData("WT.EXE -d \"{cwd}\" cmd /k {command}")]
    [InlineData("\"C:\\Program Files\\WindowsApps\\wt.exe\" -d \"{cwd}\" cmd /k {command}")]
    public void BuildCommand_FallsBackToTheMorningDefault_WhenTheTemplateStartsWithWindowsTerminal(string template)
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = template });

        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/c ");
    }

    /// <summary>AI 遂行では利用者のテンプレートをそのまま使う（閉じる必要が無い）。</summary>
    [Fact]
    public void BuildCommand_KeepsAWindowsTerminalTemplate_ForAnAiJob()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}" });

        Launcher().BuildCommand(_request).Value!.FileName.Should().Be("wt.exe");
    }

    /// <summary>先頭プロセスがウィンドウの持ち主なら、朝の実行でもそのまま使える（仕様 §5.2）。</summary>
    [Fact]
    public void BuildCommand_KeepsAPowerShellTemplate_ForAMorningRun()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "powershell.exe -NoExit -Command {command}" });

        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("powershell.exe");
        command.Arguments.Should().StartWith("-NoExit -Command ");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("wt.exe -d \"{cwd}\" cmd /k {command}", true)]
    [InlineData("\"C:\\x\\wt.exe\" {command}", true)]
    [InlineData("wt {command}", false)]
    [InlineData("pwsh.exe -Command wt.exe {command}", false)]
    public void IsWindowsTerminalTemplate_LooksOnlyAtTheFirstTokensFileName(string? template, bool expected)
    {
        TerminalLauncher.IsWindowsTerminalTemplate(template).Should().Be(expected);
    }
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: コンパイルエラー（`IsWindowsTerminalTemplate` が無い）で FAIL

- [ ] **Step 3: `TerminalLauncher` に判定と落とし込みを足す**

`src/MoTask.App/Ai/TerminalLauncher.cs` の `MorningTemplate` 定数の直後に足す:

```csharp
    /// <summary>
    /// 起動テンプレートの先頭トークンのファイル名が wt.exe か（フルパスも同じ扱い・大文字小文字は無視）。
    /// 朝の実行はこのテンプレートを使えないので既定に落とし、AI 設定画面に注意を出す（仕様 §5.2）。
    /// </summary>
    internal static bool IsWindowsTerminalTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        var (fileName, _) = CommandLine.SplitFirstToken(template);
        return fileName.Length > 0
               && string.Equals(Path.GetFileName(fileName), "wt.exe", StringComparison.OrdinalIgnoreCase);
    }
```

Task 1 Step 4 で入れたテンプレート選択を次で置き換える:

```csharp
        // 利用者定義のテンプレートは AI 遂行でも朝の実行でも効く。ただし朝の実行で wt を挟むと
        // 掴めるのが起動役の pid になり、完了時に閉じられない。そこだけ既定に落とす（仕様 §5.2）。
        var configured = settings.TerminalCommandTemplate is { Length: > 0 } text
                         && !(request.CloseOnExit && IsWindowsTerminalTemplate(text))
            ? text
            : null;
        var template = configured ?? (request.CloseOnExit ? MorningTemplate : DefaultTemplate);
```

- [ ] **Step 4: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: PASS

- [ ] **Step 5: 文言を resx とアクセサに足す**

`src/MoTask.App/Resources/Strings.resx` の末尾（`</root>` の直前）に足す:

```xml
  <data name="MorningTemplateFallsBackToDefault" xml:space="preserve"><value>wt.exe を経由すると完了時に端末を閉じられないので、朝の実行では既定の起動（cmd.exe /c）を使います。AI 遂行にはこのテンプレートがそのまま効きます。</value></data>
```

`src/MoTask.App/Resources/Strings.cs` の末尾の閉じ括弧の直前に足す:

```csharp
    public static string MorningTemplateFallsBackToDefault => Get(nameof(MorningTemplateFallsBackToDefault));
```

- [ ] **Step 6: 失敗するテストを書く（VM 側）**

`tests/MoTask.App.Tests/AiSettingsViewModelTests.cs` に足す:

```csharp
    [Fact]
    public void TemplateNotice_IsNullForAnOrdinaryTemplate()
    {
        var vm = new AiSettingsViewModel(new StubSettingsStore(AiSettings.Default()));

        vm.TemplateNotice.Should().BeNull();

        vm.TerminalCommandTemplate = "powershell.exe -NoExit -Command {command}";
        vm.TemplateNotice.Should().BeNull();
    }

    /// <summary>朝の実行では使えないテンプレートなので、保存前から画面で知らせる（仕様 §5.2）。</summary>
    [Fact]
    public void TemplateNotice_AppearsWhileTypingAWindowsTerminalTemplate()
    {
        var vm = new AiSettingsViewModel(new StubSettingsStore(AiSettings.Default()));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TerminalCommandTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}";

        vm.TemplateNotice.Should().Be(Strings.MorningTemplateFallsBackToDefault);
        raised.Should().Contain(nameof(AiSettingsViewModel.TemplateNotice));
    }
```

このファイルに `StubSettingsStore` に相当する差し替えストアが無ければ、テストクラスの中に足す:

```csharp
    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }
```

必要な `using`（`MoTask.App.Ai`・`MoTask.App.Resources`・`MoTask.App.ViewModels`・`MoTask.Core.Ai`・`FluentAssertions`・`Xunit`）が揃っているか確認する。

- [ ] **Step 7: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~AiSettingsViewModelTests" -nologo -v q`
Expected: コンパイルエラー（`TemplateNotice` が無い）で FAIL

- [ ] **Step 8: `AiSettingsViewModel` に `TemplateNotice` を足す**

`src/MoTask.App/ViewModels/AiSettingsViewModel.cs` の `using` に `MoTask.App.Ai;` を足し、`_terminalCommandTemplate` の宣言を置き換える:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemplateNotice))]
    private string _terminalCommandTemplate = "";
```

`PermissionModes` プロパティの直後に足す:

```csharp
    /// <summary>
    /// wt.exe 始まりのテンプレートへの注意（仕様 §5.2）。朝の実行では既定の起動に落ちる。
    /// 無ければ null（画面は NullToVisibility で隠す）。
    /// </summary>
    public string? TemplateNotice => TerminalLauncher.IsWindowsTerminalTemplate(TerminalCommandTemplate)
        ? Strings.MorningTemplateFallsBackToDefault
        : null;
```

- [ ] **Step 9: 画面に 1 行足す**

`src/MoTask.App/Views/AiSettingsDialog.xaml` の 47 行目（`TerminalCommandTemplate` の `TextBox`）の直後に足す:

```xml
        <TextBlock Text="{Binding TemplateNotice}" Foreground="{StaticResource Brush.TextMuted}" TextWrapping="Wrap"
                   Visibility="{Binding TemplateNotice, Converter={StaticResource NullToVisibility}}" />
```

- [ ] **Step 10: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~AiSettingsViewModelTests|FullyQualifiedName~StringsTests" -nologo -v q`
Expected: PASS

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

- [ ] **Step 11: コミット**

```bash
git add src/MoTask.App/Ai/TerminalLauncher.cs src/MoTask.App/ViewModels/AiSettingsViewModel.cs src/MoTask.App/Views/AiSettingsDialog.xaml src/MoTask.App/Resources/Strings.resx src/MoTask.App/Resources/Strings.cs tests/MoTask.App.Tests/TerminalLauncherTests.cs tests/MoTask.App.Tests/AiSettingsViewModelTests.cs
git commit -m "feat(app): wt.exe 始まりのテンプレートは朝の実行で既定に落として注意を出す

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `ISessionLauncher` に「所有する起動」を足し、`TerminalLauncher` が実装する

`Process` ハンドルを**開いたままにする**のが要点で、Windows は開いているハンドルのある pid を再利用しないため、「死んだ後に同じ pid の別プロセスを殺す」事故が起きない（仕様 §5.3）。

**Files:**
- Modify: `src/MoTask.Core/Ai/ISessionLauncher.cs`
- Modify: `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs`
- Test: `tests/MoTask.App.Tests/TerminalLauncherTests.cs`

**Interfaces:**
- Consumes: Task 1 の `TerminalLauncher` 構築子、`Messages.TerminalLaunchFailedFormat`
- Produces（以降のタスクがこの名前と型に依存する）:
  - `public sealed record OwnedSession(int ProcessId, DateTime StartedAt)` — `StartedAt` は **UTC**
  - `Result<OwnedSession> ISessionLauncher.LaunchOwned(int ownerId, TerminalCommand command)`
  - `void ISessionLauncher.CloseOwned(int ownerId)`
  - `bool ISessionLauncher.TryReattach(int ownerId, int processId, DateTime startedAt)`
  - `event EventHandler<int>? ISessionLauncher.OwnedSessionExited`
  - `FakeSessionLauncher.LaunchedOwned` / `.Closed` / `.Reattached` / `.ReattachSucceeds` / `.Session` / `.RaiseExited(int)`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/TerminalLauncherTests.cs` の先頭の `using` に `using System.Diagnostics;` を足し、クラスの末尾（`Dispose()` の直前）に足す:

```csharp
    [Fact]
    public void CloseOwned_IgnoresAnOwnerItNeverLaunched()
    {
        var launcher = Launcher();

        launcher.Invoking(l => l.CloseOwned(4242)).Should().NotThrow("知らない ownerId は黙って無視する");
    }

    [Fact]
    public void Dispose_DoesNotThrowWhenNothingIsOwned()
    {
        Launcher().Invoking(l => l.Dispose()).Should().NotThrow();
    }

    [Fact]
    public void TryReattach_FailsWhenThereIsNoSuchProcess()
    {
        Launcher().TryReattach(1, 0, DateTime.UtcNow).Should().BeFalse("pid が記録されていない");
        Launcher().TryReattach(1, int.MaxValue, DateTime.UtcNow).Should().BeFalse("そんな pid は居ない");
    }

    /// <summary>
    /// pid は再利用される。開始時刻が合わないなら無関係のプロセスなので掴んではいけない（仕様 §7）。
    /// テストプロセス自身は必ず生きているので、偽の開始時刻で拒否されることだけを確かめる。
    /// </summary>
    [Fact]
    public void TryReattach_FailsWhenTheStartTimeDoesNotMatch()
    {
        using var self = Process.GetCurrentProcess();

        Launcher().TryReattach(1, self.Id, self.StartTime.ToUniversalTime().AddSeconds(1)).Should().BeFalse();
    }

    /// <summary>掛け直しても殺さない。Dispose はハンドルを手放すだけである（仕様 §5.3）。</summary>
    [Fact]
    public void TryReattach_SucceedsWhenThePidAndStartTimeMatch()
    {
        using var self = Process.GetCurrentProcess();
        var launcher = Launcher();

        launcher.TryReattach(1, self.Id, self.StartTime.ToUniversalTime()).Should().BeTrue();

        launcher.Invoking(l => l.Dispose()).Should().NotThrow();
    }
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: コンパイルエラー（`CloseOwned` / `TryReattach` / `Dispose` が無い）で FAIL

- [ ] **Step 3: `ISessionLauncher` に契約を足す**

`src/MoTask.Core/Ai/ISessionLauncher.cs` を丸ごと次に置き換える:

```csharp
namespace MoTask.Core.Ai;

/// <summary>端末で claude を起こす口（App の TerminalLauncher）。</summary>
public interface ISessionLauncher
{
    /// <summary>ジョブ開始前の事前確認。claude が見つからなければ Fail(Messages.ClaudeNotFound)。</summary>
    Result CheckAvailable();

    /// <summary>起動コマンドを組み立てる。実行はしない（テストはここだけを見る）。</summary>
    Result<TerminalCommand> BuildCommand(SessionLaunchRequest request);

    /// <summary>所有しない起動。ハンドルはその場で捨てる（AI 遂行）。</summary>
    Result Launch(TerminalCommand command);

    /// <summary>所有する起動。ownerId に紐づけて Process を保持する（朝の実行）。</summary>
    Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command);

    /// <summary>プロセスツリーごと終了させる。知らない ownerId は黙って無視する。</summary>
    void CloseOwned(int ownerId);

    /// <summary>
    /// 再起動後に掛け直す。生きていて開始時刻（UTC）が一致すれば true。
    /// false なら「閉じる能力」だけを諦める。実行の追跡そのものは続けてよい。
    /// </summary>
    bool TryReattach(int ownerId, int processId, DateTime startedAt);

    /// <summary>
    /// 所有しているプロセスが終わった。渡すのは ownerId だけで、理由は問わない。
    /// CloseOwned で自分から閉じた分は上がらない（意図した終了なので）。
    /// </summary>
    event EventHandler<int>? OwnedSessionExited;
}

/// <summary>所有した端末 1 本。StartedAt は UTC（pid の再利用を見分けるための照合材料）。</summary>
public sealed record OwnedSession(int ProcessId, DateTime StartedAt);
```

- [ ] **Step 4: `TerminalLauncher` に所有を実装する**

`src/MoTask.App/Ai/TerminalLauncher.cs` の 1 行目に `using System.Collections.Concurrent;` を足し、クラス宣言を `public sealed class TerminalLauncher : ISessionLauncher, IDisposable` に変える。フィールド宣言（`_pathVariable` の直後）に足す:

```csharp
    /// <summary>
    /// 所有しているプロセス。ハンドルを開いたままにするのが要点で、Windows は開いている
    /// ハンドルのある pid を再利用しないため、「死んだ後に同じ pid の別プロセスを殺す」
    /// 事故が起きない（仕様 §5.3）。
    /// </summary>
    private readonly ConcurrentDictionary<int, Process> _owned = new();
```

既存の `Launch` メソッドの直後に足す:

```csharp
    public event EventHandler<int>? OwnedSessionExited;

    public Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command)
    {
        try
        {
            // UseShellExecute = true で自前のウィンドウを持たせる（Launch と同じ）。ハンドルは捨てずに持つ。
            var started = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = command.WorkingDirectory,
            });
            if (started is null)
            {
                return Result.Fail<OwnedSession>(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
            }
            var session = new OwnedSession(started.Id, started.StartTime.ToUniversalTime());
            Track(ownerId, started);
            return Result.Ok(session);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 何で失敗したかより「何を実行しようとしたか」が要る（ターミナル AI 仕様 §12）
            return Result.Fail<OwnedSession>(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
        }
    }

    public void CloseOwned(int ownerId)
    {
        // 先に辞書から外す。Kill が起こす Exited は「こちらが意図した終了」なので、
        // OnExited の TryRemove が空振りして OwnedSessionExited には流れない。
        if (!_owned.TryRemove(ownerId, out var process)) return;
        try
        {
            // cmd / claude / MoTask.Mcp.exe をまとめて落とす。終了コードに依存しないので確実に窓が消える。
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // すでに終了している。目的は窓が消えることなので、消えているならそれでよい（仕様 §5.3）。
        }
        finally
        {
            process.Dispose();
        }
    }

    public bool TryReattach(int ownerId, int processId, DateTime startedAt)
    {
        if (processId <= 0) return false;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            // pid は再利用される。開始時刻が一致しなければ無関係のプロセスなので掴まない（仕様 §7）。
            if (process.HasExited || process.StartTime.ToUniversalTime() != startedAt)
            {
                process.Dispose();
                return false;
            }
            Track(ownerId, process);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return false;
        }
    }

    private void Track(int ownerId, Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(ownerId);
        if (_owned.TryRemove(ownerId, out var previous)) previous.Dispose();
        _owned[ownerId] = process;
        // 登録し終える前に死んでいた場合、Exited は _owned に居ない ownerId を見て黙って降りている。
        // 取りこぼさないようにここで拾い直す（OnExited は TryRemove のおかげで 1 度しか通らない）。
        if (process.HasExited) OnExited(ownerId);
    }

    private void OnExited(int ownerId)
    {
        if (!_owned.TryRemove(ownerId, out var process)) return;
        OwnedSessionExited?.Invoke(this, ownerId);
        process.Dispose();
    }

    /// <summary>
    /// MoTask を閉じたときに来る。ハンドルを解放するだけで端末は殺さない（仕様 §3・§5.3）。
    /// 実行の途中でアプリを閉じただけで仕事を潰さない。
    /// </summary>
    public void Dispose()
    {
        foreach (var ownerId in _owned.Keys)
        {
            if (_owned.TryRemove(ownerId, out var process)) process.Dispose();
        }
    }
```

- [ ] **Step 5: `FakeSessionLauncher` を追随させる**

`tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs` を丸ごと次に置き換える:

```csharp
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>実起動はしない。組み立てた要求を記録して、成否だけテストが決める。</summary>
public sealed class FakeSessionLauncher : ISessionLauncher
{
    public Result Availability { get; set; } = Result.Ok();
    public Result? BuildFailure { get; set; }
    /// <summary>Launch / LaunchOwned のどちらも、これが入っていれば失敗する。</summary>
    public Result? LaunchFailure { get; set; }
    public List<SessionLaunchRequest> Requests { get; } = new();
    public List<TerminalCommand> Launched { get; } = new();

    /// <summary>所有して起こした分（ownerId 付き）。</summary>
    public List<(int OwnerId, TerminalCommand Command)> LaunchedOwned { get; } = new();

    /// <summary>CloseOwned を呼ばれた ownerId の履歴（順序どおり）。</summary>
    public List<int> Closed { get; } = new();

    /// <summary>TryReattach を頼まれた材料の履歴。</summary>
    public List<(int OwnerId, int ProcessId, DateTime StartedAt)> Reattached { get; } = new();

    /// <summary>TryReattach の戻り値。掛け直しに失敗する筋をテストが作れる。</summary>
    public bool ReattachSucceeds { get; set; } = true;

    /// <summary>LaunchOwned が返す pid と開始時刻。</summary>
    public OwnedSession Session { get; set; } = new(4242, new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc));

    public event EventHandler<int>? OwnedSessionExited;

    public Result CheckAvailable() => Availability;

    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        Requests.Add(request);
        if (BuildFailure is { } failure) return Result.Fail<TerminalCommand>(failure.Error!);
        var switches = request.CloseOnExit ? "/c" : "/k";
        return Result.Ok(new TerminalCommand("cmd.exe", $"{switches} claude", request.WorkingDirectory));
    }

    public Result Launch(TerminalCommand command)
    {
        if (LaunchFailure is { } failure) return failure;
        Launched.Add(command);
        return Result.Ok();
    }

    public Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command)
    {
        if (LaunchFailure is { } failure) return Result.Fail<OwnedSession>(failure.Error!);
        Launched.Add(command);
        LaunchedOwned.Add((ownerId, command));
        return Result.Ok(Session);
    }

    public void CloseOwned(int ownerId) => Closed.Add(ownerId);

    public bool TryReattach(int ownerId, int processId, DateTime startedAt)
    {
        Reattached.Add((ownerId, processId, startedAt));
        return ReattachSucceeds;
    }

    /// <summary>端末が先に死んだことにする（人が × で閉じた・claude が落ちた）。</summary>
    public void RaiseExited(int ownerId) => OwnedSessionExited?.Invoke(this, ownerId);
}
```

- [ ] **Step 6: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TerminalLauncherTests" -nologo -v q`
Expected: PASS（`TryReattach` の 4 本を含む）

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS（`MorningService` はまだ `Launch` を使っているので挙動は変わらない）

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core/Ai/ISessionLauncher.cs src/MoTask.App/Ai/TerminalLauncher.cs tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs tests/MoTask.App.Tests/TerminalLauncherTests.cs
git commit -m "feat(core): ISessionLauncher に所有する起動と掛け直しの契約を足す

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `MorningService` が朝の実行を所有し、`run.json` に pid を残す

**Files:**
- Modify: `src/MoTask.Core/Morning/MorningRunDescriptor.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs:168-172`
- Create: `tests/MoTask.Core.Tests/MorningRunDescriptorTests.cs`
- Test: `tests/MoTask.Core.Tests/MorningServiceStartTests.cs`

**Interfaces:**
- Consumes: Task 3 の `ISessionLauncher.LaunchOwned` / `OwnedSession`
- Produces:
  - `MorningRunDescriptor(int RunId, DateOnly Date, Guid SessionId, string JobFolder, string LaunchCommand, DateTime StartedAt, int ProcessId = 0, DateTime ProcessStartedAt = default)`
  - `static MorningRunDescriptor? MorningRunDescriptor.TryParse(string? json)`

- [ ] **Step 1: 失敗するテストを書く（`run.json` の往復）**

`tests/MoTask.Core.Tests/MorningRunDescriptorTests.cs` を新規に作る:

```csharp
using FluentAssertions;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// run.json（仕様 §7）。再起動した MoTask はこれを読んで端末に掛け直すので、
/// pid と開始時刻が往復することが要る。
/// </summary>
public class MorningRunDescriptorTests
{
    private static readonly MorningRunDescriptor Sample = new(
        7, new DateOnly(2026, 9, 13), new Guid("6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77"),
        @"C:\work\morning\0007-2026-09-13", "cmd.exe /c claude", new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc),
        ProcessId: 12345, ProcessStartedAt: new DateTime(2026, 9, 13, 6, 0, 1, DateTimeKind.Utc));

    [Fact]
    public void Serialize_ThenTryParse_RoundTripsEveryField()
    {
        var parsed = MorningRunDescriptor.TryParse(MorningRunDescriptor.Serialize(Sample));

        parsed.Should().NotBeNull();
        parsed!.RunId.Should().Be(7);
        parsed.Date.Should().Be(new DateOnly(2026, 9, 13));
        parsed.SessionId.Should().Be(Sample.SessionId);
        parsed.JobFolder.Should().Be(Sample.JobFolder);
        parsed.LaunchCommand.Should().Be("cmd.exe /c claude");
        parsed.ProcessId.Should().Be(12345);
        parsed.ProcessStartedAt.Should().Be(Sample.ProcessStartedAt);
    }

    /// <summary>旧い run.json には pid が無い。掛け直しを諦めるだけで、読めなくはない。</summary>
    [Fact]
    public void TryParse_AcceptsAFileWrittenBeforeTheProcessIdExisted()
    {
        var json = """
            {"runId":3,"date":"2026-09-07","sessionId":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77",
             "jobFolder":"C:\\work\\morning\\0003-2026-09-07","launchCommand":"wt.exe ...",
             "startedAt":"2026-09-07T06:00:00Z"}
            """;

        var parsed = MorningRunDescriptor.TryParse(json);

        parsed.Should().NotBeNull();
        parsed!.ProcessId.Should().Be(0, "掛け直しの材料が無いことは 0 で表す");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    public void TryParse_ReturnsNullForAnythingItCannotRead(string? json)
    {
        MorningRunDescriptor.TryParse(json).Should().BeNull();
    }
}
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningRunDescriptorTests" -nologo -v q`
Expected: コンパイルエラー（`TryParse` と `ProcessId` が無い）で FAIL

- [ ] **Step 3: `MorningRunDescriptor` に pid と `TryParse` を足す**

`src/MoTask.Core/Morning/MorningRunDescriptor.cs` を丸ごと次に置き換える:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Morning;

/// <summary>
/// run.json の中身（親仕様 §6）。job.json 相当で、DB が壊れてもフォルダだけで何の実行か分かるように残す。
/// ProcessId / ProcessStartedAt は再起動後に端末へ掛け直すための材料（MCP 受け渡し仕様 §7）。
/// ProcessStartedAt は UTC。0 / default は「掛け直せる材料が無い」を意味する。
/// </summary>
public sealed record MorningRunDescriptor(
    int RunId,
    DateOnly Date,
    Guid SessionId,
    string JobFolder,
    string LaunchCommand,
    DateTime StartedAt,
    int ProcessId = 0,
    DateTime ProcessStartedAt = default)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(MorningRunDescriptor descriptor)
        => JsonSerializer.Serialize(descriptor, Options);

    /// <summary>読めない・壊れている・オブジェクトでないときは null（呼び手は掛け直しを諦める）。</summary>
    public static MorningRunDescriptor? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MorningRunDescriptor>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningRunDescriptorTests" -nologo -v q`
Expected: PASS

- [ ] **Step 5: 失敗するテストを書く（所有起動）**

`tests/MoTask.Core.Tests/MorningServiceStartTests.cs` に足す:

```csharp
    /// <summary>完了時に窓を閉じるには Process ハンドルが要る（仕様 §5.3）。</summary>
    [Fact]
    public async Task Start_OwnsTheTerminalItOpened()
    {
        var run = (await _service.StartAsync()).Value!;

        var owned = _launcher.LaunchedOwned.Should().ContainSingle().Subject;
        owned.OwnerId.Should().Be(run.Id, "ownerId は runId（宛先を取り違えない）");
        owned.Command.FileName.Should().Be("cmd.exe");
    }

    /// <summary>再起動した MoTask はここから掛け直す（仕様 §7）。</summary>
    [Fact]
    public async Task Start_WritesTheProcessIdAndStartTimeIntoRunJson()
    {
        _launcher.Session = new OwnedSession(31337, new DateTime(2026, 9, 13, 6, 0, 1, DateTimeKind.Utc));

        var run = (await _service.StartAsync()).Value!;

        var text = _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName);
        var descriptor = MorningRunDescriptor.TryParse(text);
        descriptor.Should().NotBeNull();
        descriptor!.ProcessId.Should().Be(31337);
        descriptor.ProcessStartedAt.Should().Be(new DateTime(2026, 9, 13, 6, 0, 1, DateTimeKind.Utc));
    }
```

`using MoTask.Core.Morning;` がこのファイルの `using` に無ければ足す。

- [ ] **Step 6: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceStartTests" -nologo -v q`
Expected: `Start_OwnsTheTerminalItOpened` が FAIL（`LaunchedOwned` が空）、`Start_WritesTheProcessIdAndStartTimeIntoRunJson` が FAIL（`ProcessId` が 0）

- [ ] **Step 7: `MorningService.StartAsync` を所有起動に変える**

`src/MoTask.Core/Services/MorningService.cs` の現行 168〜172 行（`_folder.WriteText(... RunJsonName ...)` と `var launched = _launcher.Launch(...)` の 2 か所）を、順序ごと次で置き換える:

```csharp
            // 朝の実行は MoTask が所有する。完了時に窓を閉じるには Process ハンドルが要る（仕様 §5.3）。
            var owned = _launcher.LaunchOwned(run.Id, command.Value!);
            if (!owned.IsSuccess) return await FailAsync(run, owned.Error!).ConfigureAwait(false);

            // 掛け直しの材料（pid と開始時刻）は起動できてからでないと書けないので、run.json は
            // 起動の後に書く。起動に失敗した実行はその場で Failed になり、追いかける先も無い。
            _folder.WriteText(root, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
                new MorningRunDescriptor(run.Id, date, sessionId, root, command.Value!.Display, now,
                    owned.Value!.ProcessId, owned.Value!.StartedAt)));
```

- [ ] **Step 8: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceStartTests" -nologo -v q`
Expected: PASS（`Start_MarksTheRunFailed_WhenTheTerminalWillNotOpen` も引き続き PASS。`FakeSessionLauncher.LaunchFailure` が `LaunchOwned` にも効く）

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.Core/Morning/MorningRunDescriptor.cs src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningRunDescriptorTests.cs tests/MoTask.Core.Tests/MorningServiceStartTests.cs
git commit -m "feat(core): 朝の実行の端末を所有し run.json に pid と開始時刻を残す

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: 取り込みが終わったら端末を閉じる（「追跡をやめる」では閉じない）

仕様 §7 の「人の操作」の 2 本をそのまま残す。`CompleteAsync`（完了にする）は閉じ、`StopTrackingAsync`（追跡をやめる）は閉じない。この計画では閉じる契機は**現行のファイル契約のまま**で、`Stop` / `SessionEnd` で `result/` が揃って `Ingested` になった瞬間である。

**Files:**
- Modify: `src/MoTask.Core/Services/MorningService.cs`（`IngestAsync` の 2 か所）
- Test: `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`

**Interfaces:**
- Consumes: Task 3 の `ISessionLauncher.CloseOwned`、`FakeSessionLauncher.Closed`
- Produces: `MorningService` は `Ingested` と「`SessionEnd` で読めなかった `Failed`」の両方で `CloseOwned(runId)` を呼ぶ

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceIngestTests.cs` に足す:

```csharp
    /// <summary>仕事が終わったら窓も畳む（仕様 §7）。</summary>
    [Fact]
    public async Task Stop_ClosesTheTerminalOnceTheResultIsIngested()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }

    [Fact]
    public async Task Stop_LeavesTheTerminalOpen_WhileTheResultIsNotThereYet()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.Closed.Should().BeEmpty("Stop は何度でも来る。揃うまでは閉じない");
    }

    /// <summary>読めないまま終わった実行も後始末する。窓だけ残しても人は困る（仕様 §7）。</summary>
    [Fact]
    public async Task SessionEnd_WithoutAPlan_ClosesTheTerminalToo()
    {
        var run = await StartAsync();

        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionEnd());

        run.Status.Should().Be(MorningRunStatus.Failed);
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>人の「完了にする」はその場で閉じる。待つべき Stop が来る保証が無い（仕様 §7）。</summary>
    [Fact]
    public async Task Complete_ClosesTheTerminal()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);

        await _service.CompleteAsync(run.Id);

        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>「追跡をやめる」は端末を殺さない。既存の約束をここでは守る（仕様 §7）。</summary>
    [Fact]
    public async Task StopTracking_DoesNotCloseTheTerminal()
    {
        var run = await StartAsync();

        await _service.StopTrackingAsync(run.Id);

        run.Status.Should().Be(MorningRunStatus.Cancelled);
        _launcher.Closed.Should().BeEmpty();
    }
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: `Stop_ClosesTheTerminalOnceTheResultIsIngested` / `SessionEnd_WithoutAPlan_ClosesTheTerminalToo` / `Complete_ClosesTheTerminal` の 3 本が FAIL（`Closed` が空）

- [ ] **Step 3: `IngestAsync` の 2 つの終わり方で端末を閉じる**

`src/MoTask.Core/Services/MorningService.cs` の `IngestAsync` の中、`if (finished)` ブロックを次で置き換える:

```csharp
            if (finished)
            {
                _events.StopFollowing(run.Id);
                _turns.TryRemove(run.Id, out _);
                // 読めないまま終わった実行でも窓は畳む（仕様 §7）
                _launcher.CloseOwned(run.Id);
            }
```

同じメソッドの末尾近く、`_events.StopFollowing(run.Id); _turns.TryRemove(run.Id, out _);` の 2 行を次で置き換える:

```csharp
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        // 取り込みが終わったら窓を畳む。知らない ownerId は launcher が黙って無視するので、
        // 2 度目の Stop で重ねて呼ばれても実害は無い（仕様 §5.3）。
        _launcher.CloseOwned(run.Id);
```

- [ ] **Step 4: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: PASS（既存の 20 本以上を含めて全件）

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningServiceIngestTests.cs
git commit -m "feat(core): 取り込みが終わった朝の実行の端末を閉じる

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: 端末が先に死んだら実行を `Failed` にする

現行は端末を × で閉じても MoTask は `events.jsonl` を延々ポーリングし続け、人が「完了にする」を押すまで実行が宙に浮いたままだった。所有したことで初めてできるようになる後始末である（仕様 §7）。

**Files:**
- Modify: `src/MoTask.Core/Resources/Messages.resx`
- Modify: `src/MoTask.Core/Resources/Messages.cs`
- Modify: `src/MoTask.Core/Services/MorningService.cs`（構築子と新しいハンドラ）
- Test: `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`

**Interfaces:**
- Consumes: Task 3 の `ISessionLauncher.OwnedSessionExited` と `FakeSessionLauncher.RaiseExited(int)`
- Produces:
  - `Messages.MorningTerminalClosed`
  - `MorningService.PendingTerminalExit`（`Task`。イベントの後始末をテストが待つための口）

- [ ] **Step 1: 文言を resx とアクセサに足す**

`src/MoTask.Core/Resources/Messages.resx` の `MorningCandidatesDiscardedFormat` の行の直後に足す:

```xml
  <data name="MorningTerminalClosed" xml:space="preserve"><value>端末が閉じられました</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の `MorningCandidatesDiscardedFormat` のプロパティの直後に足す:

```csharp
    public static string MorningTerminalClosed => Get(nameof(MorningTerminalClosed));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceIngestTests.cs` に足す:

```csharp
    /// <summary>
    /// 人が × で閉じた・claude が落ちた。所有しているからこそ気づける（仕様 §7）。
    /// 気づかないと events.jsonl を延々ポーリングし続けて実行が宙に浮く。
    /// </summary>
    [Fact]
    public async Task TheTerminalDyingFirst_FailsTheRunAndStopsFollowing()
    {
        var run = await StartAsync();
        await _events.EmitAsync(run.Id, FakeJobEventSource.SessionStart());

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be(Messages.MorningTerminalClosed);
        run.EndedAt.Should().Be(_clock.UtcNow);
        _events.IsFollowing(run.Id).Should().BeFalse();
        _changes.Last().Run.Status.Should().Be(MorningRunStatus.Failed);
    }

    /// <summary>閉じたのはこちらなので、取り込み済みの実行を Failed で上書きしない。</summary>
    [Fact]
    public async Task TheTerminalDyingAfterIngesting_ChangesNothing()
    {
        var run = await StartAsync();
        PutResult(run, TwoCandidates, Plan);
        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        _launcher.RaiseExited(run.Id);
        await _service.PendingTerminalExit;

        run.Status.Should().Be(MorningRunStatus.Ingested);
        run.ErrorMessage.Should().BeNull();
    }

    /// <summary>知らない runId のイベントで落ちない。</summary>
    [Fact]
    public async Task AnExitForARunWeDoNotKnow_IsIgnored()
    {
        _launcher.RaiseExited(9999);

        await _service.PendingTerminalExit;

        _store.Runs.Should().BeEmpty();
    }
```

- [ ] **Step 3: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: コンパイルエラー（`PendingTerminalExit` が無い）で FAIL

- [ ] **Step 4: `MorningService` が `OwnedSessionExited` を購読する**

`src/MoTask.Core/Services/MorningService.cs` の構築子の末尾（`_boardService = boardService;` の直後）に足す:

```csharp
        // 所有した端末が先に死んだら気づけるようにする（仕様 §7）。MorningService も
        // ISessionLauncher もアプリに 1 つずつの singleton なので、外すことはしない。
        _launcher.OwnedSessionExited += OnOwnedSessionExited;
```

`RecoverOnStartupAsync` の直後に足す:

```csharp
    /// <summary>
    /// 端末の終了を受けた後始末。イベントは void で来るので、直近の 1 本をここに残して
    /// テストが待てるようにする（MorningPlanViewModel.PendingLoad と同じ手）。
    /// </summary>
    public Task PendingTerminalExit { get; private set; } = Task.CompletedTask;

    private void OnOwnedSessionExited(object? sender, int runId)
        => PendingTerminalExit = OnOwnedSessionExitedAsync(runId);

    /// <summary>
    /// 端末が先に死んだ（人が × で閉じた・claude が落ちた）。追跡中なら Failed にして降りる。
    /// 終端の実行はそのまま（閉じたのがこちらの CloseOwned なら、そもそもここへ来ない）。
    /// </summary>
    private async Task OnOwnedSessionExitedAsync(int runId)
    {
        MorningRun? run = null;
        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _runs.GetRunAsync(runId).ConfigureAwait(false);
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            run = current;
            current.Status = MorningRunStatus.Failed;
            current.ErrorMessage = Messages.MorningTerminalClosed;
            current.EndedAt = _clock.UtcNow;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (run is null) return;
        _events.StopFollowing(run.Id);
        _turns.TryRemove(run.Id, out _);
        Raise(run, warning, candidatesChanged: false);
    }
```

- [ ] **Step 5: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: PASS

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.Core/Resources/Messages.resx src/MoTask.Core/Resources/Messages.cs src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningServiceIngestTests.cs
git commit -m "feat(core): 端末が先に閉じられた朝の実行を Failed にして追従を止める

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: 再起動後に端末へ掛け直す

`OnExit` はハンドルを解放するだけで端末を殺さない（Task 3 の `TerminalLauncher.Dispose`、DI の singleton 破棄で呼ばれる）。だから再起動した MoTask は掛けどころを失っている。`run.json` の pid と開始時刻で掛け直す（仕様 §7）。

**Files:**
- Modify: `src/MoTask.Core/Services/MorningService.cs:385-390`（`RecoverOnStartupAsync`）
- Test: `tests/MoTask.Core.Tests/MorningServiceIngestTests.cs`

**Interfaces:**
- Consumes: Task 3 の `ISessionLauncher.TryReattach` と `FakeSessionLauncher.Reattached` / `.ReattachSucceeds`、Task 4 の `MorningRunDescriptor.TryParse`
- Produces: なし（`RecoverOnStartupAsync` の振る舞いが変わるだけ）

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/MorningServiceIngestTests.cs` に足す（`using MoTask.Core.Morning;` が無ければ `using` に足す）:

```csharp
    private void PutRunJson(MorningRun run, int processId)
        => _folder.Put(run.JobFolder, JobFolderPaths.RunJsonName, MorningRunDescriptor.Serialize(
            new MorningRunDescriptor(
                run.Id, run.Date, run.SessionId, run.JobFolder, "cmd.exe /c claude",
                new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc),
                ProcessId: processId,
                ProcessStartedAt: new DateTime(2026, 9, 7, 6, 0, 1, DateTimeKind.Utc))));

    [Fact]
    public async Task Recover_ReattachesToTheTerminalUsingRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().ContainSingle().Which.Should()
            .Be((run.Id, 31337, new DateTime(2026, 9, 7, 6, 0, 1, DateTimeKind.Utc)));
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>掛け直せた実行は、その後の取り込みでちゃんと閉じられる（仕様 §7）。</summary>
    [Fact]
    public async Task Recover_ThenIngest_StillClosesTheTerminal()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);
        await _service.RecoverOnStartupAsync();
        PutResult(run, TwoCandidates, Plan);

        await _events.EmitAsync(run.Id, FakeJobEventSource.Stop());

        run.Status.Should().Be(MorningRunStatus.Ingested);
        _launcher.Closed.Should().Equal(run.Id);
    }

    /// <summary>掛け直せなくても追従は続ける。諦めるのは「閉じる能力」だけ（仕様 §7）。</summary>
    [Fact]
    public async Task Recover_KeepsFollowingEvenWhenItCannotReattach()
    {
        _launcher.ReattachSucceeds = false;
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, 31337);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>pid の無い（この実装より前に作られた）run.json では掛け直しを試みない。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWithoutAProcessId()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        PutRunJson(run, processId: 0);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }

    /// <summary>run.json そのものが無くても、追従だけは今までどおり再開する。</summary>
    [Fact]
    public async Task Recover_DoesNotTryToReattachWhenThereIsNoRunJson()
    {
        var run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);

        await _service.RecoverOnStartupAsync();

        _launcher.Reattached.Should().BeEmpty();
        _events.IsFollowing(run.Id).Should().BeTrue();
    }
```

- [ ] **Step 2: 走らせて落ちることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: `Recover_ReattachesToTheTerminalUsingRunJson` と `Recover_ThenIngest_StillClosesTheTerminal` が FAIL（`Reattached` が空／`Closed` が空）

- [ ] **Step 3: `RecoverOnStartupAsync` に掛け直しを足す**

`src/MoTask.Core/Services/MorningService.cs` の `RecoverOnStartupAsync` を丸ごと次に置き換える:

```csharp
    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var run = await _gate.RunAsync(() => _runs.GetUnfinishedRunAsync(ct), ct).ConfigureAwait(false);
        if (run is null || run.JobFolder.Length == 0) return;

        // 掛け直せなければ「閉じる能力」だけを諦め、追従（events.jsonl）は続ける（仕様 §7）。
        // 開始時刻を照合するのは launcher 側の仕事で、ここは材料を渡すだけ。
        var descriptor = MorningRunDescriptor.TryParse(
            _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName));
        if (descriptor is { ProcessId: > 0 })
        {
            _launcher.TryReattach(run.Id, descriptor.ProcessId, descriptor.ProcessStartedAt);
        }

        Follow(run.Id, run.JobFolder, run.ProcessedLines);
    }
```

- [ ] **Step 4: 走らせて通ることを確かめる**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~MorningServiceIngestTests" -nologo -v q`
Expected: PASS

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core/Services/MorningService.cs tests/MoTask.Core.Tests/MorningServiceIngestTests.cs
git commit -m "feat(core): 再起動後に run.json の pid で朝の実行の端末へ掛け直す

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: 仕上げ — `AiJob*` の無傷を確かめ、手動確認の節を仕様に足す

**Files:**
- Modify: `docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md`（§11 の見出しに進め方の 1 行を足すだけ）
- 変更なし（確認のみ）: `src/MoTask.Core/Services/AiJobService.cs`、`tests/MoTask.Core.Tests/AiJobService*Tests.cs`

**Interfaces:**
- Consumes: Task 1〜7 のすべて
- Produces: なし

- [ ] **Step 1: `AiJob*` 一式が 1 行も変わっていないことを確かめる**

Run:

```bash
git diff --stat master -- src/MoTask.Core/Services/AiJobService.cs src/MoTask.Core/Services/IAiJobService.cs src/MoTask.Core/Model/AiJob.cs src/MoTask.Core/Model/AiJobStatus.cs src/MoTask.Core/Model/AiJobKind.cs tests/MoTask.Core.Tests/AiJobServiceStartTests.cs
```

Expected: 出力が空（仕様 §13 の完了条件）

`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs` だけは例外で、Task 1 が 1 行だけ触っている
（共有フェイクの既定出力が `wt.exe` から `cmd.exe` に変わるため。仕様 §5.2 が AI 遂行の既定も
`cmd.exe /k` にすると決めているので、これは仕様どおりの帰結である）。その 1 行だけであることを確かめる:

```bash
git diff master -- tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs
```

Expected: `-            .Which.LaunchCommand.Should().StartWith("wt.exe ");` と
`+            .Which.LaunchCommand.Should().StartWith("cmd.exe ");` の 1 行差し替えだけ

- [ ] **Step 2: 契約ファイルにも触れていないことを確かめる**

Run:

```bash
git diff --stat master -- src/MoTask.Core/Morning/MorningResultReader.cs src/MoTask.Core/Morning/MorningInstruction.cs src/MoTask.Core/Morning/BoardSnapshot.cs src/MoTask.Core/Morning/MorningPlanResolver.cs src/MoTask.App/Ai/BoardTools src/MoTask.App/Ai/MoTaskMcpServer.cs src/MoTask.Data
```

Expected: 出力が空（この計画は成果の受け渡しを変えない）

- [ ] **Step 3: `wt` の痕跡が残っていないことを確かめる**

Run: `git grep -n "WindowsTerminalTemplate\|FindWindowsTerminal\|hasWindowsTerminal\|FallbackTemplate" -- src tests`
Expected: 出力が空（`IsWindowsTerminalTemplate` は別名なのでヒットしない）

- [ ] **Step 4: 全ビルド・全テスト**

Run: `dotnet build MoTask.sln -nologo -v q -p:TreatWarningsAsErrors=true`
Expected: 0 エラー・0 警告

Run: `dotnet test MoTask.sln -nologo -v q`
Expected: 全件 PASS

- [ ] **Step 5: 仕様の §11 に進め方を 1 行足す**

`docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md` の `## 11. 手動確認が要る項目` の直下の行（`README には足さない。実施したらここにチェックを入れてコミットする。`）を次で置き換える:

```markdown
README には足さない。実施したらここにチェックを入れてコミットする。
先頭の 4 項目（端末が開く・× で閉じたとき・MoTask を閉じても残る・AI 遂行は開いたまま）と
`wt.exe` テンプレートの項目は 1 本目（端末の所有）で確認できる。残りは 2 本目（MCP への移行）で確認する。
```

- [ ] **Step 6: コミット**

```bash
git add docs/superpowers/specs/2026-09-13-motask-morning-mcp-handoff-design.md
git commit -m "docs(spec): 朝の実行の手動確認をどちらの実装計画で見るか書き添える

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: 手動確認（この計画で見られる分）**

`dotnet build` 後に `MoTask.exe` を起こして、仕様 §11 のうち次を実施し、仕様のチェックボックスに印を入れてコミットする。

- [ ] 朝の実行を起動すると端末が開き、Windows Terminal の中に出る（conhost の古い窓ではない）。既存の WT ウィンドウのタブではなく新しいウィンドウになること、「既定のターミナル アプリケーション」を conhost にしている環境では昔ながらの窓になることも見る（仕様 §5.2）
- [ ] 実行中に端末を × で閉じると、朝の画面が `Failed`（端末が閉じられました）になる
- [ ] 実行中に MoTask を閉じても端末は残り、MoTask を開き直すと追跡が続く
- [ ] AI 遂行の端末は従来どおり開いたままで、閉じない
- [ ] AI 設定で `TerminalCommandTemplate` に `wt.exe …` を入れると注意が出て、朝の実行が既定の起動になる
- [ ] `result/` が揃った実行では、取り込みと同時に端末が閉じる（2 本目で `morning_complete` に移る前の、この計画時点の挙動）

---

## Self-Review

**仕様の対応（この計画の担当分・§13「1 本目」）**

| 仕様 | 対応するタスク |
| --- | --- |
| §5.1 `cmd` は外せない | Task 1（`cmd.exe` を先頭プロセスにする） |
| §5.2 テンプレートの統一 | Task 1 |
| §5.2 `wt.exe` テンプレートの落とし込みと注意 | Task 2 |
| §5.3 所有起動・`CloseOwned`・`OwnedSessionExited` | Task 3 |
| §5.3 `OnExit` はハンドルを解放するだけ | Task 3（`TerminalLauncher.Dispose`。DI の singleton 破棄で呼ばれる） |
| §7 `run.json` の `processId` / `processStartedAt` | Task 4 |
| §7 取り込み後に閉じる／「追跡をやめる」は閉じない | Task 5 |
| §7 端末が先に死んだとき | Task 6 |
| §7 `SessionEnd` の後始末 | Task 5（この計画では「`result/` が読めない `Failed`」＋ `CloseOwned`） |
| §7 再起動後の掛け直し | Task 7 |
| §10 テスト方針（`TerminalLauncher` / `MorningService` / 掛け直し） | Task 1〜7 の各テスト |
| §11 手動確認 | Task 8 Step 7 |
| §13 `AiJob*` 無傷の完了条件 | Task 8 Step 1 |

**この計画が意図的に扱わないもの（2 本目の担当）**

§6（MCP ツール 4 本）、§8（`instruction.md` の契約差し替え）、§9（`MorningToolHost` のレイヤ分担）、`MorningResultReader` の解体、`--mcp-config` の生成、`result/` と `board.json` の廃止、閉じる契機を `morning_complete` の予約 ＋ 次の `Stop` に移すこと。
