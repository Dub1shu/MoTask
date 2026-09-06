# MoTask AI ログ — DB 記録の廃止と直近 3 行表示 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AI ジョブのイベントを DB に記録するのをやめ、詳細パネルには `events.jsonl` の直近 3 行だけを出す。

**Architecture:** `AiJobEvents` テーブルと、そこへの読み書きを丸ごと捨てる。表示は毎回ジョブフォルダの `events.jsonl` の末尾を読んで組み立てる（`IJobFolder.ReadTail`）。追従の再開位置は、保存済みイベント件数の代わりに `AiJob.ProcessedLines`（int）で持つ。依存方向は変えない。

**Tech Stack:** .NET 10 / WPF / EF Core 10 (SQLite) / xUnit + FluentAssertions + NSubstitute / CommunityToolkit.Mvvm

**Spec:** `docs/superpowers/specs/2026-09-06-motask-ai-log-trim-design.md`（前提: `docs/superpowers/specs/2026-09-05-motask-terminal-ai-design.md`）

## Global Constraints

- 対象フレームワークは `net10.0`（Core / Data / Hooks）と `net10.0-windows`（App / App.Tests）。`Directory.Build.props` は変えない。新しい NuGet 参照は追加しない。
- 依存方向: App → Data → Core、App → Core。`MoTask.Core` は他プロジェクトにも NuGet にも依存しない。
- 利用者向けの文言は日本語で resx に置く（Core: `src/MoTask.Core/Resources/Messages.resx`、App: `src/MoTask.App/Resources/Strings.resx`）。コード直書きの日本語文字列を作らない。`StringsTests.AllProperties_ResolveToNonEmptyValuesDistinctFromTheirNames` が resx 抜けを検出する。
- コード中のコメントと XML ドキュメントは日本語で書く。
- 列挙の既存番号は動かさない。`AiJobStatus` / `AiJobEventKind` / `AiJobKind` は EF で **文字列**として保存されている。
- マイグレーションは `dotnet ef` で生成する（手書きしない）。
- MoTask は端末のプロセスを所有しない。プロセスを殺すコードを一切書かない。
- 実起動（端末を開く、実セッションを走らせる）は自動テストしない。
- 既存テストの構築規約に従う: Core は `InMemoryStore` + `FakeClock`、App は NSubstitute の `IBoardService` + `TestBoards`、Data は `SqliteTestDatabase`。
- テスト実行は `dotnet test MoTask.sln`。個別は `dotnet test MoTask.sln --filter "FullyQualifiedName~<名前>"`。
- コミットは各タスクの末尾で行う。メッセージ末尾に `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>` を付ける。

## 仕様からの意図的なずれ（実装前に把握しておくこと）

**仕様 §5 の `GetResultTextAsync` を作らず、`GetEventsAsync` に行数を渡す形にする。**

仕様は「ログ用に 3 件返す `GetEventsAsync`」と「結果用に 200 行遡る `GetResultTextAsync`」の 2 つの口を置く形だった。しかし結果テキストの取り出し（`last_assistant_message` を payload から読む）は App の `AiJobEventFormatter.ResultText` が既に持っており、Core に同じ JSON 取り出しをもう 1 つ書くことになる。

代わりに `GetEventsAsync(int jobId, int lines, CancellationToken)` の 1 つにする。詳細パネルは 200 行で 1 回だけ呼び、ログには末尾 3 件、結果は 200 件全体から `ResultText` で導く。口が 1 つ減り、Core は JSON の中身を解釈せず、往復も 1 回で済む。「3 行」の上限は表示側（`TaskAiPanelViewModel`）が持つ。

## File Structure

変更するファイル:

