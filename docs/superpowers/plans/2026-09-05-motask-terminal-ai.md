# MoTask AI 遂行 — ターミナル実行への作り替え 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MoTask をエージェントのホストから「セッションの発注者兼観測者」に作り替える。ジョブフォルダを作って Windows Terminal で `claude` を対話起動し、Claude Code の hooks が書く `events.jsonl` を追従して盤面に反映する。

**Architecture:** 依存方向は変えない（App → Data → Core、App → Core、Core は他に依存しない）。Core にフック行のパーサ（`HookEventParser`）とジョブフォルダのパス規約（`JobFolderPaths`）、3 つの口（`ISessionLauncher` / `IJobFolder` / `IJobEventSource`）を置く。App がその実装（`TerminalLauncher` / `JobFolder` / `JobEventWatcher`）を持つ。フックの実体は stdin を 1 行追記するだけの新規 exe（`MoTask.Hooks`）。承認サブシステム（MCP サーバ・ダイアログ・ルール）は一式削除する。

**Tech Stack:** .NET 10 / WPF / EF Core 10 (SQLite) / xUnit + FluentAssertions + NSubstitute / CommunityToolkit.Mvvm

**Spec:** `docs/superpowers/specs/2026-09-05-motask-terminal-ai-design.md`

## Global Constraints

- 対象フレームワークは `net10.0`（Core / Data / Hooks）と `net10.0-windows`（App / App.Tests）。`Directory.Build.props` の `Nullable=enable` / `ImplicitUsings=enable` / `ManagePackageVersionsCentrally=true` は変えない。新しい NuGet 参照は追加しない。
- 利用者向けの文言はすべて日本語で、`src/MoTask.Core/Resources/Messages.resx`（Core が出す理由）と `src/MoTask.App/Resources/Strings.resx`（画面の文言）に置く。コード直書きの日本語文字列を作らない。`StringsTests.AllProperties_ResolveToNonEmptyValuesDistinctFromTheirNames` が resx の欠落を自動で検出する。
- コード中のコメントと XML ドキュメントは既存ファイルと同じく日本語で書く。
- `claude` に渡す引数のうち、`--setting-sources` / `--permission-prompt-tool` / `--tools` / `--max-turns` / `--strict-mcp-config` / `--mcp-config` は **いずれも渡さない**（仕様 §7）。`--model` は設定に値があるときだけ渡す。
- `--permission-mode` の有効値はこの 6 つだけ: `acceptEdits` / `auto` / `bypassPermissions` / `manual` / `dontAsk` / `plan`。既定は `auto`。
- 列挙の既存番号は動かさない。廃止する値の番号は空けたまま、新しい値を後ろに足す。
- `AiJobStatus` / `AiJobEventKind` / `AiJobKind` は EF で **文字列**として保存されている（`HasConversion<string>()`）。値を消すときは、その名前で保存済みの行をマイグレーションで書き換えないと読み出しが例外になる。
- MoTask は端末のプロセスを所有しない。プロセスを殺すコードを一切書かない。
- ジョブフォルダの位置は `<既定ワークフォルダ>\jobs\<JobId 4 桁>-<タスク名のスラグ>\`。
- テスト実行は常に `dotnet test MoTask.sln`。個別実行は `dotnet test MoTask.sln --filter "FullyQualifiedName~<名前>"`。
- 実起動（端末を開く、プロセスを起こす）は自動テストしない。

## File Structure

新規プロジェクト:

| パス | 責務 |
| --- | --- |
| `src/MoTask.Hooks/MoTask.Hooks.csproj` | net10.0 の Exe。参照ゼロ |
| `src/MoTask.Hooks/Program.cs` | stdin を読み切って 1 行に畳み、引数のファイルへ追記するだけ |

MoTask.Core（新規）:

| パス | 責務 |
| --- | --- |
| `Ai/JobFolderPaths.cs` | ジョブフォルダのパス規約とフォルダ名のスラグ（純粋関数） |
| `Ai/HookEvent.cs` | パース結果 1 件（Kind / ToolName / Payload） |
| `Ai/HookEventParser.cs` | フックの 1 行 → `HookEvent`。CLI との唯一の形の依存点 |
| `Ai/ISessionLauncher.cs` | 端末を開く口。`CheckAvailable` / `BuildCommand` / `Launch` |
| `Ai/SessionLaunchRequest.cs` | 1 回の起動の材料 |
| `Ai/TerminalCommand.cs` | 組み立てたコマンド（実行ファイル・引数・cwd・表示用） |
| `Ai/IJobFolder.cs` | ジョブフォルダの作成・`job.json` の記録・`artifacts/` の列挙 |
| `Ai/JobFolderRequest.cs` / `Ai/JobDescriptor.cs` | 上の引数 |
| `Ai/IJobEventSource.cs` / `Ai/JobEventSubscription.cs` | `events.jsonl` の追従の口 |

MoTask.App（新規）:

| パス | 責務 |
| --- | --- |
| `Ai/CommandLine.cs` | Windows のコマンドラインの引用と先頭トークンの切り出し（純粋関数） |
| `Ai/HooksJson.cs` | `hooks.json` の本文を組み立てる（純粋関数） |
| `Ai/JobFolder.cs` | `IJobFolder` の実体。ファイルを作る |
| `Ai/TerminalLauncher.cs` | `ISessionLauncher` の実体。`wt.exe` / `cmd.exe` を起こす |
| `Ai/JobEventWatcher.cs` | `IJobEventSource` の実体。`events.jsonl` をポーリングで追う |

削除するもの（仕様 §10）:

- Core: `Ai/AgentEvent.cs`, `Ai/AgentRunOutcome.cs`, `Ai/AgentRunRequest.cs`, `Ai/IAgentRunner.cs`, `Ai/IPermissionPolicy.cs`, `Ai/IPermissionPrompt.cs`, `Ai/PermissionDecision.cs`, `Ai/PermissionPattern.cs`, `Ai/PermissionPolicy.cs`, `Ai/PermissionRequest.cs`, `Abstractions/IPermissionRuleRepository.cs`, `Model/AiPermissionRule.cs`, `Model/RuleDecision.cs`, `Model/RuleScope.cs`
- Data: `Repositories/PermissionRuleRepository.cs`
- App: `Ai/ApprovalMcpServer.cs`, `Ai/McpProtocol.cs`, `Ai/McpConfigFile.cs`, `Ai/PermissionGate.cs`, `Ai/WpfPermissionPrompt.cs`, `Ai/ClaudeCodeParser.cs`, `Ai/ClaudeCodeRunner.cs`, `Ai/ClaudeCodeArguments.cs`, `ViewModels/PermissionDialogViewModel.cs`, `Views/PermissionDialog.xaml`, `Views/PermissionDialog.xaml.cs`
- テスト: `Core.Tests/PermissionPolicyTests.cs`, `Core.Tests/AiJobServicePermissionTests.cs`, `Core.Tests/Fakes/FakePermissionPrompt.cs`, `Core.Tests/Fakes/FakeAgentRunner.cs`, `App.Tests/PermissionGateTests.cs`, `App.Tests/PermissionDialogViewModelTests.cs`, `App.Tests/McpConfigFileTests.cs`, `App.Tests/ApprovalMcpServerTests.cs`, `App.Tests/ClaudeCodeArgumentsTests.cs`, `App.Tests/ClaudeCodeParserTests.cs`, `App.Tests/Fixtures/stream-bash.jsonl`, `App.Tests/Fixtures/stream-deny.jsonl`

`ClaudeLocator` / `ShellOpener` はそのまま流用する。

## 仕様からの意図的なずれ（実装前に把握しておくこと）

1. **起動プロンプトは `@instruction.md` ではなく絶対パスにする。** 仕様 §7 の例は `@instruction.md` だが、cwd はプロジェクトの作業フォルダでありジョブフォルダではないので、相対の `@instruction.md` は解決できない。`instruction.md` の絶対パスを本文に書き、`--add-dir` で読み取りを許す。
2. **`events.jsonl` の追従はポーリング（500 ms）で行う。** 仕様 §5 は `FileSystemWatcher` と書いているが、追記に対する通知は取りこぼしと重複が多く、結局は「最後に読んだ位置から読み直す」処理が要る。小さなファイル 1 本を 500 ms ごとに `Length` で見るだけなので、通知を足す価値が無い。挙動（追いつく・オフセットから再開する）は仕様どおり。
3. **「入力待ち」バッジは、次の `PostToolUse` か次の `Stop` まで消えない。** 仕様 §14 の「次の発話で消える」は `UserPromptSubmit` フックを使えば厳密に実現できるが、仕様 §4.4 が実測したのは 4 イベントだけなので、この計画では 4 イベントに留める。人が話しかけてモデルが道具を使えば消える。厳密化は別の作業として残す。

---

## Task 1: MoTask.Hooks — stdin を 1 行追記する exe

**Files:**
- Create: `src/MoTask.Hooks/MoTask.Hooks.csproj`
- Create: `src/MoTask.Hooks/Program.cs`
- Modify: `MoTask.sln`
- Modify: `src/MoTask.App/MoTask.App.csproj`
- Test: `tests/MoTask.App.Tests/HooksExeTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: ビルド後に `MoTask.App` の出力へ `hooks\MoTask.Hooks.exe`（＋ `.dll` と `.runtimeconfig.json`）が並ぶ。App.Tests の出力にも同じ相対位置で並ぶ。Task 6 の `JobFolder` がこのパスを見る。

- [ ] **Step 1: プロジェクトファイルを作る**

`src/MoTask.Hooks/MoTask.Hooks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MoTask.Hooks</RootNamespace>
    <AssemblyName>MoTask.Hooks</AssemblyName>
    <!-- Claude Code のフックはツール 1 回ごとに起動する。起動を軽くするため参照は増やさない。 -->
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/HooksExeTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// フックの実体は「stdin を 1 行にして追記する」だけ。壊れると盤面が黙って止まるので、
/// 組み立てたコマンドではなく実際の exe を起動して確かめる。
/// </summary>
public class HooksExeTests : IDisposable
{
    private static readonly string Exe =
        Path.Combine(AppContext.BaseDirectory, "hooks", "MoTask.Hooks.exe");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public HooksExeTests() => Directory.CreateDirectory(_dir);

    private static async Task RunAsync(string target, string stdin)
    {
        var info = new ProcessStartInfo(Exe, $"\"{target}\"")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Exe_IsCopiedNextToTheApp()
    {
        File.Exists(Exe).Should().BeTrue("MoTask.App のビルドが hooks\\MoTask.Hooks.exe を運ぶはず");
    }

    [Fact]
    public async Task Appends_OneUtf8Line_PerInvocation()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, """{"hook_event_name":"Stop","last_assistant_message":"できました"}""");
        await RunAsync(target, """{"hook_event_name":"SessionEnd","reason":"exit"}""");

        var lines = File.ReadAllLines(target, new UTF8Encoding(false));
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("できました");
        lines[1].Should().Contain("SessionEnd");
    }

    [Fact]
    public async Task Folds_MultilineStdin_IntoOneLine()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, "{\r\n  \"hook_event_name\": \"SessionStart\"\r\n}");

        File.ReadAllLines(target).Should().ContainSingle()
            .Which.Should().Be("{ \"hook_event_name\": \"SessionStart\" }");
    }

    [Fact]
    public async Task Concurrent_Invocations_DoNotInterleave()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            RunAsync(target, $$"""{"hook_event_name":"PostToolUse","tool_use_id":"{{i}}"}""")));

        var lines = File.ReadAllLines(target);
        lines.Should().HaveCount(8);
        lines.Should().OnlyContain(l => l.StartsWith("{\"hook_event_name\"") && l.EndsWith("}"));
    }

    [Fact]
    public async Task EmptyStdin_WritesNothing()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, "   \r\n  ");

        File.Exists(target).Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~HooksExeTests"`
Expected: `MoTask.Hooks.exe` が無くて全件 FAIL（`Exe_IsCopiedNextToTheApp` は「false なのに true を期待」）

- [ ] **Step 4: Program.cs を書く**

`src/MoTask.Hooks/Program.cs`:

```csharp
using System.Text;

// Claude Code のフック。stdin に来る 1 件の JSON を 1 行に畳んで、引数のファイルへ追記する。
// 中身は解釈しない（CLI がキーを足しても壊れない）。標準出力には何も書かない
// （フックの stdout は CLI に解釈されうる）。

if (args.Length < 1) return 1;
var target = args[0];

// Console.In は OEM コードページで開かれることがある。日本語を壊さないよう UTF-8 を明示する。
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var payload = input.ReadToEnd();

// 改行を空白に潰して 1 行にする（events.jsonl は「1 行 = 1 イベント」が唯一の約束）。
var line = payload.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
if (line.Length == 0) return 0;

var bytes = new UTF8Encoding(false).GetBytes(line + "\n");

// 4 種のフックが同時に走りうる。追記は排他で取り、取れなければ少し待って諦めずに再試行する。
// ここで書き損ねるとイベントが 1 件消えるだけで、CLI 側の作業は続く（終了コードは常に 0）。
for (var attempt = 0; attempt < 50; attempt++)
{
    try
    {
        using var stream = new FileStream(
            target, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        stream.Write(bytes);
        return 0;
    }
    catch (IOException)
    {
        Thread.Sleep(20);
    }
    catch (UnauthorizedAccessException)
    {
        Thread.Sleep(20);
    }
}
return 0;
```

- [ ] **Step 5: ソリューションに足す**

```bash
dotnet sln MoTask.sln add src/MoTask.Hooks/MoTask.Hooks.csproj --solution-folder src
```

- [ ] **Step 6: MoTask.App の出力に同梱する**

`src/MoTask.App/MoTask.App.csproj` の `<ItemGroup>`（`ProjectReference` のあるもの）に足す:

```xml
    <!-- フックの exe。参照はビルド順のためだけで、アセンブリとしては使わない。 -->
    <ProjectReference Include="..\MoTask.Hooks\MoTask.Hooks.csproj" ReferenceOutputAssembly="false" />
```

同じファイルの末尾（`</Project>` の直前）に足す:

```xml
  <ItemGroup>
    <!--
      hooks\ に実体を運ぶ。Content にしておくと MoTask.App を参照するプロジェクト
      （App.Tests）の出力にも同じ相対位置で並ぶので、テストから実起動できる。
      上の ProjectReference がビルド順を保証するので、コピー時には必ず存在する。
      MoTask.Hooks は net10.0 固定・出力パスも既定なので、パスを直に書いてよい。
    -->
    <Content Include="..\MoTask.Hooks\bin\$(Configuration)\net10.0\MoTask.Hooks.exe"
             Link="hooks\MoTask.Hooks.exe" CopyToOutputDirectory="PreserveNewest" Visible="false" />
    <Content Include="..\MoTask.Hooks\bin\$(Configuration)\net10.0\MoTask.Hooks.dll"
             Link="hooks\MoTask.Hooks.dll" CopyToOutputDirectory="PreserveNewest" Visible="false" />
    <Content Include="..\MoTask.Hooks\bin\$(Configuration)\net10.0\MoTask.Hooks.runtimeconfig.json"
             Link="hooks\MoTask.Hooks.runtimeconfig.json" CopyToOutputDirectory="PreserveNewest" Visible="false" />
  </ItemGroup>
```

- [ ] **Step 7: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~HooksExeTests"`
Expected: 5 件 PASS

- [ ] **Step 8: 全体が壊れていないことを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.Hooks MoTask.sln src/MoTask.App/MoTask.App.csproj tests/MoTask.App.Tests/HooksExeTests.cs
git commit -m "feat(hooks): add the tiny hook exe that appends one line to events.jsonl"
```

---

## Task 2: Core — ジョブフォルダのパス規約とスラグ

**Files:**
- Create: `src/MoTask.Core/Ai/JobFolderPaths.cs`
- Test: `tests/MoTask.Core.Tests/JobFolderPathsTests.cs`

**Interfaces:**
- Consumes: なし
- Produces:
  - `MoTask.Core.Ai.JobFolderPaths`（`sealed record`、コンストラクタ `JobFolderPaths(string Root)`）
  - プロパティ: `Root`, `JobJson`, `InstructionMarkdown`, `HooksJson`, `EventsJsonl`, `ArtifactsDirectory`（すべて `string`）
  - `static JobFolderPaths For(string root)`
  - `static string FolderName(int jobId, string taskTitle)`
  - `static string Slug(string title)`
  - `const string JobsDirectoryName = "jobs"`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/JobFolderPathsTests.cs`:

```csharp
using System.IO;
using FluentAssertions;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Core.Tests;

public class JobFolderPathsTests
{
    [Fact]
    public void For_LaysOutTheFilesUnderTheRoot()
    {
        var paths = JobFolderPaths.For(@"C:\work\jobs\0042-請求書の突合");

        paths.Root.Should().Be(@"C:\work\jobs\0042-請求書の突合");
        paths.JobJson.Should().Be(Path.Combine(paths.Root, "job.json"));
        paths.InstructionMarkdown.Should().Be(Path.Combine(paths.Root, "instruction.md"));
        paths.HooksJson.Should().Be(Path.Combine(paths.Root, "hooks.json"));
        paths.EventsJsonl.Should().Be(Path.Combine(paths.Root, "events.jsonl"));
        paths.ArtifactsDirectory.Should().Be(Path.Combine(paths.Root, "artifacts"));
    }

    [Fact]
    public void FolderName_PadsTheJobIdToFourDigits()
    {
        JobFolderPaths.FolderName(42, "請求書の突合").Should().Be("0042-請求書の突合");
        JobFolderPaths.FolderName(12345, "大きい").Should().Be("12345-大きい");
    }

    [Theory]
    // Windows のファイル名に使えない文字は - に潰す
    [InlineData(@"a/b\c:d*e?f""g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    // 空白の連なりは 1 つの - にまとめる
    [InlineData("  請求書   の 突合  ", "請求書-の-突合")]
    // 連続する区切りは 1 つにまとめ、前後の - と . は落とす
    [InlineData("--..見積り..--", "見積り")]
    // 日本語はそのまま残す
    [InlineData("月次レポート", "月次レポート")]
    public void Slug_KeepsNamesUsableAsFolderNames(string title, string expected)
    {
        JobFolderPaths.Slug(title).Should().Be(expected);
    }

    [Fact]
    public void Slug_FallsBackWhenNothingIsLeft()
    {
        JobFolderPaths.Slug("").Should().Be("task");
        JobFolderPaths.Slug("///").Should().Be("task");
    }

    [Fact]
    public void Slug_IsCappedSoThePathStaysShort()
    {
        var slug = JobFolderPaths.Slug(new string('あ', 100));

        slug.Should().HaveLength(40);
    }
}
```

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderPathsTests"`
Expected: コンパイルエラー（`JobFolderPaths` が無い）

- [ ] **Step 3: 実装を書く**

`src/MoTask.Core/Ai/JobFolderPaths.cs`:

```csharp
using System.Text;

namespace MoTask.Core.Ai;

/// <summary>
/// ジョブフォルダのレイアウト（仕様 §6）。パスを組み立てるだけで、ファイルには触らない。
/// 実体を作るのは App の JobFolder。
/// </summary>
public sealed record JobFolderPaths(string Root)
{
    /// <summary>既定ワークフォルダ直下の、ジョブフォルダを集める場所。</summary>
    public const string JobsDirectoryName = "jobs";

    /// <summary>フォルダ名に残す長さの上限。パス全体が 260 文字に近づかないようにする。</summary>
    private const int MaxSlugLength = 40;

    public string JobJson => Path.Combine(Root, "job.json");
    public string InstructionMarkdown => Path.Combine(Root, "instruction.md");
    public string HooksJson => Path.Combine(Root, "hooks.json");
    public string EventsJsonl => Path.Combine(Root, "events.jsonl");
    public string ArtifactsDirectory => Path.Combine(Root, "artifacts");

    public static JobFolderPaths For(string root) => new(root);

    /// <summary>Id を頭に置くので、同じ題名のタスクでも衝突しない。</summary>
    public static string FolderName(int jobId, string taskTitle)
        => $"{jobId:0000}-{Slug(taskTitle)}";

    /// <summary>
    /// タスク名をフォルダ名に使える形へ。日本語はそのまま残し、使えない文字と空白は - に潰す。
    /// 何も残らなければ "task"（人が見て分かる必要はあるが、識別は先頭の Id が担う）。
    /// </summary>
    public static string Slug(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            var replace = char.IsWhiteSpace(c) || char.IsControl(c) || Array.IndexOf(invalid, c) >= 0;
            var next = replace ? '-' : c;
            // 区切りの連続は 1 つにまとめる
            if (next == '-' && builder.Length > 0 && builder[^1] == '-') continue;
            if (next == '-' && builder.Length == 0) continue;
            builder.Append(next);
            if (builder.Length >= MaxSlugLength) break;
        }

        // 末尾の - と . は Windows がフォルダ名から黙って落とすので、こちらで落としておく
        var slug = builder.ToString().TrimEnd('-', '.').TrimStart('-', '.');
        return slug.Length == 0 ? "task" : slug;
    }
}
```

