# NSubstitute の撤去 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `MoTask.App.Tests` のテストダブルを手書きに置き換え、少人数コミュニティが管理する OSS である NSubstitute への依存を消す。

**Architecture:** `MoTask.Core.Tests/Fakes/` と `MoTask.Mcp.Tests/Fakes/` に既にある手書きテストダブルと同じ流儀で、`MoTask.App.Tests/Fakes/` に 5 つの偽実装を置く。各偽実装は「既定では成功を返す」「テストが変えたい所だけデリゲートで差し替える」「呼び出しは `record` のリストに積む」という共通の形にする。検証は FluentAssertions で記録リストを調べる形に書き換える。テストの本数も意味も変えない。

**Tech Stack:** .NET 10 / WPF (net10.0-windows) / xunit 2.9.3 / FluentAssertions 7.2.2

**Spec:** `docs/superpowers/specs/2026-09-12-drop-third-party-oss-deps-design.md`

## Global Constraints

- **テストの本数と意味を変えない。** 落ちるようになったテストを消して通すのは禁止。検証していたことは同じだけ検証する。
- **本体コードに触らない。** `src/` 配下は 1 行も変えない。これはテストプロジェクトだけの作業。
- **`FluentAssertions` は残す。** 検証は `.Should()` のまま書く。
- **`MoTask.Core.Tests` / `MoTask.Data.Tests` / `MoTask.Mcp.Tests` に触らない。** NSubstitute を使っているのは `MoTask.App.Tests` だけ。
- **1 ファイル移すごとにテストを通す。** まとめて移して最後に直すことはしない。
- **偽実装の置き場所**: `tests/MoTask.App.Tests/Fakes/`、名前空間は `MoTask.App.Tests.Fakes`。
- **偽実装の共通の形**:
  - 既定の応答は成功。`Task<Result>` を返すものは `Result.Ok()`、一覧を返すものは空。
  - 差し替えは `public Func<XxxCall, Task<Result>>? OnXxx { get; set; }` のような可変プロパティ。null なら既定の応答。
  - 呼び出しは `public List<XxxCall> XxxCalls { get; } = new();` に `record` で積む。`CancellationToken` は記録しない。
- **前提**: 1 本目の計画（`2026-09-13-motask-drop-gong-dragdrop.md`）が master にマージ済みであること。`DropHandlerTests` から gong への依存が消えていることを当てにする。
- **着手前の基準値**: `dotnet build` が 0 警告 0 エラー、テストが全緑。1 本目の完了時点の本数を下回らないこと。
- **MoTask.exe を起動したままビルドしない。** 出力 DLL がロックされてビルドが失敗する。
- **コミットメッセージ**は日本語の Conventional Commits。末尾にこのセッションの指示する `Co-Authored-By:` 行を付ける。
- **作業ブランチ**: `master` から `feature/drop-nsubstitute` を切って作業する。

## 移行の共通規則

すべてのファイルで次の対応をとる。個別のタスクではこの表を前提に、そのファイル固有の点だけを書く。

| NSubstitute の書き方 | 手書き偽実装での書き方 |
|---|---|
| `Substitute.For<IFoo>()` | `new FakeFoo()` |
| `_foo.BarAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(x)` | `_foo.OnBar = _ => x;` |
| `_foo.BarAsync(10, Arg.Any<CancellationToken>()).Returns(x)` | `_foo.OnBar = call => call.Id == 10 ? x : Task.FromResult(Result.Ok());` |
| `_foo.BarAsync(...).Returns(_ => { 副作用; return x; })` | `_foo.OnBar = _ => { 副作用; return x; };` |
| `_foo.BarAsync(...).Returns(Task.FromException<Result>(ex))` | `_foo.OnBar = _ => Task.FromException<Result>(ex);` |
| `await _foo.Received(1).BarAsync(10, 2, Arg.Any<CancellationToken>())` | `_foo.BarCalls.Should().ContainSingle().Which.Should().Be(new BarCall(10, 2));` |
| `await _foo.Received(2).BarAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())` | `_foo.BarCalls.Should().HaveCount(2);` |
| `await _foo.DidNotReceive().BarAsync(...)` | `_foo.BarCalls.Should().BeEmpty();` |
| `Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0)` | 記録した値を直接調べる |
| `_foo.Evt += Raise.Event<EventHandler>(this, EventArgs.Empty)` | `_foo.RaiseEvt();` |

`Received(1)` は「全体で 1 回だけ」という意味なので、`ContainSingle()` に写す。テストの途中で他の呼び出しが混ざる箇所では `.Should().HaveCount(n)` と添字での照合に写す。

---

### Task 1: Fakes の置き場所と流儀を作る

一番小さいファイルで偽実装の形を固める。`IAiSettingsStore` はメンバーが 2 つしかないので、以降のタスクの雛形になる。

**Files:**
- Create: `tests/MoTask.App.Tests/Fakes/FakeAiSettingsStore.cs`
- Test: `tests/MoTask.App.Tests/AiSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `MoTask.App.Tests.Fakes.FakeAiSettingsStore` — `AiSettings Settings { get; set; }` / `List<AiSettings> SaveCalls { get; }` / `int LoadCalls { get; }`

- [ ] **Step 1: 偽実装を書く**

`tests/MoTask.App.Tests/Fakes/FakeAiSettingsStore.cs` を新規作成する。

```csharp
using MoTask.Core.Ai;

namespace MoTask.App.Tests.Fakes;

/// <summary>ファイルは触らない。返す設定はテストが差し替え、保存された設定を記録するだけ。</summary>
public sealed class FakeAiSettingsStore : IAiSettingsStore
{
    /// <summary>Load が返す設定。テストが差し替える。</summary>
    public AiSettings Settings { get; set; } = AiSettings.Default();

    public int LoadCalls { get; private set; }

    /// <summary>Save に渡された設定を呼ばれた順に。</summary>
    public List<AiSettings> SaveCalls { get; } = new();

    public AiSettings Load()
    {
        LoadCalls++;
        return Settings;
    }

    public void Save(AiSettings settings) => SaveCalls.Add(settings);
}
```

`AiSettings.Default()` は `src/MoTask.Core/Ai/AiSettings.cs` に存在する（確認済み）。

- [ ] **Step 2: テストが偽実装を使うようにする**

`tests/MoTask.App.Tests/AiSettingsViewModelTests.cs` に次の変更を加える。

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

12 行目、

```csharp
    private readonly IAiSettingsStore _store = Substitute.For<IAiSettingsStore>();
```

を

```csharp
    private readonly FakeAiSettingsStore _store = new();
```

に。

16 行目と 121 行目の

```csharp
        _store.Load().Returns(<設定>);
```

を

```csharp
        _store.Settings = <設定>;
```

に（`<設定>` はその行にある `new AiSettings(...)` の式をそのまま使う）。

43 / 69 / 94 / 114 / 128 行目の

```csharp
        _store.Received(1).Save(<設定>);
```

を

```csharp
        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(<設定>);
```

に。

57 / 83 行目の

```csharp
        _store.DidNotReceive().Save(Arg.Any<AiSettings>());
```

を

```csharp
        _store.SaveCalls.Should().BeEmpty();