| パス | 変更内容 |
| --- | --- |
| `src/MoTask.Core/Ai/IJobFolder.cs` | `ReadTail` を追加 |
| `src/MoTask.App/Ai/JobFolder.cs` | `ReadTail` の実装 |
| `src/MoTask.Core/Model/AiJob.cs` | `ProcessedLines` を追加。`AiJobEvent.Id` は Task 4 で削除 |
| `src/MoTask.Core/Model/AiJobEvent.cs` | `Id` を削除（EF から外れるため） |
| `src/MoTask.Core/Abstractions/IAiJobRepository.cs` | `AddEvent` / `GetEventsAsync` を削除 |
| `src/MoTask.Core/Services/IAiJobService.cs` | `GetEventsAsync` に `lines` を足す |
| `src/MoTask.Core/Services/AiJobService.cs` | 記録をやめ、`ProcessedLines` を進め、`GetEventsAsync` はファイルから組み立てる |
| `src/MoTask.Data/MoTaskDbContext.cs` | `AiJobEvents` の DbSet と設定を削除、`ProcessedLines` を設定 |
| `src/MoTask.Data/Repositories/AiJobRepository.cs` | `AddEvent` / `GetEventsAsync` を削除 |
| `src/MoTask.Data/Migrations/*` | 2 本（列追加、テーブル削除） |
| `src/MoTask.App/ViewModels/BoardViewModel.cs` | `QueryAiEventsAsync` に `lines` を足す |
| `src/MoTask.App/ViewModels/TaskAiPanelViewModel.cs` | 200 行読んで末尾 3 件だけ表示する |
| `tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs` | `ReadTail` を実装 |

---

## Task 1: Core の口と App の実装 — `IJobFolder.ReadTail`

**Files:**
- Modify: `src/MoTask.Core/Ai/IJobFolder.cs`
- Modify: `src/MoTask.App/Ai/JobFolder.cs`
- Modify: `tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs`
- Test: `tests/MoTask.App.Tests/JobFolderTests.cs`

**Interfaces:**
- Consumes: `JobFolderPaths`（既存）
- Produces: `IJobFolder.ReadTail(string root, int lines) -> IReadOnlyList<string>`。
  末尾 `lines` 行を古い順で返す。空行は飛ばす。フォルダやファイルが無ければ空。
  Task 3 の `AiJobService.GetEventsAsync` が使う。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/JobFolderTests.cs` の末尾（クラスの閉じ括弧の直前）に足す:

```csharp
    [Fact]
    public void ReadTail_ReturnsEmptyWhenThereIsNoFolder()
    {
        _folder.ReadTail(Path.Combine(_root, "no-such-job"), 3).Should().BeEmpty();
    }

    [Fact]
    public void ReadTail_ReturnsEveryLineWhenThereAreFewerThanAsked()
    {
        var root = WriteEvents("a\nb\n");

        _folder.ReadTail(root, 3).Should().Equal("a", "b");
    }

    [Fact]
    public void ReadTail_ReturnsTheLastLinesInOrder()
    {
        var root = WriteEvents("1\n2\n3\n4\n5\n");

        _folder.ReadTail(root, 3).Should().Equal("3", "4", "5");
    }

    [Fact]
    public void ReadTail_KeepsALastLineThatHasNoNewline()
    {
        var root = WriteEvents("1\n2\n3");

        _folder.ReadTail(root, 2).Should().Equal("2", "3");
    }

    [Fact]
    public void ReadTail_SkipsBlankLines()
    {
        var root = WriteEvents("1\n\n2\n\n");

        _folder.ReadTail(root, 3).Should().Equal("1", "2");
    }

    [Fact]
    public void ReadTail_KeepsALongLineIntact()
    {
        var line = new string('x', 40_000);
        var root = WriteEvents("short\n" + line + "\n");

        _folder.ReadTail(root, 1).Should().Equal(line);
    }

    [Fact]
    public void ReadTail_ReturnsEmptyWhenAskedForNoLines()
    {
        var root = WriteEvents("1\n2\n");

        _folder.ReadTail(root, 0).Should().BeEmpty();
    }

    /// <summary>events.jsonl だけを持つジョブフォルダを作り、その root を返す。</summary>
    private string WriteEvents(string content)
    {
        var root = Path.Combine(_root, "0001-tail");
        Directory.CreateDirectory(root);
        File.WriteAllText(JobFolderPaths.For(root).EventsJsonl, content, new UTF8Encoding(false));
        return root;
    }