- [ ] **Step 4: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderPathsTests"`
Expected: 8 件 PASS

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core/Ai/JobFolderPaths.cs tests/MoTask.Core.Tests/JobFolderPathsTests.cs
git commit -m "feat(core): add the job folder layout and slug rules"
```

---

## Task 3: Core — HookEventParser とフックのイベント種別

**Files:**
- Modify: `src/MoTask.Core/Model/AiJobEventKind.cs`
- Create: `src/MoTask.Core/Ai/HookEvent.cs`
- Create: `src/MoTask.Core/Ai/HookEventParser.cs`
- Create: `tests/MoTask.Core.Tests/Fixtures/hook-session-start.json`
- Create: `tests/MoTask.Core.Tests/Fixtures/hook-post-tool-use.json`
- Create: `tests/MoTask.Core.Tests/Fixtures/hook-stop.json`
- Create: `tests/MoTask.Core.Tests/Fixtures/hook-session-end.json`
- Modify: `tests/MoTask.Core.Tests/MoTask.Core.Tests.csproj`
- Test: `tests/MoTask.Core.Tests/HookEventParserTests.cs`

**Interfaces:**
- Consumes: なし
- Produces:
  - `AiJobEventKind.SessionStarted = 8`, `AiJobEventKind.SessionEnded = 9`, `AiJobEventKind.TurnEnded = 10`（既存の値はそのまま）
  - `sealed record HookEvent(AiJobEventKind Kind, string? ToolName, string Payload)`
  - `static class HookEventParser { public static HookEvent Parse(string line); }`

- [ ] **Step 1: fixture を置く**

実機（Claude Code 2.1.260）のペイロードの形（仕様 §4.4）。1 行 1 件。

`tests/MoTask.Core.Tests/Fixtures/hook-session-start.json`:

```json
{"session_id":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77","transcript_path":"C:\\Users\\kzk\\.claude\\projects\\d--work\\6f2f2f1e.jsonl","cwd":"D:\\work\\sample","hook_event_name":"SessionStart","source":"startup"}
```

`tests/MoTask.Core.Tests/Fixtures/hook-post-tool-use.json`:

```json
{"session_id":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77","transcript_path":"C:\\Users\\kzk\\.claude\\projects\\d--work\\6f2f2f1e.jsonl","cwd":"D:\\work\\sample","hook_event_name":"PostToolUse","tool_name":"Write","tool_input":{"file_path":"D:\\work\\jobs\\0042-見積り\\artifacts\\report.md","content":"# 調べたこと\n"},"tool_response":{"type":"create","filePath":"D:\\work\\jobs\\0042-見積り\\artifacts\\report.md"},"tool_use_id":"toolu_01ABC","duration_ms":42,"permission_mode":"auto"}
```

`tests/MoTask.Core.Tests/Fixtures/hook-stop.json`:

```json
{"session_id":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77","transcript_path":"C:\\Users\\kzk\\.claude\\projects\\d--work\\6f2f2f1e.jsonl","cwd":"D:\\work\\sample","hook_event_name":"Stop","last_assistant_message":"見積りの根拠をまとめました。","stop_hook_active":false,"background_tasks":[]}
```

`tests/MoTask.Core.Tests/Fixtures/hook-session-end.json`:

```json
{"session_id":"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77","transcript_path":"C:\\Users\\kzk\\.claude\\projects\\d--work\\6f2f2f1e.jsonl","cwd":"D:\\work\\sample","hook_event_name":"SessionEnd","reason":"exit"}
```

`tests/MoTask.Core.Tests/MoTask.Core.Tests.csproj` の末尾（`</Project>` の直前）に足す:

```xml
  <ItemGroup>
    <None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.Core.Tests/HookEventParserTests.cs`:

```csharp
using System.IO;
using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// CLI との唯一の形の依存点。fixture は実機で採取したペイロード（仕様 §4.4）。
/// CLI が形を変えたらここが赤くなる。
/// </summary>
public class HookEventParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim();

    [Fact]
    public void SessionStart_BecomesSessionStarted()
    {
        var line = Fixture("hook-session-start.json");

        var result = HookEventParser.Parse(line);

        result.Kind.Should().Be(AiJobEventKind.SessionStarted);
        result.ToolName.Should().BeNull();
        result.Payload.Should().Be(line, "原文はそのまま残す");
    }

    [Fact]
    public void PostToolUse_BecomesToolUse_WithTheToolName()
    {
        var result = HookEventParser.Parse(Fixture("hook-post-tool-use.json"));

        result.Kind.Should().Be(AiJobEventKind.ToolUse);
        result.ToolName.Should().Be("Write");
    }

    [Fact]
    public void Stop_BecomesTurnEnded()
    {
        var result = HookEventParser.Parse(Fixture("hook-stop.json"));

        result.Kind.Should().Be(AiJobEventKind.TurnEnded);
        result.ToolName.Should().BeNull();
    }

    [Fact]
    public void SessionEnd_BecomesSessionEnded()
    {
        var result = HookEventParser.Parse(Fixture("hook-session-end.json"));

        result.Kind.Should().Be(AiJobEventKind.SessionEnded);
    }

    [Theory]
    [InlineData("これは JSON ではない")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hook_event_name\":\"PreCompact\"}")]
    [InlineData("{}")]
    [InlineData("")]
    public void UnknownOrBrokenLines_AreKeptAsSystem(string line)
    {
        var result = HookEventParser.Parse(line);

        result.Kind.Should().Be(AiJobEventKind.System);
        result.ToolName.Should().BeNull();
        result.Payload.Should().Be(line, "1 行壊れても捨てない");
    }

    [Fact]
    public void PostToolUse_WithoutToolName_StillParses()
    {
        var result = HookEventParser.Parse("""{"hook_event_name":"PostToolUse"}""");

        result.Kind.Should().Be(AiJobEventKind.ToolUse);
        result.ToolName.Should().BeNull();
    }

    [Fact]
    public void EventKinds_KeepTheirExistingNumbers()
    {
        ((int)AiJobEventKind.AssistantText).Should().Be(0);
        ((int)AiJobEventKind.ToolUse).Should().Be(1);
        ((int)AiJobEventKind.System).Should().Be(7);
        ((int)AiJobEventKind.SessionStarted).Should().Be(8);
        ((int)AiJobEventKind.SessionEnded).Should().Be(9);
        ((int)AiJobEventKind.TurnEnded).Should().Be(10);
    }
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~HookEventParserTests"`
Expected: コンパイルエラー（`HookEventParser` と新しい列挙値が無い）

- [ ] **Step 4: 列挙に値を足す**

`src/MoTask.Core/Model/AiJobEventKind.cs` を次の内容に置き換える（既存の値の番号は動かさない）:

```csharp
namespace MoTask.Core.Model;

public enum AiJobEventKind
{
    AssistantText = 0,
    ToolUse = 1,
    ToolResult = 2,
    /// <summary>廃止予定（承認は MoTask を通らない）。Task 10 で消す。</summary>
    PermissionAsked = 3,
    /// <summary>廃止予定（承認は MoTask を通らない）。Task 10 で消す。</summary>
    PermissionDecided = 4,
    Error = 5,
    Result = 6,
    /// <summary>知らないフック、パースできない行。捨てずに残すための受け皿。</summary>
    System = 7,
    /// <summary>SessionStart フック。</summary>
    SessionStarted = 8,
    /// <summary>SessionEnd フック。ジョブの完了。</summary>
    SessionEnded = 9,
    /// <summary>Stop フック。モデルの応答が終わって人の入力待ちになった。</summary>
    TurnEnded = 10,
}
```

- [ ] **Step 5: HookEvent と HookEventParser を書く**

`src/MoTask.Core/Ai/HookEvent.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>フックの 1 行から取り出した、表示と状態遷移に要る最小限。Payload は原文のまま。</summary>
public sealed record HookEvent(AiJobEventKind Kind, string? ToolName, string Payload);
```

`src/MoTask.Core/Ai/HookEventParser.cs`:

```csharp
using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>
/// events.jsonl の 1 行 → HookEvent。CLI との唯一の形の依存点なので、純粋関数にして
/// 実機で採取した fixture でテストから固定する（仕様 §5, §13）。
/// 知らないフックも壊れた行も捨てず、System として原文を残す（仕様 §12）。
/// </summary>
public static class HookEventParser
{
    public static HookEvent Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return System(line);

            return ReadString(doc.RootElement, "hook_event_name") switch
            {
                "SessionStart" => new HookEvent(AiJobEventKind.SessionStarted, null, line),
                "PostToolUse" => new HookEvent(AiJobEventKind.ToolUse, ReadString(doc.RootElement, "tool_name"), line),
                "Stop" => new HookEvent(AiJobEventKind.TurnEnded, null, line),
                "SessionEnd" => new HookEvent(AiJobEventKind.SessionEnded, null, line),
                _ => System(line),
            };
        }
        catch (JsonException)
        {
            return System(line);
        }
    }

    private static HookEvent System(string line) => new(AiJobEventKind.System, null, line);

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
```

- [ ] **Step 6: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~HookEventParserTests"`
Expected: 12 件 PASS

- [ ] **Step 7: 全体が壊れていないことを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.Core/Ai/HookEvent.cs src/MoTask.Core/Ai/HookEventParser.cs src/MoTask.Core/Model/AiJobEventKind.cs tests/MoTask.Core.Tests
git commit -m "feat(core): parse Claude Code hook lines into job events"
```

---

## Task 4: Core — 新しい口と設定項目

**Files:**
- Create: `src/MoTask.Core/Ai/TerminalCommand.cs`
- Create: `src/MoTask.Core/Ai/SessionLaunchRequest.cs`
- Create: `src/MoTask.Core/Ai/ISessionLauncher.cs`
- Create: `src/MoTask.Core/Ai/JobFolderRequest.cs`
- Create: `src/MoTask.Core/Ai/JobDescriptor.cs`
- Create: `src/MoTask.Core/Ai/IJobFolder.cs`
- Create: `src/MoTask.Core/Ai/JobEventSubscription.cs`
- Create: `src/MoTask.Core/Ai/IJobEventSource.cs`
- Modify: `src/MoTask.Core/Ai/AiSettings.cs`
- Modify: `src/MoTask.Core/Model/AiJobStatus.cs`
- Modify: `src/MoTask.Data/JsonAiSettingsStore.cs`
- Modify: `src/MoTask.App/ViewModels/AiSettingsViewModel.cs`（引数 2 つ足すだけ）
- Test: `tests/MoTask.Core.Tests/AiSettingsTests.cs`
- Modify: `tests/MoTask.Data.Tests/SettingsStoreTests.cs`
- Modify: `tests/MoTask.Core.Tests/AiModelTests.cs`

**Interfaces:**
- Consumes: `JobFolderPaths`（Task 2）
- Produces:
  - `sealed record TerminalCommand(string FileName, string Arguments, string WorkingDirectory)` — `string Display` を持つ
  - `sealed record SessionLaunchRequest(Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume)`
  - `interface ISessionLauncher { Result CheckAvailable(); Result<TerminalCommand> BuildCommand(SessionLaunchRequest request); Result Launch(TerminalCommand command); }`
  - `sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction)`
  - `sealed record JobDescriptor(int JobId, Guid SessionId, AiJobKind Kind, string WorkingDirectory, string LaunchCommand, DateTime StartedAt)`
  - `interface IJobFolder { Result<string> Create(JobFolderRequest request); void WriteJobJson(string root, JobDescriptor descriptor); IReadOnlyList<string> ListArtifacts(string root); }`
  - `sealed record JobEventSubscription(int JobId, string EventsPath, int SkipLines, Func<string, Task> OnLine, Func<string, Task> OnProblem)`
  - `interface IJobEventSource { void Follow(JobEventSubscription subscription); void StopFollowing(int jobId); }`
  - `AiJobStatus.WaitingForInput = 7`、`IsActive()` は「終わっていない」の意味に変わる
  - `AiSettings(string DefaultWorkingDirectory, int MaxConcurrentJobs, string? ClaudeExecutablePath, string? Model, int MaxTurns, string PermissionMode, string? TerminalCommandTemplate)` — 末尾に 2 つ追加。`MaxConcurrentJobs` / `MaxTurns` は Task 10 で落とす
  - `AiSettings.DefaultPermissionMode = "auto"`、`AiSettings.PermissionModes`（有効値 6 つ）

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/AiSettingsTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Core.Tests;

public class AiSettingsTests
{
    [Fact]
    public void Default_UsesAutoPermissionModeAndNoTemplate()
    {
        var settings = AiSettings.Default();

        settings.PermissionMode.Should().Be("auto");
        settings.TerminalCommandTemplate.Should().BeNull();
    }

    [Fact]
    public void PermissionModes_AreTheSixValuesTheCliAccepts()
    {
        AiSettings.PermissionModes.Should().Equal(
            "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan");
    }

    [Fact]
    public void PermissionModes_ContainsTheDefault()
    {
        AiSettings.PermissionModes.Should().Contain(AiSettings.DefaultPermissionMode);
    }
}
```

`tests/MoTask.Core.Tests/AiModelTests.cs` に足す（既存の `IsActive` / `IsTerminal` のテストはこの時点では触らない。
`IsActive` の意味が変わるのは Task 10）:

```csharp
    [Fact]
    public void AiJobStatus_KeepsTheExistingNumbers()
    {
        ((int)AiJobStatus.Pending).Should().Be(0);
        ((int)AiJobStatus.Running).Should().Be(1);
        ((int)AiJobStatus.Succeeded).Should().Be(4);
        ((int)AiJobStatus.Failed).Should().Be(5);
        ((int)AiJobStatus.Cancelled).Should().Be(6);
        ((int)AiJobStatus.WaitingForInput).Should().Be(7);
    }
```

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiSettingsTests|FullyQualifiedName~AiModelTests"`
Expected: コンパイルエラー（`WaitingForInput` と `PermissionMode` が無い）

- [ ] **Step 3: AiJobStatus に WaitingForInput を足す**

`src/MoTask.Core/Model/AiJobStatus.cs` の列挙に 1 行足すだけ（`AwaitingApproval` / `Suspended` と
`IsActive` の意味は、承認サブシステムを外す Task 10 で直す）:

```csharp
    Cancelled = 6,
    /// <summary>Stop フックが来て、人の入力を待っている。</summary>
    WaitingForInput = 7,
```

- [ ] **Step 4: 新しい口を書く**

`src/MoTask.Core/Ai/TerminalCommand.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// 組み立てた端末の起動コマンド。Arguments は生のコマンドライン文字列
/// （起動テンプレートが人の書いた 1 行なので、要素の配列には戻せない）。
/// </summary>
public sealed record TerminalCommand(string FileName, string Arguments, string WorkingDirectory)
{
    /// <summary>job.json と ErrorMessage に出す 1 行表示。</summary>
    public string Display => Arguments.Length == 0 ? FileName : $"{FileName} {Arguments}";
}
```

`src/MoTask.Core/Ai/SessionLaunchRequest.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// 1 回の端末起動。Resume が true なら --session-id ではなく --resume を渡す。
/// WorkingDirectory は cwd（プロジェクトの作業フォルダ）で、JobFolder とは別（仕様 §6）。
/// </summary>
public sealed record SessionLaunchRequest(Guid SessionId, string JobFolder, string WorkingDirectory, bool Resume);
```

`src/MoTask.Core/Ai/ISessionLauncher.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>端末で claude を起こす口（App の TerminalLauncher）。起こしたら手放す。</summary>
public interface ISessionLauncher
{
    /// <summary>ジョブ開始前の事前確認。claude が見つからなければ Fail(Messages.ClaudeNotFound)。</summary>
    Result CheckAvailable();

    /// <summary>起動コマンドを組み立てる。実行はしない（テストはここだけを見る）。</summary>
    Result<TerminalCommand> BuildCommand(SessionLaunchRequest request);

    /// <summary>端末を開いて手放す。プロセスは所有しない。</summary>
    Result Launch(TerminalCommand command);
}
```

`src/MoTask.Core/Ai/JobFolderRequest.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>ジョブフォルダを作るのに要る材料。置き場所は実装が設定から決める。</summary>
public sealed record JobFolderRequest(int JobId, string TaskTitle, string Instruction);
```

`src/MoTask.Core/Ai/JobDescriptor.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>job.json の中身。MoTask の DB が壊れても、フォルダだけで何のジョブか分かるように残す（仕様 §6）。</summary>
public sealed record JobDescriptor(
    int JobId,
    Guid SessionId,
    AiJobKind Kind,
    string WorkingDirectory,
    string LaunchCommand,
    DateTime StartedAt);
```

`src/MoTask.Core/Ai/IJobFolder.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>ジョブフォルダの実体（App の JobFolder）。</summary>
public interface IJobFolder
{
    /// <summary>
    /// フォルダを作り、instruction.md と hooks.json を書く。戻り値はフォルダの絶対パス。
    /// job.json は起動コマンドが決まってから WriteJobJson で書く。
    /// </summary>
    Result<string> Create(JobFolderRequest request);

    /// <summary>job.json を書く。書けなくてもジョブは続けるので、失敗は握り潰す。</summary>
    void WriteJobJson(string root, JobDescriptor descriptor);

    /// <summary>artifacts/ の実ファイル（絶対パス、名前順）。フォルダが無ければ空。</summary>
    IReadOnlyList<string> ListArtifacts(string root);
}
```

`src/MoTask.Core/Ai/JobEventSubscription.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// events.jsonl 1 本の追従。SkipLines は既に取り込んだ行数（events.jsonl は「1 行 = 1 イベント」
/// なので、保存済みイベントの件数がそのままオフセットになる）。
/// OnLine は行の順序どおり、直列に呼ばれる。OnProblem は追えなくなった理由（仕様 §12）。
/// </summary>
public sealed record JobEventSubscription(
    int JobId,
    string EventsPath,
    int SkipLines,
    Func<string, Task> OnLine,
    Func<string, Task> OnProblem);
```

`src/MoTask.Core/Ai/IJobEventSource.cs`:

```csharp
namespace MoTask.Core.Ai;

/// <summary>events.jsonl を追う口（App の JobEventWatcher）。ファイルがまだ無くても待つ。</summary>
public interface IJobEventSource
{
    void Follow(JobEventSubscription subscription);

    /// <summary>知らない JobId でも何もせずに返る（完了処理から何度呼ばれてもよい）。</summary>
    void StopFollowing(int jobId);
}
```

- [ ] **Step 5: AiSettings に 2 項目足す**

`src/MoTask.Core/Ai/AiSettings.cs` を次の内容に置き換える:

```csharp
namespace MoTask.Core.Ai;

/// <summary>
/// 仕様 §11「設定」。MaxConcurrentJobs / MaxTurns はターミナル実行では使わないので Task 10 で落とす。
/// </summary>
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    int MaxConcurrentJobs,
    string? ClaudeExecutablePath,
    string? Model,
    int MaxTurns,
    string PermissionMode,
    string? TerminalCommandTemplate)
{
    public const int DefaultMaxConcurrentJobs = 3;
    public const int DefaultMaxTurns = 50;

    /// <summary>--permission-mode の既定。既定で止まらず走り、危険な操作は端末で人に聞かれる（仕様 §3）。</summary>
    public const string DefaultPermissionMode = "auto";

    /// <summary>CLI が受け付ける値（仕様 §4.3）。この 6 つ以外は渡さない。</summary>
    public static readonly IReadOnlyList<string> PermissionModes =
        new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan" };

    public static string DefaultWorkingDirectoryPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MoTask");

    public static AiSettings Default() => new(
        DefaultWorkingDirectoryPath, DefaultMaxConcurrentJobs, null, null, DefaultMaxTurns,
        DefaultPermissionMode, null);
}
```

- [ ] **Step 6: JsonAiSettingsStore を追従させる**

`src/MoTask.Data/JsonAiSettingsStore.cs` の `Load` の `return new AiSettings(...)` を次に差し替える:

```csharp
            return new AiSettings(
                string.IsNullOrWhiteSpace(dto.DefaultWorkingDirectory) ? defaults.DefaultWorkingDirectory : dto.DefaultWorkingDirectory,
                dto.MaxConcurrentJobs is > 0 ? dto.MaxConcurrentJobs.Value : defaults.MaxConcurrentJobs,
                string.IsNullOrWhiteSpace(dto.ClaudeExecutablePath) ? null : dto.ClaudeExecutablePath,
                string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model,
                dto.MaxTurns is > 0 ? dto.MaxTurns.Value : defaults.MaxTurns,
                // 知らない値が入っていたら既定へ。CLI に弾かれる値を渡さない。
                dto.PermissionMode is { Length: > 0 } mode && AiSettings.PermissionModes.Contains(mode)
                    ? mode
                    : defaults.PermissionMode,
                string.IsNullOrWhiteSpace(dto.TerminalCommandTemplate) ? null : dto.TerminalCommandTemplate);
```

`Save` の `var dto = new Dto {...}` に 2 行足す:

```csharp
            PermissionMode = settings.PermissionMode,
            TerminalCommandTemplate = settings.TerminalCommandTemplate,
```

`Dto` に 2 行足す:

```csharp
        public string? PermissionMode { get; set; }
        public string? TerminalCommandTemplate { get; set; }
```

- [ ] **Step 7: 呼び出し側を追従させる**

`src/MoTask.App/ViewModels/AiSettingsViewModel.cs` の `Save()` にある `_store.Save(new AiSettings(...))` を差し替える（この画面の項目は Task 9 で足すので、ここでは今の値を保つだけ）:

```csharp
        var current = _store.Load();
        _store.Save(new AiSettings(dir, maxConcurrent, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model), maxTurns,
            current.PermissionMode, current.TerminalCommandTemplate));
```

`grep -rn "new AiSettings(" src tests` で位置指定のコンストラクタ呼び出しをすべて洗い出し、
末尾に 2 引数 `AiSettings.DefaultPermissionMode, null` を足す。`tests/MoTask.Data.Tests/SettingsStoreTests.cs` には
加えて次のテストを足す:

```csharp
    [Fact]
    public void Load_FallsBackWhenPermissionModeIsUnknown()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"PermissionMode":"すきなように","TerminalCommandTemplate":"pwsh -c {command}"}""");

        var settings = new JsonAiSettingsStore(path).Load();

        settings.PermissionMode.Should().Be("auto");
        settings.TerminalCommandTemplate.Should().Be("pwsh -c {command}");
    }

    [Fact]
    public void Save_RoundTripsPermissionModeAndTemplate()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonAiSettingsStore(path);

        store.Save(AiSettings.Default() with { PermissionMode = "plan", TerminalCommandTemplate = "wt -d {cwd} {command}" });

        var loaded = new JsonAiSettingsStore(path).Load();
        loaded.PermissionMode.Should().Be("plan");
        loaded.TerminalCommandTemplate.Should().Be("wt -d {cwd} {command}");
    }
```

> `SettingsStoreTests` に `_dir` という一時フォルダのフィールドが無ければ、既存のテストが使っている一時パスの作り方に合わせること。

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.Core src/MoTask.Data/JsonAiSettingsStore.cs src/MoTask.App/ViewModels/AiSettingsViewModel.cs tests
git commit -m "feat(core): add the launcher, job folder and event source seams"
```

---

## Task 5: Data — AiJob.JobFolder 列

**Files:**
- Modify: `src/MoTask.Core/Model/AiJob.cs`
- Modify: `src/MoTask.Data/MoTaskDbContext.cs`
- Create: `src/MoTask.Data/Migrations/<timestamp>_AddAiJobFolder.cs`（+ `.Designer.cs`、`dotnet ef` が作る）
- Modify: `src/MoTask.Data/Migrations/MoTaskDbContextModelSnapshot.cs`（`dotnet ef` が作る）
- Modify: `tests/MoTask.Data.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `AiJob.JobFolder`（`string`、既定 `""`）。Task 10 の `AiJobService` が書き、`TaskAiPanelViewModel` が読む。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/MigrationTests.cs` に足す:

```csharp
    [Fact]
    public async Task AiJob_JobFolder_RoundTrips()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var backlog = await ctx.Columns.FirstAsync();
            var now = DateTime.UtcNow;
            var task = new TaskItem { Title = "t", ColumnId = backlog.Id, CreatedAt = now, UpdatedAt = now };
            ctx.Tasks.Add(task);
            await ctx.SaveChangesAsync();
            ctx.AiJobs.Add(new AiJob
            {
                TaskId = task.Id, Kind = AiJobKind.Execute, Status = AiJobStatus.Running,
                SessionId = Guid.NewGuid(), JobFolder = @"C:\work\jobs\0001-t",
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.AiJobs.SingleAsync()).JobFolder.Should().Be(@"C:\work\jobs\0001-t");
        }
    }