```

に。

- [ ] **Step 3: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~AiSettingsViewModelTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 4: コミットする**

```bash
git add tests/MoTask.App.Tests/Fakes/FakeAiSettingsStore.cs tests/MoTask.App.Tests/AiSettingsViewModelTests.cs
git commit
```

件名: `test(app): AI 設定のテストを手書きの偽実装に移す`

---

### Task 2: FakeBoardService を書き、照会だけのテストを移す

`IBoardService` は 23 メンバーあり、5 ファイルで使われている。ここで全メンバーを書き切って、以降のタスクは移行だけにする。

**Files:**
- Create: `tests/MoTask.App.Tests/Fakes/BoardServiceCalls.cs`
- Create: `tests/MoTask.App.Tests/Fakes/FakeBoardService.cs`
- Test: `tests/MoTask.App.Tests/BoardToolHostReadTests.cs`

**Interfaces:**
- Consumes: なし
- Produces:
  - 呼び出し記録の `record`: `CreateTaskCall(int ColumnId, string Title)` / `UpdateTaskCall(TaskUpdate Update)` / `MoveTaskCall(int TaskId, int ToColumnId, int Position)` / `SetTaskLabelsCall(int TaskId, IReadOnlyCollection<int> LabelIds)` / `AddColumnCall(string Name, ColumnRole Role)` / `RenameColumnCall(int ColumnId, string Name)` / `SetColumnRoleCall(int ColumnId, ColumnRole Role)` / `ReorderColumnsCall(IReadOnlyList<int> OrderedColumnIds)` / `SetWipLimitCall(int ColumnId, int? WipLimit)` / `CreateLabelCall(string Name, string Color)` / `SetProjectWorkingDirectoryCall(int ProjectId, string? Path)`
  - `MoTask.App.Tests.Fakes.FakeBoardService` — 上記の `XxxCalls` リストと `OnXxx` デリゲート、照会用の `Board` / `Projects` / `Labels` / `History` プロパティと `OnGetBoard` / `OnGetProjects` / `OnGetLabels` / `OnGetHistory`、`int GetBoardCalls { get; }`

- [ ] **Step 1: 呼び出し記録の型を書く**

`tests/MoTask.App.Tests/Fakes/BoardServiceCalls.cs` を新規作成する。

```csharp
using MoTask.Core.Model;

namespace MoTask.App.Tests.Fakes;

// FakeBoardService が積む呼び出しの記録。CancellationToken は記録しない
// （テストが Arg.Any<CancellationToken>() で読み飛ばしていたものなので、比べる意味がない）。

public sealed record CreateTaskCall(int ColumnId, string Title);
public sealed record UpdateTaskCall(TaskUpdate Update);
public sealed record MoveTaskCall(int TaskId, int ToColumnId, int Position);
public sealed record SetTaskLabelsCall(int TaskId, IReadOnlyCollection<int> LabelIds);
public sealed record AddColumnCall(string Name, ColumnRole Role);
public sealed record RenameColumnCall(int ColumnId, string Name);
public sealed record SetColumnRoleCall(int ColumnId, ColumnRole Role);
public sealed record ReorderColumnsCall(IReadOnlyList<int> OrderedColumnIds);
public sealed record SetWipLimitCall(int ColumnId, int? WipLimit);
public sealed record CreateLabelCall(string Name, string Color);
public sealed record SetProjectWorkingDirectoryCall(int ProjectId, string? Path);
public sealed record GetHistoryCall(int TaskId);
```

- [ ] **Step 2: FakeBoardService を書く**

`tests/MoTask.App.Tests/Fakes/FakeBoardService.cs` を新規作成する。

```csharp
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

/// <summary>
/// 保存はしない。呼ばれた操作を記録し、既定では成功を返す。テストが変えたい所だけ
/// On～ にデリゲートを挿す（null なら既定の応答）。
/// </summary>
public sealed class FakeBoardService : IBoardService
{
    // ---- 照会 ----

    /// <summary>GetBoardAsync が返す盤。呼ばれるたびに読むので、テストが途中で差し替えられる。</summary>
    public Board Board { get; set; } = new();
    public IReadOnlyList<Project> Projects { get; set; } = Array.Empty<Project>();
    public IReadOnlyList<Label> Labels { get; set; } = Array.Empty<Label>();
    public IReadOnlyList<HistoryEntry> History { get; set; } = Array.Empty<HistoryEntry>();

    public Func<Task<Result<Board>>>? OnGetBoard { get; set; }
    public Func<Task<IReadOnlyList<Project>>>? OnGetProjects { get; set; }
    public Func<Task<IReadOnlyList<Label>>>? OnGetLabels { get; set; }
    public Func<GetHistoryCall, Task<IReadOnlyList<HistoryEntry>>>? OnGetHistory { get; set; }

    public int GetBoardCalls { get; private set; }
    public List<GetHistoryCall> GetHistoryCalls { get; } = new();