```

`_folder`（`JobFolder` の実体）と `_root`（一時フォルダ）はこのテストクラスの既存フィールド。
`using System.Text;` と `using MoTask.Core.Ai;` はファイル先頭に既にある。

- [ ] **Step 2: テストが落ちることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderTests"`
Expected: コンパイルエラー。`IJobFolder` に `ReadTail` が無い。

- [ ] **Step 3: 口を足す**

`src/MoTask.Core/Ai/IJobFolder.cs` の `ListArtifacts` の下に足す:

```csharp
    /// <summary>
    /// events.jsonl の末尾 lines 行を古い順で返す。空行は飛ばす。
    /// フォルダやファイルが無ければ空。読めなくても投げない（表示が空になるだけ）。
    /// </summary>
    IReadOnlyList<string> ReadTail(string root, int lines);
```

- [ ] **Step 4: 実装する**

`src/MoTask.App/Ai/JobFolder.cs` の `ListArtifacts` の下に足す:

```csharp
    public IReadOnlyList<string> ReadTail(string root, int lines)
    {
        if (string.IsNullOrWhiteSpace(root) || lines <= 0) return Array.Empty<string>();
        var path = JobFolderPaths.For(root).EventsJsonl;
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            // フックが追記中でも読めるように共有を広く取る。ファイルは小さいので
            // 先頭から読んで末尾 lines 行だけ残す（末尾からの逆読みは行の境目を跨ぐと厄介）。
            var tail = new Queue<string>(lines);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Utf8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (tail.Count == lines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return tail.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めないだけで、ジョブの状態には関係しない
            return Array.Empty<string>();
        }
    }
```

- [ ] **Step 5: フェイクにも足す**

`tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs` に足す（既存のフィールドの並びに合わせる）:

```csharp
    /// <summary>ReadTail が返す行。テストが直接積む。</summary>
    public List<string> Lines { get; } = new();

    public IReadOnlyList<string> ReadTail(string root, int lines)
    {
        if (lines <= 0) return Array.Empty<string>();
        return Lines.Count <= lines ? Lines.ToList() : Lines.Skip(Lines.Count - lines).ToList();
    }
```

- [ ] **Step 6: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~JobFolderTests"`
Expected: PASS（新規 7 件を含む）

- [ ] **Step 7: 全体を流す**

Run: `dotnet test MoTask.sln`
Expected: すべて PASS、警告なし

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.Core/Ai/IJobFolder.cs src/MoTask.App/Ai/JobFolder.cs tests/MoTask.Core.Tests/Fakes/FakeJobFolder.cs tests/MoTask.App.Tests/JobFolderTests.cs
git commit -m "feat(core): read the tail of events.jsonl through the job folder port"
```

---

## Task 2: Data — `AiJob.ProcessedLines` 列

**Files:**
- Modify: `src/MoTask.Core/Model/AiJob.cs`
- Modify: `src/MoTask.Data/MoTaskDbContext.cs`
- Create: `src/MoTask.Data/Migrations/<timestamp>_AiJobProcessedLines.cs`（+ `.Designer.cs`、`dotnet ef` が作る）
- Modify: `src/MoTask.Data/Migrations/MoTaskDbContextModelSnapshot.cs`（`dotnet ef` が作る）
- Modify: `tests/MoTask.Data.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `AiJob.ProcessedLines`（`int`、既定 `0`）。Task 4 の `AiJobService` が書き、追従の `SkipLines` に渡す。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/MigrationTests.cs` に足す:

```csharp
    [Fact]
    public async Task AiJob_ProcessedLines_RoundTrips()
    {
        await using var db = _db.CreateContext();
        await db.Database.MigrateAsync();
        var board = new Board { Name = "b" };
        var column = new BoardColumn { Board = board, Name = "c", Order = 0 };
        db.Boards.Add(board);
        db.BoardColumns.Add(column);
        var task = new TaskItem { Title = "t", Column = column };
        db.Tasks.Add(task);
        db.AiJobs.Add(new AiJob
        {
            Task = task, Kind = AiJobKind.Research, Status = AiJobStatus.Running,
            SessionId = Guid.NewGuid(), StartedAt = DateTime.UtcNow,
            WorkingDirectory = @"C:\work", JobFolder = @"C:\work\jobs\0001-x",
            ProcessedLines = 7,
        });
        await db.SaveChangesAsync();

        await using var reopened = _db.CreateContext();
        var stored = await reopened.AiJobs.SingleAsync();
        stored.ProcessedLines.Should().Be(7);
    }