```

> `MigrateAsync` だけでは列が作られないので、`DatabaseInitializer` を使う既存テストと違い、ここは `Columns.FirstAsync()` が空になる。既存の `Migrate_OnEmptyFile_CreatesSchema` と同じく `MigrateAsync` の後に既定ボードは作られない。列が要るので、このテストでは列も自分で作る:
> `var board = new Board { Name = "b" }; var backlog = new Column { Name = "c", Role = ColumnRole.Backlog, Order = 0 }; board.Columns.Add(backlog); ctx.Boards.Add(board); await ctx.SaveChangesAsync();` を `MigrateAsync` の直後に置き、`Columns.FirstAsync()` の行を消すこと。

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJob_JobFolder_RoundTrips"`
Expected: コンパイルエラー（`JobFolder` が無い）

- [ ] **Step 3: モデルと DbContext に足す**

`src/MoTask.Core/Model/AiJob.cs` の `WorkingDirectory` の下に足す:

```csharp
    /// <summary>ジョブフォルダの絶対パス（仕様 §6）。起動に失敗したジョブでは空のまま。</summary>
    public string JobFolder { get; set; } = "";
```

`src/MoTask.Data/MoTaskDbContext.cs` の `b.Entity<AiJob>` の中、`WorkingDirectory` の次の行に足す:

```csharp
            e.Property(x => x.JobFolder).IsRequired().HasDefaultValue("");
```

- [ ] **Step 4: マイグレーションを作る**

```bash
dotnet tool restore
dotnet ef migrations add AddAiJobFolder --project src/MoTask.Data --output-dir Migrations
```

生成された `Up` が `AiJobs` に `JobFolder`（TEXT, NOT NULL, defaultValue: ""）を足していることを確かめる。

- [ ] **Step 5: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.Core/Model/AiJob.cs src/MoTask.Data tests/MoTask.Data.Tests/MigrationTests.cs
git commit -m "feat(data): record the job folder on each AI job"
```

---

## Task 6: App — JobFolder（フォルダの実体と hooks.json）

**Files:**
- Create: `src/MoTask.App/Ai/HooksJson.cs`
- Create: `src/MoTask.App/Ai/JobFolder.cs`
- Modify: `src/MoTask.Core/Resources/Messages.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx`
- Test: `tests/MoTask.App.Tests/JobFolderTests.cs`
- Test: `tests/MoTask.App.Tests/HooksJsonTests.cs`

**Interfaces:**
- Consumes: `IJobFolder` / `JobFolderRequest` / `JobDescriptor` / `JobFolderPaths`（Task 2, 4）、`IAiSettingsStore`
- Produces:
  - `MoTask.App.Ai.HooksJson.Build(string hooksExecutable, string eventsPath) -> string`
  - `MoTask.App.Ai.JobFolder : IJobFolder`。コンストラクタ `JobFolder(IAiSettingsStore settings)`。テスト用に `internal string HooksExecutable { get; set; }`
  - `Messages.HooksExecutableNotFound`, `Messages.JobFolderFailedFormat`

- [ ] **Step 1: 文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の `</root>` の直前に足す:

```xml
  <data name="HooksExecutableNotFound" xml:space="preserve"><value>フックの実行ファイル（hooks\MoTask.Hooks.exe）が見つかりません。MoTask をビルドし直してください</value></data>
  <data name="JobFolderFailedFormat" xml:space="preserve"><value>ジョブフォルダを作成できません: {0}（{1}）</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の末尾（クラスの閉じかっこの直前）に足す:

```csharp
    public static string HooksExecutableNotFound => Get(nameof(HooksExecutableNotFound));
    public static string JobFolderFailedFormat => Get(nameof(JobFolderFailedFormat));
```

- [ ] **Step 2: hooks.json の失敗するテストを書く**

`tests/MoTask.App.Tests/HooksJsonTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// --settings に渡すファイル。形が崩れると CLI が黙ってフックを無視し、盤面が一切動かなくなる。
/// 生成物の形をここで固定する（仕様 §7, §13）。
/// </summary>
public class HooksJsonTests
{
    private const string Exe = @"C:\Program Files\MoTask\hooks\MoTask.Hooks.exe";
    private const string Events = @"C:\work\jobs\0042-見積り\events.jsonl";

    [Fact]
    public void Build_WiresTheSameCommandToTheFourEvents()
    {
        using var doc = JsonDocument.Parse(HooksJson.Build(Exe, Events));

        var hooks = doc.RootElement.GetProperty("hooks");
        hooks.EnumerateObject().Select(p => p.Name).Should()
            .Equal("SessionStart", "PostToolUse", "Stop", "SessionEnd");

        foreach (var entry in hooks.EnumerateObject())
        {
            var command = entry.Value[0].GetProperty("hooks")[0];
            command.GetProperty("type").GetString().Should().Be("command");
            command.GetProperty("command").GetString().Should().Be($"\"{Exe}\" \"{Events}\"");
        }
    }

    [Fact]
    public void Build_StaysReadableForHumans()
    {
        var json = HooksJson.Build(Exe, Events);

        // 人が開いて読めること（\u005C や \u30xx だらけにしない）。JSON としての \\ は残る。
        json.Should().Contain("MoTask.Hooks.exe").And.NotContain("u005C").And.NotContain("u898B");
    }
}
```

- [ ] **Step 3: JobFolder の失敗するテストを書く**

`tests/MoTask.App.Tests/JobFolderTests.cs`:

```csharp
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class JobFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly StubSettingsStore _settings;
    private readonly JobFolder _folder;
    private readonly string _hooksExe;

    public JobFolderTests()
    {
        Directory.CreateDirectory(_root);
        _hooksExe = Path.Combine(_root, "MoTask.Hooks.exe");
        File.WriteAllText(_hooksExe, "");
        _settings = new StubSettingsStore(AiSettings.Default() with { DefaultWorkingDirectory = _root });
        _folder = new JobFolder(_settings) { HooksExecutable = _hooksExe };
    }

    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }

    [Fact]
    public void Create_LaysOutTheFolderUnderTheDefaultWorkFolder()
    {
        var created = _folder.Create(new JobFolderRequest(42, "請求書の突合", "やること"));

        created.IsSuccess.Should().BeTrue();
        var paths = JobFolderPaths.For(created.Value!);
        paths.Root.Should().Be(Path.Combine(_root, "jobs", "0042-請求書の突合"));
        Directory.Exists(paths.ArtifactsDirectory).Should().BeTrue();
        File.Exists(paths.HooksJson).Should().BeTrue();
        File.ReadAllText(paths.InstructionMarkdown, new UTF8Encoding(false)).Should().Be("やること");
    }

    [Fact]
    public void Create_PointsTheHooksAtThisJobsEventLog()
    {
        var root = _folder.Create(new JobFolderRequest(1, "t", "i")).Value!;

        using var doc = JsonDocument.Parse(File.ReadAllText(JobFolderPaths.For(root).HooksJson));
        doc.RootElement.GetProperty("hooks").GetProperty("SessionEnd")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString()
            .Should().Contain(JobFolderPaths.For(root).EventsJsonl);
    }

    [Fact]
    public void Create_IsIdempotent_SoReopeningDoesNotFail()
    {
        _folder.Create(new JobFolderRequest(7, "t", "一回目")).IsSuccess.Should().BeTrue();

        var again = _folder.Create(new JobFolderRequest(7, "t", "二回目"));

        again.IsSuccess.Should().BeTrue();
        File.ReadAllText(JobFolderPaths.For(again.Value!).InstructionMarkdown).Should().Be("二回目");
    }

    [Fact]
    public void Create_FailsWhenTheHookExeIsMissing()
    {
        var folder = new JobFolder(_settings) { HooksExecutable = Path.Combine(_root, "no-such.exe") };

        var created = folder.Create(new JobFolderRequest(1, "t", "i"));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(MoTask.Core.Messages.HooksExecutableNotFound);
    }

    [Fact]
    public void WriteJobJson_RecordsWhatTheFolderIsFor()
    {
        var root = _folder.Create(new JobFolderRequest(3, "見積り", "i")).Value!;
        var session = Guid.NewGuid();

        _folder.WriteJobJson(root, new JobDescriptor(3, session, AiJobKind.Execute, @"D:\work\sample",
            "wt.exe -d ...", new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc)));

        using var doc = JsonDocument.Parse(File.ReadAllText(JobFolderPaths.For(root).JobJson));
        doc.RootElement.GetProperty("JobId").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("SessionId").GetString().Should().Be(session.ToString("D"));
        doc.RootElement.GetProperty("Kind").GetString().Should().Be("Execute");
        doc.RootElement.GetProperty("WorkingDirectory").GetString().Should().Be(@"D:\work\sample");
        doc.RootElement.GetProperty("LaunchCommand").GetString().Should().Be("wt.exe -d ...");
    }

    [Fact]
    public void ListArtifacts_ReturnsEveryRealFile_InNameOrder()
    {
        var root = _folder.Create(new JobFolderRequest(5, "t", "i")).Value!;
        var artifacts = JobFolderPaths.For(root).ArtifactsDirectory;
        File.WriteAllText(Path.Combine(artifacts, "b.md"), "b");
        // Write / Edit を通らないファイル（リダイレクトで作ったもの）も見えること
        File.WriteAllText(Path.Combine(artifacts, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(artifacts, "sub"));
        File.WriteAllText(Path.Combine(artifacts, "sub", "c.csv"), "c");

        var listed = _folder.ListArtifacts(root);

        listed.Select(Path.GetFileName).Should().Equal("a.txt", "b.md", "c.csv");
        listed.Should().OnlyContain(p => Path.IsPathRooted(p));
    }

    [Fact]
    public void ListArtifacts_IsEmptyWhenTheFolderIsGone()
    {
        _folder.ListArtifacts(Path.Combine(_root, "no-such-job")).Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 4: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderTests|FullyQualifiedName~HooksJsonTests"`
Expected: コンパイルエラー（`HooksJson` と `JobFolder` が無い）

- [ ] **Step 5: HooksJson を書く**

`src/MoTask.App/Ai/HooksJson.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.App.Ai;

/// <summary>
/// --settings に渡すフック定義（仕様 §7）。4 イベントすべてで同じ exe を呼び、引数は追記先 1 つ。
/// --setting-sources を渡さないので、利用者の user / project / local 設定はそのまま効く（仕様 §4.1）。
/// </summary>
public static class HooksJson
{
    private static readonly string[] Events = { "SessionStart", "PostToolUse", "Stop", "SessionEnd" };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 人が開いて読めるように、バックスラッシュや日本語を \uXXXX にしない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string hooksExecutable, string eventsPath)
    {
        var command = new { type = "command", command = $"\"{hooksExecutable}\" \"{eventsPath}\"" };
        var matcher = new[] { new { hooks = new[] { command } } };
        var hooks = new Dictionary<string, object>();
        foreach (var name in Events) hooks[name] = matcher;
        return JsonSerializer.Serialize(new { hooks }, Options);
    }
}
```

- [ ] **Step 6: JobFolder を書く**

`src/MoTask.App/Ai/JobFolder.cs`:

```csharp
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// ジョブフォルダの実体（仕様 §6）。cwd はここではなくプロジェクトの作業フォルダなので、
/// このフォルダは --add-dir で読み書きを許す。
/// </summary>
public sealed class JobFolder : IJobFolder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JobJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAiSettingsStore _settings;

    public JobFolder(IAiSettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>MoTask.App のビルドが hooks\ へ運ぶ exe。テストは差し替える。</summary>
    internal string HooksExecutable { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "hooks", "MoTask.Hooks.exe");

    public Result<string> Create(JobFolderRequest request)
    {
        // フックが無いと端末は動くが盤面が一切追従しない。黙って走らせず、開始時に止める。
        if (!File.Exists(HooksExecutable)) return Result.Fail<string>(Messages.HooksExecutableNotFound);

        var root = Path.Combine(
            _settings.Load().DefaultWorkingDirectory,
            JobFolderPaths.JobsDirectoryName,
            JobFolderPaths.FolderName(request.JobId, request.TaskTitle));
        var paths = JobFolderPaths.For(root);
        try
        {
            // 既にあっても作り直さない（--resume で開き直すときに同じフォルダへ戻る）
            Directory.CreateDirectory(paths.ArtifactsDirectory);
            File.WriteAllText(paths.InstructionMarkdown, request.Instruction, Utf8);
            File.WriteAllText(paths.HooksJson, HooksJson.Build(HooksExecutable, paths.EventsJsonl), Utf8);
            return Result.Ok(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail<string>(string.Format(Messages.JobFolderFailedFormat, root, ex.Message));
        }
    }

    public void WriteJobJson(string root, JobDescriptor descriptor)
    {
        try
        {
            File.WriteAllText(JobFolderPaths.For(root).JobJson,
                JsonSerializer.Serialize(descriptor, JobJsonOptions), Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // job.json は DB が壊れたときの保険。書けなくてもジョブは続ける。
        }
    }

    public IReadOnlyList<string> ListArtifacts(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Array.Empty<string>();
        var artifacts = JobFolderPaths.For(root).ArtifactsDirectory;
        try
        {
            if (!Directory.Exists(artifacts)) return Array.Empty<string>();
            return Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一覧が出ないだけで、ジョブの状態には関係しない
            return Array.Empty<string>();
        }
    }
}
```

- [ ] **Step 7: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderTests|FullyQualifiedName~HooksJsonTests"`
Expected: 9 件 PASS

- [ ] **Step 8: 全体が壊れていないことを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.App/Ai/HooksJson.cs src/MoTask.App/Ai/JobFolder.cs src/MoTask.Core/Resources tests/MoTask.App.Tests/JobFolderTests.cs tests/MoTask.App.Tests/HooksJsonTests.cs
git commit -m "feat(app): create the job folder and write the hook definitions"
```

---

## Task 7: App — TerminalLauncher（起動コマンドの組み立て）

**Files:**
- Create: `src/MoTask.App/Ai/CommandLine.cs`
- Create: `src/MoTask.App/Ai/TerminalLauncher.cs`
- Modify: `src/MoTask.Core/Resources/Messages.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx`
- Test: `tests/MoTask.App.Tests/CommandLineTests.cs`
- Test: `tests/MoTask.App.Tests/TerminalLauncherTests.cs`

**Interfaces:**
- Consumes: `ISessionLauncher` / `SessionLaunchRequest` / `TerminalCommand` / `JobFolderPaths` / `AiSettings`（Task 2, 4）、`ClaudeLocator`
- Produces:
  - `MoTask.App.Ai.CommandLine.Quote(string value) -> string`、`CommandLine.SplitFirstToken(string line) -> (string FileName, string Arguments)`
  - `MoTask.App.Ai.TerminalLauncher : ISessionLauncher`
    - `public TerminalLauncher(IAiSettingsStore settings)`
    - `internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable, Func<bool> hasWindowsTerminal)`
    - `internal const string WindowsTerminalTemplate`、`internal const string FallbackTemplate`
  - `Messages.TerminalLaunchFailedFormat`, `Messages.TerminalStartPromptFormat`

- [ ] **Step 1: 文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の `</root>` の直前に足す:

```xml
  <data name="TerminalLaunchFailedFormat" xml:space="preserve"><value>端末を起動できませんでした: {0}</value></data>
  <data name="TerminalStartPromptFormat" xml:space="preserve"><value>{0} を読んで作業を始めてください。調査に使ったファイルと成果物は {1} に出してください。</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の末尾に足す:

```csharp
    public static string TerminalLaunchFailedFormat => Get(nameof(TerminalLaunchFailedFormat));
    public static string TerminalStartPromptFormat => Get(nameof(TerminalStartPromptFormat));
```

- [ ] **Step 2: CommandLine の失敗するテストを書く**

`tests/MoTask.App.Tests/CommandLineTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData(@"C:\Program Files\claude.exe", "\"C:\\Program Files\\claude.exe\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("末尾が\\", "\"末尾が\\\\\"")]
    [InlineData("引用符\"入り", "\"引用符\\\"入り\"")]
    public void Quote_WrapsAndEscapesForWindows(string value, string expected)
    {
        CommandLine.Quote(value).Should().Be(expected);
    }

    [Fact]
    public void SplitFirstToken_TakesTheExecutable()
    {
        var (file, args) = CommandLine.SplitFirstToken("wt.exe -d \"C:\\a b\" cmd /k x");

        file.Should().Be("wt.exe");
        args.Should().Be("-d \"C:\\a b\" cmd /k x");
    }

    [Fact]
    public void SplitFirstToken_HandlesAQuotedExecutable()
    {
        var (file, args) = CommandLine.SplitFirstToken("\"C:\\Program Files\\wt.exe\" -d x");

        file.Should().Be("C:\\Program Files\\wt.exe");
        args.Should().Be("-d x");
    }

    [Fact]
    public void SplitFirstToken_HandlesAnExecutableOnItsOwn()
    {
        CommandLine.SplitFirstToken("cmd.exe").Should().Be(("cmd.exe", ""));
    }
}
```

- [ ] **Step 3: TerminalLauncher の失敗するテストを書く**

`tests/MoTask.App.Tests/TerminalLauncherTests.cs`:

```csharp
using System.IO;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>実起動はしない（仕様 §13）。組み立てたコマンドを文字列として見る。</summary>
public class TerminalLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly string _claude;
    private readonly StubSettingsStore _store;
    private readonly SessionLaunchRequest _request;

    public TerminalLauncherTests()
    {
        Directory.CreateDirectory(_dir);
        _claude = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(_claude, "");
        _store = new StubSettingsStore(AiSettings.Default() with { ClaudeExecutablePath = _claude });
        _request = new SessionLaunchRequest(
            new Guid("6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77"),
            @"C:\work\jobs\0042-見積り",
            @"D:\repo\sample",
            Resume: false);
    }

    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }

    private TerminalLauncher Launcher(bool hasWt = true)
        => new(_store, pathVariable: "", hasWindowsTerminal: () => hasWt);

    [Fact]
    public void CheckAvailable_FailsWhenClaudeIsMissing()
    {
        _store.Save(_store.Load() with { ClaudeExecutablePath = Path.Combine(_dir, "no-such.exe") });

        Launcher().CheckAvailable().Error.Should().Be(Messages.ClaudeNotFound);
    }

    [Fact]
    public void CheckAvailable_SucceedsWhenClaudeIsThere()
    {
        Launcher().CheckAvailable().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void BuildCommand_UsesWindowsTerminalWithTheProjectAsCwd()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("wt.exe");
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
        command.Arguments.Should().StartWith("-d \"D:\\repo\\sample\" cmd /k ");
    }

    [Fact]
    public void BuildCommand_PassesTheHookSettingsSessionIdPermissionModeAndJobFolder()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should().Contain("--settings\" \"C:\\work\\jobs\\0042-見積り\\hooks.json\"");
        command.Arguments.Should().Contain("--session-id\" \"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77\"");
        command.Arguments.Should().Contain("--permission-mode\" \"auto\"");
        command.Arguments.Should().Contain("--add-dir\" \"C:\\work\\jobs\\0042-見積り\"");
    }

    [Fact]
    public void BuildCommand_PointsThePromptAtTheInstructionAndTheArtifactsFolder()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should()
            .Contain(@"C:\work\jobs\0042-見積り\instruction.md")
            .And.Contain(@"C:\work\jobs\0042-見積り\artifacts");
    }

    [Fact]
    public void BuildCommand_NeverPassesTheArgumentsTheSpecForbids()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should()
            .NotContain("--setting-sources").And.NotContain("--permission-prompt-tool")
            .And.NotContain("--tools").And.NotContain("--max-turns")
            .And.NotContain("--strict-mcp-config").And.NotContain("--mcp-config");
    }

    [Fact]
    public void BuildCommand_OmitsTheModelUnlessItIsConfigured()
    {
        Launcher().BuildCommand(_request).Value!.Arguments.Should().NotContain("--model");

        _store.Save(_store.Load() with { Model = "claude-opus-5" });
        Launcher().BuildCommand(_request).Value!.Arguments.Should().Contain("--model\" \"claude-opus-5\"");
    }

    [Fact]
    public void BuildCommand_ResumesWithTheSameSessionId()
    {
        var command = Launcher().BuildCommand(_request with { Resume = true }).Value!;

        command.Arguments.Should()
            .Contain("--resume\" \"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77\"")
            .And.NotContain("--session-id");
    }

    [Fact]
    public void BuildCommand_FallsBackToCmdWhenWindowsTerminalIsMissing()
    {
        var command = Launcher(hasWt: false).BuildCommand(_request).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/k ");
        // wt が無いので cwd は ProcessStartInfo 側で渡す
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
    }

    [Fact]
    public void BuildCommand_HonoursTheConfiguredTemplate()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "pwsh.exe -NoExit -Command {command}" });

        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("pwsh.exe");
        command.Arguments.Should().StartWith("-NoExit -Command ");
        command.Arguments.Should().Contain("--session-id");
    }

    [Fact]
    public void BuildCommand_SubstitutesCwdInTheConfiguredTemplate()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "wt.exe -d \"{cwd}\" wsl {command}" });

        Launcher().BuildCommand(_request).Value!.Arguments.Should().StartWith("-d \"D:\\repo\\sample\" wsl ");
    }

    [Fact]
    public void BuildCommand_FailsWhenClaudeIsMissing()
    {
        _store.Save(_store.Load() with { ClaudeExecutablePath = Path.Combine(_dir, "no-such.exe") });

        var command = Launcher().BuildCommand(_request);

        command.IsSuccess.Should().BeFalse();
        command.Error.Should().Be(Messages.ClaudeNotFound);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 4: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~CommandLineTests|FullyQualifiedName~TerminalLauncherTests"`
Expected: コンパイルエラー（`CommandLine` と `TerminalLauncher` が無い）

- [ ] **Step 5: CommandLine を書く**

`src/MoTask.App/Ai/CommandLine.cs`:

```csharp
using System.Text;

namespace MoTask.App.Ai;

/// <summary>
/// Windows のコマンドライン 1 本を組み立て／分解する。起動テンプレートが人の書いた 1 行なので、
/// ProcessStartInfo.ArgumentList には載せられず、自分で引用符を付ける必要がある。
/// </summary>
public static class CommandLine
{
    /// <summary>CommandLineToArgvW の規則に沿って 1 要素を引用する。</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 8).Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                // " の直前のバックスラッシュは 2 倍にしてから \" を置く
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        // 閉じ " の直前のバックスラッシュも 2 倍にする
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>1 行のコマンドラインを（実行ファイル, 残りの引数）に割る。引用符付きの実行ファイルも扱う。</summary>
    public static (string FileName, string Arguments) SplitFirstToken(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0) return ("", "");

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return (text[1..], "");
            return (text[1..close], text[(close + 1)..].TrimStart());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].TrimStart());
    }
}
```

- [ ] **Step 6: TerminalLauncher を書く**

`src/MoTask.App/Ai/TerminalLauncher.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 端末で claude を対話起動して手放す（仕様 §7）。プロセスは所有しないので、
/// 起動したハンドルはその場で捨てる。
/// </summary>
public sealed class TerminalLauncher : ISessionLauncher
{
    /// <summary>wt.exe があるときの既定。cwd は wt に渡す（新しいタブがそこで開く）。</summary>
    internal const string WindowsTerminalTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}";

    /// <summary>wt.exe が無い環境の逃げ道。cwd は ProcessStartInfo 側で渡す。</summary>
    internal const string FallbackTemplate = "cmd.exe /k {command}";

    private readonly IAiSettingsStore _settings;
    private readonly string? _pathVariable;
    private readonly Func<bool> _hasWindowsTerminal;

    public TerminalLauncher(IAiSettingsStore settings)
        : this(settings, null, () => FindWindowsTerminal() is not null)
    {
    }

    internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable, Func<bool> hasWindowsTerminal)
    {
        _settings = settings;
        _pathVariable = pathVariable;
        _hasWindowsTerminal = hasWindowsTerminal;
    }

    public Result CheckAvailable()
        => ClaudeLocator.Find(_settings.Load().ClaudeExecutablePath, _pathVariable) is null
            ? Result.Fail(Messages.ClaudeNotFound)
            : Result.Ok();

    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        var settings = _settings.Load();
        var claude = ClaudeLocator.Find(settings.ClaudeExecutablePath, _pathVariable);
        if (claude is null) return Result.Fail<TerminalCommand>(Messages.ClaudeNotFound);

        var paths = JobFolderPaths.For(request.JobFolder);
        var parts = new List<string> { claude, "--settings", paths.HooksJson };

        // 再開は --session-id ではなく --resume。同じ ID を両方に渡さない。
        parts.Add(request.Resume ? "--resume" : "--session-id");
        parts.Add(request.SessionId.ToString("D"));

        parts.Add("--permission-mode");
        parts.Add(settings.PermissionMode);
        // cwd はプロジェクト。ジョブフォルダはここで読み書きを許す（仕様 §6）。
        parts.Add("--add-dir");
        parts.Add(request.JobFolder);
        if (settings.Model is { Length: > 0 } model)
        {
            parts.Add("--model");
            parts.Add(model.Trim());
        }
        // 指示文そのものは渡さない。長文の引用符・改行をコマンドラインに持ち込まないため（仕様 §7）。
        parts.Add(string.Format(Messages.TerminalStartPromptFormat, paths.InstructionMarkdown, paths.ArtifactsDirectory));

        var inner = string.Join(" ", parts.Select(CommandLine.Quote));
        var template = settings.TerminalCommandTemplate is { Length: > 0 } configured
            ? configured
            : _hasWindowsTerminal() ? WindowsTerminalTemplate : FallbackTemplate;
        var line = template.Replace("{cwd}", request.WorkingDirectory).Replace("{command}", inner);

        var (fileName, arguments) = CommandLine.SplitFirstToken(line);
        return Result.Ok(new TerminalCommand(fileName, arguments, request.WorkingDirectory));
    }

    public Result Launch(TerminalCommand command)
    {
        try
        {
            // UseShellExecute = true で自前のウィンドウを持たせる。返るハンドルは使わないので閉じる。
            using var started = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = command.WorkingDirectory,
            });
            return Result.Ok();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 何で失敗したかより「何を実行しようとしたか」が要る（仕様 §12）
            return Result.Fail(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
        }
    }

    private static string? FindWindowsTerminal()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH に不正な文字が混ざっていても探索を続ける
            }
        }
        return null;
    }
}
```

- [ ] **Step 7: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~CommandLineTests|FullyQualifiedName~TerminalLauncherTests"`
Expected: 19 件 PASS

- [ ] **Step 8: 全体が壊れていないことを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.App/Ai/CommandLine.cs src/MoTask.App/Ai/TerminalLauncher.cs src/MoTask.Core/Resources tests/MoTask.App.Tests/CommandLineTests.cs tests/MoTask.App.Tests/TerminalLauncherTests.cs
git commit -m "feat(app): build the terminal launch command for claude"
```

---

## Task 8: App — JobEventWatcher（events.jsonl の追従）

**Files:**
- Create: `src/MoTask.App/Ai/JobEventWatcher.cs`
- Modify: `src/MoTask.Core/Resources/Messages.cs`
- Modify: `src/MoTask.Core/Resources/Messages.resx`
- Test: `tests/MoTask.App.Tests/JobEventWatcherTests.cs`

**Interfaces:**
- Consumes: `IJobEventSource` / `JobEventSubscription`（Task 4）
- Produces:
  - `MoTask.App.Ai.JobEventWatcher : IJobEventSource, IDisposable`
    - `public JobEventWatcher()`（500 ms 間隔）
    - `internal JobEventWatcher(TimeSpan pollInterval)`
  - `Messages.EventsFileGoneFormat`

- [ ] **Step 1: 文言を足す**

`src/MoTask.Core/Resources/Messages.resx` の `</root>` の直前に足す:

```xml
  <data name="EventsFileGoneFormat" xml:space="preserve"><value>イベントログを追えなくなりました（削除されたか作り直されました）: {0}</value></data>