    public Task<Result<Board>> GetBoardAsync(CancellationToken ct = default)
    {
        GetBoardCalls++;
        return OnGetBoard?.Invoke() ?? Task.FromResult(Result.Ok(Board));
    }

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => OnGetProjects?.Invoke() ?? Task.FromResult(Projects);

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => OnGetLabels?.Invoke() ?? Task.FromResult(Labels);

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default)
    {
        var call = new GetHistoryCall(taskId);
        GetHistoryCalls.Add(call);
        return OnGetHistory?.Invoke(call) ?? Task.FromResult(History);
    }

    // ---- タスク ----

    public List<CreateTaskCall> CreateTaskCalls { get; } = new();
    public List<UpdateTaskCall> UpdateTaskCalls { get; } = new();
    public List<MoveTaskCall> MoveTaskCalls { get; } = new();
    public List<int> DeleteTaskCalls { get; } = new();
    public List<int> RestoreTaskCalls { get; } = new();
    public List<SetTaskLabelsCall> SetTaskLabelsCalls { get; } = new();

    public Func<CreateTaskCall, Task<Result<TaskItem>>>? OnCreateTask { get; set; }
    public Func<UpdateTaskCall, Task<Result>>? OnUpdateTask { get; set; }
    public Func<MoveTaskCall, Task<Result>>? OnMoveTask { get; set; }
    public Func<int, Task<Result>>? OnDeleteTask { get; set; }
    public Func<int, Task<Result>>? OnRestoreTask { get; set; }
    public Func<SetTaskLabelsCall, Task<Result>>? OnSetTaskLabels { get; set; }

    public Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default)
    {
        var call = new CreateTaskCall(columnId, title);
        CreateTaskCalls.Add(call);
        return OnCreateTask?.Invoke(call) ?? Task.FromResult(Result.Ok(new TaskItem { Title = title }));
    }

    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default)
    {
        var call = new UpdateTaskCall(update);
        UpdateTaskCalls.Add(call);
        return OnUpdateTask?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default)
    {
        var call = new MoveTaskCall(taskId, toColumnId, position);
        MoveTaskCalls.Add(call);
        return OnMoveTask?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default)
    {
        DeleteTaskCalls.Add(taskId);
        return OnDeleteTask?.Invoke(taskId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default)
    {
        RestoreTaskCalls.Add(taskId);
        return OnRestoreTask?.Invoke(taskId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var call = new SetTaskLabelsCall(taskId, labelIds.ToList());
        SetTaskLabelsCalls.Add(call);
        return OnSetTaskLabels?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    // ---- 列 ----

    public List<AddColumnCall> AddColumnCalls { get; } = new();
    public List<RenameColumnCall> RenameColumnCalls { get; } = new();
    public List<SetColumnRoleCall> SetColumnRoleCalls { get; } = new();
    public List<ReorderColumnsCall> ReorderColumnsCalls { get; } = new();
    public List<SetWipLimitCall> SetWipLimitCalls { get; } = new();
    public List<int> DeleteColumnCalls { get; } = new();

    public Func<AddColumnCall, Task<Result<Column>>>? OnAddColumn { get; set; }
    public Func<RenameColumnCall, Task<Result>>? OnRenameColumn { get; set; }
    public Func<SetColumnRoleCall, Task<Result>>? OnSetColumnRole { get; set; }
    public Func<ReorderColumnsCall, Task<Result>>? OnReorderColumns { get; set; }
    public Func<SetWipLimitCall, Task<Result>>? OnSetWipLimit { get; set; }
    public Func<int, Task<Result>>? OnDeleteColumn { get; set; }

    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default)
    {
        var call = new AddColumnCall(name, role);
        AddColumnCalls.Add(call);
        return OnAddColumn?.Invoke(call) ?? Task.FromResult(Result.Ok(new Column { Name = name, Role = role }));
    }

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default)
    {
        var call = new RenameColumnCall(columnId, name);
        RenameColumnCalls.Add(call);
        return OnRenameColumn?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default)
    {
        var call = new SetColumnRoleCall(columnId, role);
        SetColumnRoleCalls.Add(call);
        return OnSetColumnRole?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default)
    {
        var call = new ReorderColumnsCall(orderedColumnIds.ToList());
        ReorderColumnsCalls.Add(call);
        return OnReorderColumns?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default)
    {
        var call = new SetWipLimitCall(columnId, wipLimit);
        SetWipLimitCalls.Add(call);
        return OnSetWipLimit?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default)
    {
        DeleteColumnCalls.Add(columnId);
        return OnDeleteColumn?.Invoke(columnId) ?? Task.FromResult(Result.Ok());
    }

    // ---- 分類 ----

    public List<string> CreateProjectCalls { get; } = new();
    public List<int> ArchiveProjectCalls { get; } = new();
    public List<int> UnarchiveProjectCalls { get; } = new();
    public List<SetProjectWorkingDirectoryCall> SetProjectWorkingDirectoryCalls { get; } = new();
    public List<CreateLabelCall> CreateLabelCalls { get; } = new();
    public List<int> ArchiveLabelCalls { get; } = new();
    public List<int> UnarchiveLabelCalls { get; } = new();

    public Func<string, Task<Result<Project>>>? OnCreateProject { get; set; }
    public Func<int, Task<Result>>? OnArchiveProject { get; set; }
    public Func<int, Task<Result>>? OnUnarchiveProject { get; set; }
    public Func<SetProjectWorkingDirectoryCall, Task<Result>>? OnSetProjectWorkingDirectory { get; set; }
    public Func<CreateLabelCall, Task<Result<Label>>>? OnCreateLabel { get; set; }
    public Func<int, Task<Result>>? OnArchiveLabel { get; set; }
    public Func<int, Task<Result>>? OnUnarchiveLabel { get; set; }

    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default)
    {
        CreateProjectCalls.Add(name);
        return OnCreateProject?.Invoke(name) ?? Task.FromResult(Result.Ok(new Project { Name = name }));
    }

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default)
    {
        ArchiveProjectCalls.Add(projectId);
        return OnArchiveProject?.Invoke(projectId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> UnarchiveProjectAsync(int projectId, CancellationToken ct = default)
    {
        UnarchiveProjectCalls.Add(projectId);
        return OnUnarchiveProject?.Invoke(projectId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> SetProjectWorkingDirectoryAsync(int projectId, string? path, CancellationToken ct = default)
    {
        var call = new SetProjectWorkingDirectoryCall(projectId, path);
        SetProjectWorkingDirectoryCalls.Add(call);
        return OnSetProjectWorkingDirectory?.Invoke(call) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default)
    {
        var call = new CreateLabelCall(name, color);
        CreateLabelCalls.Add(call);
        return OnCreateLabel?.Invoke(call) ?? Task.FromResult(Result.Ok(new Label { Name = name }));
    }

    public Task<Result> ArchiveLabelAsync(int labelId, CancellationToken ct = default)
    {
        ArchiveLabelCalls.Add(labelId);
        return OnArchiveLabel?.Invoke(labelId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> UnarchiveLabelAsync(int labelId, CancellationToken ct = default)
    {
        UnarchiveLabelCalls.Add(labelId);
        return OnUnarchiveLabel?.Invoke(labelId) ?? Task.FromResult(Result.Ok());
    }
}
```

使っている初期化子は確認済みである。`Column` は `Name` / `Role`、`TaskItem` は `Title`、`Label` は `Name`、`Project` は `Name`、`Board` は引数なしで作れる。

- [ ] **Step 3: ビルドが通ることを確かめる**

```
dotnet build tests/MoTask.App.Tests
```

Expected: 0 エラー。ここまでは既存テストに影響しない。

- [ ] **Step 4: BoardToolHostReadTests を移す**

`tests/MoTask.App.Tests/BoardToolHostReadTests.cs` に次の変更を加える。

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

15 行目、

```csharp
    private readonly IBoardService _service = Substitute.For<IBoardService>();
```

を

```csharp
    private readonly FakeBoardService _service = new();
```

に。

21〜25 行目、

```csharp
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
```

を

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { TestBoards.Urgent() };
```

に。`_board` はテストが途中で差し替えるフィールドなので、`Board` プロパティではなくデリゲートで包んで今の書き方を保つ。

68 行目、

```csharp
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail<Board>("ボードがありません")));
```

を

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("ボードがありません"));
```

に。

- [ ] **Step 5: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~BoardToolHostReadTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 6: コミットする**

```bash
git add tests/MoTask.App.Tests/Fakes tests/MoTask.App.Tests/BoardToolHostReadTests.cs
git commit
```

件名: `test(app): FakeBoardService を足して読み取り系のテストを移す`

---

### Task 3: IBoardService だけを使う残り 2 ファイルを移す

**Files:**
- Test: `tests/MoTask.App.Tests/MoTaskMcpServerTests.cs`
- Test: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`

**Interfaces:**
- Consumes: `FakeBoardService`（Task 2）
- Produces: なし

- [ ] **Step 1: MoTaskMcpServerTests を移す**

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

23 行目を `private readonly FakeBoardService _board = new();` に。

29〜33 行目、

```csharp
        _board.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok(TestBoards.Sample())));
        _board.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(Array.Empty<Project>()));
        _board.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(Array.Empty<Label>()));
```

を

```csharp
        _board.Board = TestBoards.Sample();
```

に。`Projects` と `Labels` の既定値が空なので、その 2 つの設定は要らなくなる。

113 / 122 行目、

```csharp
        await _board.DidNotReceive().GetBoardAsync(Arg.Any<CancellationToken>());
```

を

```csharp
        _board.GetBoardCalls.Should().Be(0);
```

に。

175 行目、

```csharp
        _board.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
```

を

```csharp
        _board.OnGetBoard = () => gate.Task;
```

に。

192 行目の

```csharp
        _board.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
```

で始まる差し替えは、`_board.OnGetBoard = async () =>` に置き換える。ラムダの中身はそのまま。引数 `_` を取らなくなる点だけ直す。

- [ ] **Step 2: MoTaskMcpServerTests が通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~MoTaskMcpServerTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 3: MorningPlanViewModelTests を移す**

このファイルは `IMorningService` を既に手書きの `FakeMorningService`（ネストされたクラス）で差し替えている。NSubstitute を使っているのは `IBoardService` の 4 箇所だけ。

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

155 行目を `private readonly FakeBoardService _boards = new();` に。

162〜163 行目、

```csharp
        _boards.GetBoardAsync().Returns(Task.FromResult(Result.Ok(TestBoards.Sample())));
        _boards.GetProjectsAsync().Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
```

を

```csharp
        _boards.Board = TestBoards.Sample();
        _boards.Projects = new[] { TestBoards.ProjectA() };
```

に。

680 行目、

```csharp
        _boards.GetBoardAsync().Returns(Task.FromResult(Result.Fail<Board>("接続できません")));
```

を

```csharp
        _boards.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("接続できません"));
```

に。

- [ ] **Step 4: MorningPlanViewModelTests が通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~MorningPlanViewModelTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 5: コミットする**

```bash
git add tests/MoTask.App.Tests/MoTaskMcpServerTests.cs tests/MoTask.App.Tests/MorningPlanViewModelTests.cs
git commit
```

件名: `test(app): MCP と朝プランのテストを FakeBoardService に移す`

---

### Task 4: FakeAiJobService と FakeBoardChangeSource を足し、BoardViewModel 系 3 ファイルを移す

`BoardViewModel` のコンストラクタは `IBoardService` / `IClock` / `IAiJobService` / `IBoardChangeSource` を取る。後ろ 2 つは多くのテストで「ただ居るだけ」の置物だが、`BoardViewModelTests` はイベントを発火させる。

**Files:**
- Create: `tests/MoTask.App.Tests/Fakes/FakeBoardChangeSource.cs`
- Create: `tests/MoTask.App.Tests/Fakes/FakeAiJobService.cs`
- Test: `tests/MoTask.App.Tests/BoardViewModelTests.cs`
- Test: `tests/MoTask.App.Tests/ManageClassificationsViewModelTests.cs`
- Test: `tests/MoTask.App.Tests/TaskDetailViewModelTests.cs`

**Interfaces:**
- Consumes: `FakeBoardService`（Task 2）
- Produces:
  - `MoTask.App.Tests.Fakes.FakeBoardChangeSource` — `void RaiseBoardChanged()`
  - `MoTask.App.Tests.Fakes.FakeAiJobService` — `List<AiJob> Jobs { get; }` / `List<AiJobEvent> Events { get; }` / `List<string> Artifacts { get; }` / `Dictionary<int, int> TurnCounts { get; }` / `Func<StartJobCall, Task<Result<AiJob>>>? OnStartJob` / `Func<GetEventsCall, Task<IReadOnlyList<AiJobEvent>>>? OnGetEvents` / `Func<int, Task<IReadOnlyList<string>>>? OnGetArtifacts` / `List<StartJobCall> StartJobCalls` / `List<GetEventsCall> GetEventsCalls` / `List<int> GetArtifactsCalls` / `List<int> CompleteJobCalls` / `List<int> StopTrackingCalls` / `List<int> ReopenTerminalCalls` / `void RaiseJobChanged(AiJobSnapshot job, AiJobEvent? newEvent = null, string? warning = null)`
  - 呼び出し記録の `record`: `StartJobCall(int TaskId, AiJobKind Kind, string Instruction)` / `GetEventsCall(int JobId, int Lines)`

- [ ] **Step 1: FakeBoardChangeSource を書く**

`tests/MoTask.App.Tests/Fakes/FakeBoardChangeSource.cs` を新規作成する。

```csharp
using MoTask.App.Ai;

namespace MoTask.App.Tests.Fakes;

/// <summary>MCP からの書き換え通知。テストが好きなときに上げる。</summary>
public sealed class FakeBoardChangeSource : IBoardChangeSource
{
    public event EventHandler? BoardChanged;

    public void RaiseBoardChanged() => BoardChanged?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 2: FakeAiJobService を書く**

`tests/MoTask.App.Tests/Fakes/FakeAiJobService.cs` を新規作成する。

```csharp
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

public sealed record StartJobCall(int TaskId, AiJobKind Kind, string Instruction);
public sealed record GetEventsCall(int JobId, int Lines);

/// <summary>
/// 端末は開かない。ジョブとイベントは 1 つのリストに持ち、照会は実サービスと同じ規則で絞る。
/// 既定では成功を返し、テストが変えたい所だけ On～ を挿す。
/// </summary>
public sealed class FakeAiJobService : IAiJobService
{
    public event EventHandler<AiJobChangedEventArgs>? JobChanged;

    /// <summary>テストが直接積むジョブ。GetJobsForTaskAsync と GetUnfinishedJobsAsync はここから絞る。</summary>
    public List<AiJob> Jobs { get; } = new();

    /// <summary>テストが直接積むイベント。GetEventsAsync はここから絞る。</summary>
    public List<AiJobEvent> Events { get; } = new();

    /// <summary>GetArtifactsAsync が返すもの。</summary>
    public List<string> Artifacts { get; } = new();

    /// <summary>TurnCountOf が返す値。無ければ 0。</summary>
    public Dictionary<int, int> TurnCounts { get; } = new();

    public List<StartJobCall> StartJobCalls { get; } = new();
    public List<GetEventsCall> GetEventsCalls { get; } = new();
    public List<int> GetArtifactsCalls { get; } = new();
    public List<int> CompleteJobCalls { get; } = new();
    public List<int> StopTrackingCalls { get; } = new();
    public List<int> ReopenTerminalCalls { get; } = new();
    public int RecoverOnStartupCalls { get; private set; }

    public Func<StartJobCall, Task<Result<AiJob>>>? OnStartJob { get; set; }
    public Func<GetEventsCall, Task<IReadOnlyList<AiJobEvent>>>? OnGetEvents { get; set; }
    public Func<int, Task<IReadOnlyList<string>>>? OnGetArtifacts { get; set; }
    public Func<int, Task<Result>>? OnCompleteJob { get; set; }
    public Func<int, Task<Result>>? OnStopTracking { get; set; }
    public Func<int, Task<Result>>? OnReopenTerminal { get; set; }

    /// <summary>
    /// 実サービスと同じく、上げるのは AiJob そのものではなく AiJobSnapshot。
    /// TaskAiPanelViewModelTests の Snapshot(...) ヘルパーがこの型を作る。
    /// </summary>
    public void RaiseJobChanged(AiJobSnapshot job, AiJobEvent? newEvent = null, string? warning = null)
        => JobChanged?.Invoke(this, new AiJobChangedEventArgs(job, newEvent, warning));

    public Task<Result<AiJob>> StartJobAsync(int taskId, AiJobKind kind, string instruction, CancellationToken ct = default)
    {
        var call = new StartJobCall(taskId, kind, instruction);
        StartJobCalls.Add(call);
        return OnStartJob?.Invoke(call) ?? Task.FromResult(Result.Ok(new AiJob { TaskId = taskId, Kind = kind }));
    }

    public Task<IReadOnlyList<AiJob>> GetJobsForTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiJob>>(
            Jobs.Where(j => j.TaskId == taskId).OrderByDescending(j => j.Id).ToList());

    public Task<IReadOnlyList<AiJobEvent>> GetEventsAsync(int jobId, int lines, CancellationToken ct = default)
    {
        var call = new GetEventsCall(jobId, lines);
        GetEventsCalls.Add(call);
        return OnGetEvents?.Invoke(call)
               ?? Task.FromResult<IReadOnlyList<AiJobEvent>>(
                   Events.Where(e => e.JobId == jobId).OrderBy(e => e.Seq).ToList());
    }

    public Task<IReadOnlyList<AiJob>> GetUnfinishedJobsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiJob>>(Jobs.Where(j => !j.Status.IsTerminal()).ToList());

    public Task<IReadOnlyList<string>> GetArtifactsAsync(int jobId, CancellationToken ct = default)
    {
        GetArtifactsCalls.Add(jobId);
        return OnGetArtifacts?.Invoke(jobId) ?? Task.FromResult<IReadOnlyList<string>>(Artifacts.ToList());
    }

    public int TurnCountOf(int jobId) => TurnCounts.TryGetValue(jobId, out var count) ? count : 0;

    public Task<Result> ReopenTerminalAsync(int jobId, CancellationToken ct = default)
    {
        ReopenTerminalCalls.Add(jobId);
        return OnReopenTerminal?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> CompleteJobAsync(int jobId, CancellationToken ct = default)
    {
        CompleteJobCalls.Add(jobId);
        return OnCompleteJob?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task<Result> StopTrackingAsync(int jobId, CancellationToken ct = default)
    {
        StopTrackingCalls.Add(jobId);
        return OnStopTracking?.Invoke(jobId) ?? Task.FromResult(Result.Ok());
    }

    public Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        RecoverOnStartupCalls++;
        return Task.CompletedTask;
    }
}
```

使っている型は確認済みである。`AiJob` は `Id` / `TaskId` / `Kind` / `Status` を持つ可変クラス、`AiJobEvent` は `JobId` / `Seq` を持つ可変クラス、`IsTerminal()` は `MoTask.Core.Model.AiJobStatus` の拡張メソッド、`AiJobChangedEventArgs` の第 1 引数は `AiJobSnapshot`（`AiJob` ではない）。

- [ ] **Step 3: ビルドが通ることを確かめる**

```
dotnet build tests/MoTask.App.Tests
```

Expected: 0 エラー

- [ ] **Step 4: BoardViewModelTests を移す**

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

17〜18 行目を

```csharp
    private readonly FakeBoardService _service = new();
    private readonly FakeBoardChangeSource _externalChanges = new();
```

に。

26〜33 行目の照会の差し替えを

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { _urgent };
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), _externalChanges);
```

に（`GetHistoryAsync` の既定は空なので設定が要らなくなる）。

47 / 77 / 79 行目、

```csharp
        _externalChanges.BoardChanged += Raise.Event<EventHandler>(this, EventArgs.Empty);
```

を

```csharp
        _externalChanges.RaiseBoardChanged();
```

に。

45 / 61 / 199 / 279 / 417 / 510 行目、

```csharp
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());
```

を

```csharp
        _service.GetBoardCalls.Should().Be(1);
```

に。49 / 249 行目の `Received(2)` は `.Should().Be(2)` に。

374 行目、

```csharp
        await _service.DidNotReceive().CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
```

を

```csharp
        _service.CreateTaskCalls.Should().BeEmpty();
```

に。682 行目の `DidNotReceive().SetWipLimitAsync(...)` は `_service.SetWipLimitCalls.Should().BeEmpty();` に。

458〜459 行目、

```csharp
        await _service.Received(1).ReorderColumnsAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 2, 1, 3 })), Arg.Any<CancellationToken>());
```

を

```csharp
        _service.ReorderColumnsCalls.Should().ContainSingle()
            .Which.OrderedColumnIds.Should().Equal(2, 1, 3);
```

に。

残りの `.Returns(...)` はすべて「移行の共通規則」の表に従って `On～` へ写す。引数で振り分けているものは記録型のプロパティで判定する。たとえば 188 行目の

```csharp
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail("だめ")));
```

は

```csharp
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromResult(Result.Fail("だめ"))
            : Task.FromResult(Result.Ok());
```

に。596 行目の

```csharp
        _service.AddColumnAsync("確認待ち", ColumnRole.Review, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<Column>>(new InvalidOperationException("disk I/O error")));
```

は

```csharp
        _service.OnAddColumn = call => call is { Name: "確認待ち", Role: ColumnRole.Review }
            ? Task.FromException<Result<Column>>(new InvalidOperationException("disk I/O error"))
            : Task.FromResult(Result.Ok(new Column { Name = call.Name, Role = call.Role }));
```

に。同じ形を 216 / 217 / 238 / 257 / 291 / 313 / 382 / 404 / 423 / 436 / 450 / 489 / 503 / 517 / 531 / 545 / 548 / 565 / 579 / 637 / 655 / 694 / 715 / 733 / 754 / 786 行目にも適用する。

- [ ] **Step 5: BoardViewModelTests が通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~BoardViewModelTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 6: ManageClassificationsViewModelTests を移す**

using を入れ替え、14 行目を `private readonly FakeBoardService _service = new();` に。

25〜31 行目の照会の差し替えを

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.OnGetProjects = () => Task.FromResult<IReadOnlyList<Project>>(new[] { _projectA, _unused });
        _service.OnGetLabels = () => Task.FromResult<IReadOnlyList<Label>>(new[] { _urgent, _spare });
```

に（`_projectA` などをテストが書き換えるので、プロパティではなくデリゲートで包む）。

32〜51 行目の 4 つの `.Returns(_ => {...})` を

```csharp
        _service.OnArchiveProject = _ => { 元のラムダの中身 };
        _service.OnUnarchiveProject = _ => { 元のラムダの中身 };
        _service.OnArchiveLabel = _ => { 元のラムダの中身 };
        _service.OnUnarchiveLabel = _ => { 元のラムダの中身 };
```

に。

52 行目、

```csharp
        _vm = new BoardViewModel(_service, new TestClock(), Substitute.For<IAiJobService>(), Substitute.For<IBoardChangeSource>());
```

を

```csharp
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), new FakeBoardChangeSource());
```

に。

95 / 110 / 122 / 136 行目、

```csharp
        await _service.Received(1).ArchiveProjectAsync(100, Arg.Any<CancellationToken>());
```

の形を

```csharp
        _service.ArchiveProjectCalls.Should().ContainSingle().Which.Should().Be(100);
```

の形に（メソッド名に応じて `UnarchiveProjectCalls` / `ArchiveLabelCalls` / `UnarchiveLabelCalls`、値は 100 / 100 / 200 / 200）。

- [ ] **Step 7: TaskDetailViewModelTests を移す**

using を入れ替え、15 行目を `private readonly FakeBoardService _service = new();` に。

22〜35 行目の照会と書き込みの差し替えを、共通規則に従って `On～` とプロパティに写す。`GetHistoryAsync` は 70 行目と 144 行目でテストが差し替えるので、`_service.History = ...` ではなく `_service.OnGetHistory = _ => ...` を使う。144 行目の `GetHistoryAsync(10, ...)` は `call.TaskId == 10` で振り分ける。

35 行目の `Substitute.For<IAiJobService>(), Substitute.For<IBoardChangeSource>()` を `new FakeAiJobService(), new FakeBoardChangeSource()` に。

59 / 104 / 116 / 242 行目の `DidNotReceive().UpdateTaskAsync(...)` を `_service.UpdateTaskCalls.Should().BeEmpty();` に。

90〜93 行目、

```csharp
        await _service.Received(1).UpdateTaskAsync(
            Arg.Is<TaskUpdate>(u => u.TaskId == 10 && u.Title == "新しい題" && u.ProjectId == 100
                && ...),
            Arg.Any<CancellationToken>());
```

を

```csharp
        var update = _service.UpdateTaskCalls.Should().ContainSingle().Which.Update;
        update.TaskId.Should().Be(10);
        update.Title.Should().Be("新しい題");
        update.ProjectId.Should().Be(100);
        // 元の Arg.Is の条件式に含まれる残りの項目も 1 行ずつ確かめる
```

に。元の条件式に入っている項目をすべて 1 行ずつ書き出す。落とさないこと。

128 行目、`await _service.Received(1).MoveTaskAsync(10, 2, 1, ...)` を

```csharp
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 2, 1));
```

に。

199〜200 行目、

```csharp
        await _service.Received(1).SetTaskLabelsAsync(10, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0),
            Arg.Any<CancellationToken>());