```

`_db` は同ファイルの `SqliteTestDatabase` フィールド。ボードと列の組み立ては、同ファイルの既存テスト
`AiJob_JobFolder_RoundTrips` と同じ形に合わせること。

- [ ] **Step 2: テストが落ちることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~MigrationTests"`
Expected: コンパイルエラー。`AiJob` に `ProcessedLines` が無い。

- [ ] **Step 3: プロパティを足す**

`src/MoTask.Core/Model/AiJob.cs` の `JobFolder` の下に足す:

```csharp
    /// <summary>events.jsonl から取り込み済みの行数。追従を張り直すときの読み飛ばし数になる。</summary>
    public int ProcessedLines { get; set; }
```

- [ ] **Step 4: EF に設定を足す**

`src/MoTask.Data/MoTaskDbContext.cs` の `AiJob` の設定で、`JobFolder` の設定の直後に足す:

```csharp
            e.Property(x => x.ProcessedLines).HasDefaultValue(0);
```

- [ ] **Step 5: マイグレーションを作る**

```bash
dotnet ef migrations add AiJobProcessedLines --project src/MoTask.Data --startup-project src/MoTask.App
```

生成物には手を入れない。`Up` が `AiJobs.ProcessedLines` を既定 0 で追加していることだけ目で確かめる。

- [ ] **Step 6: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~MigrationTests"`
Expected: PASS

- [ ] **Step 7: 全体を流す**