```

`src/MoTask.Core/Resources/Messages.cs` の末尾に足す:

```csharp
    public static string EventsFileGoneFormat => Get(nameof(EventsFileGoneFormat));
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/MoTask.App.Tests/JobEventWatcherTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class JobEventWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly string _events;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly JobEventWatcher _watcher = new(TimeSpan.FromMilliseconds(20));

    public JobEventWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _events = Path.Combine(_dir, "events.jsonl");
    }

    private JobEventSubscription Subscription(int skipLines = 0) => new(
        JobId: 1, EventsPath: _events, SkipLines: skipLines,
        OnLine: line => { _lines.Enqueue(line); return Task.CompletedTask; },
        OnProblem: message => { _problems.Enqueue(message); return Task.CompletedTask; });

    private static void Append(string path, params string[] lines)
        => File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));

    /// <summary>ポーリングなので、条件が満たされるまで少し待つ。</summary>
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        condition().Should().BeTrue("5 秒以内に条件が満たされるはず");
    }

    [Fact]
    public async Task Follow_PicksUpLinesAppendedAfterItStarted()
    {
        _watcher.Follow(Subscription());

        Append(_events, "{\"a\":1}", "{\"a\":2}");

        await EventuallyAsync(() => _lines.Count == 2);
        _lines.Should().Equal("{\"a\":1}", "{\"a\":2}");
    }

    [Fact]
    public async Task Follow_WaitsForAFileThatDoesNotExistYet()
    {
        _watcher.Follow(Subscription());
        await Task.Delay(60);

        Append(_events, "{\"late\":true}");

        await EventuallyAsync(() => _lines.Count == 1);
        _problems.Should().BeEmpty("まだ書かれていないだけなので、問題ではない");
    }

    [Fact]
    public async Task Follow_SkipsTheLinesAlreadyRecorded()
    {
        Append(_events, "{\"a\":1}", "{\"a\":2}", "{\"a\":3}");

        _watcher.Follow(Subscription(skipLines: 2));

        await EventuallyAsync(() => _lines.Count == 1);
        _lines.Should().Equal("{\"a\":3}");
    }

    [Fact]
    public async Task Follow_IgnoresAPartiallyWrittenTrailingLine()
    {
        _watcher.Follow(Subscription());
        File.AppendAllText(_events, "{\"whole\":1}\n{\"half\":", new UTF8Encoding(false));

        await EventuallyAsync(() => _lines.Count == 1);
        await Task.Delay(60);
        _lines.Should().Equal("{\"whole\":1}");

        File.AppendAllText(_events, "2}\n", new UTF8Encoding(false));

        await EventuallyAsync(() => _lines.Count == 2);
        _lines.Should().Equal("{\"whole\":1}", "{\"half\":2}");
    }

    [Fact]
    public async Task Follow_ReadsJapaneseAsUtf8()
    {
        _watcher.Follow(Subscription());

        Append(_events, "{\"last_assistant_message\":\"見積りをまとめました\"}");

        await EventuallyAsync(() => _lines.Count == 1);
        _lines.Single().Should().Contain("見積りをまとめました");
    }

    [Fact]
    public async Task Follow_ReportsWhenTheFileDisappears()
    {
        _watcher.Follow(Subscription());
        Append(_events, "{\"a\":1}");
        await EventuallyAsync(() => _lines.Count == 1);

        File.Delete(_events);

        await EventuallyAsync(() => _problems.Count == 1);
        _problems.Single().Should().Contain(_events);
    }

    [Fact]
    public async Task StopFollowing_StopsDelivering()
    {
        _watcher.Follow(Subscription());
        Append(_events, "{\"a\":1}");
        await EventuallyAsync(() => _lines.Count == 1);

        _watcher.StopFollowing(1);
        Append(_events, "{\"a\":2}");
        await Task.Delay(120);

        _lines.Should().HaveCount(1);
    }

    [Fact]
    public void StopFollowing_IsSafeForAJobThatWasNeverFollowed()
    {
        _watcher.Invoking(w => w.StopFollowing(999)).Should().NotThrow();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobEventWatcherTests"`
Expected: コンパイルエラー（`JobEventWatcher` が無い）

- [ ] **Step 4: JobEventWatcher を書く**

`src/MoTask.App/Ai/JobEventWatcher.cs`:

```csharp
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// events.jsonl を追う（仕様 §5）。フックが追記するだけのファイルなので、最後に読んだバイト位置から
/// 差分を読み直すのが本体で、通知は要らない。FileSystemWatcher は追記の通知を取りこぼす／重複させる
/// うえ、結局この読み直しが必要になるので使わない。小さなファイル 1 本を 500 ms ごとに見るだけ。
/// </summary>
public sealed class JobEventWatcher : IJobEventSource, IDisposable
{
    private sealed class Follower
    {
        public required JobEventSubscription Subscription { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<int, Follower> _followers = new();
    private readonly TimeSpan _pollInterval;
    private bool _disposed;

    public JobEventWatcher() : this(TimeSpan.FromMilliseconds(500))
    {
    }

    internal JobEventWatcher(TimeSpan pollInterval)
    {
        _pollInterval = pollInterval;
    }

    public void Follow(JobEventSubscription subscription)
    {
        if (_disposed) return;
        StopFollowing(subscription.JobId);
        var follower = new Follower { Subscription = subscription, Cancellation = new CancellationTokenSource() };
        _followers[subscription.JobId] = follower;
        _ = Task.Run(() => LoopAsync(follower));
    }

    public void StopFollowing(int jobId)
    {
        if (!_followers.TryRemove(jobId, out var follower)) return;
        try
        {
            follower.Cancellation.Cancel();
        }
        catch (Exception)
        {
            // 追従をやめるだけ。止め方の失敗で呼び出し側を落とさない。
        }
    }

    private async Task LoopAsync(Follower follower)
    {
        var subscription = follower.Subscription;
        var token = follower.Cancellation.Token;
        long offset = 0;
        var remaining = subscription.SkipLines;
        var seenTheFile = false;
        var partial = "";

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var info = new FileInfo(subscription.EventsPath);
                if (!info.Exists)
                {
                    // まだ 1 行も書かれていないだけなら待つ。一度見えていたのに消えたのは事故（仕様 §12）。
                    if (!seenTheFile) continue;
                    await ReportAsync(subscription).ConfigureAwait(false);
                    return;
                }
                seenTheFile = true;
                // 追記専用のはずのファイルが縮んだ＝作り直された。同じ扱いで追従をやめる。
                if (info.Length < offset)
                {
                    await ReportAsync(subscription).ConfigureAwait(false);
                    return;
                }
                if (info.Length == offset) continue;

                string text;
                try
                {
                    using var stream = new FileStream(subscription.EventsPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(offset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Utf8);
                    text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    offset = stream.Position;
                }
                catch (IOException)
                {
                    continue; // フックが書いている最中。次の周回で読み直す。
                }

                // 末尾の改行までが「完成した行」。途中まで書かれた行は次の周回へ持ち越す。
                partial += text;
                var lastBreak = partial.LastIndexOf('\n');
                if (lastBreak < 0) continue;
                var complete = partial[..lastBreak];
                partial = partial[(lastBreak + 1)..];

                foreach (var raw in complete.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (remaining > 0)
                    {
                        remaining--;
                        continue;
                    }
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        await subscription.OnLine(line).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 1 行の取り込み失敗で追従を止めない（取り込み側が自分で警告を出す）
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopFollowing / Dispose
        }
    }

    private static async Task ReportAsync(JobEventSubscription subscription)
    {
        try
        {
            await subscription.OnProblem(string.Format(Messages.EventsFileGoneFormat, subscription.EventsPath))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 通知が失敗しても、この追従はもう終わっている
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var jobId in _followers.Keys.ToList()) StopFollowing(jobId);
    }
}
```

- [ ] **Step 5: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobEventWatcherTests"`
Expected: 8 件 PASS

- [ ] **Step 6: 全体が壊れていないことを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.App/Ai/JobEventWatcher.cs src/MoTask.Core/Resources tests/MoTask.App.Tests/JobEventWatcherTests.cs
git commit -m "feat(app): follow events.jsonl from the recorded offset"
```

---

## Task 9: 表示側の先行対応（ログの整形・入力待ちバッジ・設定項目）

承認サブシステムを外す前に、フックのイベントを読める形にしておく。ここまでは追加だけで、既存の動作は壊さない。

**Files:**
- Modify: `src/MoTask.App/ViewModels/AiJobEventFormatter.cs`
- Modify: `src/MoTask.App/ViewModels/TaskCardViewModel.cs`
- Modify: `src/MoTask.App/Views/TaskCardView.xaml`
- Modify: `src/MoTask.App/ViewModels/AiSettingsViewModel.cs`
- Modify: `src/MoTask.App/Views/AiSettingsDialog.xaml`
- Modify: `src/MoTask.App/Resources/Strings.cs`
- Modify: `src/MoTask.App/Resources/Strings.resx`
- Test: `tests/MoTask.App.Tests/AiJobEventFormatterTests.cs`
- Test: `tests/MoTask.App.Tests/TaskCardAiBadgeTests.cs`
- Test: `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `AiJobEventKind.SessionStarted / SessionEnded / TurnEnded`（Task 3）、`AiJobStatus.WaitingForInput`、`AiSettings.PermissionMode / TerminalCommandTemplate`（Task 4）
- Produces:
  - `AiJobEventFormatter.ResultText(IEnumerable<AiJobEvent>)` が Stop の `last_assistant_message` を返す
  - `TaskCardViewModel.IsWaitingForInput`（`IsAwaitingApproval` を置き換える）
  - `AiSettingsViewModel.PermissionMode`（`string`）、`AiSettingsViewModel.PermissionModes`（`IReadOnlyList<string>`）、`AiSettingsViewModel.TerminalCommandTemplate`（`string`）

- [ ] **Step 1: 文言を足す／消す**

`src/MoTask.App/Resources/Strings.resx` に足す:

```xml
  <data name="AiStatusWaitingForInput" xml:space="preserve"><value>入力待ち</value></data>
  <data name="AiLogSessionStartedFormat" xml:space="preserve"><value>セッション開始（{0}）</value></data>
  <data name="AiLogSessionEndedFormat" xml:space="preserve"><value>セッション終了（{0}）</value></data>
  <data name="AiLogTurnEnded" xml:space="preserve"><value>応答が終わりました（入力待ち）</value></data>
  <data name="AiLogTurnEndedFormat" xml:space="preserve"><value>応答が終わりました: {0}</value></data>
  <data name="SettingsPermissionMode" xml:space="preserve"><value>ツール承認のモード（--permission-mode）。承認そのものは利用者の settings.json に従います</value></data>
  <data name="SettingsTerminalTemplate" xml:space="preserve"><value>端末の起動コマンド（空なら既定。{cwd} と {command} が置き換わります）</value></data>
  <data name="PermissionModeInvalid" xml:space="preserve"><value>権限モードは一覧から選んでください</value></data>
```

`AiStatusPending` の値を `AI 待機中` から `端末を開いています` に変える（Pending は「端末が開くまで」の意味になった）。

- [ ] **Step 2: 失敗するテストを書く（整形）**

`tests/MoTask.App.Tests/AiJobEventFormatterTests.cs` に足す:

```csharp
    private static AiJobEvent Event(AiJobEventKind kind, string payload, string? toolName = null)
        => new() { Id = 1, JobId = 1, Seq = 1, At = new DateTime(2026, 9, 5, 3, 4, 0, DateTimeKind.Utc), Kind = kind, ToolName = toolName, Payload = payload };

    [Fact]
    public void Format_SessionStarted_ShowsTheSource()
    {
        var line = AiJobEventFormatter.Format(
            Event(AiJobEventKind.SessionStarted, """{"hook_event_name":"SessionStart","source":"startup"}"""));

        line.Text.Should().Be("セッション開始（startup）");
        line.IsError.Should().BeFalse();
    }

    [Fact]
    public void Format_SessionEnded_ShowsTheReason()
    {
        var line = AiJobEventFormatter.Format(
            Event(AiJobEventKind.SessionEnded, """{"hook_event_name":"SessionEnd","reason":"exit"}"""));

        line.Text.Should().Be("セッション終了（exit）");
    }

    [Fact]
    public void Format_TurnEnded_ShowsTheLastMessage()
    {
        var line = AiJobEventFormatter.Format(
            Event(AiJobEventKind.TurnEnded, """{"hook_event_name":"Stop","last_assistant_message":"見積りをまとめました。\n根拠は artifacts に置きました。"}"""));

        line.Text.Should().Be("応答が終わりました: 見積りをまとめました。");
    }

    [Fact]
    public void Format_TurnEnded_WithoutAMessage_StillReads()
    {
        AiJobEventFormatter.Format(Event(AiJobEventKind.TurnEnded, """{"hook_event_name":"Stop"}"""))
            .Text.Should().Be("応答が終わりました（入力待ち）");
    }

    [Fact]
    public void Format_ToolUse_ReadsTheHookToolInput()
    {
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse,
            """{"hook_event_name":"PostToolUse","tool_name":"Bash","tool_input":{"command":"dotnet test"}}""",
            toolName: "Bash"));

        line.Text.Should().Be("▶ Bash: dotnet test");
    }

    [Fact]
    public void Format_ToolUse_StillReadsOldStreamJsonRows()
    {
        // 作り替え前に保存された行も、同じ画面に並ぶ
        var line = AiJobEventFormatter.Format(Event(AiJobEventKind.ToolUse,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\a.md"}}]}}""",
            toolName: "Write"));

        line.Text.Should().Be(@"▶ Write: C:\a.md");
    }

    [Fact]
    public void ResultText_TakesTheLatestStopMessage()
    {
        var events = new[]
        {
            Event(AiJobEventKind.TurnEnded, """{"last_assistant_message":"途中経過"}"""),
            Event(AiJobEventKind.ToolUse, """{"tool_name":"Read"}""", "Read"),
            Event(AiJobEventKind.TurnEnded, """{"last_assistant_message":"できました"}"""),
        };

        AiJobEventFormatter.ResultText(events).Should().Be("できました");
    }

    [Fact]
    public void ResultText_IsNullWhenNothingHasBeenSaidYet()
    {
        AiJobEventFormatter.ResultText(new[] { Event(AiJobEventKind.SessionStarted, "{}") }).Should().BeNull();
    }
```

承認イベント（`PermissionAsked` / `PermissionDecided`）の整形を確かめるテストは削除する。
`ArtifactPathOf` / `ArtifactPaths` を使うテストは Task 10 まで残す（本体をまだ消さないため）。

- [ ] **Step 3: 失敗するテストを書く（バッジ・設定）**

`tests/MoTask.App.Tests/TaskCardAiBadgeTests.cs` の `AwaitingApproval` / `Suspended` を使うテストを
次に置き換える（この 2 つの状態はもうバッジに出ない）:

```csharp
    [Fact]
    public void SetAiState_WaitingForInput_ShowsTheInputBadge()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Execute, AiJobStatus.WaitingForInput, 3, null, "", ""));

        card.HasAiBadge.Should().BeTrue();
        card.IsWaitingForInput.Should().BeTrue();
        card.AiBadgeText.Should().Be("入力待ち");
    }

    [Fact]
    public void SetAiState_Running_ShowsTheKindAndTurns()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Research, AiJobStatus.Running, 2, null, "", ""));

        card.IsWaitingForInput.Should().BeFalse();
        card.AiBadgeText.Should().Be("AI 調査中 · 2 ターン");
    }

    [Fact]
    public void SetAiState_Pending_ShowsNoBadgeYet()
    {
        var card = new TaskCardViewModel(new TaskItem { Id = 1, Title = "t" });

        card.SetAiState(new AiJobSnapshot(1, 1, AiJobKind.Execute, AiJobStatus.Pending, 0, null, "", ""));

        card.HasAiBadge.Should().BeFalse();
    }
```

> `AiJobSnapshot` の並びは Task 10 で `(JobId, TaskId, Kind, Status, TurnCount, ErrorMessage, WorkingDirectory, JobFolder)` になる。Task 9 の時点ではまだ `TotalCostUsd` が 6 番目にあるので、このテストは `new AiJobSnapshot(1, 1, AiJobKind.Execute, AiJobStatus.WaitingForInput, 3, null, null, "")` の形で書き、Task 10 で引数を詰め直すこと。

`tests/MoTask.App.Tests/AiSettingsViewModelTests.cs` に足す:

```csharp
    [Fact]
    public void Save_PersistsThePermissionModeAndTemplate()
    {
        var store = new InMemoryAiSettingsStore();
        var vm = NewViewModel(store);

        vm.PermissionMode = "plan";
        vm.TerminalCommandTemplate = "pwsh.exe -NoExit -Command {command}";
        vm.SaveCommand.Execute(null);

        store.Load().PermissionMode.Should().Be("plan");
        store.Load().TerminalCommandTemplate.Should().Be("pwsh.exe -NoExit -Command {command}");
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Save_RejectsAPermissionModeTheCliDoesNotKnow()
    {
        var store = new InMemoryAiSettingsStore();
        var vm = NewViewModel(store);

        vm.PermissionMode = "すきなように";
        vm.SaveCommand.Execute(null);

        vm.ErrorMessage.Should().Be("権限モードは一覧から選んでください");
    }

    [Fact]
    public void Save_TreatsABlankTemplateAsTheDefault()
    {
        var store = new InMemoryAiSettingsStore();
        var vm = NewViewModel(store);

        vm.TerminalCommandTemplate = "   ";
        vm.SaveCommand.Execute(null);

        store.Load().TerminalCommandTemplate.Should().BeNull();
    }

    [Fact]
    public void PermissionModes_AreOfferedForTheDropDown()
    {
        NewViewModel(new InMemoryAiSettingsStore()).PermissionModes.Should().Equal(AiSettings.PermissionModes);
    }
```

> `NewViewModel` / `InMemoryAiSettingsStore` は既存の `AiSettingsViewModelTests` が使っているヘルパーをそのまま使う。
> `AiSettings.PermissionModes` を参照するので、ファイルの先頭に `using MoTask.Core.Ai;` があることを確かめること。

- [ ] **Step 4: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJobEventFormatterTests|FullyQualifiedName~TaskCardAiBadgeTests|FullyQualifiedName~AiSettingsViewModelTests"`
Expected: コンパイルエラー（新しいプロパティが無い）

- [ ] **Step 5: 整形を書き換える**

`src/MoTask.App/ViewModels/AiJobEventFormatter.cs`:

1. `using MoTask.Core.Ai;` を消す（`PermissionPattern` を使わなくなる）。
2. `ArtifactPathOf` と `ArtifactPaths` は **まだ消さない**（`TaskAiPanelViewModel` がまだ呼んでいる。
   成果物一覧をサービスから取るようにする Task 10 で、呼び出し側と一緒に消す）。
3. `ResultText` を差し替える:

```csharp
    /// <summary>
    /// いま出せる「結果」。対話なので確定した最終回答は無い。直近の Stop フックが持つ
    /// last_assistant_message を出す（作り替え前に保存された result 行も拾う）。
    /// </summary>
    public static string? ResultText(IEnumerable<AiJobEvent> events)
    {
        var list = events as IReadOnlyList<AiJobEvent> ?? events.ToList();
        var stop = list.LastOrDefault(e => e.Kind == AiJobEventKind.TurnEnded);
        if (stop is not null)
        {
            using var stopped = TryParse(stop.Payload);
            var message = stopped is null ? null : ReadString(stopped.RootElement, "last_assistant_message");
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }

        var result = list.LastOrDefault(e => e.Kind == AiJobEventKind.Result);
        if (result is null) return null;
        using var doc = TryParse(result.Payload);
        return doc is null ? null : ReadString(doc.RootElement, "result");
    }
```

4. `Body` の `switch` に 3 つ足す:

```csharp
            AiJobEventKind.SessionStarted => (string.Format(Strings.AiLogSessionStartedFormat, ReadString(root, "source") ?? "?"), false),
            AiJobEventKind.SessionEnded => (string.Format(Strings.AiLogSessionEndedFormat, ReadString(root, "reason") ?? "?"), false),
            AiJobEventKind.TurnEnded => (TurnEnded(root), false),
```

そして `PermissionAsked` / `PermissionDecided` の分岐と `PermissionAsked` / `PermissionDecided` メソッドを消す。

5. `TurnEnded` を足す:

```csharp
    private static string TurnEnded(JsonElement root)
    {
        var message = FirstLine(ReadString(root, "last_assistant_message") ?? "");
        return message.Length == 0 ? Strings.AiLogTurnEnded : string.Format(Strings.AiLogTurnEndedFormat, message);
    }
```

6. `ToolUse` をフックのペイロード優先に直す:

```csharp
    private static string ToolUse(JsonElement root, string? toolName)
    {
        var name = toolName ?? ReadString(root, "tool_name") ?? "?";
        var summary = ToolInput(root, toolName) is { } input ? ArgumentSummary(name, input) : null;
        return summary is null
            ? string.Format(Strings.AiLogToolUseNoArg, name)
            : string.Format(Strings.AiLogToolUseFormat, name, summary);
    }

    /// <summary>
    /// フックの PostToolUse は root.tool_input。作り替え前に保存された stream-json の行は
    /// message.content[].input なので、そちらへも落ちる。
    /// </summary>
    private static JsonElement? ToolInput(JsonElement root, string? toolName)
    {
        if (root.TryGetProperty("tool_input", out var hookInput) && hookInput.ValueKind == JsonValueKind.Object) return hookInput;
        var block = FindToolUse(root, toolName);
        return block is { } b && b.TryGetProperty("input", out var input) ? input : null;
    }
```

- [ ] **Step 6: カードのバッジを直す**

`src/MoTask.App/ViewModels/TaskCardViewModel.cs`:

- `[ObservableProperty] private bool _isAwaitingApproval;` を `[ObservableProperty] private bool _isWaitingForInput;` に変える。
- `SetAiState` を差し替える:

```csharp
    public void SetAiState(AiJobSnapshot? job)
    {
        if (job is null || job.Status.IsTerminal() || job.Status == AiJobStatus.Pending)
        {
            AiBadgeText = null;
            IsWaitingForInput = false;
            HasAiBadge = false;
            return;
        }

        IsWaitingForInput = job.Status == AiJobStatus.WaitingForInput;
        AiBadgeText = IsWaitingForInput
            ? Strings.AiStatusWaitingForInput
            : string.Format(CultureInfo.CurrentCulture, Strings.AiBadgeTurnsFormat,
                job.Kind == AiJobKind.Research ? Strings.AiStatusResearching : Strings.AiStatusExecuting, job.TurnCount);
        HasAiBadge = true;
    }
```

`src/MoTask.App/Views/TaskCardView.xaml` の 2 か所の `Binding="{Binding IsAwaitingApproval}"` を `Binding="{Binding IsWaitingForInput}"` に変える。

- [ ] **Step 7: 設定ダイアログに 2 項目足す**

`src/MoTask.App/ViewModels/AiSettingsViewModel.cs`:

```csharp
    [ObservableProperty] private string _permissionMode = "";
    [ObservableProperty] private string _terminalCommandTemplate = "";

    /// <summary>CLI が受け付ける値だけを選ばせる（仕様 §4.3）。</summary>
    public IReadOnlyList<string> PermissionModes => AiSettings.PermissionModes;
```

コンストラクタの初期化に足す:

```csharp
        _permissionMode = s.PermissionMode;
        _terminalCommandTemplate = s.TerminalCommandTemplate ?? "";
```

`Save()` の検証に足す（`_store.Save` の直前）:

```csharp
        var mode = PermissionMode.Trim();
        if (!AiSettings.PermissionModes.Contains(mode))
        {
            ErrorMessage = Strings.PermissionModeInvalid;
            return;
        }
```

`_store.Save(...)` を差し替える:

```csharp
        _store.Save(new AiSettings(dir, maxConcurrent, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model), maxTurns,
            mode, NullIfBlank(TerminalCommandTemplate)));
```

`src/MoTask.App/Views/AiSettingsDialog.xaml` の「モデル」の下に足す:

```xml
        <TextBlock Text="{x:Static res:Strings.SettingsPermissionMode}" Style="{StaticResource Settings.Label}" TextWrapping="Wrap" />
        <ComboBox ItemsSource="{Binding PermissionModes}" SelectedItem="{Binding PermissionMode}" Width="200" HorizontalAlignment="Left" />

        <TextBlock Text="{x:Static res:Strings.SettingsTerminalTemplate}" Style="{StaticResource Settings.Label}" TextWrapping="Wrap" />
        <TextBox Text="{Binding TerminalCommandTemplate, UpdateSourceTrigger=PropertyChanged}" />
```

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.App tests/MoTask.App.Tests
git commit -m "feat(app): read hook payloads in the log, badge and settings"
```

---

## Task 10: AiJobService の作り替えと承認サブシステムの撤去

このタスクだけは大きい。サービスの公開面が変わると App の呼び出し側が一斉に変わるので、
分けるとどちらの片割れもコンパイルできない。**削除は最後にまとめて行い、順番を守ること。**

**Files:**
- Modify: `src/MoTask.Core/Services/IAiJobService.cs`
- Modify: `src/MoTask.Core/Services/AiJobChangedEventArgs.cs`
- Modify: `src/MoTask.Core/Services/AiJobService.cs`（全面書き換え）
- Modify: `src/MoTask.Core/Model/AiJob.cs`（`TotalCostUsd` 削除）
- Modify: `src/MoTask.Core/Model/AiJobStatus.cs`（`AwaitingApproval` / `Suspended` 削除、`IsActive` の意味変更）
- Modify: `src/MoTask.Core/Model/AiJobEventKind.cs`（`PermissionAsked` / `PermissionDecided` 削除）
- Modify: `src/MoTask.Core/Ai/AiSettings.cs`（`MaxConcurrentJobs` / `MaxTurns` 削除）
- Modify: `src/MoTask.Core/Resources/Messages.cs` / `Messages.resx`
- Delete: `src/MoTask.Core/Ai/{AgentEvent,AgentRunOutcome,AgentRunRequest,IAgentRunner,IPermissionPolicy,IPermissionPrompt,PermissionDecision,PermissionPattern,PermissionPolicy,PermissionRequest}.cs`
- Delete: `src/MoTask.Core/Abstractions/IPermissionRuleRepository.cs`
- Delete: `src/MoTask.Core/Model/{AiPermissionRule,RuleDecision,RuleScope}.cs`
- Modify: `src/MoTask.Data/MoTaskDbContext.cs`, `src/MoTask.Data/ServiceCollectionExtensions.cs`, `src/MoTask.Data/JsonAiSettingsStore.cs`
- Delete: `src/MoTask.Data/Repositories/PermissionRuleRepository.cs`
- Create: `src/MoTask.Data/Migrations/<timestamp>_TerminalAiRework.cs`（+ Designer、`dotnet ef` が作る）
- Delete: `src/MoTask.App/Ai/{ApprovalMcpServer,McpProtocol,McpConfigFile,PermissionGate,WpfPermissionPrompt,ClaudeCodeParser,ClaudeCodeRunner,ClaudeCodeArguments}.cs`
- Delete: `src/MoTask.App/ViewModels/PermissionDialogViewModel.cs`, `src/MoTask.App/Views/PermissionDialog.xaml{,.cs}`
- Modify: `src/MoTask.App/App.xaml.cs`, `src/MoTask.App/Views/MainWindow.xaml`, `src/MoTask.App/Views/MainWindow.xaml.cs`
- Modify: `src/MoTask.App/ViewModels/{BoardViewModel,TaskAiPanelViewModel,AiSettingsViewModel}.cs`
- Modify: `src/MoTask.App/Resources/Strings.cs` / `Strings.resx`
- Delete: `tests/MoTask.Core.Tests/{PermissionPolicyTests,AiJobServicePermissionTests}.cs`, `tests/MoTask.Core.Tests/Fakes/{FakeAgentRunner,FakePermissionPrompt}.cs`
- Delete: `tests/MoTask.App.Tests/{PermissionGateTests,PermissionDialogViewModelTests,McpConfigFileTests,ApprovalMcpServerTests,ClaudeCodeArgumentsTests,ClaudeCodeParserTests}.cs`, `tests/MoTask.App.Tests/Fixtures/{stream-bash,stream-deny}.jsonl`
- Create: `tests/MoTask.Core.Tests/Fakes/{FakeSessionLauncher,FakeJobFolder,FakeJobEventSource}.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`
- Rewrite: `tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs`, `tests/MoTask.Core.Tests/AiJobServiceStartTests.cs`
- Modify: `tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs`, `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`, `tests/MoTask.Data.Tests/AiRepositoryTests.cs`, `tests/MoTask.Data.Tests/MigrationTests.cs`, `tests/MoTask.Core.Tests/AiModelTests.cs`

**Interfaces:**
- Consumes: `ISessionLauncher` / `IJobFolder` / `IJobEventSource` / `JobFolderPaths` / `HookEventParser`（Task 2–4）、それらの実体（Task 6–8）
- Produces:
  - `AiJobSnapshot(int JobId, int TaskId, AiJobKind Kind, AiJobStatus Status, int TurnCount, string? ErrorMessage, string WorkingDirectory, string JobFolder)`
  - `IAiJobService`: `StartJobAsync` / `GetJobsForTaskAsync` / `GetEventsAsync` / `GetUnfinishedJobsAsync` / `GetArtifactsAsync` / `TurnCountOf` / `ReopenTerminalAsync` / `CompleteJobAsync` / `StopTrackingAsync` / `RecoverOnStartupAsync`
  - `BoardViewModel`: `CompleteAiJobAsync(int)` / `StopTrackingAiJobAsync(int)` / `ReopenAiTerminalAsync(int)` / `QueryAiArtifactsAsync(int)`
  - `TaskAiPanelViewModel`: `IsWaitingForInput` / `CanControl` / `CompleteCommand` / `StopTrackingCommand` / `ReopenTerminalCommand` / `OpenJobFolderCommand` / `OpenArtifactCommand`
  - `Messages.AiJobAlreadyFinished`, `Messages.AiJobFolderMissing`

- [ ] **Step 1: 文言を入れ替える**

`src/MoTask.Core/Resources/Messages.resx` から次を消す:
`ConcurrencyLimitFormat`, `AiJobNotActive`, `AiJobNotSuspended`, `SuspendedByShutdown`, `StoppedByUser`, `ResumeInstruction`, `ResumeFailedFormat`, `AgentExitedWithCodeFormat`, `AgentFailedFormat`, `PermissionRuleNotFound`, `DeniedByRule`, `DeniedByHuman`, `ApprovalUiFailed`

同じファイルに足す:

```xml
  <data name="AiJobAlreadyFinished" xml:space="preserve"><value>このジョブは終了済みです</value></data>
  <data name="AiJobFolderMissing" xml:space="preserve"><value>このジョブにはジョブフォルダがありません</value></data>
```

`TaskAlreadyHasActiveJob` の値を `このタスクには追跡中の AI ジョブがあります` に変える。

`src/MoTask.Core/Resources/Messages.cs` から消したキーのプロパティを消し、足した 2 つのプロパティを足す。

`src/MoTask.App/Resources/Strings.resx` から次を消す:
`PermissionTitle`, `PermissionToolFormat`, `PermissionTask`, `PermissionWorkingDirectory`, `PermissionAllow`, `PermissionDeny`, `PermissionAllowAlways`, `PermissionDenyAlways`, `PermissionRememberFormat`, `PermissionRememberToolOnly`, `PermissionRememberBashFormat`, `PermissionRememberDirFormat`, `PermissionScopeProject`, `PermissionScopeGlobal`, `PermissionEditFormat`, `AiLogPermissionAskedFormat`, `AiLogPermissionDecidedFormat`, `AiLogAllow`, `AiLogDeny`, `AiLogByRule`, `AiLogByHuman`, `AiLogByShutdown`, `AiStatusAwaiting`
（`AiLogResultFormat` / `AiLogResultErrorFormat` は残す。作り替え前に保存された result 行を、いまも同じ画面に並べる）, `AiStatusSuspended`, `AiCost`, `AiCostFormat`, `AiStop`, `AiResume`, `SettingsMaxConcurrent`, `SettingsMaxTurns`, `SettingsRules`, `SettingsNoRules`, `SettingsRuleFormat`, `SettingsRuleAllow`, `SettingsRuleDeny`, `SettingsRuleAllTool`, `SettingsScopeGlobal`, `SettingsScopeProjectFormat`, `SettingsDeleteRule`, `MaxConcurrentMustBePositive`, `MaxConcurrentTooLargeFormat`, `MaxTurnsMustBePositive`

同じファイルに足す:

```xml
  <data name="AiOpenJobFolder" xml:space="preserve"><value>ジョブフォルダを開く</value></data>
  <data name="AiReopenTerminal" xml:space="preserve"><value>端末を開き直す</value></data>
  <data name="AiComplete" xml:space="preserve"><value>完了にする</value></data>
  <data name="AiStopTracking" xml:space="preserve"><value>追跡をやめる</value></data>
  <data name="AiStopTrackingHint" xml:space="preserve"><value>「追跡をやめる」は端末を閉じません。端末はご自分で閉じてください</value></data>
```

`AiNoArtifacts` の値を `artifacts フォルダにファイルはありません` に変える。
`AiOpenWorkingDirectory` の値を `作業フォルダ（cwd）を開く` に変える。

`src/MoTask.App/Resources/Strings.cs` を同じように直す（消したキーのプロパティを消し、足した 5 つを足す）。

- [ ] **Step 2: 新しいテストのための fake を書く**

`tests/MoTask.Core.Tests/Fakes/FakeSessionLauncher.cs`:

```csharp
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>実起動はしない。組み立てた要求を記録して、成否だけテストが決める。</summary>
public sealed class FakeSessionLauncher : ISessionLauncher
{
    public Result Availability { get; set; } = Result.Ok();
    public Result? BuildFailure { get; set; }
    public Result? LaunchFailure { get; set; }
    public List<SessionLaunchRequest> Requests { get; } = new();
    public List<TerminalCommand> Launched { get; } = new();

    public Result CheckAvailable() => Availability;

    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        Requests.Add(request);
        if (BuildFailure is { } failure) return Result.Fail<TerminalCommand>(failure.Error!);
        return Result.Ok(new TerminalCommand("wt.exe", $"-d \"{request.WorkingDirectory}\" cmd /k claude", request.WorkingDirectory));
    }

    public Result Launch(TerminalCommand command)
    {
        if (LaunchFailure is { } failure) return failure;
        Launched.Add(command);
        return Result.Ok();
    }
}
```

`tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs`:

```csharp
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