```

を

```csharp
        var labels = _service.SetTaskLabelsCalls.Should().ContainSingle().Which;
        labels.TaskId.Should().Be(10);
        labels.LabelIds.Should().BeEmpty();
```

に。

241 行目、`await _service.Received(1).SetProjectWorkingDirectoryAsync(100, @"C:\work\a", ...)` を

```csharp
        _service.SetProjectWorkingDirectoryCalls.Should().ContainSingle()
            .Which.Should().Be(new SetProjectWorkingDirectoryCall(100, @"C:\work\a"));
```

に。

- [ ] **Step 8: 3 ファイルとも通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~BoardViewModelTests|FullyQualifiedName~ManageClassificationsViewModelTests|FullyQualifiedName~TaskDetailViewModelTests"
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 9: コミットする**

```bash
git add -A
git commit
```

件名: `test(app): BoardViewModel 系のテストを手書きの偽実装に移す`

---

### Task 5: TaskAiPanelViewModelTests を移す

`IAiJobService` を本格的に使う唯一のファイル。イベントの発火と引数依存の応答が両方ある。

**Files:**
- Test: `tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs`

**Interfaces:**
- Consumes: `FakeBoardService` / `FakeAiJobService` / `FakeBoardChangeSource`（Task 2、Task 4）
- Produces: なし

- [ ] **Step 1: 差し替えを移す**

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

17〜18 行目を

```csharp
    private readonly FakeBoardService _service = new();
    private readonly FakeAiJobService _ai = new();