Run: `dotnet test MoTask.sln`
Expected: すべて PASS、警告なし

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.Core/Model/AiJob.cs src/MoTask.Data/MoTaskDbContext.cs src/MoTask.Data/Migrations tests/MoTask.Data.Tests/MigrationTests.cs
git commit -m "feat(data): record how many event lines a job has taken in"
```

---

## Task 3: 表示側 — ログはファイルの末尾から作る

このタスクの終わりでも DB への記録は続いている（読むのをやめるだけ）。撤去は Task 4。

**Files:**
- Modify: `src/MoTask.Core/Services/IAiJobService.cs`
- Modify: `src/MoTask.Core/Services/AiJobService.cs`
- Modify: `src/MoTask.App/ViewModels/BoardViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/TaskAiPanelViewModel.cs`
- Modify: `tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs`
- Modify: `tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs`

**Interfaces:**
- Consumes: `IJobFolder.ReadTail`（Task 1）、`HookEventParser`（既存）
- Produces:
  - `IAiJobService.GetEventsAsync(int jobId, int lines, CancellationToken ct = default)` —
    `events.jsonl` の末尾 `lines` 行を `AiJobEvent` に組み立てて古い順で返す。`Seq` は 1 から振り直す
  - `BoardViewModel.QueryAiEventsAsync(int jobId, int lines)`
  - `TaskAiPanelViewModel` は 200 行読み、`Log` には末尾 3 件だけ入れる

- [ ] **Step 1: 失敗するテストを書く（Core）**

`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs` に足す:

```csharp
    [Fact]
    public async Task GetEvents_ReadsTheTailOfTheEventsFile()
    {
        var job = await StartAsync();
        _folder.Lines.Add(FakeJobEventSource.SessionStart());
        _folder.Lines.Add(FakeJobEventSource.PostToolUse("Read"));
        _folder.Lines.Add(FakeJobEventSource.PostToolUse("Bash"));
        _folder.Lines.Add(FakeJobEventSource.Stop("できました"));

        var events = await _service.GetEventsAsync(job.Id, 3);

        events.Should().HaveCount(3);
        events[0].Kind.Should().Be(AiJobEventKind.ToolUse);
        events[0].ToolName.Should().Be("Read");
        events[2].Kind.Should().Be(AiJobEventKind.TurnEnded);
        events.Select(e => e.Seq).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task GetEvents_ReturnsEmptyForAJobWithoutAFolder()
    {
        var job = await StartAsync();
        _store.Jobs.Single(j => j.Id == job.Id).JobFolder = "";

        (await _service.GetEventsAsync(job.Id, 3)).Should().BeEmpty();
    }

    /// <summary>結果（最終回答）は末尾 3 行の外にあることが多い。広く読めば届くことを固定する。</summary>
    [Fact]
    public async Task GetEvents_ReachesAnEarlierTurnWhenAskedForMoreLines()
    {
        var job = await StartAsync();
        _folder.Lines.Add(FakeJobEventSource.Stop("まとめました"));
        for (var i = 0; i < 5; i++) _folder.Lines.Add(FakeJobEventSource.PostToolUse("Read"));

        var forTheLog = await _service.GetEventsAsync(job.Id, 3);
        var forTheResult = await _service.GetEventsAsync(job.Id, 200);

        forTheLog.Should().OnlyContain(e => e.Kind == AiJobEventKind.ToolUse);
        forTheResult.Should().Contain(e => e.Kind == AiJobEventKind.TurnEnded);
    }
```

`_service` / `_store` / `_folder` はこのテストクラスの既存フィールド。`StartAsync()` は引数なしの
既存ヘルパ。`FakeJobEventSource.SessionStart()` / `PostToolUse(tool)` / `Stop(message)` は
フェイクの静的ヘルパで、フックの 1 行をそのまま返す。

- [ ] **Step 2: テストが落ちることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJobServiceLifecycleTests"`
Expected: コンパイルエラー。`GetEventsAsync` が 2 引数を取らない。

- [ ] **Step 3: 口を差し替える**

`src/MoTask.Core/Services/IAiJobService.cs` の `GetEventsAsync` の行を次に置き換える:

```csharp
    /// <summary>
    /// events.jsonl の末尾 lines 行を古い順で返す。DB には記録していないので、毎回ファイルを読む。
    /// Seq は返す並びに 1 から振り直したもので、ジョブ内の通し番号ではない（表示順にしか使わない）。
    /// </summary>
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, int lines, CancellationToken ct = default);
```

- [ ] **Step 4: 実装を差し替える**

`src/MoTask.Core/Services/AiJobService.cs` の既存の `GetEventsAsync`（`_jobs.GetEventsAsync` に委譲している 2 行）を次に置き換える:

```csharp
    public async Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, int lines, CancellationToken ct = default)
    {
        var job = await _gate.RunAsync(() => _jobs.GetAsync(jobId, ct), ct).ConfigureAwait(false);
        if (job is null || job.JobFolder.Length == 0) return Array.Empty<AiJobEvent>();

        var tail = _folder.ReadTail(job.JobFolder, lines);
        var events = new List<AiJobEvent>(tail.Count);
        var seq = 0;
        foreach (var line in tail)
        {
            var parsed = HookEventParser.Parse(line);
            events.Add(new AiJobEvent
            {
                JobId = jobId, Seq = ++seq, At = _clock.UtcNow,
                Kind = parsed.Kind, ToolName = parsed.ToolName, Payload = parsed.Payload,
            });
        }
        return events;
    }
```

`_folder` はこのクラスが既に持っている `IJobFolder` のフィールド。名前が違う場合はそのフィールド名を使う。

- [ ] **Step 5: テストが通ることを確かめる（Core）**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJobServiceLifecycleTests"`
Expected: PASS

- [ ] **Step 6: 失敗するテストを書く（App）**

`tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs` に足す:

```csharp
    [Fact]
    public async Task Panel_ShowsOnlyTheLastThreeLogLines()
    {
        var vm = NewPanel(out var service);
        service.GetEventsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Event(1, AiJobEventKind.ToolUse, "Read"),
                Event(2, AiJobEventKind.ToolUse, "Grep"),
                Event(3, AiJobEventKind.ToolUse, "Bash"),
                Event(4, AiJobEventKind.ToolUse, "Write"),
                Event(5, AiJobEventKind.ToolUse, "Edit"),
            });

        await vm.LoadAsync();

        vm.Log.Should().HaveCount(3);
    }
```

`NewPanel` / `Event(...)` は同ファイルの既存テストの組み立てに合わせる。ヘルパが無ければ、
そのファイルが `TaskAiPanelViewModel` を作っている既存のやり方をそのまま真似て書く。

- [ ] **Step 7: 呼び出し側を直す**

`src/MoTask.App/ViewModels/BoardViewModel.cs` の `QueryAiEventsAsync` を次に置き換える:

```csharp
    public async Task<IReadOnlyList<AiJobEvent>> QueryAiEventsAsync(int jobId, int lines)
    {
        var events = await QueryAsync(() => AiJobs.GetEventsAsync(jobId, lines));
        if (events.IsSuccess) return events.Value!;
        ShowFailure(events);
        return Array.Empty<AiJobEvent>();
    }
```

`src/MoTask.App/ViewModels/TaskAiPanelViewModel.cs` のクラス先頭（フィールドの並びの先頭）に足す:

```csharp
    /// <summary>画面に残すログの行数。進行は端末で見えるので、直近だけ出す。</summary>
    private const int LogLines = 3;

    /// <summary>結果（最終回答）を探しに遡る行数。これより古い Stop は諦める。</summary>
    private const int ResultScanLines = 200;
```

同ファイルの `LoadAsync` の中の次の 3 行

```csharp
        var events = await _board.QueryAiEventsAsync(latest.Id);
        if (generation != _loadGeneration) return;
        foreach (var e in events) Log.Add(AiJobEventFormatter.Format(e));
```

を次に置き換える:

```csharp
        // 結果は末尾 3 行の外にあることが多いので、広めに読んでから表示だけ絞る
        var events = await _board.QueryAiEventsAsync(latest.Id, ResultScanLines);
        if (generation != _loadGeneration) return;
        foreach (var e in events.TakeLast(LogLines)) Log.Add(AiJobEventFormatter.Format(e));
```

同ファイルの `OnJobChanged` の中の次の 1 行

```csharp
            Log.Add(AiJobEventFormatter.Format(ev));
```

を次に置き換える:

```csharp
            Log.Add(AiJobEventFormatter.Format(ev));
            while (Log.Count > LogLines) Log.RemoveAt(0);
```

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~TaskAiPanelViewModelTests"`
Expected: PASS

- [ ] **Step 9: 全体を流す**

Run: `dotnet test MoTask.sln`
Expected: すべて PASS、警告なし

- [ ] **Step 10: コミット**

```bash
git add src/MoTask.Core/Services/IAiJobService.cs src/MoTask.Core/Services/AiJobService.cs src/MoTask.App/ViewModels/BoardViewModel.cs src/MoTask.App/ViewModels/TaskAiPanelViewModel.cs tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs
git commit -m "feat(app): build the AI log from the tail of events.jsonl"
```

---

## Task 4: 記録の撤去 — `AiJobEvents` を捨てる

このタスクは削除を含む。**順番を守ること**: 先にサービスを `ProcessedLines` に切り替え、
最後にリポジトリ・DbContext・テーブルを消す。逆にするとコンパイルが通らない。

**Files:**
- Modify: `src/MoTask.Core/Services/AiJobService.cs`
- Modify: `src/MoTask.Core/Model/AiJobEvent.cs`
- Modify: `src/MoTask.Core/Abstractions/IAiJobRepository.cs`
- Modify: `src/MoTask.Data/Repositories/AiJobRepository.cs`
- Modify: `src/MoTask.Data/MoTaskDbContext.cs`
- Create: `src/MoTask.Data/Migrations/<timestamp>_DropAiJobEvents.cs`（+ Designer、`dotnet ef` が作る）
- Modify: `src/MoTask.Data/Migrations/MoTaskDbContextModelSnapshot.cs`（`dotnet ef` が作る）
- Modify: `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`
- Modify: `tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs`
- Modify: `tests/MoTask.Data.Tests/AiRepositoryTests.cs`
- Modify: `tests/MoTask.Data.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: `AiJob.ProcessedLines`（Task 2）、`GetEventsAsync(jobId, lines)`（Task 3）
- Produces: なし（このタスクが計画の終わり）

- [ ] **Step 1: 失敗するテストを書く（再開位置）**

`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs` に足す:

```csharp
    [Fact]
    public async Task HookLine_AdvancesTheProcessedLineCount()
    {
        var job = await StartAsync();

        await _events.EmitAsync(job.Id, FakeJobEventSource.SessionStart());
        await _events.EmitAsync(job.Id, FakeJobEventSource.PostToolUse("Read"));

        _store.Jobs.Single(j => j.Id == job.Id).ProcessedLines.Should().Be(2);
    }

    [Fact]
    public async Task Recover_SkipsTheLinesAlreadyTakenIn()
    {
        var job = await StartAsync();
        _store.Jobs.Single(j => j.Id == job.Id).ProcessedLines = 5;

        await _service.RecoverOnStartupAsync();

        _events.SkipLinesOf(job.Id).Should().Be(5);
    }
```

併せて既存の 2 本、`Recover_PicksUpWhereTheEventLogWasLeft`（247 行付近）と
`Recover_FinishesAJobWhoseSessionEndArrivedWhileTheAppWasClosed`（262 行付近）を見る。
どちらも `_store.JobEvents` に行を積んで「取り込み済みの件数」を作っているので、
その仕込みを `_store.Jobs.Single(...).ProcessedLines = <件数>` に置き換える。
検証している中身（再開位置、閉じている間に届いた `SessionEnd` で完了すること）は変えない。

- [ ] **Step 2: テストが落ちることを確かめる**

Run: `dotnet test MoTask.sln --filter "FullyQualifiedName~AiJobServiceLifecycleTests"`
Expected: FAIL。`ProcessedLines` が 0 のまま進まない。

- [ ] **Step 3: サービスの記録をやめる**

`src/MoTask.Core/Services/AiJobService.cs` の `OnHookLineAsync` の中で、
`stored` を作って `_jobs.AddEvent(stored)` している部分を次に置き換える:

```csharp
            var tracked = await TrackedForAsync(current).ConfigureAwait(false);
            tracked.Seq++;
            current.ProcessedLines = tracked.Seq;
            // DB には残さない。画面に出す 1 件だけを組み立てて JobChanged で渡す。
            stored = new AiJobEvent
            {
                JobId = jobId, Seq = tracked.Seq, At = _clock.UtcNow,
                Kind = parsed.Kind, ToolName = parsed.ToolName, Payload = parsed.Payload,
            };
```

- [ ] **Step 4: 再開位置の作り方を変える**

同ファイルで `_jobs.GetEventsAsync(...)` を呼んで `events.Count` を `Seq` や `skipLines` に
使っている箇所（`ReopenTerminalAsync`、`RecoverOnStartupAsync`、`TrackedForAsync` の 3 か所）を、
その `AiJob` の `ProcessedLines` を使う形に書き換える。例:

```csharp
        tracked = new TrackedJob { TaskId = job.TaskId, Seq = job.ProcessedLines, Turns = job.NumTurns ?? 0 };
```

```csharp
        Follow(job.Id, job.JobFolder, job.ProcessedLines);
```

書き換え後、`AiJobService.cs` に `_jobs.GetEventsAsync` と `_jobs.AddEvent` の呼び出しが
1 つも残っていないことを確かめる:

```bash
grep -n "_jobs.GetEventsAsync\|_jobs.AddEvent" src/MoTask.Core/Services/AiJobService.cs
```

Expected: 出力なし

- [ ] **Step 5: リポジトリから口を消す**

`src/MoTask.Core/Abstractions/IAiJobRepository.cs` から次の 3 行（コメント含む）を消す:

```csharp
    void AddEvent(AiJobEvent entry);
    /// <summary>Seq 昇順。</summary>
    Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, CancellationToken ct = default);