public sealed class FakeJobFolder : IJobFolder
{
    public Result<string>? CreateFailure { get; set; }
    public List<JobFolderRequest> Created { get; } = new();
    public List<JobDescriptor> Descriptors { get; } = new();
    public List<string> Artifacts { get; } = new();

    public Result<string> Create(JobFolderRequest request)
    {
        Created.Add(request);
        if (CreateFailure is { } failure) return failure;
        return Result.Ok($@"C:\work\jobs\{request.JobId:0000}-{request.TaskTitle}");
    }

    public void WriteJobJson(string root, JobDescriptor descriptor) => Descriptors.Add(descriptor);

    public IReadOnlyList<string> ListArtifacts(string root) => Artifacts;
}
```

`tests/MoTask.Core.Tests/Fakes/FakeJobEventSource.cs`:

```csharp
using System.Collections.Concurrent;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>テストが events.jsonl の代わりに行を流し込む。</summary>
public sealed class FakeJobEventSource : IJobEventSource
{
    private readonly ConcurrentDictionary<int, JobEventSubscription> _subscriptions = new();

    public List<JobEventSubscription> Subscriptions { get; } = new();
    public List<int> Stopped { get; } = new();

    public bool IsFollowing(int jobId) => _subscriptions.ContainsKey(jobId);
    public int SkipLinesOf(int jobId) => _subscriptions[jobId].SkipLines;
    public string EventsPathOf(int jobId) => _subscriptions[jobId].EventsPath;

    public void Follow(JobEventSubscription subscription)
    {
        _subscriptions[subscription.JobId] = subscription;
        Subscriptions.Add(subscription);
    }

    public void StopFollowing(int jobId)
    {
        _subscriptions.TryRemove(jobId, out _);
        Stopped.Add(jobId);
    }

    /// <summary>フックが 1 行書いたことにする。</summary>
    public Task EmitAsync(int jobId, string line) => _subscriptions[jobId].OnLine(line);

    public Task ProblemAsync(int jobId, string message) => _subscriptions[jobId].OnProblem(message);

    // ---- よく使う行 ----

    public static string SessionStart(string source = "startup")
        => $$"""{"hook_event_name":"SessionStart","source":"{{source}}"}""";

    public static string PostToolUse(string tool = "Bash")
        => $$"""{"hook_event_name":"PostToolUse","tool_name":"{{tool}}","tool_input":{"command":"dir"}}""";

    public static string Stop(string message = "できました")
        => $$"""{"hook_event_name":"Stop","last_assistant_message":"{{message}}"}""";

    public static string SessionEnd(string reason = "exit")
        => $$"""{"hook_event_name":"SessionEnd","reason":"{{reason}}"}""";
}
```

`tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs` から `IPermissionRuleRepository` の実装（`Rules` フィールド、`GetAllAsync`、`IPermissionRuleRepository.GetAsync`、`Add(AiPermissionRule)`、`Remove`）とクラス宣言の `, IPermissionRuleRepository` を消す。

- [ ] **Step 3: サービスの新しい振る舞いをテストで書く**

`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs` を全面的に書き直す:

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
/// ターミナル実行のライフサイクル（仕様 §8）。
/// Pending → SessionStart → Running ⇄ Stop → WaitingForInput → SessionEnd → Succeeded。
/// </summary>
public class AiJobServiceLifecycleTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly AiJobService _service;
    private readonly Column _active;
    private readonly Column _review;
    private readonly TaskItem _task;
    private readonly List<AiJobChangedEventArgs> _changes = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public AiJobServiceLifecycleTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active);
        _review = _store.SeedColumn("確認待ち", ColumnRole.Review);
        _task = _store.SeedTask(_active, "見積り");
        // cwd の解決が既定ワークフォルダを実際に作るので、ホームではなく一時フォルダを指す
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new AiJobService(_store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.JobChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private async Task<AiJob> StartAsync()
    {
        var started = await _service.StartJobAsync(_task.Id, AiJobKind.Execute, "やること");
        started.IsSuccess.Should().BeTrue(started.Error);
        return started.Value!;
    }

    [Fact]
    public async Task Start_LeavesTheJobPendingUntilTheSessionActuallyStarts()
    {
        var job = await StartAsync();

        job.Status.Should().Be(AiJobStatus.Pending);
        job.SessionId.Should().NotBeEmpty();
        job.JobFolder.Should().Be(@"C:\work\jobs\0001-見積り");
        _launcher.Launched.Should().ContainSingle();
        _events.IsFollowing(job.Id).Should().BeTrue();
        _events.EventsPathOf(job.Id).Should().Be(JobFolderPaths.For(job.JobFolder).EventsJsonl);
    }

    [Fact]
    public async Task Start_WritesJobJsonWithTheLaunchCommand()
    {
        var job = await StartAsync();

        _folder.Descriptors.Should().ContainSingle()
            .Which.LaunchCommand.Should().StartWith("wt.exe ");
        _folder.Descriptors[0].SessionId.Should().Be(job.SessionId);
    }

    [Fact]
    public async Task Start_RecordsTheHistoryEntry()
    {
        await StartAsync();

        _store.History.Should().ContainSingle(h => h.Kind == HistoryKind.AiJobStarted);
    }

    [Fact]
    public async Task SessionStart_MovesTheJobToRunning()
    {
        var job = await StartAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        job.Status.Should().Be(AiJobStatus.Running);
        _store.JobEvents.Should().ContainSingle()
            .Which.Kind.Should().Be(AiJobEventKind.SessionStarted);
    }

    [Fact]
    public async Task Stop_MovesTheJobToWaitingForInput_AndCountsTheTurn()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        job.Status.Should().Be(AiJobStatus.WaitingForInput);
        job.NumTurns.Should().Be(1);
    }

    [Fact]
    public async Task ToolUse_AfterStop_GoesBackToRunning()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse("Read"));

        job.Status.Should().Be(AiJobStatus.Running);
        _store.JobEvents.Last().ToolName.Should().Be("Read");
    }

    [Fact]
    public async Task SessionEnd_SucceedsTheJob_MovesTheTask_AndStopsFollowing()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionEnd());

        job.Status.Should().Be(AiJobStatus.Succeeded);
        job.EndedAt.Should().NotBeNull();
        _task.ColumnId.Should().Be(_review.Id);
        _events.Stopped.Should().Contain(job.Id);
        _store.History.Should().ContainSingle(h => h.Kind == HistoryKind.AiJobFinished);
    }

    [Fact]
    public async Task Events_AreNumberedInOrder_AndKeepTheirRawLine()
    {
        var job = await StartAsync();
        var line = FakeJobEventSource.SessionStart();

        await _events.EmitAsync(job.Id, line);
        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse());

        _store.JobEvents.Select(e => e.Seq).Should().Equal(1, 2);
        _store.JobEvents[0].Payload.Should().Be(line);
    }

    [Fact]
    public async Task BrokenLines_AreKeptAsSystem_AndDoNotChangeTheStatus()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        await _events.EmitAsync(job.Id, "これは JSON ではない");

        job.Status.Should().Be(AiJobStatus.Running);
        _store.JobEvents.Last().Kind.Should().Be(AiJobEventKind.System);
        _store.JobEvents.Last().Payload.Should().Be("これは JSON ではない");
    }

    [Fact]
    public async Task Complete_TreatsTheJobAsIfSessionEndArrived()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        var completed = await _service.CompleteJobAsync(job.Id);

        completed.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Succeeded);
        _task.ColumnId.Should().Be(_review.Id);
        _events.Stopped.Should().Contain(job.Id);
    }

    [Fact]
    public async Task StopTracking_CancelsTheJob_WithoutMovingTheTask()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());

        var stopped = await _service.StopTrackingAsync(job.Id);

        stopped.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Cancelled);
        _task.ColumnId.Should().Be(_active.Id, "追跡をやめただけで、仕事が終わったわけではない");
        _events.Stopped.Should().Contain(job.Id);
    }

    [Fact]
    public async Task LinesArrivingAfterTheJobFinished_AreIgnored()
    {
        var job = await StartAsync();
        await _service.StopTrackingAsync(job.Id);
        var before = _store.JobEvents.Count;

        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        _store.JobEvents.Should().HaveCount(before);
        job.Status.Should().Be(AiJobStatus.Cancelled);
    }

    [Fact]
    public async Task Complete_FailsForAJobThatAlreadyFinished()
    {
        var job = await StartAsync();
        await _service.CompleteJobAsync(job.Id);

        (await _service.CompleteJobAsync(job.Id)).Error.Should().Be(Messages.AiJobAlreadyFinished);
    }

    [Fact]
    public async Task ReopenTerminal_ResumesTheSameSession_WithoutChangingTheStatus()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());

        var reopened = await _service.ReopenTerminalAsync(job.Id);

        reopened.IsSuccess.Should().BeTrue();
        _launcher.Requests.Last().Resume.Should().BeTrue();
        _launcher.Requests.Last().SessionId.Should().Be(job.SessionId);
        job.Status.Should().Be(AiJobStatus.WaitingForInput);
        _events.SkipLinesOf(job.Id).Should().Be(2, "既に取り込んだ行は読み直さない");
    }

    [Fact]
    public async Task EventLogGone_RaisesAWarning_ButLeavesTheJobAlone()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        lock (_changes) _changes.Clear();

        await _events.ProblemAsync(job.Id, "イベントログを追えなくなりました: x");

        job.Status.Should().Be(AiJobStatus.Running);
        lock (_changes) _changes.Should().ContainSingle().Which.Warning.Should().Contain("追えなくなりました");
    }

    [Fact]
    public async Task Recover_PicksUpWhereTheEventLogWasLeft()
    {
        var job = await StartAsync();
        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.Stop());
        // アプリを閉じ直したことにする
        _events.StopFollowing(job.Id);

        await _service.RecoverOnStartupAsync();

        _events.IsFollowing(job.Id).Should().BeTrue();
        _events.SkipLinesOf(job.Id).Should().Be(2);
    }

    [Fact]
    public async Task Recover_FinishesAJobWhoseSessionEndArrivedWhileTheAppWasClosed()
    {
        var job = await StartAsync();
        _events.StopFollowing(job.Id);
        await _service.RecoverOnStartupAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionEnd());

        job.Status.Should().Be(AiJobStatus.Succeeded);
        _task.ColumnId.Should().Be(_review.Id);
    }

    [Fact]
    public async Task GetUnfinishedJobs_ReturnsPendingRunningAndWaiting()
    {
        var job = await StartAsync();

        (await _service.GetUnfinishedJobsAsync()).Should().ContainSingle().Which.Id.Should().Be(job.Id);

        await _service.StopTrackingAsync(job.Id);
        (await _service.GetUnfinishedJobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetArtifacts_ListsWhatIsInTheJobFolder()
    {
        var job = await StartAsync();
        _folder.Artifacts.Add(@"C:\work\jobs\0001-見積り\artifacts\report.md");

        (await _service.GetArtifactsAsync(job.Id)).Should().ContainSingle();
    }
}
```

`tests/MoTask.Core.Tests/AiJobServiceStartTests.cs` を全面的に書き直す:

```csharp
using System.IO;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>開始時の検証と、端末が開くまでに失敗したときの畳み方（仕様 §12）。</summary>
public class AiJobServiceStartTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly AiJobService _service;
    private readonly Column _backlog;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public AiJobServiceStartTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("確認待ち", ColumnRole.Review);
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new AiJobService(_store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
    }

    private TaskItem Seed(string title = "a") => _store.SeedTask(_backlog, title);

    [Fact]
    public async Task Start_RejectsAnEmptyInstruction()
    {
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "   ");

        started.Error.Should().Be(Messages.InstructionRequired);
        _store.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_FailsWhenClaudeIsMissing()
    {
        _launcher.Availability = Result.Fail(Messages.ClaudeNotFound);
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        started.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Jobs.Should().BeEmpty("claude が無いならジョブの行も作らない");
    }

    [Fact]
    public async Task Start_FailsForAMissingTask()
    {
        (await _service.StartJobAsync(9999, AiJobKind.Execute, "やる")).Error.Should().Be(Messages.TaskNotFound);
    }

    [Fact]
    public async Task Start_FailsForADeletedTask()
    {
        var task = Seed();
        task.IsDeleted = true;

        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる"))
            .Error.Should().Be(Messages.TaskDeletedCannotRunAi);
    }

    [Fact]
    public async Task Start_RejectsASecondJobWhileOneIsStillTracked()
    {
        var task = Seed();
        (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();

        var second = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        second.Error.Should().Be(Messages.TaskAlreadyHasActiveJob);
    }

    [Fact]
    public async Task Start_AllowsANewJobOnceTheOldOneFinished()
    {
        var task = Seed();
        var first = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;
        await _service.StopTrackingAsync(first.Id);

        var second = await _service.StartJobAsync(task.Id, AiJobKind.Research, "調べる");

        second.IsSuccess.Should().BeTrue(second.Error);
    }

    [Fact]
    public async Task Start_FailsWhenTheProjectWorkingDirectoryIsMissing()
    {
        var project = _store.SeedProject("p");
        project.WorkingDirectory = Path.Combine(_tempDir, "no-such-dir");
        var task = Seed();
        task.ProjectId = project.Id;

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Contain("プロジェクトの作業フォルダが見つかりません");
        _launcher.Launched.Should().BeEmpty("既定へ逃げずに開始しない");
    }

    [Fact]
    public async Task Start_UsesTheProjectWorkingDirectoryAsCwd()
    {
        var dir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(dir);
        var project = _store.SeedProject("p");
        project.WorkingDirectory = dir;
        var task = Seed();
        task.ProjectId = project.Id;

        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).Value!;

        job.WorkingDirectory.Should().Be(dir);
        _launcher.Requests.Single().WorkingDirectory.Should().Be(dir);
        _launcher.Requests.Single().JobFolder.Should().NotBe(dir, "ジョブフォルダは cwd とは別（仕様 §6）");
    }

    [Fact]
    public async Task Start_MarksTheJobFailedWhenTheJobFolderCannotBeCreated()
    {
        _folder.CreateFailure = Result.Fail<string>("ジョブフォルダを作成できません: x（権限がありません）");
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        var job = _store.Jobs.Should().ContainSingle().Subject;
        job.Status.Should().Be(AiJobStatus.Failed);
        job.ErrorMessage.Should().Be("ジョブフォルダを作成できません: x（権限がありません）");
        _events.Subscriptions.Should().BeEmpty();
        _store.History.Should().Contain(h => h.Kind == HistoryKind.AiJobFinished);
    }

    [Fact]
    public async Task Start_MarksTheJobFailedWhenTheTerminalWillNotOpen()
    {
        _launcher.LaunchFailure = Result.Fail("端末を起動できませんでした: wt.exe -d ...");
        var task = Seed();

        var started = await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる");

        started.IsSuccess.Should().BeFalse();
        var job = _store.Jobs.Single();
        job.Status.Should().Be(AiJobStatus.Failed);
        job.ErrorMessage.Should().Contain("wt.exe", "何を実行しようとしたかが残る（仕様 §12）");
        _events.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_NeverLimitsHowManyJobsRunAtOnce()
    {
        // 同時実行の上限は廃止した。端末を開くのは人であって、アプリが数を絞る意味が無い（仕様 §8）。
        foreach (var i in Enumerable.Range(0, 5))
        {
            var task = Seed($"t{i}");
            (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "やる")).IsSuccess.Should().BeTrue();
        }

        _launcher.Launched.Should().HaveCount(5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 4: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJobService"`
Expected: コンパイルエラー（新しいコンストラクタとメソッドが無い）

- [ ] **Step 5: IAiJobService と AiJobChangedEventArgs を書き換える**

`src/MoTask.Core/Services/AiJobChangedEventArgs.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// UI が読む用の値のコピー。エンティティを UI スレッドに渡さない。
/// 費用は持たない（フックに来ないので、盤面に出す手立てが無い。仕様 §9）。
/// </summary>
public sealed record AiJobSnapshot(
    int JobId, int TaskId, AiJobKind Kind, AiJobStatus Status, int TurnCount,
    string? ErrorMessage, string WorkingDirectory, string JobFolder);

/// <summary>NewEvent はイベント追記のときだけ。Warning は保存失敗・列移動の注意・イベントログの消失。</summary>
public sealed class AiJobChangedEventArgs : EventArgs
{
    public AiJobChangedEventArgs(AiJobSnapshot job, AiJobEvent? newEvent, string? warning)
    {
        Job = job;
        NewEvent = newEvent;
        Warning = warning;
    }

    public AiJobSnapshot Job { get; }
    public AiJobEvent? NewEvent { get; }
    public string? Warning { get; }
}
```

`src/MoTask.Core/Services/IAiJobService.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Services;

public interface IAiJobService
{
    /// <summary>状態・イベントの変化。追従スレッドから上がるので、UI 側で Dispatcher へ載せ替える。</summary>
    event EventHandler<AiJobChangedEventArgs>? JobChanged;

    /// <summary>
    /// ジョブフォルダを作り、端末を開いて手放す。返るジョブは Pending
    /// （SessionStart フックが届いて初めて Running になる）。
    /// </summary>
    Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default);

    Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default);
    /// <summary>Pending / Running / WaitingForInput。カードのバッジ初期化と起動時の追いつきに使う。</summary>
    Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default);
    /// <summary>ジョブフォルダの artifacts/ にある実ファイル（仕様 §6）。</summary>
    Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default);
    /// <summary>Stop フックを数えたターン数。追跡していなければ 0。</summary>
    int TurnCountOf(int jobId);

    /// <summary>--resume で端末を開き直す。状態は変えない（SessionStart フックが Running に戻す）。</summary>
    Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// 端末を × で閉じられて SessionEnd が来なかったジョブを、人の手で閉じる。
    /// SessionEnd を受け取ったのと同じ扱い（Succeeded にして確認待ち列へ）。
    /// </summary>
    Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>追跡をやめて Cancelled にする。端末のプロセスは殺さない（仕様 §8）。</summary>
    Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default);

    /// <summary>未完了ジョブの events.jsonl を、記録済みの行数から読み直して追いつく。起動時に呼ぶ。</summary>
    Task RecoverOnStartupAsync(CancellationToken ct = default);
}
```

- [ ] **Step 6: AiJobService を書き換える**

`src/MoTask.Core/Services/AiJobService.cs` を次の内容にまるごと置き換える:

```csharp
using System.Collections.Concurrent;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.Core.Services;

/// <summary>
/// AI ジョブのライフサイクル（仕様 §8）。MoTask はプロセスを所有せず、events.jsonl を読んで
/// 状態を写すだけ。DB は BoardService と共有の OperationGate で直列化する。ゲートの中から
/// IBoardService を呼ぶとデッドロックするので、完了時の列移動はゲートの外で呼ぶ。
/// JobChanged もゲートの外で上げる。
/// </summary>
public sealed class AiJobService : IAiJobService
{
    /// <summary>追跡中のジョブの数え。DB には持たない。</summary>
    private sealed class TrackedJob
    {
        public required int TaskId { get; init; }
        public int Seq { get; set; }
        public int Turns { get; set; }
    }

    private readonly IAiJobRepository _jobs;
    private readonly IBoardRepository _boards;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly OperationGate _gate;
    private readonly ISessionLauncher _launcher;
    private readonly IJobFolder _folder;
    private readonly IJobEventSource _events;
    private readonly IAiSettingsStore _settings;
    private readonly IBoardService _boardService;
    private readonly ConcurrentDictionary<int, TrackedJob> _tracked = new();

    public event EventHandler<AiJobChangedEventArgs>? JobChanged;

    public AiJobService(
        IAiJobRepository jobs, IBoardRepository boards, IHistoryRepository history, IUnitOfWork uow,
        IClock clock, OperationGate gate, ISessionLauncher launcher, IJobFolder folder,
        IJobEventSource events, IAiSettingsStore settings, IBoardService boardService)
    {
        _jobs = jobs;
        _boards = boards;
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

    public Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetForTaskAsync(taskId, ct), ct);

    public Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetEventsAsync(jobId, ct), ct);

    public Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default)
        => _gate.RunAsync(() => _jobs.GetByStatusAsync(
            new[] { AiJobStatus.Pending, AiJobStatus.Running, AiJobStatus.WaitingForInput }, ct), ct);

    public async Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId, ct), ct).ConfigureAwait(false);
        // 一覧はファイルシステムが真実。ToolUse からは拾わない（仕様 §6）。
        return job is null || job.JobFolder.Length == 0
            ? Array.Empty<string>()
            : _folder.ListArtifacts(job.JobFolder);
    }

    public int TurnCountOf(int jobId) => _tracked.TryGetValue(jobId, out var tracked) ? tracked.Turns : 0;

    // ---------- 開始 ----------

    public async Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default)
    {
        instruction = instruction.Trim();
        if (instruction.Length == 0) return Result.Fail<AiJob>(Messages.InstructionRequired);

        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return Result.Fail<AiJob>(available.Error!);

        var settings = _settings.Load();
        var title = "";
        var created = await _gate.RunAsync(async () =>
        {
            try
            {
                var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
                if (task is null) return Result.Fail<AiJob>(Messages.TaskNotFound);
                if (task.IsDeleted) return Result.Fail<AiJob>(Messages.TaskDeletedCannotRunAi);
                // 追跡中かどうかは DB で数える。MoTask を閉じても端末は走り続けるので記憶に頼れない。
                var existing = await _jobs.GetForTaskAsync(taskId, ct).ConfigureAwait(false);
                if (existing.Any(j => !j.Status.IsTerminal())) return Result.Fail<AiJob>(Messages.TaskAlreadyHasActiveJob);

                var cwd = await ResolveWorkingDirectoryAsync(task, settings, ct).ConfigureAwait(false);
                if (!cwd.IsSuccess) return Result.Fail<AiJob>(cwd.Error!);

                var now = _clock.UtcNow;
                var job = new AiJob
                {
                    TaskId = task.Id, Kind = kind, Status = AiJobStatus.Pending, SessionId = Guid.NewGuid(),
                    Instruction = instruction, WorkingDirectory = cwd.Value!, StartedAt = now,
                };
                _jobs.Add(job);
                _history.Add(new HistoryEntry
                {
                    Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.AiJobStarted,
                    Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(kind, null)),
                });
                await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
                title = task.Title;
                _tracked[job.Id] = new TrackedJob { TaskId = task.Id };
                return Result.Ok(job);
            }
            catch (PersistenceException ex)
            {
                return Result.Fail<AiJob>($"{Messages.SaveFailed}: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);

        if (!created.IsSuccess) return created;
        var job = created.Value!;

        // ここから先はファイル操作と端末の起動なので、ゲートの外でやる。
        var folder = _folder.Create(new JobFolderRequest(job.Id, title, instruction));
        if (!folder.IsSuccess) return await FailAsync(job, folder.Error!).ConfigureAwait(false);

        var command = _launcher.BuildCommand(
            new SessionLaunchRequest(job.SessionId, folder.Value!, job.WorkingDirectory, Resume: false));
        if (!command.IsSuccess) return await FailAsync(job, command.Error!).ConfigureAwait(false);

        // job.json は起動コマンドまで決まってから書く（DB が壊れてもフォルダだけで素性が分かる）
        _folder.WriteJobJson(folder.Value!, new JobDescriptor(
            job.Id, job.SessionId, job.Kind, job.WorkingDirectory, command.Value!.Display, job.StartedAt ?? _clock.UtcNow));

        var launched = _launcher.Launch(command.Value!);
        if (!launched.IsSuccess) return await FailAsync(job, launched.Error!).ConfigureAwait(false);

        var warning = await _gate.RunAsync(async () =>
        {
            job.JobFolder = folder.Value!;
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        Follow(job.Id, folder.Value!, skipLines: 0);
        Raise(job, null, warning);
        return Result.Ok(job);
    }

    /// <summary>
    /// プロジェクトに作業フォルダがあればそれ（無ければ既定へフォールバックせず失敗: 意図した場所と違うところで
    /// 任意コマンドを走らせないため）。プロジェクト無し／未設定なら既定ワークフォルダを作って使う。
    /// </summary>
    private async Task<Result<string>> ResolveWorkingDirectoryAsync(TaskItem task, AiSettings settings, CancellationToken ct)
    {
        if (task.ProjectId is int pid)
        {
            var project = await _boards.GetProjectAsync(pid, ct).ConfigureAwait(false);
            if (project?.WorkingDirectory is { Length: > 0 } dir)
            {
                return Directory.Exists(dir)
                    ? Result.Ok(dir)
                    : Result.Fail<string>(string.Format(Messages.WorkingDirectoryMissingFormat, dir));
            }
        }

        var fallback = settings.DefaultWorkingDirectory;
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail<string>(string.Format(Messages.DefaultWorkingDirectoryFailedFormat, fallback));
        }
        return Result.Ok(fallback);
    }

    private async Task<Result<AiJob>> FailAsync(AiJob job, string error)
    {
        await _gate.RunAsync(async () =>
        {
            job.ErrorMessage = error;
            Finish(job, AiJobStatus.Failed);
            await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _tracked.TryRemove(job.Id, out _);
        Raise(job, null, null);
        return Result.Fail<AiJob>(error);
    }

    // ---------- 追従 ----------

    private void Follow(int jobId, string jobFolder, int skipLines)
    {
        if (jobFolder.Length == 0) return;
        _events.Follow(new JobEventSubscription(
            jobId, JobFolderPaths.For(jobFolder).EventsJsonl, skipLines,
            line => OnHookLineAsync(jobId, line),
            message => OnProblemAsync(jobId, message)));
    }

    /// <summary>フックが 1 行書くたびに呼ばれる（行の順序どおり、直列）。</summary>
    private async Task OnHookLineAsync(int jobId, string line)
    {
        var parsed = HookEventParser.Parse(line);
        AiJob? job = null;
        AiJobEvent? stored = null;
        var finished = false;

        var warning = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId).ConfigureAwait(false);
            // 追跡をやめた後・完了にした後に届いた行は捨てる（終わったジョブを蘇らせない）
            if (current is null || current.Status.IsTerminal()) return (string?)null;
            job = current;

            var tracked = await TrackedForAsync(current).ConfigureAwait(false);
            tracked.Seq++;
            stored = new AiJobEvent
            {
                JobId = jobId, Seq = tracked.Seq, At = _clock.UtcNow,
                Kind = parsed.Kind, ToolName = parsed.ToolName, Payload = parsed.Payload,
            };
            _jobs.AddEvent(stored);

            switch (parsed.Kind)
            {
                case AiJobEventKind.SessionStarted:
                case AiJobEventKind.ToolUse:
                    current.Status = AiJobStatus.Running;
                    break;
                case AiJobEventKind.TurnEnded:
                    current.Status = AiJobStatus.WaitingForInput;
                    tracked.Turns++;
                    current.NumTurns = tracked.Turns;
                    break;
                case AiJobEventKind.SessionEnded:
                    Finish(current, AiJobStatus.Succeeded);
                    finished = true;
                    break;
            }
            return await SaveQuietlyAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (job is null) return;
        if (finished)
        {
            _events.StopFollowing(jobId);
            _tracked.TryRemove(jobId, out _);
            // 保存に失敗していても列移動は必ず試みる。バナーは 1 本なので保存失敗の方を優先する。
            var moveWarning = await MoveToReviewAsync(job.TaskId).ConfigureAwait(false);
            warning ??= moveWarning;
        }
        Raise(job, stored, warning);
    }

    /// <summary>events.jsonl が消えた／作り直された（仕様 §12）。状態は変えず、注意だけ出す。</summary>
    private async Task OnProblemAsync(int jobId, string message)
    {
        _events.StopFollowing(jobId);
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId)).ConfigureAwait(false);
        if (job is null) return;
        Raise(job, null, message);
    }

    // ---------- 人の操作 ----------

    public async Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default)
    {
        var available = _launcher.CheckAvailable();
        if (!available.IsSuccess) return available;

        AiJob? job = null;
        var found = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.AiJobNotFound);
            if (current.Status.IsTerminal()) return Result.Fail(Messages.AiJobAlreadyFinished);
            if (current.JobFolder.Length == 0) return Result.Fail(Messages.AiJobFolderMissing);
            job = current;
            return Result.Ok();
        }, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return found;

        var command = _launcher.BuildCommand(
            new SessionLaunchRequest(job!.SessionId, job.JobFolder, job.WorkingDirectory, Resume: true));
        if (!command.IsSuccess) return Result.Fail(command.Error!);
        var launched = _launcher.Launch(command.Value!);
        if (!launched.IsSuccess) return launched;

        // 前に追従が切れていても掛け直す。取り込み済みの行は読み飛ばす。
        var events = await _gate.RunAsync(() => _jobs.GetEventsAsync(jobId, ct), ct).ConfigureAwait(false);
        Follow(jobId, job.JobFolder, events.Count);
        return Result.Ok();
    }

    public Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default)
        => FinishByHandAsync(jobId, AiJobStatus.Succeeded, ct);

    public Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default)
        => FinishByHandAsync(jobId, AiJobStatus.Cancelled, ct);

    private async Task<Result> FinishByHandAsync(int jobId, AiJobStatus status, CancellationToken ct)
    {
        AiJob? job = null;
        string? warning = null;
        var result = await _gate.RunAsync(async () =>
        {
            var current = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
            if (current is null) return Result.Fail(Messages.AiJobNotFound);
            if (current.Status.IsTerminal()) return Result.Fail(Messages.AiJobAlreadyFinished);
            job = current;
            Finish(current, status);
            warning = await SaveQuietlyAsync().ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return result;

        _events.StopFollowing(jobId);
        _tracked.TryRemove(jobId, out _);
        // 「完了にする」は SessionEnd と同じ扱い。「追跡をやめる」は仕事が終わったわけではないので動かさない。
        if (status == AiJobStatus.Succeeded) warning ??= await MoveToReviewAsync(job!.TaskId).ConfigureAwait(false);
        Raise(job!, null, warning);
        return result;
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        var unfinished = await GetUnfinishedJobsAsync(ct).ConfigureAwait(false);
        foreach (var job in unfinished)
        {
            if (job.JobFolder.Length == 0) continue;
            var events = await _gate.RunAsync(() => _jobs.GetEventsAsync(job.Id, ct), ct).ConfigureAwait(false);
            _tracked[job.Id] = new TrackedJob { TaskId = job.TaskId, Seq = events.Count, Turns = job.NumTurns ?? 0 };
            Follow(job.Id, job.JobFolder, events.Count);
        }
    }

    // ---------- 補助 ----------

    /// <summary>
    /// ゲートの中で呼ぶ。記憶に無ければ保存済みイベントから数え直す
    /// （events.jsonl は「1 行 = 1 イベント」なので、件数がそのまま Seq とオフセットになる）。
    /// </summary>
    private async Task<TrackedJob> TrackedForAsync(AiJob job)
    {
        if (_tracked.TryGetValue(job.Id, out var tracked)) return tracked;
        var events = await _jobs.GetEventsAsync(job.Id).ConfigureAwait(false);
        tracked = new TrackedJob { TaskId = job.TaskId, Seq = events.Count, Turns = job.NumTurns ?? 0 };
        _tracked[job.Id] = tracked;
        return tracked;
    }

    /// <summary>ゲートの中で呼ぶ。終了状態を書いて履歴を残す（保存は呼び出し側）。</summary>
    private void Finish(AiJob job, AiJobStatus status)
    {
        var now = _clock.UtcNow;
        job.Status = status;
        job.EndedAt = now;
        _history.Add(new HistoryEntry
        {
            TaskId = job.TaskId, At = now, Kind = HistoryKind.AiJobFinished,
            Detail = AiJobHistoryDetail.Serialize(new AiJobHistoryDetail(job.Kind, status)),
        });
    }

    /// <summary>ゲートの外から呼ぶ（BoardService も同じゲートを取る）。戻り値はバナー向けの警告。</summary>
    private async Task<string?> MoveToReviewAsync(int taskId)
    {
        var board = await _boardService.GetBoardAsync().ConfigureAwait(false);
        if (!board.IsSuccess) return board.Error;

        var review = board.Value!.Columns.Where(c => c.Role == ColumnRole.Review).OrderBy(c => c.Order).FirstOrDefault();
        if (review is null) return Messages.NoReviewColumn;

        var moved = await _boardService.MoveTaskAsync(taskId, review.Id, int.MaxValue).ConfigureAwait(false);
        if (!moved.IsSuccess) return moved.Error;
        return moved.Warnings.Count > 0 ? string.Join(" / ", moved.Warnings) : null;
    }

    /// <summary>追従スレッドからの保存失敗でジョブを殺さない。理由は警告として UI へ回す。</summary>
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

    private void Raise(AiJob job, AiJobEvent? newEvent, string? warning)
    {
        var snapshot = new AiJobSnapshot(
            job.Id, job.TaskId, job.Kind, job.Status, job.NumTurns ?? TurnCountOf(job.Id),
            job.ErrorMessage, job.WorkingDirectory, job.JobFolder);
        JobChanged?.Invoke(this, new AiJobChangedEventArgs(snapshot, newEvent, warning));
    }
}
```

- [ ] **Step 7: モデルと設定から廃止した項目を落とす**

`src/MoTask.Core/Model/AiJobStatus.cs` を次の内容に置き換える（廃止する値の番号は空けたままにする）:

```csharp
namespace MoTask.Core.Model;

public enum AiJobStatus
{
    /// <summary>ジョブは作ったが、まだ SessionStart フックが来ていない（端末が開くまでの数百 ms）。</summary>
    Pending = 0,
    Running = 1,
    /// <summary>2 と 3 は廃止した AwaitingApproval / Suspended の番号。空けたままにする。</summary>
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    /// <summary>Stop フックが来て、人の入力を待っている。</summary>
    WaitingForInput = 7,
}

public static class AiJobStatusExtensions
{
    /// <summary>MoTask がまだ追いかけているジョブ（Pending / Running / WaitingForInput）。</summary>
    public static bool IsActive(this AiJobStatus status) => !status.IsTerminal();

    /// <summary>もう追いかけないジョブ。端末が生きているかどうかは MoTask には分からない。</summary>
    public static bool IsTerminal(this AiJobStatus status)
        => status is AiJobStatus.Succeeded or AiJobStatus.Failed or AiJobStatus.Cancelled;
}
```

`src/MoTask.Core/Model/AiJobEventKind.cs` から `PermissionAsked = 3` と `PermissionDecided = 4` を消し、
代わりにコメントを 1 行残す:

```csharp
    /// <summary>3 と 4 は廃止した承認イベント（PermissionAsked / PermissionDecided）の番号。空けたままにする。</summary>
```

`tests/MoTask.Core.Tests/AiModelTests.cs` の `AiJobStatus_IsActive_OnlyForRunningAndAwaitingApproval` と
`AiJobStatus_IsTerminal_ForSucceededFailedCancelled` を、この 2 つに置き換える:

```csharp
    [Fact]
    public void AiJobStatus_IsActive_ForEveryStatusThatIsNotFinished()
    {
        AiJobStatus.Pending.IsActive().Should().BeTrue();
        AiJobStatus.Running.IsActive().Should().BeTrue();
        AiJobStatus.WaitingForInput.IsActive().Should().BeTrue();
        AiJobStatus.Succeeded.IsActive().Should().BeFalse();
        AiJobStatus.Failed.IsActive().Should().BeFalse();
        AiJobStatus.Cancelled.IsActive().Should().BeFalse();
    }

    [Fact]
    public void AiJobStatus_IsTerminal_ForSucceededFailedCancelled()
    {
        AiJobStatus.Succeeded.IsTerminal().Should().BeTrue();
        AiJobStatus.Failed.IsTerminal().Should().BeTrue();
        AiJobStatus.Cancelled.IsTerminal().Should().BeTrue();
        AiJobStatus.Running.IsTerminal().Should().BeFalse();
        AiJobStatus.WaitingForInput.IsTerminal().Should().BeFalse();
    }
```

`src/MoTask.Core/Model/AiJob.cs` から `TotalCostUsd` の行を消し、`NumTurns` のコメントを直す:

```csharp
    /// <summary>Stop フックを数えたターン数（仕様 §9）。費用はフックに来ないので持たない。</summary>
    public int? NumTurns { get; set; }
```

`src/MoTask.Core/Ai/AiSettings.cs` から `MaxConcurrentJobs` / `MaxTurns` の 2 つのパラメータ、
`DefaultMaxConcurrentJobs` / `DefaultMaxTurns` の 2 つの定数を消し、`Default()` を直す:

```csharp
public sealed record AiSettings(
    string DefaultWorkingDirectory,
    string? ClaudeExecutablePath,
    string? Model,
    string PermissionMode,
    string? TerminalCommandTemplate)
{
    ...
    public static AiSettings Default() => new(DefaultWorkingDirectoryPath, null, null, DefaultPermissionMode, null);
}
```

`src/MoTask.Data/JsonAiSettingsStore.cs` の `Load` / `Save` / `Dto` から `MaxConcurrentJobs` と `MaxTurns` を消す。
（`Dto` から消すと、古い settings.json に残っている値は黙って無視される。それでよい。）

- [ ] **Step 8: 承認サブシステムを消す**

```bash
git rm src/MoTask.Core/Ai/AgentEvent.cs src/MoTask.Core/Ai/AgentRunOutcome.cs src/MoTask.Core/Ai/AgentRunRequest.cs \
       src/MoTask.Core/Ai/IAgentRunner.cs src/MoTask.Core/Ai/IPermissionPolicy.cs src/MoTask.Core/Ai/IPermissionPrompt.cs \
       src/MoTask.Core/Ai/PermissionDecision.cs src/MoTask.Core/Ai/PermissionPattern.cs src/MoTask.Core/Ai/PermissionPolicy.cs \
       src/MoTask.Core/Ai/PermissionRequest.cs src/MoTask.Core/Abstractions/IPermissionRuleRepository.cs \
       src/MoTask.Core/Model/AiPermissionRule.cs src/MoTask.Core/Model/RuleDecision.cs src/MoTask.Core/Model/RuleScope.cs \
       src/MoTask.Data/Repositories/PermissionRuleRepository.cs \
       src/MoTask.App/Ai/ApprovalMcpServer.cs src/MoTask.App/Ai/McpProtocol.cs src/MoTask.App/Ai/McpConfigFile.cs \
       src/MoTask.App/Ai/PermissionGate.cs src/MoTask.App/Ai/WpfPermissionPrompt.cs src/MoTask.App/Ai/ClaudeCodeParser.cs \
       src/MoTask.App/Ai/ClaudeCodeRunner.cs src/MoTask.App/Ai/ClaudeCodeArguments.cs \
       src/MoTask.App/ViewModels/PermissionDialogViewModel.cs \
       src/MoTask.App/Views/PermissionDialog.xaml src/MoTask.App/Views/PermissionDialog.xaml.cs \
       tests/MoTask.Core.Tests/PermissionPolicyTests.cs tests/MoTask.Core.Tests/AiJobServicePermissionTests.cs \
       tests/MoTask.Core.Tests/Fakes/FakeAgentRunner.cs tests/MoTask.Core.Tests/Fakes/FakePermissionPrompt.cs \
       tests/MoTask.App.Tests/PermissionGateTests.cs tests/MoTask.App.Tests/PermissionDialogViewModelTests.cs \
       tests/MoTask.App.Tests/McpConfigFileTests.cs tests/MoTask.App.Tests/ApprovalMcpServerTests.cs \
       tests/MoTask.App.Tests/ClaudeCodeArgumentsTests.cs tests/MoTask.App.Tests/ClaudeCodeParserTests.cs \
       tests/MoTask.App.Tests/Fixtures/stream-bash.jsonl tests/MoTask.App.Tests/Fixtures/stream-deny.jsonl
```

- [ ] **Step 9: Data を追従させ、マイグレーションを作る**

`src/MoTask.Data/MoTaskDbContext.cs`:
- `public DbSet<AiPermissionRule> AiPermissionRules => Set<AiPermissionRule>();` を消す
- `b.Entity<AiPermissionRule>(...)` のブロックを消す

`src/MoTask.Data/ServiceCollectionExtensions.cs` から
`services.AddSingleton<IPermissionRuleRepository, PermissionRuleRepository>();` を消す。

マイグレーションを作る:

```bash
dotnet ef migrations add TerminalAiRework --project src/MoTask.Data --output-dir Migrations
```

生成された `Up(MigrationBuilder)` の **先頭** に、データの寄せを手で足す（列やテーブルを消す前に走らせる）:

```csharp
            // 廃止した状態のまま残っている行を Cancelled に寄せる（仕様 §9）。
            // 端末のプロセスはもう追えないので、成功でも失敗でもなく「追跡をやめた」が正しい。
            migrationBuilder.Sql(
                "UPDATE AiJobs SET Status = 'Cancelled', EndedAt = COALESCE(EndedAt, StartedAt) " +
                "WHERE Status IN ('AwaitingApproval', 'Suspended')");

            // 列挙は文字列で保存されている。消した名前が残っていると読み出しで例外になるので、
            // 承認イベントは System に寄せる（Payload は原文のまま残る）。
            migrationBuilder.Sql(
                "UPDATE AiJobEvents SET Kind = 'System' WHERE Kind IN ('PermissionAsked', 'PermissionDecided')");
```

生成された `Up` が次の 3 つを含んでいることを確かめる:
- `AiPermissionRules` テーブルの削除
- `AiJobs.TotalCostUsd` 列の削除
- `AiJobs.JobFolder` 列は Task 5 で追加済みなので **触らない**

- [ ] **Step 10: App を追従させる**

`src/MoTask.App/App.xaml.cs`:

- `using MoTask.App.Ai;` はそのまま。`BuildHost` の登録を差し替える:

```csharp
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IBoardService, BoardService>();
        // 端末で claude を起こす／ジョブフォルダを作る／events.jsonl を追う
        builder.Services.AddSingleton<ISessionLauncher, TerminalLauncher>();
        builder.Services.AddSingleton<IJobFolder, JobFolder>();
        builder.Services.AddSingleton<JobEventWatcher>();
        builder.Services.AddSingleton<IJobEventSource>(sp => sp.GetRequiredService<JobEventWatcher>());
        builder.Services.AddSingleton<IAiJobService, AiJobService>();
        builder.Services.AddSingleton<ViewModels.BoardViewModel>();
        builder.Services.AddSingleton<MainWindow>();
```

- `OnStartup` から `ApprovalMcpServer` を立てる 2 行（コメント込み）を消す。
  `RecoverOnStartupAsync` の呼び出しは残し、コメントを直す:

```csharp
            // 前回閉じたあとも端末は走り続けている。未完了ジョブの events.jsonl に追いつく（仕様 §8）
            await _host.Services.GetRequiredService<IAiJobService>().RecoverOnStartupAsync();
```

`src/MoTask.App/Views/MainWindow.xaml.cs`:

- `OnClosing` メソッドと `_shutdownReady` / `_shuttingDown` フィールドを丸ごと消す
  （プロセスを所有しないので、終了時に畳むものが無い）。
- `using System.ComponentModel;` を消す。
- `OnAiSettingsClick` の `new AiSettingsViewModel(_settings, _aiJobs, _vm.Projects)` を
  `new AiSettingsViewModel(_settings)` に変える（後述）。
- `_aiJobs` フィールドとコンストラクタ引数が他で使われていなければ、併せて消す。

`src/MoTask.App/Views/MainWindow.xaml` の `Window` 要素から `Closing="OnClosing"` を消す。

`src/MoTask.App/ViewModels/AiSettingsViewModel.cs`:

- `IAiJobService` と `IReadOnlyList<Project>` の依存、`Rules` / `PermissionRuleRow` / `LoadRulesAsync` /
  `Describe` / `DeleteRuleAsync` / `PendingLoad` / `MaxConcurrentJobsLimit` /
  `MaxConcurrentText` / `MaxTurnsText` / `TryParsePositive` を消す。
- コンストラクタは `public AiSettingsViewModel(IAiSettingsStore store)` だけにする。
- `Save()` の `_store.Save(...)` を差し替える:

```csharp
        _store.Save(new AiSettings(dir, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model),
            mode, NullIfBlank(TerminalCommandTemplate)));
```

`src/MoTask.App/Views/AiSettingsDialog.xaml` から「同時実行の上限」「最大ターン数」「記憶した許可ルール」の
ブロック（`SettingsMaxConcurrent` / `SettingsMaxTurns` / `SettingsRules` / `SettingsNoRules` を使う部分と
ルール一覧の `ItemsControl`）を消す。

`src/MoTask.App/ViewModels/BoardViewModel.cs`:

- `Snapshot` を差し替える:

```csharp
    private AiJobSnapshot Snapshot(AiJob job)
        => new(job.Id, job.TaskId, job.Kind, job.Status, job.NumTurns ?? AiJobs.TurnCountOf(job.Id),
            job.ErrorMessage, job.WorkingDirectory, job.JobFolder);
```

- `ApplyAiStatesAsync` の XML コメントを「未完了ジョブ（Pending / Running / WaitingForInput）から
  カードのバッジを組み直す」に直す。
- `StopAiJobAsync` / `ResumeAiJobAsync` を消して、3 つを足す:

```csharp
    /// <summary>端末を × で閉じてしまったジョブを、人の手で閉じる。</summary>
    public async Task<bool> CompleteAiJobAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.CompleteJobAsync(jobId)));

    /// <summary>追跡をやめる。端末のプロセスは殺さない。</summary>
    public async Task<bool> StopTrackingAiJobAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.StopTrackingAsync(jobId)));

    /// <summary>--resume で端末を開き直す。</summary>
    public async Task<bool> ReopenAiTerminalAsync(int jobId)
        => await HandleAsync(await GuardAsync(() => AiJobs.ReopenTerminalAsync(jobId)));

    /// <summary>失敗しても空を返す（一覧が出ないだけ）。</summary>
    public async Task<IReadOnlyList<string>> QueryAiArtifactsAsync(int jobId)
    {
        var artifacts = await QueryAsync(() => AiJobs.GetArtifactsAsync(jobId));
        if (artifacts.IsSuccess) return artifacts.Value!;
        ShowFailure(artifacts);
        return Array.Empty<string>();
    }
```

`src/MoTask.App/ViewModels/TaskAiPanelViewModel.cs` を次のように直す:

- `[ObservableProperty] private bool _isSuspended;` と `[ObservableProperty] private string? _costText;` を消し、
  代わりに 3 つ足す:

```csharp
    [ObservableProperty] private bool _isWaitingForInput;
    /// <summary>「開き直す」「完了にする」「追跡をやめる」を出してよいか（＝まだ追跡中か）。</summary>
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private string? _jobFolder;
```
- `Artifacts` は `LoadAsync` で `_board.QueryAiArtifactsAsync(latest.Id)` の戻り値を
  `ArtifactItem` に包んで作る。合わせて `AiJobEventFormatter` から `ArtifactPathOf` と
  `ArtifactPaths`、およびそれらを使うテストを消す。
- `ApplySnapshot` を差し替える:

```csharp
    private void ApplySnapshot(AiJobSnapshot s)
    {
        HasJob = true;
        IsActive = s.Status.IsActive();
        IsWaitingForInput = s.Status == AiJobStatus.WaitingForInput;
        // 追跡中のジョブがある間は新しい依頼を受けない
        CanStart = !IsActive && !_card.IsDeleted;
        // 追跡中なら「開き直す」「完了にする」「追跡をやめる」が押せる
        CanControl = IsActive;
        WorkingDirectory = s.WorkingDirectory;
        JobFolder = s.JobFolder;
        ErrorMessage = s.Status == AiJobStatus.Failed ? s.ErrorMessage : null;
        StatusText = s.Status switch
        {
            AiJobStatus.Running => s.Kind == AiJobKind.Research ? Strings.AiStatusResearching : Strings.AiStatusExecuting,
            AiJobStatus.WaitingForInput => Strings.AiStatusWaitingForInput,
            AiJobStatus.Succeeded => Strings.AiStatusSucceeded,
            AiJobStatus.Failed => Strings.AiStatusFailed,
            AiJobStatus.Cancelled => Strings.AiStatusCancelled,
            _ => Strings.AiStatusPending,
        };
    }
```

- `StopAsync` / `ResumeAsync` を消して、4 つのコマンドを足す:

```csharp
    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_job is null) return;
        if (await _board.CompleteAiJobAsync(_job.Id)) await LoadAsync();
    }

    [RelayCommand]
    private async Task StopTrackingAsync()
    {
        if (_job is null) return;
        if (await _board.StopTrackingAiJobAsync(_job.Id)) await LoadAsync();
    }

    [RelayCommand]
    private async Task ReopenTerminalAsync()
    {
        if (_job is null) return;
        await _board.ReopenAiTerminalAsync(_job.Id);
    }

    [RelayCommand]
    private void OpenJobFolder()
    {
        if (JobFolder is { Length: > 0 } folder) _board.OpenPath(folder);
    }
```

- `OnJobChanged` の `if (e.Job.Status.IsTerminal() || e.Job.Status == AiJobStatus.Suspended)` を
  `if (e.Job.Status.IsTerminal())` に直す。また `AiJobEventFormatter.ArtifactPathOf` を使う分岐を消し、
  代わりに `ToolUse` のときは何もしない（成果物は `LoadAsync` の再読み込みで拾う）。
  `if (ev.Kind == AiJobEventKind.Result)` の分岐を
  `if (ev.Kind == AiJobEventKind.TurnEnded) ResultText = AiJobEventFormatter.ResultText(new[] { ev }) ?? ResultText;` に直す。
- `Apply(AiJob? job)` の `ApplySnapshot(new AiJobSnapshot(...))` を新しい並びに合わせる:

```csharp
        ApplySnapshot(new AiJobSnapshot(job.Id, job.TaskId, job.Kind, job.Status,
            job.NumTurns ?? _board.AiJobs.TurnCountOf(job.Id), job.ErrorMessage, job.WorkingDirectory, job.JobFolder));
```

- `Apply(null)` の分岐で `IsWaitingForInput = false; CanControl = false; JobFolder = null;` も落とす。

- [ ] **Step 11: 残りのテストを追従させる**

- `tests/MoTask.Data.Tests/AiRepositoryTests.cs`: `TotalCostUsd` と `AiJobStatus.Suspended` を使う箇所を、
  `JobFolder` と `AiJobStatus.WaitingForInput` に置き換える。`AiPermissionRule` を使うテストは消す。
- `tests/MoTask.Data.Tests/MigrationTests.cs`: `Migrate_OnEmptyFile_CreatesSchema` の期待テーブル一覧から
  `AiPermissionRules` を消す。次のテストを足す:

```csharp
    [Fact]
    public async Task Migrate_LeavesNoAiPermissionRulesTable()
    {
        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();

        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();

        tables.Should().NotContain("AiPermissionRules");
    }
```

- `tests/MoTask.Core.Tests/AiModelTests.cs`: `Messages_ResolveAiStrings` を、残っている文言で書き直す:

```csharp
    [Fact]
    public void Messages_ResolveAiStrings()
    {
        Messages.TaskAlreadyHasActiveJob.Should().Be("このタスクには追跡中の AI ジョブがあります");
        Messages.AiJobAlreadyFinished.Should().Be("このジョブは終了済みです");
        string.Format(Messages.TerminalStartPromptFormat, "a.md", "b").Should().StartWith("a.md を読んで");
    }
```

- `tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs`: 停止・再開・費用のテストを、
  「完了にする」「追跡をやめる」「端末を開き直す」「成果物一覧はサービスから来る」に書き直す。
  `AiJobSnapshot` の並びを新しいものに合わせる。
- `tests/MoTask.App.Tests/TaskCardAiBadgeTests.cs`: Task 9 で書いた `AiJobSnapshot` の引数を新しい並びに詰め直す。
- `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`: `AiSettingsViewModel` の生成を
  `new AiSettingsViewModel(store)` に直し、同時実行数・最大ターン数・ルール一覧のテストを消す。
- `tests/MoTask.App.Tests/HostWiringTests.cs`: そのままで通るはず（同じゲートを共有する不変条件は変わらない）。
  通らなければ、`BuildHost` の登録漏れを疑うこと。

- [ ] **Step 12: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS。`AiJobServiceLifecycleTests` が 18 件通ること。

- [ ] **Step 13: 実際に起動して、承認ダイアログが出ないことを確かめる**

Run: `dotnet run --project src/MoTask.App`
Expected: 起動する。タスクを選んで「遂行させる」→ 端末が開く。MoTask 側に承認ダイアログは出ない。
確認したら閉じる。

- [ ] **Step 14: コミット**

```bash
git add -A
git commit -m "feat: hand AI execution to a real terminal and drop the approval subsystem"
```

---

## Task 11: 詳細パネルの操作と手動チェックリスト

**Files:**
- Modify: `src/MoTask.App/Views/TaskDetailPanel.xaml`
- Modify: `README.md`
- Test: `tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs`（Task 10 で書いたものに 3 件足す）

**Interfaces:**
- Consumes: `TaskAiPanelViewModel` の `IsWaitingForInput` / `CanControl` / `JobFolder` / `Artifacts` /
  `CompleteCommand` / `StopTrackingCommand` / `ReopenTerminalCommand` / `OpenJobFolderCommand` /
  `OpenArtifactCommand` / `OpenWorkingDirectoryCommand`（Task 10）
- Produces: なし（このタスクが計画の終わり）

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs` に足す:

```csharp
    /// <summary>ジョブが 1 件走っている状態のパネル。既存の OpenAsync と同じ手順で開く。</summary>
    private async Task<TaskAiPanelViewModel> PanelWithRunningJobAsync()
    {
        _jobs.Add(new AiJob
        {
            Id = 1, TaskId = 10, Kind = AiJobKind.Execute, Status = AiJobStatus.Running,
            WorkingDirectory = @"C:\w", JobFolder = @"C:\w\jobs\0001-a",
        });
        return await OpenAsync();
    }

    [Fact]
    public async Task Panel_OffersTheControlsWhileTheJobIsTracked()
    {
        var panel = await PanelWithRunningJobAsync();

        panel.CanControl.Should().BeTrue();
        panel.CanStart.Should().BeFalse("追跡中は新しい依頼を受けない");
        panel.JobFolder.Should().Be(@"C:\w\jobs\0001-a");
    }

    [Fact]
    public async Task OpenJobFolder_OpensTheFolderOfTheCurrentJob()
    {
        var panel = await PanelWithRunningJobAsync();

        panel.OpenJobFolderCommand.Execute(null);

        _opened.Should().ContainSingle().Which.Should().Be(@"C:\w\jobs\0001-a");
    }

    [Fact]
    public async Task Panel_ListsTheArtifactsTheServiceReports()
    {
        _ai.GetArtifactsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { @"C:\w\jobs\0001-a\artifacts\report.md" }));

        var panel = await PanelWithRunningJobAsync();

        panel.Artifacts.Should().ContainSingle().Which.FileName.Should().Be("report.md");
    }
```

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~TaskAiPanelViewModelTests"`
Expected: FAIL（`JobFolder` が空、または成果物一覧が空）

- [ ] **Step 3: 詳細パネルの XAML を差し替える**

`src/MoTask.App/Views/TaskDetailPanel.xaml` の「最新ジョブの状態・費用・結果・成果物・進行状況」の
`StackPanel`（`HasJob` で出るもの）の中身を差し替える。

停止・再開のボタンが並ぶ `DockPanel` を、これに置き換える:

```xml
              <DockPanel>
                <TextBlock Text="{Binding StatusText}" FontWeight="Medium" VerticalAlignment="Center" />
              </DockPanel>
              <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource Brush.Danger}" TextWrapping="Wrap"
                         Visibility="{Binding ErrorMessage, Converter={StaticResource NullToVisibility}}" />
```

「費用」の 2 つの `TextBlock`（`AiCost` / `CostText`）を消す。

「結果」はそのまま残す。

「成果物」の `ItemsControl` はそのまま。その下の「作業フォルダを開く」ボタンを、操作のまとまりに置き換える:

```xml
              <!-- 操作（仕様 §11）。MoTask は端末のプロセスを所有しないので、殺すボタンは無い -->
              <TextBlock Text="{x:Static res:Strings.AiSection}" Style="{StaticResource Field.Label}" />
              <WrapPanel>
                <Button Content="{x:Static res:Strings.AiOpenJobFolder}" Command="{Binding OpenJobFolderCommand}"
                        Style="{StaticResource Btn.Ghost}" Margin="{StaticResource Gap.Right.4}" />
                <Button Content="{x:Static res:Strings.AiOpenWorkingDirectory}" Command="{Binding OpenWorkingDirectoryCommand}"
                        Style="{StaticResource Btn.Ghost}" Margin="{StaticResource Gap.Right.4}" />
                <Button Content="{x:Static res:Strings.AiReopenTerminal}" Command="{Binding ReopenTerminalCommand}"
                        Margin="{StaticResource Gap.Right.4}"
                        Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
                <Button Content="{x:Static res:Strings.AiComplete}" Command="{Binding CompleteCommand}"
                        Style="{StaticResource Btn.Primary}" Margin="{StaticResource Gap.Right.4}"
                        Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
                <Button Content="{x:Static res:Strings.AiStopTracking}" Command="{Binding StopTrackingCommand}"
                        Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
              </WrapPanel>
              <TextBlock Text="{x:Static res:Strings.AiStopTrackingHint}" Style="{StaticResource Text.Caption}" TextWrapping="Wrap"
                         Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}" />
```

- [ ] **Step 4: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 5: 実際に起動して、画面が壊れていないことを確かめる**

Run: `dotnet run --project src/MoTask.App`
Expected: 詳細パネルの AI セクションに、状態・結果・成果物・操作の 4 つが縦に並ぶ。
デバッガの出力ウィンドウに `System.Windows.Data Error` が出ないこと。

- [ ] **Step 6: README の手動チェックリストを書き直す**

`README.md` の「### 8. AI 遂行（Claude Code CLI）」の節をまるごと次に置き換える:

```markdown
### 8. AI 遂行（ターミナル実行）

前提: `claude --version` が 2.1.x を返し、ログイン済み。設定は `%LOCALAPPDATA%\MoTask\settings.json`。
仕様は `docs/superpowers/specs/2026-09-05-motask-terminal-ai-design.md`。

- [ ] 「AI 設定」ダイアログで既定の作業フォルダ・claude のパス・モデル・権限モード・端末の起動コマンドを
      変えて保存し、再起動後も残っている。
- [ ] タスクを選び「遂行させる」→ 指示文を確認して「開始」。Windows Terminal が開き、
      `instruction.md` を読んで作業が始まる。
- [ ] 走っている途中で人が割り込み、「そっちじゃない、こっちを調べて」と方向転換できる。
- [ ] Claude が人に質問してきたとき、端末でそれに答えられる。
- [ ] ツール承認が自分の `~/.claude/settings.json` の方針どおりに出る。MoTask のダイアログは出ない。
- [ ] モデルの応答が終わるたびにカードに「入力待ち」バッジが付き、次に道具を使うと消える。
- [ ] 端末で `/exit` すると、タスクが「確認待ち」列へ移り、履歴に「AI 遂行が完了」が残る。
- [ ] 端末を × で強制終了すると、ジョブは「AI 遂行中」のまま残る。詳細パネルの「完了にする」を押すと
      「確認待ち」列へ移る。
- [ ] MoTask を閉じたまま端末で作業を続け、MoTask を開き直すと、その間のやりとりが進行状況に
      追いついている。
- [ ] 「端末を開き直す」を押すと `--resume` で同じセッションが開き、会話の続きから始まる。
- [ ] 「追跡をやめる」を押すと「AI 停止」になるが、**端末は開いたまま**（プロセスは殺さない）。
- [ ] `<既定ワークフォルダ>\jobs\<番号>-<タスク名>\` に `job.json` / `instruction.md` / `hooks.json` /
      `events.jsonl` / `artifacts\` が揃っている。「ジョブフォルダを開く」で開ける。
- [ ] 成果物一覧に `artifacts\` の実ファイルが出る。`... > report.md` のようにリダイレクトで作った
      ファイルも出る（Write / Edit を通らなくても見える）。クリックで既定のアプリが開く。
- [ ] 存在しないパスをプロジェクトの作業フォルダに設定したタスクで開始すると、バナーに
      「プロジェクトの作業フォルダが見つかりません: …」と出て開始しない（既定へ逃げない）。
- [ ] `wt.exe` が無い環境（PATH から外して確認）では `cmd.exe` のウィンドウで開く。
- [ ] 端末の起動コマンドを `pwsh.exe -NoExit -Command {command}` にすると PowerShell で開く。
```

同じファイルの「自動テスト（Core / Data / App、373 本）」の件数を、`dotnet test` の実際の出力に合わせて直す。

- [ ] **Step 7: 全体を通す**

Run: `dotnet test MoTask.sln`
Expected: 全件 PASS

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.App/Views/TaskDetailPanel.xaml README.md tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs
git commit -m "feat(app): give the detail panel the terminal-era controls"
```

---

## 実装後の確認

すべてのタスクが済んだら、次を確かめてから `superpowers:finishing-a-development-branch` へ進む。

- [ ] `dotnet test MoTask.sln` が全件通る
- [ ] `grep -rn "Permission\|IAgentRunner\|TotalCostUsd\|MaxConcurrent\|MaxTurns\|Suspended\|AwaitingApproval" src tests --include=*.cs --include=*.xaml --include=*.resx` が、
      マイグレーションの中の SQL 文字列以外に当たらない
- [ ] `dotnet run --project src/MoTask.App` で起動し、README のチェックリスト §8 を上から通す