```

に。

28〜31 行目の照会を、`_service.OnGetBoard = () => Task.FromResult(Result.Ok(board));` と `_service.Projects` / `_service.Labels` に写す。`GetHistoryAsync` の既定は空なので設定は不要。

33〜39 行目は、`FakeAiJobService` の既定の振る舞いがまさにこの絞り込みなので、**すべて削除する**。テストが `_jobs` / `_events` / `_artifacts` に積んでいたものは、代わりに `_ai.Jobs` / `_ai.Events` / `_ai.Artifacts` に積むよう、それらのフィールドの使用箇所をすべて置き換える。

40〜48 行目、

```csharp
        _ai.StartJobAsync(...).Returns(ci => { ... });
        _ai.CompleteJobAsync(...).Returns(Task.FromResult(Result.Ok()));
        _ai.StopTrackingAsync(...).Returns(Task.FromResult(Result.Ok()));
        _ai.ReopenTerminalAsync(...).Returns(Task.FromResult(Result.Ok()));
```

のうち、後ろ 3 つは既定が `Result.Ok()` なので削除する。`StartJobAsync` は

```csharp
        _ai.OnStartJob = call => { 元のラムダの中身。ci.Arg<int>() などは call.TaskId / call.Kind / call.Instruction に直す };
```

に。

50 行目の `Substitute.For<IBoardChangeSource>()` を `new FakeBoardChangeSource()` に。

64 行目、

```csharp
        => _ai.JobChanged += Raise.EventWith(_ai, new AiJobChangedEventArgs(job, newEvent, warning));