```

`src/MoTask.Data/Repositories/AiJobRepository.cs` から `AddEvent` と `GetEventsAsync` の実装を消す。
`tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs` からも同じ 2 つの実装（とイベントを溜めている
コレクション）を消す。

- [ ] **Step 6: モデルと EF から外す**

`src/MoTask.Core/Model/AiJobEvent.cs` の `Id` プロパティを消し、クラスの XML ドキュメントを
次に差し替える:

```csharp
/// <summary>画面に出す 1 行。DB には保存しない（events.jsonl が実体）。</summary>
```

`src/MoTask.Data/MoTaskDbContext.cs` から `AiJobEvents` の `DbSet` と、`b.Entity<AiJobEvent>(...)`
の設定ブロックを丸ごと消す。

- [ ] **Step 7: テーブルを落とすマイグレーションを作る**

```bash
dotnet ef migrations add DropAiJobEvents --project src/MoTask.Data --startup-project src/MoTask.App
```

生成物に手は入れない。`Up` が `AiJobEvents` を `DropTable` していることだけ目で確かめる。

- [ ] **Step 8: 落ちるテストを直す**

`tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs` には `_store.JobEvents` を検証している箇所が
5 か所ある（96、122、150-151、163-164、200-204 行付近）。これらが見ているのは
「1 行につき 1 件のイベントが上がること」「payload が生のまま残ること」「壊れた行は `System` になること」
「終わったジョブに遅れて届いた行は捨てること」で、いずれも今も守るべき振る舞い。
DB ではなく `JobChanged` で上がった `_changes`（このクラスが既に集めている）に対する検証へ移す。例:

```csharp
        // 変更前: _store.JobEvents.Last().ToolName.Should().Be("Read");
        _changes.Last().NewEvent!.ToolName.Should().Be("Read");