```

を

```csharp
        => _ai.RaiseJobChanged(job, newEvent, warning);
```

に。

125〜126 行目を

```csharp
        _ai.OnStartJob = _ => Task.FromResult(Result.Fail<AiJob>(Messages.ClaudeNotFound));
```

に。

287 行目、

```csharp
        _ai.GetEventsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci => Gated(ci.ArgAt<int>(0)));
```

を

```csharp
        _ai.OnGetEvents = call => Gated(call.JobId);
```

に。

386 行目、`_ai.TurnCountOf(2).Returns(6);` を `_ai.TurnCounts[2] = 6;` に。

439〜440 行目を

```csharp
        _ai.OnGetArtifacts = _ => Task.FromResult<IReadOnlyList<string>>(new[] { @"C:\w\jobs\0001-a\artifacts\report.md" });
```

に。

458〜459 行目の `GetEventsAsync(...).Returns(new AiJobEvent[] {...})` は、`_ai.OnGetEvents = _ => Task.FromResult<IReadOnlyList<AiJobEvent>>(new AiJobEvent[] { ... });` に。

243 / 400 行目の `_service.GetBoardAsync(...).Returns(_ => Task.FromResult(Result.Ok(board)))` は `_service.OnGetBoard = () => Task.FromResult(Result.Ok(board));` に。

- [ ] **Step 2: 検証を移す**

111 行目、

```csharp
        await _ai.Received(1).StartJobAsync(10, AiJobKind.Execute, "請求先を最新にしてください", Arg.Any<CancellationToken>());
```

を

```csharp
        _ai.StartJobCalls.Should().ContainSingle()
            .Which.Should().Be(new StartJobCall(10, AiJobKind.Execute, "請求先を最新にしてください"));
```

に。

330 / 341 / 352 行目、

```csharp
        await _ai.Received(1).CompleteJobAsync(5, Arg.Any<CancellationToken>());
```

の形を

```csharp
        _ai.CompleteJobCalls.Should().ContainSingle().Which.Should().Be(5);
```

の形に（`StopTrackingCalls` / `ReopenTerminalCalls` も同様、値はいずれも 5）。

362 行目、

```csharp
        await _ai.Received(1).GetArtifactsAsync(1, Arg.Any<CancellationToken>());
```

を

```csharp
        _ai.GetArtifactsCalls.Should().ContainSingle().Which.Should().Be(1);
```

に。

- [ ] **Step 3: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~TaskAiPanelViewModelTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 4: コミットする**

```bash
git add tests/MoTask.App.Tests/TaskAiPanelViewModelTests.cs
git commit
```

件名: `test(app): AI パネルのテストを FakeAiJobService に移す`

---

### Task 6: FakeMorningService を切り出し、TriagePanelViewModelTests を移す

`MorningPlanViewModelTests` には既に手書きの `FakeMorningService` がネストされたクラスとして入っている。同じ interface の偽実装を 2 つ持たないよう、これを `Fakes/` へ出して両方から使う。

**Files:**
- Create: `tests/MoTask.App.Tests/Fakes/FakeMorningService.cs`
- Modify: `tests/MoTask.App.Tests/MorningPlanViewModelTests.cs`
- Test: `tests/MoTask.App.Tests/TriagePanelViewModelTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `MoTask.App.Tests.Fakes.FakeMorningService` — 既存のネストされたクラスの公開面（`Current` / `Candidates` / `Calls` / `StartResult` / `RegisterResult` / `LastDecision` / `FailNextGetCurrentRun` / `BulkResult` / `Raise(...)`）に加えて `List<MergeCall> MergeCalls` / `List<CandidateDecision> RegisterCalls` / `List<int> RejectCalls` / `List<int> PostponeCalls` / `Func<int, Task<Result>>? OnReject`
- 追加する `record`: `MergeCall(int CandidateId, int TargetTaskId)`

- [ ] **Step 1: ネストされたクラスを Fakes へ移す**

`tests/MoTask.App.Tests/MorningPlanViewModelTests.cs` の中にある `private sealed class FakeMorningService : IMorningService { ... }` の全体を切り取り、`tests/MoTask.App.Tests/Fakes/FakeMorningService.cs` に次の形で貼り付ける。

```csharp
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

public sealed record MergeCall(int CandidateId, int TargetTaskId);

/// <summary>
/// 呼ばれた操作を記録する偽サービス。候補は全状態を 1 つのリストに持ち、キューは実リポジトリと
/// 同じ規則（この実行の Pending ＋ 他の実行の Later）で計算する。4 アクションは実サービスと同じく
/// Status / ResultTaskId を書き換える（そうしないと「登録した行が実タスクに変わる」を試せない）。
/// </summary>
public sealed class FakeMorningService : IMorningService
{
    // ここに元のネストされたクラスの中身をそのまま貼る。
    // 変更点は 3 つだけ:
    //   1. class の宣言を private sealed から public sealed に
    //   2. 呼び出し記録を足す（Step 2）
    //   3. RejectAsync に差し替えの口を足す（Step 2）
}
```

`MorningPlanViewModelTests.cs` の using に `using MoTask.App.Tests.Fakes;` を足す。ネストされたクラスを消した以外は触らない。`_service.Calls` / `_service.Candidates` などの使い方はそのまま動く。

- [ ] **Step 2: TriagePanelViewModelTests が要る記録と口を足す**

`FakeMorningService` に次を足す。既存のメンバーは消さない。

```csharp
    public List<MergeCall> MergeCalls { get; } = new();
    public List<CandidateDecision> RegisterCalls { get; } = new();
    public List<int> PostponeCalls { get; } = new();
    public List<int> RejectCalls { get; } = new();

    /// <summary>Reject の応答を差し替える口。TriagePanelViewModelTests が待たせるのに使う。</summary>
    public Func<int, Task<Result>>? OnReject { get; set; }
```

`RegisterAsync` の先頭に `RegisterCalls.Add(decision);` を足す。
`MergeAsync` の先頭に `MergeCalls.Add(new MergeCall(candidateId, targetTaskId));` を足す。
`PostponeAsync` の先頭に `PostponeCalls.Add(candidateId);` を足す。
`RejectAsync` を次に書き換える。

```csharp
    public Task<Result> RejectAsync(int candidateId, CancellationToken ct = default)
    {
        Calls.Add("Reject");
        RejectCalls.Add(candidateId);
        if (OnReject is { } on) return on(candidateId);
        Decide(candidateId, TriageStatus.Rejected);
        return Task.FromResult(Result.Ok());
    }
```

- [ ] **Step 3: MorningPlanViewModelTests が変わらず通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~MorningPlanViewModelTests
```

Expected: 移行前と同じ本数がすべて緑。切り出しで壊れていないことをここで押さえる。

- [ ] **Step 4: TriagePanelViewModelTests を移す**

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

15 行目を `private readonly FakeMorningService _service = new();` に。

22〜25 行目、

```csharp
        _service.MergeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _service.RegisterAsync(Arg.Any<CandidateDecision>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok(new TaskItem { Id = 99 })));
```

を

```csharp
        _service.RegisterResult = Result.Ok(new TaskItem { Id = 99 });
```

に。`MergeAsync` の既定は `Result.Ok()` なので設定が要らなくなる。

85 行目、

```csharp
        await _service.Received(1).MergeAsync(1, 10, Arg.Any<CancellationToken>());
```

を

```csharp
        _service.MergeCalls.Should().ContainSingle().Which.Should().Be(new MergeCall(1, 10));
```

に。

96 / 123 行目、

```csharp
        await _service.DidNotReceive().MergeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
```

を

```csharp
        _service.MergeCalls.Should().BeEmpty();
```

に。

111〜113 行目、

```csharp
        await _service.Received(1).RegisterAsync(
            new CandidateDecision(1, "書き換えた題名", new DateOnly(2026, 9, 10), "別プロジェクト", 2),
            Arg.Any<CancellationToken>());
```

を

```csharp
        _service.RegisterCalls.Should().ContainSingle().Which.Should()
            .Be(new CandidateDecision(1, "書き換えた題名", new DateOnly(2026, 9, 10), "別プロジェクト", 2));
```

に。`CandidateDecision` は `record` なので値で比べられる。

129 / 142 行目、

```csharp
        _service.RejectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _service.RejectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(tcs.Task);
```

を、前者は既定なので削除、後者は

```csharp
        _service.OnReject = _ => tcs.Task;
```

に。

134 / 150 行目、

```csharp
        await _service.Received(1).RejectAsync(1, Arg.Any<CancellationToken>());
```

を

```csharp
        _service.RejectCalls.Should().ContainSingle().Which.Should().Be(1);
```

に。

- [ ] **Step 5: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~TriagePanelViewModelTests|FullyQualifiedName~MorningPlanViewModelTests"
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 6: コミットする**

```bash
git add -A
git commit
```

件名: `test(app): FakeMorningService を共有化して仕分けのテストを移す`

---

### Task 7: BoardToolHostWriteTests を移す

`IBoardService` の書き込み系を一番使うファイル。45 箇所ある。

**Files:**
- Test: `tests/MoTask.App.Tests/BoardToolHostWriteTests.cs`

**Interfaces:**
- Consumes: `FakeBoardService`（Task 2）
- Produces: なし

- [ ] **Step 1: 差し替えを移す**

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

16 行目を `private readonly FakeBoardService _service = new();` に。

23〜32 行目、

```csharp
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
        _service.UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _service.SetTaskLabelsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
```

を

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { TestBoards.Urgent() };
```

に。`UpdateTaskAsync` / `SetTaskLabelsAsync` / `MoveTaskAsync` の既定はいずれも `Result.Ok()` なので設定が要らなくなる。

39〜40 行目、

```csharp
        => _service.CreateTaskAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
```

を

```csharp
        => _service.OnCreateTask = call =>
```

に。ラムダの中で `call.Arg<int>()` / `call.ArgAt<string>(1)` のように引数を取っていた箇所は、`call.ColumnId` / `call.Title` に直す。

134〜135 / 148〜149 / 163〜164 行目の失敗の差し替えを

```csharp
        _service.OnCreateTask = _ => Task.FromResult(Result.Fail<TaskItem>("保存に失敗しました: disk full"));
        _service.OnUpdateTask = _ => Task.FromResult(Result.Fail("保存に失敗しました: disk full"));
        _service.OnSetTaskLabels = _ => Task.FromResult(Result.Fail("ラベルの保存に失敗しました"));
```

に。

247〜248 行目の

```csharp
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
```