```

```csharp
        // 変更前: var before = _store.JobEvents.Count;  …  _store.JobEvents.Should().HaveCount(before);
        var before = _changes.Count;
        // …
        _changes.Should().HaveCount(before);
```

移し終えたら、このファイルに `_store.JobEvents` が 1 つも残っていないことを確かめる:

```bash
grep -n "JobEvents" tests/MoTask.Core.Tests/AiJobServiceLifecycleTests.cs
```

Expected: 出力なし

`tests/MoTask.Data.Tests/AiRepositoryTests.cs` と `tests/MoTask.Data.Tests/MigrationTests.cs` から、
`AiJobEvents` を読み書きしているテストを消す。`MigrationTests` には次を足す:

```csharp
    [Fact]
    public async Task Migrate_DropsTheAiJobEventsTable()
    {
        await using var db = _db.CreateContext();
        await db.Database.MigrateAsync();

        var names = await db.Database
            .SqlQuery<string>($"select name from sqlite_master where type = 'table'")
            .ToListAsync();

        names.Should().NotContain("AiJobEvents");
    }
```

- [ ] **Step 9: テストが通ることを確かめる**

Run: `dotnet test MoTask.sln`
Expected: すべて PASS、警告なし

- [ ] **Step 10: 記録が消えたことを確かめる**

```bash
grep -rn "AiJobEvents" src/ --include=*.cs | grep -v "src/MoTask.Data/Migrations"
```

Expected: 出力なし（マイグレーションの中の文字列だけが残る）

- [ ] **Step 11: コミット**

```bash
git add -A
git commit -m "feat: stop recording AI job events in the database"
```

---

## 実装後の確認

自動テストでは守れないので、人が 1 回やる。

- [ ] MoTask を起動する。既存の DB にマイグレーションが当たり、盤面が出る
- [ ] AI ジョブのあるタスクを開き、ログが 3 行までしか出ないことを見る
- [ ] 結果ペインに最終回答が出ていることを見る（3 行の外にある Stop から拾えている）
- [ ] 新しく「AI で調査」を始め、行が届くたびにログが 3 行を保ったまま入れ替わることを見る
- [ ] MoTask を再起動し、同じジョブのログ 3 行と結果が復元されることを見る
- [ ] `sqlite3` などで `AiJobEvents` テーブルが消えていること、DB のサイズが縮んでいることを見る