を `_service.OnMoveTask = call =>` に。ラムダ内の引数取り出しを `call.TaskId` / `call.ToColumnId` / `call.Position` に直す。

- [ ] **Step 2: 検証を移す**

69 / 82 行目、

```csharp
        await _service.Received(1).CreateTaskAsync(2, "MCP I/F を作る", Arg.Any<CancellationToken>());
```

を

```csharp
        _service.CreateTaskCalls.Should().ContainSingle().Which.Should().Be(new CreateTaskCall(2, "MCP I/F を作る"));
```

に（82 行目は `new CreateTaskCall(1, "あとで")`）。

93〜94 / 190〜191 / 200〜201 / 209〜210 行目、

```csharp
        await _service.Received(1).UpdateTaskAsync(
            new TaskUpdate(52, "見積", "本文", 100, new DateOnly(2026, 9, 10)), Arg.Any<CancellationToken>());
```

を

```csharp
        _service.UpdateTaskCalls.Should().ContainSingle()
            .Which.Update.Should().Be(new TaskUpdate(52, "見積", "本文", 100, new DateOnly(2026, 9, 10)));
```

に（`TaskUpdate` の式はそれぞれの行のものをそのまま使う）。

95〜96 行目、

```csharp
        await _service.Received(1).SetTaskLabelsAsync(52, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 200),
            Arg.Any<CancellationToken>());
```

を

```csharp
        var labels = _service.SetTaskLabelsCalls.Should().ContainSingle().Which;
        labels.TaskId.Should().Be(52);
        labels.LabelIds.Should().ContainSingle().Which.Should().Be(200);
```

に。

220 行目、

```csharp
        await _service.Received(1).SetTaskLabelsAsync(11, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
```

を

```csharp
        var cleared = _service.SetTaskLabelsCalls.Should().ContainSingle().Which;
        cleared.TaskId.Should().Be(11);
        cleared.LabelIds.Should().BeEmpty();
```

に。

258 / 268 行目、

```csharp
        await _service.Received(1).MoveTaskAsync(10, 3, int.MaxValue, Arg.Any<CancellationToken>());
```

を

```csharp
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 3, int.MaxValue));
```

に（268 行目は `new MoveTaskCall(10, 2, 0)`）。

106 / 107 / 127 / 217 / 230 / 241 / 290 行目の `DidNotReceive()` は、それぞれ `UpdateTaskCalls` / `SetTaskLabelsCalls` / `CreateTaskCalls` / `MoveTaskCalls` を `.Should().BeEmpty()` に。

- [ ] **Step 3: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~BoardToolHostWriteTests
```

Expected: 移行前と同じ本数がすべて緑

- [ ] **Step 4: コミットする**

```bash
git add tests/MoTask.App.Tests/BoardToolHostWriteTests.cs
git commit
```

件名: `test(app): MCP 書き込み系のテストを FakeBoardService に移す`

---

### Task 8: DropHandlerTests を移し、NSubstitute を削除する

最後の 1 ファイルを移し、パッケージ参照を消す。

**Files:**
- Test: `tests/MoTask.App.Tests/DropHandlerTests.cs`
- Modify: `tests/MoTask.App.Tests/MoTask.App.Tests.csproj`
- Modify: `Directory.Packages.props`

**Interfaces:**
- Consumes: `FakeBoardService` / `FakeAiJobService` / `FakeBoardChangeSource`（Task 2、Task 4）
- Produces: なし

- [ ] **Step 1: DropHandlerTests を移す**

1 本目の計画で gong への依存は既に消えているので、残るのは NSubstitute だけ。

using から `using NSubstitute;` を削除し、`using MoTask.App.Tests.Fakes;` を足す。

`private readonly IBoardService _service = Substitute.For<IBoardService>();` を

```csharp
    private readonly FakeBoardService _service = new();
```

に。

コンストラクタの照会と書き込みの差し替えを

```csharp
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), new FakeBoardChangeSource());
```

に。`GetProjectsAsync` / `GetLabelsAsync` の既定は空、`MoveTaskAsync` / `ReorderColumnsAsync` の既定は `Result.Ok()` なので、それらの設定は要らなくなる。

`await _service.Received(1).MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>());` の形を

```csharp
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 2, 0));
```

の形に（引数はそれぞれの行のものを使う。`(10, 1, 1)` と `(14, 1, 2)` の箇所がある）。

`await _service.DidNotReceive().MoveTaskAsync(...)` を `_service.MoveTaskCalls.Should().BeEmpty();` に。
`await _service.DidNotReceive().ReorderColumnsAsync(...)` を `_service.ReorderColumnsCalls.Should().BeEmpty();` に。

```csharp
        await _service.Received(1).ReorderColumnsAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 2, 1, 3 })), Arg.Any<CancellationToken>());
```

を

```csharp
        _service.ReorderColumnsCalls.Should().ContainSingle()
            .Which.OrderedColumnIds.Should().Equal(2, 1, 3);
```

に。

例外を返す 2 箇所、

```csharp
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("database is locked")));
        _service.ReorderColumnsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("disk I/O error")));
```

を

```csharp
        _service.OnMoveTask = call => call is { TaskId: 10, ToColumnId: 2, Position: 0 }
            ? Task.FromException<Result>(new InvalidOperationException("database is locked"))
            : Task.FromResult(Result.Ok());
        _service.OnReorderColumns = _ => Task.FromException<Result>(new InvalidOperationException("disk I/O error"));
```

に。

- [ ] **Step 2: パッケージ参照を削除する**

`Directory.Packages.props` から次の 1 行を削除する。

```xml
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
```

`tests/MoTask.App.Tests/MoTask.App.Tests.csproj` から次の 1 行を削除する。

```xml
    <PackageReference Include="NSubstitute" />
```

- [ ] **Step 3: ビルドと全テストを通す**

MoTask.exe が起動していないことを確かめてから実行する。

```
dotnet build
```

Expected: 0 警告 0 エラー

```
dotnet test tests/MoTask.Core.Tests
dotnet test tests/MoTask.Data.Tests
dotnet test tests/MoTask.App.Tests
dotnet test tests/MoTask.Mcp.Tests
```

Expected: 全緑。本数が着手前を下回らない

- [ ] **Step 4: NSubstitute が残っていないことを確かめる**

```
grep -rn --include=*.cs --include=*.csproj --include=*.props -i "NSubstitute" src tests Directory.Packages.props | grep -v "/obj/" | grep -v "/bin/"
```

Expected: 何も出ない

あわせて、個人 OSS が両方とも消えたことを確かめる。

```
grep -rn --include=*.cs --include=*.xaml --include=*.csproj --include=*.props -iE "gong|GongSolutions|NSubstitute" src tests Directory.Packages.props | grep -v "/obj/" | grep -v "/bin/"
```

Expected: 何も出ない

- [ ] **Step 5: 仕様書のチェックリストを閉じる**

`docs/superpowers/specs/2026-09-12-drop-third-party-oss-deps-design.md` の §7.3 に、自動テストのみで確認した旨を追記する。§7.1 の 3 項目が満たされたことも記す。

- [ ] **Step 6: コミットする**

```bash
git add -A
git commit
```

件名: `test(app): NSubstitute への依存を外す`

- [ ] **Step 7: master へ統合する**

```bash
git checkout master
git merge --no-ff feature/drop-nsubstitute
```

`git rebase` と `git reset --hard` は使わない。統合後にもう一度ビルドとテストを通す。
