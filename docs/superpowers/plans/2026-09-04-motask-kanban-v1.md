# MoTask v1 個人用カンバン 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF + SQLite の個人用カンバン（タスク CRUD、列管理、D&D、フィルタ、状態変更履歴、Industry テーマ）を、後続機能が UI を触らずに載せられる Core 層とともに作る。

**Architecture:** 3 プロジェクト構成。`MoTask.Core`（ドメイン・ユースケース `BoardService`・`TaskFilter`、外部依存なし）、`MoTask.Data`（EF Core + SQLite、リポジトリ実装、マイグレーション、初期投入）、`MoTask.App`（WPF、CommunityToolkit.Mvvm、GongSolutions D&D、DI ホスト）。ViewModel は `IBoardService` だけを見る。エンティティは追跡された共有オブジェクトで、`BoardService` が変更したあと ViewModel は `Refresh()` で差分を取り込む。保存失敗時は `GetBoard()` で全体を読み直して巻き戻す。

**Tech Stack:** .NET 10 SDK 10.0.400、WPF、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.Hosting 10.0.11、Microsoft.EntityFrameworkCore.Sqlite 10.0.11、gong-wpf-dragdrop 4.0.0、xUnit 2.9.3、FluentAssertions 7.2.2（Apache-2.0 の最終系列。8.x は商用ライセンスのため使わない）、NSubstitute 6.2.0。

**Spec:** `docs/superpowers/specs/2026-09-04-motask-kanban-v1-design.md`

## Global Constraints

- 対象 OS: Windows 11。ランタイム .NET 10（LTS）。`dotnet --version` が `10.0.400` を返すことを確認済み。
- ソリューション構成は仕様 §4 の通り: `src/MoTask.Core`, `src/MoTask.Data`, `src/MoTask.App`, `tests/MoTask.Core.Tests`, `tests/MoTask.Data.Tests`, `tests/MoTask.App.Tests`。依存方向は App → Data → Core、App → Core。Core は他プロジェクトにも NuGet にも依存しない。
- DB の場所: `%LOCALAPPDATA%\MoTask\motask.db`。
- 表示言語は日本語のみ。ユーザーに見える文言は resx に置く（Core: `Resources/Messages.resx`、App: `Resources/Strings.resx`）。
- 履歴（`HistoryEntry`）は追記専用。タスク変更と**同じ `SaveChanges`** で書く。列内の並び替えだけの `MoveTask` は履歴を書かない。
- `Done` 列は必ず1つ。削除不可、Role 変更不可、他の列を `Done` にできない。
- WIP 制限は警告のみ。移動は拒否しない。件数に論理削除済みは含めない。
- 色トークン: `--color-bg` #f2f2f3、`--color-text` #1d1f20、`--color-accent` #5980a6、`--color-divider` = text 16%。字体: 見出し Barlow Condensed（600）、本文 Barlow、日本語は Noto Sans JP → Yu Gothic UI にフォールバック。角は直角、枠線 1px。
- パッケージバージョンは `Directory.Packages.props` で一元管理する（各 csproj の `PackageReference` には `Version` を書かない）。
- **仕様からの命名上の逸脱（意図的）**: エンティティ `Task` は `ImplicitUsings` の `System.Threading.Tasks.Task` と衝突するため、コード上は `TaskItem` と命名する。DB のテーブル名は `Tasks` のまま。
- コミットは各タスクの末尾で行う。コミットメッセージ末尾に `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` を付ける。
- テスト実行コマンドはリポジトリルートで `dotnet test MoTask.sln`。個別は `dotnet test tests/<Project> --filter "FullyQualifiedName~<TestClass>"`。

---

## ファイル構成（全体像）

```
MoTask.sln
Directory.Build.props                 共通ビルド設定（Nullable, ImplicitUsings, CPM 有効化）
Directory.Packages.props              パッケージバージョン一元管理
.config/dotnet-tools.json             dotnet-ef ローカルツール
src/MoTask.Core/
  Model/ColumnRole.cs, HistoryKind.cs, Board.cs, Column.cs, TaskItem.cs, Project.cs, Label.cs, HistoryEntry.cs
  Model/HistoryDetail.cs              Edited の Detail JSON の直列化（FieldChange）
  Result.cs                           Result / Result<T>
  Abstractions/IClock.cs, SystemClock.cs
  Abstractions/IBoardRepository.cs, IHistoryRepository.cs, IUnitOfWork.cs, PersistenceException.cs
  Services/IBoardService.cs, BoardService.cs, TaskUpdate.cs
  Filtering/TaskFilter.cs, DueFilter.cs, DueStatus.cs
  Resources/Messages.resx, Messages.cs  ユーザー向け失敗理由・警告文
src/MoTask.Data/
  MoTaskDbContext.cs, MoTaskDbContextFactory.cs, TaskLabel.cs
  Migrations/                          dotnet ef で生成
  DbPaths.cs, DefaultBoard.cs, DatabaseInitializer.cs, DatabaseRecovery.cs
  Repositories/BoardRepository.cs, HistoryRepository.cs, EfUnitOfWork.cs
  ServiceCollectionExtensions.cs      AddMoTaskData
src/MoTask.App/
  App.xaml(.cs)                        ホスト構築、DB 初期化と復旧、MainWindow 解決
  Themes/Industry.xaml                 トークン（色・間隔・字体）
  Themes/Controls.xaml                 Button/TextBox/ComboBox/ListBox 等の基本スタイル
  Fonts/*.ttf, Fonts/OFL.txt
  Resources/Strings.resx, Strings.cs
  Converters/RampBrushConverter.cs, DueStatusBrushConverter.cs, BoolToVisibilityConverter.cs
  ViewModels/Options.cs, TaskCardViewModel.cs, ColumnViewModel.cs, FilterViewModel.cs, BoardViewModel.cs, TaskDetailViewModel.cs, HistoryFormatter.cs
  DragDrop/DropPositionCalculator.cs, CardDropHandler.cs, ColumnDropHandler.cs
  Views/MainWindow.xaml(.cs), FilterBar.xaml(.cs), BoardView.xaml, ColumnView.xaml(.cs), TaskCardView.xaml, TaskDetailPanel.xaml(.cs)
tests/MoTask.Core.Tests/
  Fakes/InMemoryStore.cs, FakeClock.cs
  ModelTests.cs, BoardServiceTaskTests.cs, BoardServiceMoveTests.cs, BoardServiceUpdateTests.cs,
  BoardServiceDeleteTests.cs, BoardServiceColumnTests.cs, BoardServiceClassificationTests.cs, TaskFilterTests.cs
tests/MoTask.Data.Tests/
  SqliteTestDatabase.cs, MigrationTests.cs, InitializerTests.cs, RepositoryTests.cs, TransactionTests.cs, RecoveryTests.cs
tests/MoTask.App.Tests/
  TestBoards.cs, HistoryFormatterTests.cs, BoardViewModelTests.cs, TaskDetailViewModelTests.cs, DropPositionCalculatorTests.cs
```

---

### Task 1: ソリューションとプロジェクトの骨組み

**Files:**
- Create: `MoTask.sln`, `Directory.Build.props`, `Directory.Packages.props`, `.config/dotnet-tools.json`
- Create: `src/MoTask.Core/MoTask.Core.csproj`, `src/MoTask.Data/MoTask.Data.csproj`, `src/MoTask.App/MoTask.App.csproj`
- Create: `tests/MoTask.Core.Tests/MoTask.Core.Tests.csproj`, `tests/MoTask.Data.Tests/MoTask.Data.Tests.csproj`, `tests/MoTask.App.Tests/MoTask.App.Tests.csproj`
- Create: `tests/MoTask.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: 以降の全タスクが使うプロジェクト参照とパッケージ一覧。

- [ ] **Step 1: プロジェクトを生成する**

リポジトリルート（`d:\source\cs\MoTask`）で実行:

```bash
dotnet new sln -n MoTask
dotnet new classlib -n MoTask.Core -o src/MoTask.Core -f net10.0
dotnet new classlib -n MoTask.Data -o src/MoTask.Data -f net10.0
dotnet new wpf      -n MoTask.App  -o src/MoTask.App  -f net10.0
dotnet new xunit    -n MoTask.Core.Tests -o tests/MoTask.Core.Tests -f net10.0
dotnet new xunit    -n MoTask.Data.Tests -o tests/MoTask.Data.Tests -f net10.0
dotnet new xunit    -n MoTask.App.Tests  -o tests/MoTask.App.Tests  -f net10.0
dotnet sln MoTask.sln add src/MoTask.Core src/MoTask.Data src/MoTask.App tests/MoTask.Core.Tests tests/MoTask.Data.Tests tests/MoTask.App.Tests
rm -f src/MoTask.Core/Class1.cs src/MoTask.Data/Class1.cs tests/MoTask.Core.Tests/UnitTest1.cs tests/MoTask.Data.Tests/UnitTest1.cs tests/MoTask.App.Tests/UnitTest1.cs
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.11
mkdir -p src/MoTask.App/Fonts && touch src/MoTask.App/Fonts/.gitkeep
```

- [ ] **Step 2: 共通ビルド設定を書く**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.11" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.11" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="gong-wpf-dragdrop" Version="4.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="FluentAssertions" Version="7.2.2" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
  </ItemGroup>
</Project>
```

`dotnet new xunit` が生成した csproj に `coverlet.collector` の `PackageReference` が含まれる場合は行ごと削除する（CPM でバージョン未定義のためビルドが失敗する）。

- [ ] **Step 3: 各 csproj を書き換える**

`src/MoTask.Core/MoTask.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MoTask.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

`src/MoTask.Data/MoTask.Data.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MoTask.Data</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\MoTask.Core\MoTask.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/MoTask.App/MoTask.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RootNamespace>MoTask.App</RootNamespace>
    <AssemblyName>MoTask</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="gong-wpf-dragdrop" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\MoTask.Core\MoTask.Core.csproj" />
    <ProjectReference Include="..\MoTask.Data\MoTask.Data.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Resource Include="Fonts\*.ttf" />
  </ItemGroup>
</Project>
```

`tests/MoTask.Core.Tests/MoTask.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\MoTask.Core\MoTask.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/MoTask.Data.Tests/MoTask.Data.Tests.csproj`: 上と同じ構成で、`ProjectReference` を `..\..\src\MoTask.Data\MoTask.Data.csproj` と `..\..\src\MoTask.Core\MoTask.Core.csproj` の2つにし、`<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />` と `<PackageReference Include="Microsoft.Extensions.Hosting" />` を追加する。

`tests/MoTask.App.Tests/MoTask.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\MoTask.App\MoTask.App.csproj" />
    <ProjectReference Include="..\..\src\MoTask.Core\MoTask.Core.csproj" />
  </ItemGroup>
</Project>
```

`dotnet new wpf` が生成した `src/MoTask.App/MainWindow.xaml` と `.xaml.cs` はこの時点では触らない（Task 14 で `Views/` に移す）。

- [ ] **Step 4: スモークテストを書く**

`tests/MoTask.Core.Tests/SmokeTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace MoTask.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TestInfrastructure_Works()
    {
        (1 + 1).Should().Be(2);
    }
}
```

- [ ] **Step 5: ビルドとテストを通す**

Run: `dotnet build MoTask.sln && dotnet test MoTask.sln`
Expected: ビルド成功、`Passed! - Failed: 0, Passed: 1`（App.Tests / Data.Tests はテスト0件の警告のみ）

- [ ] **Step 6: コミット**

```bash
git add -A
git commit -m "chore: scaffold MoTask solution with Core/Data/App and test projects

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Core のエンティティ・列挙・Result・時計・メッセージ

**Files:**
- Create: `src/MoTask.Core/Model/ColumnRole.cs`, `HistoryKind.cs`, `Board.cs`, `Column.cs`, `TaskItem.cs`, `Project.cs`, `Label.cs`, `HistoryEntry.cs`, `HistoryDetail.cs`
- Create: `src/MoTask.Core/Result.cs`
- Create: `src/MoTask.Core/Abstractions/IClock.cs`, `SystemClock.cs`
- Create: `src/MoTask.Core/Resources/Messages.resx`, `Messages.cs`
- Test: `tests/MoTask.Core.Tests/ModelTests.cs`

**Interfaces:**
- Produces: `Column.ActiveCount`, `Column.IsOverWip`, `TaskItem.IsDeleted`, `Result.Ok()/Fail()/Ok<T>()/Fail<T>()`, `HistoryDetail.Serialize/Deserialize`, `FieldChange(From, To)`, `Messages.*`。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/ModelTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class ModelTests
{
    [Fact]
    public void Column_ActiveCount_ExcludesDeletedTasks()
    {
        var column = new Column { Name = "進行中", WipLimit = 1 };
        column.Tasks.Add(new TaskItem { Title = "a" });
        column.Tasks.Add(new TaskItem { Title = "b", DeletedAt = DateTime.UtcNow });

        column.ActiveCount.Should().Be(1);
        column.IsOverWip.Should().BeFalse();

        column.Tasks.Add(new TaskItem { Title = "c" });
        column.IsOverWip.Should().BeTrue();
    }

    [Fact]
    public void Column_WithoutWipLimit_IsNeverOverWip()
    {
        var column = new Column { Name = "未着手" };
        column.Tasks.Add(new TaskItem { Title = "a" });
        column.IsOverWip.Should().BeFalse();
    }

    [Fact]
    public void Result_Ok_CarriesWarnings()
    {
        var r = Result.Ok(new[] { "warn" });
        r.IsSuccess.Should().BeTrue();
        r.Error.Should().BeNull();
        r.Warnings.Should().ContainSingle().Which.Should().Be("warn");

        var f = Result.Fail<int>("だめ");
        f.IsSuccess.Should().BeFalse();
        f.Error.Should().Be("だめ");
        f.Value.Should().Be(default);
    }

    [Fact]
    public void HistoryDetail_RoundTrips_WithJapanese()
    {
        var changes = new Dictionary<string, FieldChange>
        {
            ["Title"] = new("旧", "新"),
            ["DueDate"] = new(null, "2026-09-10"),
        };
        var json = HistoryDetail.Serialize(changes);
        json.Should().Contain("旧").And.NotContain("\\u");
        var back = HistoryDetail.Deserialize(json);
        back["Title"].Should().Be(new FieldChange("旧", "新"));
        back["DueDate"].From.Should().BeNull();
        HistoryDetail.Deserialize("").Should().BeEmpty();
    }

    [Fact]
    public void Messages_ResolveFromResx()
    {
        Messages.TitleRequired.Should().Be("タイトルを入力してください");
        string.Format(Messages.WipExceededFormat, "進行中", 3).Should().Be("進行中 の WIP 制限 3 を超えています");
    }
}
```

- [ ] **Step 2: テストが失敗（コンパイルエラー）することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `error CS0246: The type or namespace name 'Column' could not be found` など

- [ ] **Step 3: 列挙とエンティティを書く**

`src/MoTask.Core/Model/ColumnRole.cs`:

```csharp
namespace MoTask.Core.Model;

public enum ColumnRole
{
    Backlog = 0,
    Active = 1,
    Review = 2,
    Done = 3,
}
```

`src/MoTask.Core/Model/HistoryKind.cs`:

```csharp
namespace MoTask.Core.Model;

public enum HistoryKind
{
    Created = 0,
    Moved = 1,
    Edited = 2,
    Deleted = 3,
    Restored = 4,
}
```

`src/MoTask.Core/Model/Board.cs`:

```csharp
namespace MoTask.Core.Model;

public sealed class Board
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Column> Columns { get; set; } = new();
}
```

`src/MoTask.Core/Model/Column.cs`:

```csharp
namespace MoTask.Core.Model;

public sealed class Column
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public int? WipLimit { get; set; }
    public ColumnRole Role { get; set; } = ColumnRole.Active;
    public List<TaskItem> Tasks { get; set; } = new();

    /// <summary>論理削除済みを除いた件数。WIP 判定と列ヘッダーの表示に使う。</summary>
    public int ActiveCount => Tasks.Count(t => t.DeletedAt is null);

    public bool IsOverWip => WipLimit is int limit && ActiveCount > limit;
}
```

`src/MoTask.Core/Model/TaskItem.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>仕様上の "Task"。System.Threading.Tasks.Task との衝突を避けるため TaskItem と命名。</summary>
public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int ColumnId { get; set; }
    public int Position { get; set; }
    public int? ProjectId { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public List<Label> Labels { get; set; } = new();

    public bool IsDeleted => DeletedAt is not null;
}
```

`src/MoTask.Core/Model/Project.cs`:

```csharp
namespace MoTask.Core.Model;

public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
}
```

`src/MoTask.Core/Model/Label.cs`:

```csharp
namespace MoTask.Core.Model;

public sealed class Label
{
    public const string DefaultColor = "accent-300";

    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>アクセントランプの段の名前（"accent-300" など）。</summary>
    public string Color { get; set; } = DefaultColor;
}
```

`src/MoTask.Core/Model/HistoryEntry.cs`:

```csharp
namespace MoTask.Core.Model;

/// <summary>追記専用。更新も削除もしない。</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }
    public int TaskId { get; set; }
    /// <summary>新規作成タスクと同じ SaveChanges で保存するためのナビゲーション。DB 列にはならない。</summary>
    public TaskItem? Task { get; set; }
    public DateTime At { get; set; }
    public HistoryKind Kind { get; set; }
    public int? FromColumnId { get; set; }
    public int? ToColumnId { get; set; }
    /// <summary>Edited のとき変更項目と前後の値を JSON で持つ（HistoryDetail 参照）。それ以外は空文字。</summary>
    public string Detail { get; set; } = "";
}
```

`src/MoTask.Core/Model/HistoryDetail.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoTask.Core.Model;

public sealed record FieldChange(string? From, string? To);

public static class HistoryDetail
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IReadOnlyDictionary<string, FieldChange> changes)
        => JsonSerializer.Serialize(changes, Options);

    public static IReadOnlyDictionary<string, FieldChange> Deserialize(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return new Dictionary<string, FieldChange>();
        }
        return JsonSerializer.Deserialize<Dictionary<string, FieldChange>>(detail, Options)
               ?? new Dictionary<string, FieldChange>();
    }
}
```

- [ ] **Step 4: Result と IClock を書く**

`src/MoTask.Core/Result.cs`:

```csharp
namespace MoTask.Core;

public class Result
{
    protected Result(bool isSuccess, string? error, IReadOnlyList<string> warnings)
    {
        IsSuccess = isSuccess;
        Error = error;
        Warnings = warnings;
    }

    public bool IsSuccess { get; }
    /// <summary>失敗時のユーザー向け理由。成功時は null。</summary>
    public string? Error { get; }
    /// <summary>成功時の警告（WIP 超過など）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    // params にしておくと Ok(string[]) が generic の Ok<T>(T) より優先される
    public static Result Ok(params string[] warnings) => new(true, null, warnings);

    public static Result Fail(string error) => new(false, error, Array.Empty<string>());

    public static Result<T> Ok<T>(T value, IReadOnlyList<string>? warnings = null)
        => new(true, value, null, warnings ?? Array.Empty<string>());

    public static Result<T> Fail<T>(string error) => new(false, default, error, Array.Empty<string>());
}

public sealed class Result<T> : Result
{
    internal Result(bool isSuccess, T? value, string? error, IReadOnlyList<string> warnings)
        : base(isSuccess, error, warnings)
    {
        Value = value;
    }

    public T? Value { get; }
}
```

`src/MoTask.Core/Abstractions/IClock.cs`:

```csharp
namespace MoTask.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>ローカル日付。期限の「今日」「今週」「期限切れ」の判定に使う。</summary>
    DateOnly Today { get; }
}
```

`src/MoTask.Core/Abstractions/SystemClock.cs`:

```csharp
namespace MoTask.Core.Abstractions;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
```

- [ ] **Step 5: メッセージ resx とラッパーを書く**

`src/MoTask.Core/Resources/Messages.resx`（最小形式。`resheader` 4件は必須）:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="TitleRequired" xml:space="preserve"><value>タイトルを入力してください</value></data>
  <data name="TaskNotFound" xml:space="preserve"><value>タスクが見つかりません</value></data>
  <data name="ColumnNotFound" xml:space="preserve"><value>列が見つかりません</value></data>
  <data name="BoardNotFound" xml:space="preserve"><value>ボードが見つかりません</value></data>
  <data name="ProjectNotFound" xml:space="preserve"><value>プロジェクトが見つかりません</value></data>
  <data name="LabelNotFound" xml:space="preserve"><value>ラベルが見つかりません</value></data>
  <data name="TaskAlreadyDeleted" xml:space="preserve"><value>このタスクはすでに削除されています</value></data>
  <data name="TaskNotDeleted" xml:space="preserve"><value>このタスクは削除されていません</value></data>
  <data name="ColumnNameRequired" xml:space="preserve"><value>列の名前を入力してください</value></data>
  <data name="DoneColumnCannotChangeRole" xml:space="preserve"><value>完了の列の種別は変更できません</value></data>
  <data name="CannotAssignDoneRole" xml:space="preserve"><value>完了の種別は他の列に設定できません</value></data>
  <data name="DoneColumnCannotBeDeleted" xml:space="preserve"><value>完了の列は削除できません</value></data>
  <data name="ColumnHasTasks" xml:space="preserve"><value>タスクが残っている列は削除できません（削除済みのタスクも含みます）</value></data>
  <data name="ReorderMustIncludeAllColumns" xml:space="preserve"><value>並び替えにはすべての列を含めてください</value></data>
  <data name="WipLimitMustBePositive" xml:space="preserve"><value>WIP 制限は 1 以上の整数にしてください</value></data>
  <data name="ProjectNameRequired" xml:space="preserve"><value>プロジェクト名を入力してください</value></data>
  <data name="LabelNameRequired" xml:space="preserve"><value>ラベル名を入力してください</value></data>
  <data name="WipExceededFormat" xml:space="preserve"><value>{0} の WIP 制限 {1} を超えています</value></data>
  <data name="SaveFailed" xml:space="preserve"><value>保存に失敗しました</value></data>
</root>
```

`src/MoTask.Core/Resources/Messages.cs`（`dotnet build` は Designer ファイルを生成しないため手書き）:

```csharp
using System.Globalization;
using System.Resources;

namespace MoTask.Core;

public static class Messages
{
    private static readonly ResourceManager Rm =
        new("MoTask.Core.Resources.Messages", typeof(Messages).Assembly);

    private static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string TitleRequired => Get(nameof(TitleRequired));
    public static string TaskNotFound => Get(nameof(TaskNotFound));
    public static string ColumnNotFound => Get(nameof(ColumnNotFound));
    public static string BoardNotFound => Get(nameof(BoardNotFound));
    public static string ProjectNotFound => Get(nameof(ProjectNotFound));
    public static string LabelNotFound => Get(nameof(LabelNotFound));
    public static string TaskAlreadyDeleted => Get(nameof(TaskAlreadyDeleted));
    public static string TaskNotDeleted => Get(nameof(TaskNotDeleted));
    public static string ColumnNameRequired => Get(nameof(ColumnNameRequired));
    public static string DoneColumnCannotChangeRole => Get(nameof(DoneColumnCannotChangeRole));
    public static string CannotAssignDoneRole => Get(nameof(CannotAssignDoneRole));
    public static string DoneColumnCannotBeDeleted => Get(nameof(DoneColumnCannotBeDeleted));
    public static string ColumnHasTasks => Get(nameof(ColumnHasTasks));
    public static string ReorderMustIncludeAllColumns => Get(nameof(ReorderMustIncludeAllColumns));
    public static string WipLimitMustBePositive => Get(nameof(WipLimitMustBePositive));
    public static string ProjectNameRequired => Get(nameof(ProjectNameRequired));
    public static string LabelNameRequired => Get(nameof(LabelNameRequired));
    public static string WipExceededFormat => Get(nameof(WipExceededFormat));
    public static string SaveFailed => Get(nameof(SaveFailed));
}
```

- [ ] **Step 6: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): add domain entities, Result, IClock and messages

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: リポジトリ抽象・テスト用インメモリ実装・BoardService（作成と照会）

**Files:**
- Create: `src/MoTask.Core/Abstractions/IBoardRepository.cs`, `IHistoryRepository.cs`, `IUnitOfWork.cs`, `PersistenceException.cs`
- Create: `src/MoTask.Core/Services/IBoardService.cs`, `BoardService.cs`, `TaskUpdate.cs`
- Create: `tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`, `FakeClock.cs`
- Test: `tests/MoTask.Core.Tests/BoardServiceTaskTests.cs`

**Interfaces:**
- Produces: `IBoardRepository`, `IHistoryRepository`, `IUnitOfWork`（Data が実装）、`IBoardService`（App が使う）、`BoardService(IBoardRepository, IHistoryRepository, IUnitOfWork, IClock)`。このタスクで `IBoardService` の**全メソッドの署名**を確定し、`BoardService` では未実装分を `throw new NotImplementedException()` にしておく。Task 4〜8 で順に実装する。

- [ ] **Step 1: 抽象を書く**

`src/MoTask.Core/Abstractions/IBoardRepository.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

/// <summary>
/// 読み取りは追跡された同一インスタンスを返す（同じ Id のエンティティは同じ参照）。
/// 一覧の順序は Column は Order、TaskItem は Position。TaskItem には論理削除済みも含む。
/// </summary>
public interface IBoardRepository
{
    /// <summary>列（Order 順）と各列のタスク（Position 順、削除済み含む）とラベルまで含めて返す。</summary>
    Task<Board?> GetBoardAsync(CancellationToken ct = default);

    /// <summary>Tasks（削除済み含む）と各タスクの Labels を含めて返す。</summary>
    Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default);

    /// <summary>Labels を含めて返す。</summary>
    Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default);

    Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default);
    Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default);

    void AddColumn(Column column);
    void RemoveColumn(Column column);
    void AddTask(TaskItem task);
    void AddProject(Project project);
    void AddLabel(Label label);
}
```

`src/MoTask.Core/Abstractions/IHistoryRepository.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Abstractions;

public interface IHistoryRepository
{
    void Add(HistoryEntry entry);

    /// <summary>新しい順（At 降順、同時刻は Id 降順）。</summary>
    Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default);
}
```

`src/MoTask.Core/Abstractions/IUnitOfWork.cs`:

```csharp
namespace MoTask.Core.Abstractions;

public interface IUnitOfWork
{
    /// <summary>
    /// 溜まった変更を1トランザクションで書く。失敗時は PersistenceException を投げ、
    /// 実装側は未保存の変更を破棄した状態（追跡クリア）にしておく。
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
```

`src/MoTask.Core/Abstractions/PersistenceException.cs`:

```csharp
namespace MoTask.Core.Abstractions;

public sealed class PersistenceException : Exception
{
    public PersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
```

`src/MoTask.Core/Services/TaskUpdate.cs`:

```csharp
namespace MoTask.Core.Services;

/// <summary>詳細パネルからの一括更新。Labels は SetTaskLabelsAsync で別に扱う。</summary>
public sealed record TaskUpdate(int TaskId, string Title, string Description, int? ProjectId, DateOnly? DueDate);
```

`src/MoTask.Core/Services/IBoardService.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Services;

public interface IBoardService
{
    // 照会
    Task<Result<Board>> GetBoardAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default);

    // タスク
    Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default);
    Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default);
    /// <param name="position">移動先の列で、移動するタスクを除いた Position 順リストへの挿入位置。範囲外は端に丸める。</param>
    Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default);
    Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default);
    Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default);
    Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default);

    // 列
    Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default);
    Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default);
    Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default);
    Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default);
    Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default);
    Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default);

    // 分類
    Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default);
    Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default);
    Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default);
}
```

- [ ] **Step 2: テスト用のインメモリ実装と時計を書く**

`tests/MoTask.Core.Tests/Fakes/FakeClock.cs`:

```csharp
using MoTask.Core.Abstractions;

namespace MoTask.Core.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 9, 4);

    public void Advance(TimeSpan by) => UtcNow += by;
}
```

`tests/MoTask.Core.Tests/Fakes/InMemoryStore.cs`:

```csharp
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Core.Tests.Fakes;

/// <summary>
/// IBoardRepository / IHistoryRepository / IUnitOfWork をまとめて実装するテスト用ストア。
/// 参照は常に同一インスタンスを返す（EF の追跡と同じ契約）。Id は Add 時に即採番する。
/// </summary>
public sealed class InMemoryStore : IBoardRepository, IHistoryRepository, IUnitOfWork
{
    private int _nextId = 1;
    private long _nextHistoryId = 1;

    public Board Board { get; } = new() { Id = 1, Name = "テスト" };
    public List<Project> Projects { get; } = new();
    public List<Label> Labels { get; } = new();
    public List<HistoryEntry> History { get; } = new();
    public int SaveCount { get; private set; }
    public bool FailNextSave { get; set; }

    // ---- テスト用の投入ヘルパー ----

    public Column SeedColumn(string name, ColumnRole role, int? wipLimit = null)
    {
        var column = new Column
        {
            Id = _nextId++, BoardId = Board.Id, Name = name, Role = role,
            Order = Board.Columns.Count, WipLimit = wipLimit,
        };
        Board.Columns.Add(column);
        return column;
    }

    public TaskItem SeedTask(Column column, string title, DateTime? createdAt = null)
    {
        var at = createdAt ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem
        {
            Id = _nextId++, Title = title, ColumnId = column.Id, Position = column.Tasks.Count,
            CreatedAt = at, UpdatedAt = at,
            CompletedAt = column.Role == ColumnRole.Done ? at : null,
        };
        column.Tasks.Add(task);
        return task;
    }

    public Project SeedProject(string name)
    {
        var p = new Project { Id = _nextId++, Name = name };
        Projects.Add(p);
        return p;
    }

    public Label SeedLabel(string name, string color = Label.DefaultColor)
    {
        var l = new Label { Id = _nextId++, Name = name, Color = color };
        Labels.Add(l);
        return l;
    }

    public IEnumerable<TaskItem> AllTasks => Board.Columns.SelectMany(c => c.Tasks);

    // ---- IBoardRepository ----

    public Task<Board?> GetBoardAsync(CancellationToken ct = default) => Task.FromResult<Board?>(Board);

    public Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default)
        => Task.FromResult(Board.Columns.FirstOrDefault(c => c.Id == columnId));

    public Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult(AllTasks.FirstOrDefault(t => t.Id == taskId));

    public Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Id == projectId));

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>(Projects.OrderBy(p => p.Name).ToList());

    public Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default)
        => Task.FromResult(Labels.FirstOrDefault(l => l.Id == labelId));

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Label>>(Labels.OrderBy(l => l.Name).ToList());

    public void AddColumn(Column column) { if (column.Id == 0) column.Id = _nextId++; }
    public void RemoveColumn(Column column) { }
    public void AddTask(TaskItem task) { if (task.Id == 0) task.Id = _nextId++; }
    public void AddProject(Project project) { if (project.Id == 0) project.Id = _nextId++; Projects.Add(project); }
    public void AddLabel(Label label) { if (label.Id == 0) label.Id = _nextId++; Labels.Add(label); }

    // ---- IHistoryRepository ----

    public void Add(HistoryEntry entry)
    {
        entry.Id = _nextHistoryId++;
        if (entry.Task is not null) entry.TaskId = entry.Task.Id;
        History.Add(entry);
    }

    public Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HistoryEntry>>(
            History.Where(h => h.TaskId == taskId).OrderByDescending(h => h.At).ThenByDescending(h => h.Id).ToList());

    // ---- IUnitOfWork ----

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new PersistenceException("テスト用の保存失敗");
        }
        SaveCount++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: 失敗するテストを書く（作成と照会）**

`tests/MoTask.Core.Tests/BoardServiceTaskTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceTaskTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceTaskTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active, wipLimit: 1);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock);
    }

    [Fact]
    public async Task CreateTask_AppendsToColumn_AndWritesCreatedHistory()
    {
        _store.SeedTask(_backlog, "既存");

        var result = await _service.CreateTaskAsync(_backlog.Id, "  新しいタスク  ");

        result.IsSuccess.Should().BeTrue();
        var task = result.Value!;
        task.Id.Should().BePositive();
        task.Title.Should().Be("新しいタスク");
        task.ColumnId.Should().Be(_backlog.Id);
        task.Position.Should().Be(1);
        task.CreatedAt.Should().Be(_clock.UtcNow);
        task.UpdatedAt.Should().Be(_clock.UtcNow);
        task.CompletedAt.Should().BeNull();
        _backlog.Tasks.Should().Contain(task);

        var history = await _service.GetHistoryAsync(task.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Created);
        history[0].ToColumnId.Should().Be(_backlog.Id);
        history[0].FromColumnId.Should().BeNull();
        history[0].Detail.Should().BeEmpty();
        _store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateTask_InDoneColumn_SetsCompletedAt()
    {
        var result = await _service.CreateTaskAsync(_done.Id, "完了扱い");
        result.Value!.CompletedAt.Should().Be(_clock.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTask_EmptyTitle_IsRejected(string title)
    {
        var result = await _service.CreateTaskAsync(_backlog.Id, title);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TitleRequired);
        _backlog.Tasks.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTask_UnknownColumn_IsRejected()
    {
        var result = await _service.CreateTaskAsync(999, "x");
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.ColumnNotFound);
    }

    [Fact]
    public async Task CreateTask_OverWip_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "1件目");

        var result = await _service.CreateTaskAsync(_active.Id, "2件目");

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be(string.Format(Messages.WipExceededFormat, "進行中", 1));
    }

    [Fact]
    public async Task CreateTask_WhenSaveFails_ReturnsFailure()
    {
        _store.FailNextSave = true;

        var result = await _service.CreateTaskAsync(_backlog.Id, "x");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith(Messages.SaveFailed);
    }

    [Fact]
    public async Task GetBoard_ReturnsBoard()
    {
        var result = await _service.GetBoardAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Columns.Should().HaveCount(3);
    }
}
```

- [ ] **Step 4: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceTaskTests"`
Expected: コンパイルエラー `'BoardService' could not be found`

- [ ] **Step 5: BoardService を書く（作成・照会を実装、他は NotImplemented）**

`src/MoTask.Core/Services/BoardService.cs`:

```csharp
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Core.Services;

public sealed class BoardService : IBoardService
{
    private readonly IBoardRepository _boards;
    private readonly IHistoryRepository _history;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    // DbContext は同時に1操作しか受け付けないので、ユースケースを直列化する
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardService(IBoardRepository boards, IHistoryRepository history, IUnitOfWork uow, IClock clock)
    {
        _boards = boards;
        _history = history;
        _uow = uow;
        _clock = clock;
    }

    // ---------- 照会 ----------

    public Task<Result<Board>> GetBoardAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        return board is null ? Result.Fail<Board>(Messages.BoardNotFound) : Result.Ok(board);
    }, ct);

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default)
        => GateAsync(() => _history.GetForTaskAsync(taskId, ct), ct);

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => GateAsync(() => _boards.GetProjectsAsync(ct), ct);

    public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => GateAsync(() => _boards.GetLabelsAsync(ct), ct);

    // ---------- タスク ----------

    public Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default) => RunAsync(async () =>
    {
        title = title.Trim();
        if (title.Length == 0) return Result.Fail<TaskItem>(Messages.TitleRequired);

        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail<TaskItem>(Messages.ColumnNotFound);

        var now = _clock.UtcNow;
        var task = new TaskItem
        {
            Title = title,
            ColumnId = column.Id,
            Position = column.Tasks.Count,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = column.Role == ColumnRole.Done ? now : null,
        };
        column.Tasks.Add(task);
        _boards.AddTask(task);
        _history.Add(new HistoryEntry
        {
            Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Created, ToColumnId = column.Id,
        });

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(task, WipWarnings(column));
    }, ct);

    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 列 ----------

    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 分類 ----------

    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default)
        => throw new NotImplementedException();

    // ---------- 共通 ----------

    private static string[] WipWarnings(Column column)
        => column.IsOverWip
            ? new[] { string.Format(Messages.WipExceededFormat, column.Name, column.WipLimit) }
            : Array.Empty<string>();

    private static IEnumerable<TaskItem> Ordered(Column column)
        => column.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id);

    private static void Renumber(IList<TaskItem> tasks)
    {
        for (var i = 0; i < tasks.Count; i++) tasks[i].Position = i;
    }

    private async Task<T> GateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<Result> RunAsync(Func<Task<Result>> action, CancellationToken ct) => GateAsync(async () =>
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            return Result.Fail($"{Messages.SaveFailed}: {ex.Message}");
        }
    }, ct);

    private Task<Result<T>> RunAsync<T>(Func<Task<Result<T>>> action, CancellationToken ct) => GateAsync(async () =>
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            return Result.Fail<T>($"{Messages.SaveFailed}: {ex.Message}");
        }
    }, ct);
}
```

`Ordered` と `Renumber` は Task 4 以降で使う。未使用警告が出ても構わない。

- [ ] **Step 6: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 14`

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): add repository abstractions and BoardService task creation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: MoveTask（再採番・CompletedAt・履歴・WIP 警告）

**Files:**
- Modify: `src/MoTask.Core/Services/BoardService.cs`（`MoveTaskAsync`）
- Test: `tests/MoTask.Core.Tests/BoardServiceMoveTests.cs`

**Interfaces:**
- Consumes: Task 3 の `BoardService`、`InMemoryStore`、`FakeClock`。
- Produces: `MoveTaskAsync(taskId, toColumnId, position)` の確定した挙動。`position` は「移動先列で、移動タスクを除いた Position 順リストへの挿入位置」。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardServiceMoveTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceMoveTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceMoveTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active, wipLimit: 2);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock);
    }

    private static int[] Positions(Column c) => c.Tasks.OrderBy(t => t.Position).Select(t => t.Position).ToArray();
    private static string[] TitlesInOrder(Column c) => c.Tasks.OrderBy(t => t.Position).Select(t => t.Title).ToArray();

    [Fact]
    public async Task Move_AcrossColumns_RenumbersBothColumns()
    {
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        var c = _store.SeedTask(_backlog, "c");
        var x = _store.SeedTask(_active, "x");

        var result = await _service.MoveTaskAsync(b.Id, _active.Id, position: 0);

        result.IsSuccess.Should().BeTrue();
        b.ColumnId.Should().Be(_active.Id);
        TitlesInOrder(_backlog).Should().Equal("a", "c");
        Positions(_backlog).Should().Equal(0, 1);
        TitlesInOrder(_active).Should().Equal("b", "x");
        Positions(_active).Should().Equal(0, 1);
        _backlog.Tasks.Should().NotContain(b);
        _active.Tasks.Should().Contain(b);
    }

    [Fact]
    public async Task Move_WithinColumn_ReordersAndWritesNoHistory()
    {
        var a = _store.SeedTask(_backlog, "a");
        var b = _store.SeedTask(_backlog, "b");
        var c = _store.SeedTask(_backlog, "c");
        var before = _store.History.Count;

        // a を c の後ろへ（a を除いたリスト [b, c] の位置 2 = 末尾）
        var result = await _service.MoveTaskAsync(a.Id, _backlog.Id, position: 2);

        result.IsSuccess.Should().BeTrue();
        TitlesInOrder(_backlog).Should().Equal("b", "c", "a");
        Positions(_backlog).Should().Equal(0, 1, 2);
        _store.History.Count.Should().Be(before);
        a.UpdatedAt.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "並び替えだけでは更新時刻を変えない");
        _store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Move_PositionOutOfRange_IsClamped()
    {
        var a = _store.SeedTask(_backlog, "a");
        _store.SeedTask(_active, "x");

        (await _service.MoveTaskAsync(a.Id, _active.Id, position: 99)).IsSuccess.Should().BeTrue();
        TitlesInOrder(_active).Should().Equal("x", "a");

        (await _service.MoveTaskAsync(a.Id, _active.Id, position: -5)).IsSuccess.Should().BeTrue();
        TitlesInOrder(_active).Should().Equal("a", "x");
    }

    [Fact]
    public async Task Move_IntoDone_SetsCompletedAt_AndOutOfDone_ClearsIt()
    {
        var a = _store.SeedTask(_backlog, "a");
        _clock.UtcNow = new DateTime(2026, 9, 4, 8, 40, 0, DateTimeKind.Utc);

        await _service.MoveTaskAsync(a.Id, _done.Id, 0);
        a.CompletedAt.Should().Be(_clock.UtcNow);
        a.UpdatedAt.Should().Be(_clock.UtcNow);

        _clock.Advance(TimeSpan.FromHours(1));
        await _service.MoveTaskAsync(a.Id, _active.Id, 0);
        a.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Move_AcrossColumns_WritesMovedHistoryOnce()
    {
        var a = _store.SeedTask(_backlog, "a");

        await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        var history = await _service.GetHistoryAsync(a.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Moved);
        history[0].FromColumnId.Should().Be(_backlog.Id);
        history[0].ToColumnId.Should().Be(_active.Id);
        history[0].At.Should().Be(_clock.UtcNow);
        history[0].Detail.Should().BeEmpty();
    }

    [Fact]
    public async Task Move_OverWip_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "x");
        _store.SeedTask(_active, "y");
        var a = _store.SeedTask(_backlog, "a");

        var result = await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be(string.Format(Messages.WipExceededFormat, "進行中", 2));
        a.ColumnId.Should().Be(_active.Id);
    }

    [Fact]
    public async Task Move_WipCount_IgnoresDeletedTasks()
    {
        var x = _store.SeedTask(_active, "x");
        x.DeletedAt = _clock.UtcNow;
        _store.SeedTask(_active, "y");
        var a = _store.SeedTask(_backlog, "a");

        var result = await _service.MoveTaskAsync(a.Id, _active.Id, 0);

        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Move_UnknownTaskOrColumn_IsRejected()
    {
        var a = _store.SeedTask(_backlog, "a");
        (await _service.MoveTaskAsync(999, _active.Id, 0)).Error.Should().Be(Messages.TaskNotFound);
        (await _service.MoveTaskAsync(a.Id, 999, 0)).Error.Should().Be(Messages.ColumnNotFound);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceMoveTests"`
Expected: `NotImplementedException` で 8 件失敗

- [ ] **Step 3: MoveTaskAsync を実装する**

`BoardService.cs` の `MoveTaskAsync` を置き換える:

```csharp
    public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var source = await _boards.GetColumnAsync(task.ColumnId, ct).ConfigureAwait(false);
        var target = await _boards.GetColumnAsync(toColumnId, ct).ConfigureAwait(false);
        if (source is null || target is null) return Result.Fail(Messages.ColumnNotFound);

        var columnChanged = source.Id != target.Id;

        // 移動タスクを除いた順序リストを作り、そこへ挿入して再採番する
        var sourceTasks = Ordered(source).Where(t => t.Id != task.Id).ToList();
        var targetTasks = columnChanged ? Ordered(target).ToList() : sourceTasks;
        position = Math.Clamp(position, 0, targetTasks.Count);
        targetTasks.Insert(position, task);

        if (columnChanged)
        {
            source.Tasks.Remove(task);
            target.Tasks.Add(task);
            task.ColumnId = target.Id;
            Renumber(sourceTasks);
        }
        Renumber(targetTasks);

        if (columnChanged)
        {
            var now = _clock.UtcNow;
            task.UpdatedAt = now;
            task.CompletedAt = target.Role == ColumnRole.Done ? now : null;
            _history.Add(new HistoryEntry
            {
                Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Moved,
                FromColumnId = source.Id, ToColumnId = target.Id,
            });
        }

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(columnChanged ? WipWarnings(target) : Array.Empty<string>());
    }, ct);
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 22`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): implement MoveTask with renumbering, CompletedAt and history

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: UpdateTask と SetTaskLabels（Edited 履歴の Detail）

**Files:**
- Modify: `src/MoTask.Core/Services/BoardService.cs`（`UpdateTaskAsync`, `SetTaskLabelsAsync`）
- Test: `tests/MoTask.Core.Tests/BoardServiceUpdateTests.cs`

**Interfaces:**
- Consumes: `TaskUpdate`, `HistoryDetail`, `FieldChange`。
- Produces: Detail JSON のキーは `Title`, `Description`, `Project`, `DueDate`, `Labels`。`Project` と `Labels` は名前で保持（`Labels` は `", "` 区切り）。`DueDate` は `yyyy-MM-dd`。App の `HistoryFormatter` がこのキーを日本語名に変換する。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardServiceUpdateTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceUpdateTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly TaskItem _task;

    public BoardServiceUpdateTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("完了", ColumnRole.Done);
        _task = _store.SeedTask(_backlog, "元のタイトル");
        _service = new BoardService(_store, _store, _store, _clock);
    }

    private TaskUpdate Unchanged() => new(_task.Id, _task.Title, _task.Description, _task.ProjectId, _task.DueDate);

    [Fact]
    public async Task Update_NoChanges_WritesNoHistoryAndDoesNotSave()
    {
        var result = await _service.UpdateTaskAsync(Unchanged());

        result.IsSuccess.Should().BeTrue();
        _store.History.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_OnlyChangedFields_AppearInDetail()
    {
        var project = _store.SeedProject("顧客A対応");
        _clock.UtcNow = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);

        var result = await _service.UpdateTaskAsync(Unchanged() with
        {
            Title = " 新しいタイトル ",
            ProjectId = project.Id,
            DueDate = new DateOnly(2026, 9, 10),
        });

        result.IsSuccess.Should().BeTrue();
        _task.Title.Should().Be("新しいタイトル");
        _task.ProjectId.Should().Be(project.Id);
        _task.DueDate.Should().Be(new DateOnly(2026, 9, 10));
        _task.UpdatedAt.Should().Be(_clock.UtcNow);

        var history = await _service.GetHistoryAsync(_task.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Edited);
        var detail = HistoryDetail.Deserialize(history[0].Detail);
        detail.Keys.Should().BeEquivalentTo(new[] { "Title", "Project", "DueDate" });
        detail["Title"].Should().Be(new FieldChange("元のタイトル", "新しいタイトル"));
        detail["Project"].Should().Be(new FieldChange(null, "顧客A対応"));
        detail["DueDate"].Should().Be(new FieldChange(null, "2026-09-10"));
    }

    [Fact]
    public async Task Update_DescriptionOnly_RecordsDescription()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { Description = "詳細を書く" });

        result.IsSuccess.Should().BeTrue();
        var detail = HistoryDetail.Deserialize(_store.History.Single().Detail);
        detail.Should().ContainKey("Description").WhoseValue.Should().Be(new FieldChange("", "詳細を書く"));
    }

    [Fact]
    public async Task Update_EmptyTitle_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { Title = "  " });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TitleRequired);
        _task.Title.Should().Be("元のタイトル");
    }

    [Fact]
    public async Task Update_UnknownProject_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { ProjectId = 999 });
        result.Error.Should().Be(Messages.ProjectNotFound);
    }

    [Fact]
    public async Task Update_UnknownTask_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(new TaskUpdate(999, "x", "", null, null));
        result.Error.Should().Be(Messages.TaskNotFound);
    }

    [Fact]
    public async Task SetTaskLabels_ReplacesLabels_AndRecordsNames()
    {
        var urgent = _store.SeedLabel("至急");
        var routine = _store.SeedLabel("定例");
        _task.Labels.Add(urgent);

        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { routine.Id });

        result.IsSuccess.Should().BeTrue();
        _task.Labels.Select(l => l.Name).Should().Equal("定例");
        var detail = HistoryDetail.Deserialize(_store.History.Single().Detail);
        detail["Labels"].Should().Be(new FieldChange("至急", "定例"));
    }

    [Fact]
    public async Task SetTaskLabels_SameSet_WritesNoHistory()
    {
        var urgent = _store.SeedLabel("至急");
        _task.Labels.Add(urgent);

        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { urgent.Id, urgent.Id });

        result.IsSuccess.Should().BeTrue();
        _store.History.Should().BeEmpty();
    }

    [Fact]
    public async Task SetTaskLabels_UnknownLabel_IsRejected()
    {
        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { 999 });
        result.Error.Should().Be(Messages.LabelNotFound);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceUpdateTests"`
Expected: `NotImplementedException` で 9 件失敗

- [ ] **Step 3: 実装する**

`BoardService.cs` の `UpdateTaskAsync` と `SetTaskLabelsAsync` を置き換え、ヘルパーを追加:

```csharp
    public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default) => RunAsync(async () =>
    {
        var title = update.Title.Trim();
        if (title.Length == 0) return Result.Fail(Messages.TitleRequired);

        var task = await _boards.GetTaskAsync(update.TaskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var description = update.Description ?? "";
        var changes = new Dictionary<string, FieldChange>();

        if (task.Title != title) changes["Title"] = new FieldChange(task.Title, title);
        if (task.Description != description) changes["Description"] = new FieldChange(task.Description, description);
        if (task.ProjectId != update.ProjectId)
        {
            Project? newProject = null;
            if (update.ProjectId is int pid)
            {
                newProject = await _boards.GetProjectAsync(pid, ct).ConfigureAwait(false);
                if (newProject is null) return Result.Fail(Messages.ProjectNotFound);
            }
            var oldProject = task.ProjectId is int oid ? await _boards.GetProjectAsync(oid, ct).ConfigureAwait(false) : null;
            changes["Project"] = new FieldChange(oldProject?.Name, newProject?.Name);
        }
        if (task.DueDate != update.DueDate)
        {
            changes["DueDate"] = new FieldChange(FormatDate(task.DueDate), FormatDate(update.DueDate));
        }

        if (changes.Count == 0) return Result.Ok();

        task.Title = title;
        task.Description = description;
        task.ProjectId = update.ProjectId;
        task.DueDate = update.DueDate;
        AddEditedHistory(task, changes);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);

        var all = await _boards.GetLabelsAsync(ct).ConfigureAwait(false);
        var wanted = labelIds.Distinct().OrderBy(id => id).ToList();
        var newLabels = wanted.Select(id => all.FirstOrDefault(l => l.Id == id)).ToList();
        if (newLabels.Any(l => l is null)) return Result.Fail(Messages.LabelNotFound);

        var current = task.Labels.Select(l => l.Id).OrderBy(id => id).ToList();
        if (current.SequenceEqual(wanted)) return Result.Ok();

        var changes = new Dictionary<string, FieldChange>
        {
            ["Labels"] = new FieldChange(JoinNames(task.Labels), JoinNames(newLabels!)),
        };
        task.Labels.Clear();
        task.Labels.AddRange(newLabels!);
        AddEditedHistory(task, changes);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    private void AddEditedHistory(TaskItem task, Dictionary<string, FieldChange> changes)
    {
        var now = _clock.UtcNow;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry
        {
            Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Edited,
            Detail = HistoryDetail.Serialize(changes),
        });
    }

    private static string? FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    private static string JoinNames(IEnumerable<Label> labels)
        => string.Join(", ", labels.OrderBy(l => l.Name).Select(l => l.Name));
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 31`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): implement UpdateTask and SetTaskLabels with edited history detail

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: DeleteTask / RestoreTask

**Files:**
- Modify: `src/MoTask.Core/Services/BoardService.cs`（`DeleteTaskAsync`, `RestoreTaskAsync`）
- Test: `tests/MoTask.Core.Tests/BoardServiceDeleteTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardServiceDeleteTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceDeleteTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly TaskItem _task;

    public BoardServiceDeleteTests()
    {
        var backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("完了", ColumnRole.Done);
        _task = _store.SeedTask(backlog, "a");
        _service = new BoardService(_store, _store, _store, _clock);
    }

    [Fact]
    public async Task Delete_SetsDeletedAt_AndWritesDeletedHistory()
    {
        _clock.UtcNow = new DateTime(2026, 9, 4, 2, 0, 0, DateTimeKind.Utc);

        var result = await _service.DeleteTaskAsync(_task.Id);

        result.IsSuccess.Should().BeTrue();
        _task.DeletedAt.Should().Be(_clock.UtcNow);
        _task.IsDeleted.Should().BeTrue();
        _task.UpdatedAt.Should().Be(_clock.UtcNow);
        var history = await _service.GetHistoryAsync(_task.Id);
        history.Should().ContainSingle().Which.Kind.Should().Be(HistoryKind.Deleted);
    }

    [Fact]
    public async Task Delete_Twice_IsRejected()
    {
        await _service.DeleteTaskAsync(_task.Id);
        var result = await _service.DeleteTaskAsync(_task.Id);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TaskAlreadyDeleted);
        _store.History.Should().ContainSingle();
    }

    [Fact]
    public async Task Restore_ClearsDeletedAt_AndWritesRestoredHistory()
    {
        await _service.DeleteTaskAsync(_task.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await _service.RestoreTaskAsync(_task.Id);

        result.IsSuccess.Should().BeTrue();
        _task.DeletedAt.Should().BeNull();
        var history = await _service.GetHistoryAsync(_task.Id);
        history.Select(h => h.Kind).Should().Equal(HistoryKind.Restored, HistoryKind.Deleted);
    }

    [Fact]
    public async Task Restore_NotDeleted_IsRejected()
    {
        var result = await _service.RestoreTaskAsync(_task.Id);
        result.Error.Should().Be(Messages.TaskNotDeleted);
    }

    [Fact]
    public async Task Delete_UnknownTask_IsRejected()
    {
        (await _service.DeleteTaskAsync(999)).Error.Should().Be(Messages.TaskNotFound);
        (await _service.RestoreTaskAsync(999)).Error.Should().Be(Messages.TaskNotFound);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceDeleteTests"`
Expected: `NotImplementedException` で 5 件失敗

- [ ] **Step 3: 実装する**

```csharp
    public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);
        if (task.IsDeleted) return Result.Fail(Messages.TaskAlreadyDeleted);

        var now = _clock.UtcNow;
        task.DeletedAt = now;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry { Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Deleted });

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var task = await _boards.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return Result.Fail(Messages.TaskNotFound);
        if (!task.IsDeleted) return Result.Fail(Messages.TaskNotDeleted);

        var now = _clock.UtcNow;
        task.DeletedAt = null;
        task.UpdatedAt = now;
        _history.Add(new HistoryEntry { Task = task, TaskId = task.Id, At = now, Kind = HistoryKind.Restored });

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 36`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): implement soft delete and restore with history

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: 列のユースケース（追加・改名・Role・並び替え・WIP・削除）

**Files:**
- Modify: `src/MoTask.Core/Services/BoardService.cs`
- Test: `tests/MoTask.Core.Tests/BoardServiceColumnTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardServiceColumnTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceColumnTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly Column _active;
    private readonly Column _done;

    public BoardServiceColumnTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _active = _store.SeedColumn("進行中", ColumnRole.Active);
        _done = _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, _clock);
    }

    [Fact]
    public async Task AddColumn_AppendsWithNextOrder_DefaultRoleActive()
    {
        var result = await _service.AddColumnAsync(" 保留 ");

        result.IsSuccess.Should().BeTrue();
        var column = result.Value!;
        column.Id.Should().BePositive();
        column.Name.Should().Be("保留");
        column.Role.Should().Be(ColumnRole.Active);
        column.Order.Should().Be(3);
        column.BoardId.Should().Be(_store.Board.Id);
        _store.Board.Columns.Should().Contain(column);
    }

    [Fact]
    public async Task AddColumn_WithDoneRole_IsRejected()
    {
        var result = await _service.AddColumnAsync("もう一つの完了", ColumnRole.Done);
        result.Error.Should().Be(Messages.CannotAssignDoneRole);
        _store.Board.Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task AddColumn_EmptyName_IsRejected()
    {
        (await _service.AddColumnAsync("  ")).Error.Should().Be(Messages.ColumnNameRequired);
    }

    [Fact]
    public async Task RenameColumn_Works_AndRejectsEmpty()
    {
        (await _service.RenameColumnAsync(_backlog.Id, " 未処理 ")).IsSuccess.Should().BeTrue();
        _backlog.Name.Should().Be("未処理");
        (await _service.RenameColumnAsync(_backlog.Id, "")).Error.Should().Be(Messages.ColumnNameRequired);
        (await _service.RenameColumnAsync(999, "x")).Error.Should().Be(Messages.ColumnNotFound);
    }

    [Fact]
    public async Task SetColumnRole_ChangesNonDoneColumns()
    {
        (await _service.SetColumnRoleAsync(_backlog.Id, ColumnRole.Review)).IsSuccess.Should().BeTrue();
        _backlog.Role.Should().Be(ColumnRole.Review);
    }

    [Fact]
    public async Task SetColumnRole_OnDoneColumn_IsRejected()
    {
        var result = await _service.SetColumnRoleAsync(_done.Id, ColumnRole.Active);
        result.Error.Should().Be(Messages.DoneColumnCannotChangeRole);
        _done.Role.Should().Be(ColumnRole.Done);
    }

    [Fact]
    public async Task SetColumnRole_ToDone_IsRejected()
    {
        var result = await _service.SetColumnRoleAsync(_active.Id, ColumnRole.Done);
        result.Error.Should().Be(Messages.CannotAssignDoneRole);
        _active.Role.Should().Be(ColumnRole.Active);
    }

    [Fact]
    public async Task ReorderColumns_AssignsOrderByIndex()
    {
        var result = await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _active.Id });

        result.IsSuccess.Should().BeTrue();
        _done.Order.Should().Be(0);
        _backlog.Order.Should().Be(1);
        _active.Order.Should().Be(2);
    }

    [Fact]
    public async Task ReorderColumns_MustIncludeEveryColumnExactlyOnce()
    {
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _backlog.Id })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
        (await _service.ReorderColumnsAsync(new[] { _done.Id, _backlog.Id, _active.Id, 999 })).Error.Should().Be(Messages.ReorderMustIncludeAllColumns);
    }

    [Fact]
    public async Task SetWipLimit_AcceptsPositiveOrNull_RejectsZero()
    {
        (await _service.SetWipLimitAsync(_active.Id, 3)).IsSuccess.Should().BeTrue();
        _active.WipLimit.Should().Be(3);
        (await _service.SetWipLimitAsync(_active.Id, null)).IsSuccess.Should().BeTrue();
        _active.WipLimit.Should().BeNull();
        (await _service.SetWipLimitAsync(_active.Id, 0)).Error.Should().Be(Messages.WipLimitMustBePositive);
        (await _service.SetWipLimitAsync(_active.Id, -1)).Error.Should().Be(Messages.WipLimitMustBePositive);
    }

    [Fact]
    public async Task SetWipLimit_BelowCurrentCount_SucceedsWithWarning()
    {
        _store.SeedTask(_active, "x");
        _store.SeedTask(_active, "y");

        var result = await _service.SetWipLimitAsync(_active.Id, 1);

        result.IsSuccess.Should().BeTrue();
        result.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteColumn_EmptyNonDone_RemovesAndRenumbersOrder()
    {
        var result = await _service.DeleteColumnAsync(_backlog.Id);

        result.IsSuccess.Should().BeTrue();
        _store.Board.Columns.Should().NotContain(_backlog);
        _active.Order.Should().Be(0);
        _done.Order.Should().Be(1);
    }

    [Fact]
    public async Task DeleteColumn_Done_IsRejected()
    {
        (await _service.DeleteColumnAsync(_done.Id)).Error.Should().Be(Messages.DoneColumnCannotBeDeleted);
        _store.Board.Columns.Should().Contain(_done);
    }

    [Fact]
    public async Task DeleteColumn_WithTasks_IncludingDeleted_IsRejected()
    {
        var t = _store.SeedTask(_backlog, "a");
        t.DeletedAt = _clock.UtcNow;

        var result = await _service.DeleteColumnAsync(_backlog.Id);

        result.Error.Should().Be(Messages.ColumnHasTasks);
        _store.Board.Columns.Should().Contain(_backlog);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceColumnTests"`
Expected: `NotImplementedException` で 14 件失敗

- [ ] **Step 3: 実装する**

```csharp
    public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Column>(Messages.ColumnNameRequired);
        if (role == ColumnRole.Done) return Result.Fail<Column>(Messages.CannotAssignDoneRole);

        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail<Column>(Messages.BoardNotFound);

        var column = new Column
        {
            BoardId = board.Id,
            Name = name,
            Role = role,
            Order = board.Columns.Count == 0 ? 0 : board.Columns.Max(c => c.Order) + 1,
        };
        board.Columns.Add(column);
        _boards.AddColumn(column);

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(column);
    }, ct);

    public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail(Messages.ColumnNameRequired);
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Name == name) return Result.Ok();

        column.Name = name;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default) => RunAsync(async () =>
    {
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Role == ColumnRole.Done) return Result.Fail(Messages.DoneColumnCannotChangeRole);
        if (role == ColumnRole.Done) return Result.Fail(Messages.CannotAssignDoneRole);
        if (column.Role == role) return Result.Ok();

        column.Role = role;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail(Messages.BoardNotFound);

        var existing = board.Columns.Select(c => c.Id).ToHashSet();
        var requested = orderedColumnIds.ToHashSet();
        if (orderedColumnIds.Count != existing.Count || !requested.SetEquals(existing))
        {
            return Result.Fail(Messages.ReorderMustIncludeAllColumns);
        }

        for (var i = 0; i < orderedColumnIds.Count; i++)
        {
            board.Columns.First(c => c.Id == orderedColumnIds[i]).Order = i;
        }
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (wipLimit is int limit && limit < 1) return Result.Fail(Messages.WipLimitMustBePositive);
        var column = await _boards.GetColumnAsync(columnId, ct).ConfigureAwait(false);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);

        column.WipLimit = wipLimit;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(WipWarnings(column));
    }, ct);

    public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var board = await _boards.GetBoardAsync(ct).ConfigureAwait(false);
        if (board is null) return Result.Fail(Messages.BoardNotFound);
        var column = board.Columns.FirstOrDefault(c => c.Id == columnId);
        if (column is null) return Result.Fail(Messages.ColumnNotFound);
        if (column.Role == ColumnRole.Done) return Result.Fail(Messages.DoneColumnCannotBeDeleted);
        if (column.Tasks.Count > 0) return Result.Fail(Messages.ColumnHasTasks);

        board.Columns.Remove(column);
        _boards.RemoveColumn(column);
        var remaining = board.Columns.OrderBy(c => c.Order).ToList();
        for (var i = 0; i < remaining.Count; i++) remaining[i].Order = i;

        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 50`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): implement column use cases with Done column rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: プロジェクト・ラベルのユースケース

**Files:**
- Modify: `src/MoTask.Core/Services/BoardService.cs`
- Test: `tests/MoTask.Core.Tests/BoardServiceClassificationTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/BoardServiceClassificationTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceClassificationTests
{
    private readonly InMemoryStore _store = new();
    private readonly BoardService _service;

    public BoardServiceClassificationTests()
    {
        _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, new FakeClock());
    }

    [Fact]
    public async Task CreateProject_TrimsName_AndAssignsId()
    {
        var result = await _service.CreateProjectAsync(" 顧客A対応 ");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().BePositive();
        result.Value.Name.Should().Be("顧客A対応");
        result.Value.Archived.Should().BeFalse();
        (await _service.GetProjectsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateProject_EmptyName_IsRejected()
    {
        (await _service.CreateProjectAsync("  ")).Error.Should().Be(Messages.ProjectNameRequired);
    }

    [Fact]
    public async Task ArchiveProject_SetsArchived()
    {
        var p = _store.SeedProject("合宿");
        (await _service.ArchiveProjectAsync(p.Id)).IsSuccess.Should().BeTrue();
        p.Archived.Should().BeTrue();
        (await _service.ArchiveProjectAsync(999)).Error.Should().Be(Messages.ProjectNotFound);
    }

    [Fact]
    public async Task CreateLabel_UsesGivenColor_OrDefault()
    {
        var a = await _service.CreateLabelAsync("至急", "accent-500");
        a.Value!.Color.Should().Be("accent-500");

        var b = await _service.CreateLabelAsync("定例", "");
        b.Value!.Color.Should().Be(Label.DefaultColor);

        (await _service.GetLabelsAsync()).Select(l => l.Name).Should().Equal("定例", "至急");
    }

    [Fact]
    public async Task CreateLabel_EmptyName_IsRejected()
    {
        (await _service.CreateLabelAsync(" ", "accent-300")).Error.Should().Be(Messages.LabelNameRequired);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~BoardServiceClassificationTests"`
Expected: `NotImplementedException` で 5 件失敗

- [ ] **Step 3: 実装する**

```csharp
    public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Project>(Messages.ProjectNameRequired);

        var project = new Project { Name = name };
        _boards.AddProject(project);
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(project);
    }, ct);

    public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var project = await _boards.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null) return Result.Fail(Messages.ProjectNotFound);
        if (project.Archived) return Result.Ok();

        project.Archived = true;
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }, ct);

    public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default) => RunAsync(async () =>
    {
        name = name.Trim();
        if (name.Length == 0) return Result.Fail<Label>(Messages.LabelNameRequired);

        var label = new Label { Name = name, Color = string.IsNullOrWhiteSpace(color) ? Label.DefaultColor : color.Trim() };
        _boards.AddLabel(label);
        await _uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok(label);
    }, ct);
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 55`。`BoardService.cs` に `NotImplementedException` が残っていないことを `grep -n NotImplemented src/MoTask.Core/Services/BoardService.cs` で確認（出力なし）。

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): implement project and label use cases

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: TaskFilter と期限区分

**Files:**
- Create: `src/MoTask.Core/Filtering/DueFilter.cs`, `DueStatus.cs`, `TaskFilter.cs`
- Test: `tests/MoTask.Core.Tests/TaskFilterTests.cs`

**Interfaces:**
- Produces: `TaskFilter(ProjectId, LabelIds, Due, SearchText, ShowDeleted)`、`TaskFilter.Apply(IEnumerable<TaskItem>, DateOnly today)`、`TaskFilter.WeekOf(DateOnly)`、`DueStatuses.Of(TaskItem, DateOnly)` → `DueStatus.None/Upcoming/Today/Overdue`。App の ViewModel が使う。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Core.Tests/TaskFilterTests.cs`:

```csharp
using FluentAssertions;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class TaskFilterTests
{
    // 2026-09-04 は金曜日。週は月曜 8/31 〜 日曜 9/6。
    private static readonly DateOnly Today = new(2026, 9, 4);

    private static readonly Label Urgent = new() { Id = 1, Name = "至急" };
    private static readonly Label Routine = new() { Id = 2, Name = "定例" };

    private static TaskItem Make(int id, string title, string description = "", int? projectId = null,
        DateOnly? due = null, bool deleted = false, bool completed = false, params Label[] labels)
        => new()
        {
            Id = id, Title = title, Description = description, ProjectId = projectId, DueDate = due,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            CompletedAt = completed ? DateTime.UtcNow : null,
            Labels = labels.ToList(),
        };

    private static readonly TaskItem[] Tasks =
    {
        Make(1, "請求先情報を更新する", projectId: 10, due: new DateOnly(2026, 9, 8), labels: new[] { Urgent }),
        Make(2, "会場候補を3つに絞る", "合宿の下見", projectId: 20, due: new DateOnly(2026, 9, 4), labels: new[] { Urgent, Routine }),
        Make(3, "求人票の文面を見直す", due: new DateOnly(2026, 9, 1)),
        Make(4, "古いタスク", due: new DateOnly(2026, 9, 1), completed: true),
        Make(5, "削除済み", deleted: true),
        Make(6, "週末までに", due: new DateOnly(2026, 9, 6)),
    };

    private static int[] Ids(TaskFilter f) => f.Apply(Tasks, Today).Select(t => t.Id).ToArray();

    [Fact]
    public void None_HidesDeletedOnly()
    {
        Ids(TaskFilter.None).Should().Equal(1, 2, 3, 4, 6);
    }

    [Fact]
    public void ShowDeleted_IncludesDeletedAndLive()
    {
        Ids(new TaskFilter(ShowDeleted: true)).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void Project_FiltersExactly()
    {
        Ids(new TaskFilter(ProjectId: 10)).Should().Equal(1);
    }

    [Fact]
    public void Labels_AreAnded()
    {
        Ids(new TaskFilter(LabelIds: new HashSet<int> { 1 })).Should().Equal(1, 2);
        Ids(new TaskFilter(LabelIds: new HashSet<int> { 1, 2 })).Should().Equal(2);
        Ids(new TaskFilter(LabelIds: new HashSet<int>())).Should().Equal(1, 2, 3, 4, 6);
    }

    [Fact]
    public void Due_Today_ThisWeek_Overdue()
    {
        Ids(new TaskFilter(Due: DueFilter.Today)).Should().Equal(2);
        Ids(new TaskFilter(Due: DueFilter.ThisWeek)).Should().Equal(2, 3, 4, 6, because: "月曜〜日曜の週全体");
        Ids(new TaskFilter(Due: DueFilter.Overdue)).Should().Equal(3, because: "完了済みは期限切れに含めない");
    }

    [Fact]
    public void Search_MatchesTitleOrDescription_CaseInsensitive()
    {
        Ids(new TaskFilter(SearchText: "合宿")).Should().Equal(2);
        Ids(new TaskFilter(SearchText: " 求人 ")).Should().Equal(3);
        Ids(new TaskFilter(SearchText: "ない")).Should().BeEmpty();
    }

    [Fact]
    public void Combination_AppliesAllConditions()
    {
        var f = new TaskFilter(ProjectId: 20, LabelIds: new HashSet<int> { 2 }, Due: DueFilter.Today, SearchText: "会場");
        Ids(f).Should().Equal(2);
    }

    [Fact]
    public void WeekOf_StartsMonday()
    {
        TaskFilter.WeekOf(new DateOnly(2026, 9, 4)).Should().Be((new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 6)));
        TaskFilter.WeekOf(new DateOnly(2026, 9, 6)).Should().Be((new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 6)));
        TaskFilter.WeekOf(new DateOnly(2026, 9, 7)).Should().Be((new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 13)));
    }

    [Fact]
    public void DueStatuses_Of()
    {
        DueStatuses.Of(Tasks[0], Today).Should().Be(DueStatus.Upcoming);
        DueStatuses.Of(Tasks[1], Today).Should().Be(DueStatus.Today);
        DueStatuses.Of(Tasks[2], Today).Should().Be(DueStatus.Overdue);
        DueStatuses.Of(Tasks[3], Today).Should().Be(DueStatus.Upcoming, because: "完了済みは超過扱いにしない");
        DueStatuses.Of(Tasks[4], Today).Should().Be(DueStatus.None);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Core.Tests --filter "FullyQualifiedName~TaskFilterTests"`
Expected: コンパイルエラー（`TaskFilter` 未定義）

- [ ] **Step 3: 実装する**

`src/MoTask.Core/Filtering/DueFilter.cs`:

```csharp
namespace MoTask.Core.Filtering;

public enum DueFilter
{
    All = 0,
    Today = 1,
    ThisWeek = 2,
    Overdue = 3,
}
```

`src/MoTask.Core/Filtering/DueStatus.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Filtering;

public enum DueStatus
{
    None = 0,
    Upcoming = 1,
    Today = 2,
    Overdue = 3,
}

public static class DueStatuses
{
    /// <summary>カードの期限表示色の判定。完了済みタスクは超過扱いにしない。</summary>
    public static DueStatus Of(TaskItem task, DateOnly today)
    {
        if (task.DueDate is not DateOnly due) return DueStatus.None;
        if (due == today) return DueStatus.Today;
        if (due < today && task.CompletedAt is null) return DueStatus.Overdue;
        return DueStatus.Upcoming;
    }
}
```

`src/MoTask.Core/Filtering/TaskFilter.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Core.Filtering;

/// <summary>
/// フィルタ条件と、それを適用する純関数。ViewModel とテストが同じロジックを使う。
/// LabelIds は AND（すべて付いているタスクだけ）。null または空は条件なし。
/// ShowDeleted=false で削除済みを隠す。true では削除済みも生きているものも表示する。
/// </summary>
public sealed record TaskFilter(
    int? ProjectId = null,
    IReadOnlySet<int>? LabelIds = null,
    DueFilter Due = DueFilter.All,
    string SearchText = "",
    bool ShowDeleted = false)
{
    public static readonly TaskFilter None = new();

    public IEnumerable<TaskItem> Apply(IEnumerable<TaskItem> tasks, DateOnly today)
    {
        var search = SearchText.Trim();
        var (weekStart, weekEnd) = WeekOf(today);

        foreach (var task in tasks)
        {
            if (!ShowDeleted && task.IsDeleted) continue;
            if (ProjectId is int projectId && task.ProjectId != projectId) continue;
            if (LabelIds is { Count: > 0 } ids && !ids.All(id => task.Labels.Any(l => l.Id == id))) continue;
            if (!MatchesDue(task, today, weekStart, weekEnd)) continue;
            if (search.Length > 0 && !Contains(task.Title, search) && !Contains(task.Description, search)) continue;
            yield return task;
        }
    }

    /// <summary>月曜始まり・日曜終わりの週。</summary>
    public static (DateOnly Start, DateOnly End) WeekOf(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        var start = day.AddDays(-offset);
        return (start, start.AddDays(6));
    }

    private bool MatchesDue(TaskItem task, DateOnly today, DateOnly weekStart, DateOnly weekEnd) => Due switch
    {
        DueFilter.All => true,
        DueFilter.Today => task.DueDate == today,
        DueFilter.ThisWeek => task.DueDate is DateOnly d && d >= weekStart && d <= weekEnd,
        DueFilter.Overdue => task.DueDate is DateOnly d && d < today && task.CompletedAt is null,
        _ => true,
    };

    private static bool Contains(string text, string search)
        => text.Contains(search, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 64`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Core tests/MoTask.Core.Tests
git commit -m "feat(core): add TaskFilter and DueStatus

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: DbContext・マイグレーション・DB パス

**Files:**
- Create: `src/MoTask.Data/TaskLabel.cs`, `MoTaskDbContext.cs`, `MoTaskDbContextFactory.cs`, `DbPaths.cs`
- Create: `src/MoTask.Data/Migrations/*`（`dotnet ef` が生成）
- Create: `tests/MoTask.Data.Tests/SqliteTestDatabase.cs`
- Test: `tests/MoTask.Data.Tests/MigrationTests.cs`

**Interfaces:**
- Produces: `MoTaskDbContext`（`Boards`, `Columns`, `Tasks`, `Projects`, `Labels`, `History`）、`MoTaskDbContextOptions.Create(connectionString)`、`DbPaths.DefaultDirectory / DefaultDatabase / ConnectionString(path)`。

- [ ] **Step 1: テストヘルパーと失敗するテストを書く**

`tests/MoTask.Data.Tests/SqliteTestDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MoTask.Data.Tests;

/// <summary>テストごとに一時ファイルの SQLite を作り、Dispose で消す。</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public string Path { get; }

    public SqliteTestDatabase()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MoTaskTests");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.db");
    }

    public MoTaskDbContext CreateContext()
        => new(MoTaskDbContextOptions.Create(DbPaths.ConnectionString(Path)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = Path + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
```

`tests/MoTask.Data.Tests/MigrationTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Data.Tests;

public class MigrationTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task Migrate_OnEmptyFile_CreatesSchema()
    {
        await using var ctx = _db.CreateContext();

        await ctx.Database.MigrateAsync();

        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        tables.Should().Contain(new[] { "Boards", "Columns", "Tasks", "Projects", "Labels", "TaskLabels", "History" });
    }

    [Fact]
    public async Task Model_RoundTrips_DateOnlyAndEnumsAndLabels()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "b" };
            var column = new Column { Name = "c", Role = ColumnRole.Review, Order = 0 };
            board.Columns.Add(column);
            var label = new Label { Name = "至急", Color = "accent-500" };
            var task = new TaskItem
            {
                Title = "t", DueDate = new DateOnly(2026, 9, 10),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            task.Labels.Add(label);
            column.Tasks.Add(task);
            ctx.Boards.Add(board);
            ctx.History.Add(new HistoryEntry { Task = task, At = DateTime.UtcNow, Kind = HistoryKind.Created });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var task = await ctx.Tasks.Include(t => t.Labels).SingleAsync();
            task.DueDate.Should().Be(new DateOnly(2026, 9, 10));
            task.Labels.Should().ContainSingle().Which.Name.Should().Be("至急");
            (await ctx.Columns.SingleAsync()).Role.Should().Be(ColumnRole.Review);
            var history = await ctx.History.SingleAsync();
            history.TaskId.Should().Be(task.Id);
            history.Kind.Should().Be(HistoryKind.Created);
            history.Detail.Should().BeEmpty();
        }
    }

    [Fact]
    public void DbPaths_PointToLocalAppData()
    {
        DbPaths.DefaultDirectory.Should().EndWith("MoTask");
        DbPaths.DefaultDatabase.Should().EndWith(Path.Combine("MoTask", "motask.db"));
        DbPaths.ConnectionString(@"C:\x\y.db").Should().Contain("y.db");
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: コンパイルエラー（`MoTaskDbContext` 未定義）

- [ ] **Step 3: DbContext と周辺を書く**

`src/MoTask.Data/TaskLabel.cs`:

```csharp
namespace MoTask.Data;

/// <summary>Task と Label の多対多の結合エンティティ。Core には出さない。</summary>
public sealed class TaskLabel
{
    public int TaskId { get; set; }
    public int LabelId { get; set; }
}
```

`src/MoTask.Data/MoTaskDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;

namespace MoTask.Data;

public sealed class MoTaskDbContext : DbContext
{
    public MoTaskDbContext(DbContextOptions<MoTaskDbContext> options) : base(options)
    {
    }

    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Board>(e =>
        {
            e.ToTable("Boards");
            e.Property(x => x.Name).IsRequired();
            e.HasMany(x => x.Columns).WithOne().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Column>(e =>
        {
            e.ToTable("Columns");
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.BoardId, x.Order });
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.Property(x => x.Title).IsRequired();
            e.Property(x => x.Description).IsRequired().HasDefaultValue("");
            e.HasIndex(x => new { x.ColumnId, x.Position });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Labels).WithMany().UsingEntity<TaskLabel>(
                right => right.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("TaskLabels");
                    join.HasKey(x => new { x.TaskId, x.LabelId });
                });
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("Projects");
            e.Property(x => x.Name).IsRequired();
        });

        b.Entity<Label>(e =>
        {
            e.ToTable("Labels");
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Color).IsRequired();
        });

        b.Entity<HistoryEntry>(e =>
        {
            e.ToTable("History");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Detail).IsRequired().HasDefaultValue("");
            e.HasIndex(x => x.TaskId);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public static class MoTaskDbContextOptions
{
    public static DbContextOptions<MoTaskDbContext> Create(string connectionString)
        => new DbContextOptionsBuilder<MoTaskDbContext>().UseSqlite(connectionString).Options;
}
```

`src/MoTask.Data/MoTaskDbContextFactory.cs`（`dotnet ef` がマイグレーションを作るときに使う）:

```csharp
using Microsoft.EntityFrameworkCore.Design;

namespace MoTask.Data;

public sealed class MoTaskDbContextFactory : IDesignTimeDbContextFactory<MoTaskDbContext>
{
    public MoTaskDbContext CreateDbContext(string[] args)
        => new(MoTaskDbContextOptions.Create("Data Source=design-time.db"));
}
```

`src/MoTask.Data/DbPaths.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MoTask.Data;

public static class DbPaths
{
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoTask");

    public static string DefaultDatabase => Path.Combine(DefaultDirectory, "motask.db");

    public static string ConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
}
```

- [ ] **Step 4: マイグレーションを生成する**

```bash
dotnet build src/MoTask.Data
dotnet ef migrations add Initial --project src/MoTask.Data --output-dir Migrations
```

Expected: `src/MoTask.Data/Migrations/` に `<timestamp>_Initial.cs`, `<timestamp>_Initial.Designer.cs`, `MoTaskDbContextModelSnapshot.cs` が生成される。生成された `Initial.cs` の `Up` に `Boards`, `Columns`, `Tasks`, `Projects`, `Labels`, `TaskLabels`, `History` の `CreateTable` があることを目視確認。`UsingEntity` の引数順で型エラーが出る場合は `right` と `left` のラムダを入れ替える。

- [ ] **Step 5: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: コミット**

```bash
git add src/MoTask.Data tests/MoTask.Data.Tests
git commit -m "feat(data): add EF Core DbContext, initial migration and DB paths

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: 初期化（マイグレーション適用と既定ボード投入）

**Files:**
- Create: `src/MoTask.Data/DefaultBoard.cs`, `DatabaseInitializer.cs`
- Test: `tests/MoTask.Data.Tests/InitializerTests.cs`

**Interfaces:**
- Produces: `DatabaseInitializer(MoTaskDbContext).InitializeAsync(ct)`、`DefaultBoard.Create()`。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/InitializerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Data.Tests;

public class InitializerTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task Initialize_OnEmptyDb_SeedsDefaultBoardAndFourColumns()
    {
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
            ctx.ChangeTracker.Entries().Should().BeEmpty("投入後に追跡をクリアする");
        }

        await using (var ctx = _db.CreateContext())
        {
            var board = await ctx.Boards.Include(b => b.Columns.OrderBy(c => c.Order)).SingleAsync();
            board.Name.Should().Be(DefaultBoard.Name);
            board.Columns.Select(c => (c.Name, c.Role, c.Order)).Should().Equal(
                ("未着手", ColumnRole.Backlog, 0),
                ("進行中", ColumnRole.Active, 1),
                ("確認待ち", ColumnRole.Review, 2),
                ("完了", ColumnRole.Done, 3));
            board.Columns.Should().OnlyContain(c => c.WipLimit == null);
        }
    }

    [Fact]
    public async Task Initialize_Twice_DoesNotDuplicateBoard()
    {
        await using var ctx = _db.CreateContext();
        var initializer = new DatabaseInitializer(ctx);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        (await ctx.Boards.CountAsync()).Should().Be(1);
        (await ctx.Columns.CountAsync()).Should().Be(4);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Data.Tests --filter "FullyQualifiedName~InitializerTests"`
Expected: コンパイルエラー

- [ ] **Step 3: 実装する**

`src/MoTask.Data/DefaultBoard.cs`:

```csharp
using MoTask.Core.Model;

namespace MoTask.Data;

public static class DefaultBoard
{
    public const string Name = "マイボード";

    public static Board Create() => new()
    {
        Name = Name,
        Columns =
        {
            new Column { Name = "未着手", Order = 0, Role = ColumnRole.Backlog },
            new Column { Name = "進行中", Order = 1, Role = ColumnRole.Active },
            new Column { Name = "確認待ち", Order = 2, Role = ColumnRole.Review },
            new Column { Name = "完了", Order = 3, Role = ColumnRole.Done },
        },
    };
}
```

`src/MoTask.Data/DatabaseInitializer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace MoTask.Data;

/// <summary>起動時に呼ぶ。マイグレーションを適用し、ボードが無ければ既定のボードと4列を投入する。</summary>
public sealed class DatabaseInitializer
{
    private readonly MoTaskDbContext _db;

    public DatabaseInitializer(MoTaskDbContext db)
    {
        _db = db;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);
        if (!await _db.Boards.AnyAsync(ct))
        {
            _db.Boards.Add(DefaultBoard.Create());
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
}
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Data tests/MoTask.Data.Tests
git commit -m "feat(data): add DatabaseInitializer with default board seed

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: リポジトリ実装・UnitOfWork・DI 登録・同一トランザクション検証

**Files:**
- Create: `src/MoTask.Data/Repositories/BoardRepository.cs`, `HistoryRepository.cs`, `EfUnitOfWork.cs`
- Create: `src/MoTask.Data/ServiceCollectionExtensions.cs`
- Test: `tests/MoTask.Data.Tests/RepositoryTests.cs`, `TransactionTests.cs`

**Interfaces:**
- Consumes: Core の `IBoardRepository`, `IHistoryRepository`, `IUnitOfWork`, `PersistenceException`, `BoardService`。
- Produces: `services.AddMoTaskData(dbPath)`（`MoTaskDbContext` をシングルトン、リポジトリ3種と `DatabaseInitializer` を登録）。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/RepositoryTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class RepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task GetBoard_ReturnsColumnsByOrder_AndTasksByPosition_IncludingDeleted()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "並び確認" };
            var second = new Column { Name = "二番目", Order = 1 };
            var first = new Column { Name = "一番目", Order = 0 };
            board.Columns.Add(second);
            board.Columns.Add(first);
            var now = DateTime.UtcNow;
            first.Tasks.Add(new TaskItem { Title = "p2", Position = 2, CreatedAt = now, UpdatedAt = now });
            first.Tasks.Add(new TaskItem { Title = "p0", Position = 0, CreatedAt = now, UpdatedAt = now });
            first.Tasks.Add(new TaskItem { Title = "p1-deleted", Position = 1, CreatedAt = now, UpdatedAt = now, DeletedAt = now });
            ctx.Boards.Add(board);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new BoardRepository(ctx);
            var board = (await repo.GetBoardAsync())!;
            board.Columns.Select(c => c.Name).Should().Equal("一番目", "二番目");
            board.Columns[0].Tasks.Select(t => t.Title).Should().Equal("p0", "p1-deleted", "p2");
        }
    }

    [Fact]
    public async Task BoardService_OverSqlite_CreatesAndMovesTasks_PersistingHistory()
    {
        await using var services = new ServiceCollection()
            .AddMoTaskData(_db.Path)
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBoardService, BoardService>()
            .BuildServiceProvider();

        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        var service = services.GetRequiredService<IBoardService>();
        var board = (await service.GetBoardAsync()).Value!;
        var backlog = board.Columns.Single(c => c.Role == ColumnRole.Backlog);
        var done = board.Columns.Single(c => c.Role == ColumnRole.Done);

        var created = await service.CreateTaskAsync(backlog.Id, "永続化テスト");
        created.IsSuccess.Should().BeTrue(created.Error);
        var moved = await service.MoveTaskAsync(created.Value!.Id, done.Id, 0);
        moved.IsSuccess.Should().BeTrue(moved.Error);

        await using var ctx = _db.CreateContext();
        var task = await ctx.Tasks.SingleAsync();
        task.ColumnId.Should().Be(done.Id);
        task.CompletedAt.Should().NotBeNull();
        (await ctx.History.Where(h => h.TaskId == task.Id).OrderBy(h => h.Id).Select(h => h.Kind).ToListAsync())
            .Should().Equal(HistoryKind.Created, HistoryKind.Moved);
    }

    public void Dispose() => _db.Dispose();
}
```

`tests/MoTask.Data.Tests/TransactionTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class TransactionTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task SaveChanges_WhenHistoryInsertFails_RollsBackTaskChange_AndClearsTracker()
    {
        int taskId;
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
            var column = await ctx.Columns.FirstAsync();
            var now = DateTime.UtcNow;
            var task = new TaskItem { Title = "元", ColumnId = column.Id, CreatedAt = now, UpdatedAt = now };
            ctx.Tasks.Add(task);
            await ctx.SaveChangesAsync();
            taskId = task.Id;
        }

        await using (var ctx = _db.CreateContext())
        {
            var boards = new BoardRepository(ctx);
            var history = new HistoryRepository(ctx);
            var uow = new EfUnitOfWork(ctx);

            var task = (await boards.GetTaskAsync(taskId))!;
            task.Title = "変更後";
            // 存在しないタスクを指す履歴 → 外部キー違反で SaveChanges が失敗する
            history.Add(new HistoryEntry { TaskId = 999_999, At = DateTime.UtcNow, Kind = HistoryKind.Edited, Detail = "{}" });

            var act = () => uow.SaveChangesAsync();

            await act.Should().ThrowAsync<PersistenceException>();
            ctx.ChangeTracker.Entries().Should().BeEmpty("失敗後は未保存の変更を捨てる");
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.Tasks.SingleAsync(t => t.Id == taskId)).Title.Should().Be("元");
            (await ctx.History.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task HistoryRepository_ReturnsNewestFirst()
    {
        await using var ctx = _db.CreateContext();
        await new DatabaseInitializer(ctx).InitializeAsync();
        var column = await ctx.Columns.FirstAsync();
        var task = new TaskItem { Title = "t", ColumnId = column.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.Tasks.Add(task);
        var t0 = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        ctx.History.Add(new HistoryEntry { Task = task, At = t0, Kind = HistoryKind.Created });
        ctx.History.Add(new HistoryEntry { Task = task, At = t0.AddMinutes(1), Kind = HistoryKind.Moved });
        await ctx.SaveChangesAsync();

        var list = await new HistoryRepository(ctx).GetForTaskAsync(task.Id);

        list.Select(h => h.Kind).Should().Equal(HistoryKind.Moved, HistoryKind.Created);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: コンパイルエラー（`BoardRepository` 未定義）

- [ ] **Step 3: 実装する**

`src/MoTask.Data/Repositories/BoardRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class BoardRepository : IBoardRepository
{
    private readonly MoTaskDbContext _db;

    public BoardRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public Task<Board?> GetBoardAsync(CancellationToken ct = default)
        => _db.Boards
            .Include(b => b.Columns.OrderBy(c => c.Order))
            .ThenInclude(c => c.Tasks.OrderBy(t => t.Position))
            .ThenInclude(t => t.Labels)
            .FirstOrDefaultAsync(ct);

    public Task<Column?> GetColumnAsync(int columnId, CancellationToken ct = default)
        => _db.Columns
            .Include(c => c.Tasks.OrderBy(t => t.Position))
            .ThenInclude(t => t.Labels)
            .FirstOrDefaultAsync(c => c.Id == columnId, ct);

    public Task<TaskItem?> GetTaskAsync(int taskId, CancellationToken ct = default)
        => _db.Tasks.Include(t => t.Labels).FirstOrDefaultAsync(t => t.Id == taskId, ct);

    public Task<Project?> GetProjectAsync(int projectId, CancellationToken ct = default)
        => _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => await _db.Projects.OrderBy(p => p.Name).ToListAsync(ct);

    public Task<Label?> GetLabelAsync(int labelId, CancellationToken ct = default)
        => _db.Labels.FirstOrDefaultAsync(l => l.Id == labelId, ct);

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default)
        => await _db.Labels.OrderBy(l => l.Name).ToListAsync(ct);

    public void AddColumn(Column column) => _db.Columns.Add(column);
    public void RemoveColumn(Column column) => _db.Columns.Remove(column);
    public void AddTask(TaskItem task) => _db.Tasks.Add(task);
    public void AddProject(Project project) => _db.Projects.Add(project);
    public void AddLabel(Label label) => _db.Labels.Add(label);
}
```

`src/MoTask.Data/Repositories/HistoryRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.Data.Repositories;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly MoTaskDbContext _db;

    public HistoryRepository(MoTaskDbContext db)
    {
        _db = db;
    }

    public void Add(HistoryEntry entry) => _db.History.Add(entry);

    public async Task<IReadOnlyList<HistoryEntry>> GetForTaskAsync(int taskId, CancellationToken ct = default)
        => await _db.History.AsNoTracking()
            .Where(h => h.TaskId == taskId)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .ToListAsync(ct);
}
```

`src/MoTask.Data/Repositories/EfUnitOfWork.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;

namespace MoTask.Data.Repositories;

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly MoTaskDbContext _db;

    public EfUnitOfWork(MoTaskDbContext db)
    {
        _db = db;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException)
        {
            // 失敗した変更を追跡から捨て、次の GetBoard で DB の状態を読み直せるようにする
            _db.ChangeTracker.Clear();
            throw new PersistenceException(ex.GetBaseException().Message, ex);
        }
    }
}
```

`src/MoTask.Data/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Data.Repositories;

namespace MoTask.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 単一ユーザーのデスクトップアプリなので DbContext はシングルトン。
    /// BoardService 側で操作を直列化しているため同時アクセスは起きない。
    /// </summary>
    public static IServiceCollection AddMoTaskData(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<MoTaskDbContext>(
            o => o.UseSqlite(DbPaths.ConnectionString(dbPath)),
            contextLifetime: ServiceLifetime.Singleton,
            optionsLifetime: ServiceLifetime.Singleton);
        services.AddSingleton<IBoardRepository, BoardRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: `Passed! - Failed: 0, Passed: 9`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Data tests/MoTask.Data.Tests
git commit -m "feat(data): implement repositories, unit of work and DI registration

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: DB 復旧（バックアップして再作成）

**Files:**
- Create: `src/MoTask.Data/DatabaseRecovery.cs`
- Test: `tests/MoTask.Data.Tests/RecoveryTests.cs`

**Interfaces:**
- Produces: `DatabaseRecovery.BackupAndReset(string dbPath, DateTime now)` → バックアップ先パス。App 起動時（Task 20）が使う。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.Data.Tests/RecoveryTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace MoTask.Data.Tests;

public class RecoveryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public void BackupAndReset_CopiesToTimestampedBak_AndRemovesOriginalAndSidecars()
    {
        File.WriteAllText(_db.Path, "broken");
        File.WriteAllText(_db.Path + "-wal", "wal");
        File.WriteAllText(_db.Path + "-shm", "shm");

        var backup = DatabaseRecovery.BackupAndReset(_db.Path, new DateTime(2026, 9, 4, 8, 40, 5));

        backup.Should().Be(_db.Path + ".bak-20260904-084005");
        File.ReadAllText(backup).Should().Be("broken");
        File.Exists(_db.Path).Should().BeFalse();
        File.Exists(_db.Path + "-wal").Should().BeFalse();
        File.Exists(_db.Path + "-shm").Should().BeFalse();

        File.Delete(backup);
    }

    [Fact]
    public void BackupAndReset_WhenNoFile_ReturnsPathWithoutCreatingBackup()
    {
        var backup = DatabaseRecovery.BackupAndReset(_db.Path, new DateTime(2026, 9, 4, 8, 40, 5));
        File.Exists(backup).Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.Data.Tests --filter "FullyQualifiedName~RecoveryTests"`
Expected: コンパイルエラー

- [ ] **Step 3: 実装する**

`src/MoTask.Data/DatabaseRecovery.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MoTask.Data;

public static class DatabaseRecovery
{
    /// <summary>
    /// 壊れた DB を `motask.db.bak-yyyyMMdd-HHmmss` にコピーしてから、本体と WAL/SHM を削除する。
    /// 呼び出し前に DbContext を破棄しておくこと。戻り値はバックアップ先のパス。
    /// </summary>
    public static string BackupAndReset(string dbPath, DateTime now)
    {
        SqliteConnection.ClearAllPools();
        var backup = $"{dbPath}.bak-{now:yyyyMMdd-HHmmss}";
        if (File.Exists(dbPath))
        {
            File.Copy(dbPath, backup, overwrite: true);
        }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
        return backup;
    }
}
```

- [ ] **Step 4: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.Data.Tests`
Expected: `Passed! - Failed: 0, Passed: 11`

- [ ] **Step 5: コミット**

```bash
git add src/MoTask.Data tests/MoTask.Data.Tests
git commit -m "feat(data): add DatabaseRecovery backup-and-reset

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: App の骨組み（フォント・テーマ・文言・ホスト・空の MainWindow）

**Files:**
- Create: `src/MoTask.App/Fonts/Barlow-Regular.ttf`, `Barlow-Medium.ttf`, `Barlow-Bold.ttf`, `BarlowCondensed-Regular.ttf`, `BarlowCondensed-SemiBold.ttf`, `OFL.txt`
- Create: `src/MoTask.App/Themes/Industry.xaml`, `Themes/Controls.xaml`
- Create: `src/MoTask.App/Resources/Strings.resx`, `Resources/Strings.cs`
- Create: `src/MoTask.App/Converters/RampBrushConverter.cs`, `DueStatusBrushConverter.cs`, `BoolToVisibilityConverter.cs`, `NullToVisibilityConverter.cs`
- Modify: `src/MoTask.App/App.xaml`, `App.xaml.cs`
- Move: `src/MoTask.App/MainWindow.xaml(.cs)` → `src/MoTask.App/Views/MainWindow.xaml(.cs)`
- Test: `tests/MoTask.App.Tests/StringsTests.cs`

**Interfaces:**
- Produces: リソースキー `Brush.Bg/Surface/Text/TextMuted/Accent/Divider/Danger`, `Brush.Neutral.100..900`, `Brush.Accent.100..900`, `Font.Heading/Font.Body`, `FontSize.Body(15)/Small(13)/Caption(11)/Control(14)/H4(20)/H5(16)`, スタイル `Btn.Primary`, `Btn.Ghost`, `Chip.Toggle`, `Text.Heading/Brand/Small/Caption/Label`, `Focus.Ring`; コンバータ `RampBrush`, `DueStatusBrush`, `BoolToVisibility`, `BoolToVisibilityInverse`, `NullToVisibility`; `Strings.*`。`App.BuildHost` は Task 17 で `BoardViewModel` を追加登録する。

- [ ] **Step 1: フォントを取得する**

```bash
cd src/MoTask.App/Fonts && rm -f .gitkeep
for f in Barlow-Regular Barlow-Medium Barlow-Bold; do curl -sSL -o $f.ttf https://raw.githubusercontent.com/google/fonts/main/ofl/barlow/$f.ttf; done
for f in BarlowCondensed-Regular BarlowCondensed-SemiBold; do curl -sSL -o $f.ttf https://raw.githubusercontent.com/google/fonts/main/ofl/barlowcondensed/$f.ttf; done
curl -sSL -o OFL.txt https://raw.githubusercontent.com/google/fonts/main/ofl/barlow/OFL.txt
ls -la
```

Expected: 5 つの `.ttf`（各 100KB 前後）と `OFL.txt`。

- [ ] **Step 2: テーマトークンを書く**

`src/MoTask.App/Themes/Industry.xaml`（`_ds/.../styles.css` の `:root` を移植）:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">

  <!-- 色（styles.css :root） -->
  <Color x:Key="Color.Bg">#F2F2F3</Color>
  <Color x:Key="Color.Surface">#E9E9EA</Color>
  <Color x:Key="Color.Text">#1D1F20</Color>
  <Color x:Key="Color.Accent">#5980A6</Color>
  <Color x:Key="Color.Divider">#291D1F20</Color>
  <Color x:Key="Color.Danger">#B3261E</Color>

  <Color x:Key="Color.Neutral.100">#F5F5F8</Color>
  <Color x:Key="Color.Neutral.200">#E7E7EA</Color>
  <Color x:Key="Color.Neutral.300">#D4D4D7</Color>
  <Color x:Key="Color.Neutral.400">#B7B7BA</Color>
  <Color x:Key="Color.Neutral.500">#98989B</Color>
  <Color x:Key="Color.Neutral.600">#7A7A7D</Color>
  <Color x:Key="Color.Neutral.700">#5D5D60</Color>
  <Color x:Key="Color.Neutral.800">#424244</Color>
  <Color x:Key="Color.Neutral.900">#2B2B2D</Color>

  <Color x:Key="Color.Accent.100">#EEF6FF</Color>
  <Color x:Key="Color.Accent.200">#D6EBFF</Color>
  <Color x:Key="Color.Accent.300">#B5D9FD</Color>
  <Color x:Key="Color.Accent.400">#94BCE3</Color>
  <Color x:Key="Color.Accent.500">#749DC4</Color>
  <Color x:Key="Color.Accent.600">#597EA3</Color>
  <Color x:Key="Color.Accent.700">#416180</Color>
  <Color x:Key="Color.Accent.800">#2C455D</Color>
  <Color x:Key="Color.Accent.900">#1D2D3D</Color>

  <SolidColorBrush x:Key="Brush.Bg" Color="{StaticResource Color.Bg}" />
  <SolidColorBrush x:Key="Brush.Surface" Color="{StaticResource Color.Surface}" />
  <SolidColorBrush x:Key="Brush.Text" Color="{StaticResource Color.Text}" />
  <SolidColorBrush x:Key="Brush.TextMuted" Color="{StaticResource Color.Neutral.600}" />
  <SolidColorBrush x:Key="Brush.Accent" Color="{StaticResource Color.Accent}" />
  <SolidColorBrush x:Key="Brush.Divider" Color="{StaticResource Color.Divider}" />
  <SolidColorBrush x:Key="Brush.Danger" Color="{StaticResource Color.Danger}" />

  <SolidColorBrush x:Key="Brush.Neutral.100" Color="{StaticResource Color.Neutral.100}" />
  <SolidColorBrush x:Key="Brush.Neutral.200" Color="{StaticResource Color.Neutral.200}" />
  <SolidColorBrush x:Key="Brush.Neutral.300" Color="{StaticResource Color.Neutral.300}" />
  <SolidColorBrush x:Key="Brush.Neutral.400" Color="{StaticResource Color.Neutral.400}" />
  <SolidColorBrush x:Key="Brush.Neutral.500" Color="{StaticResource Color.Neutral.500}" />
  <SolidColorBrush x:Key="Brush.Neutral.600" Color="{StaticResource Color.Neutral.600}" />
  <SolidColorBrush x:Key="Brush.Neutral.700" Color="{StaticResource Color.Neutral.700}" />
  <SolidColorBrush x:Key="Brush.Neutral.800" Color="{StaticResource Color.Neutral.800}" />
  <SolidColorBrush x:Key="Brush.Neutral.900" Color="{StaticResource Color.Neutral.900}" />

  <SolidColorBrush x:Key="Brush.Accent.100" Color="{StaticResource Color.Accent.100}" />
  <SolidColorBrush x:Key="Brush.Accent.200" Color="{StaticResource Color.Accent.200}" />
  <SolidColorBrush x:Key="Brush.Accent.300" Color="{StaticResource Color.Accent.300}" />
  <SolidColorBrush x:Key="Brush.Accent.400" Color="{StaticResource Color.Accent.400}" />
  <SolidColorBrush x:Key="Brush.Accent.500" Color="{StaticResource Color.Accent.500}" />
  <SolidColorBrush x:Key="Brush.Accent.600" Color="{StaticResource Color.Accent.600}" />
  <SolidColorBrush x:Key="Brush.Accent.700" Color="{StaticResource Color.Accent.700}" />
  <SolidColorBrush x:Key="Brush.Accent.800" Color="{StaticResource Color.Accent.800}" />
  <SolidColorBrush x:Key="Brush.Accent.900" Color="{StaticResource Color.Accent.900}" />

  <!-- 間隔（--space-*） -->
  <sys:Double x:Key="Space.1">3.4</sys:Double>
  <sys:Double x:Key="Space.2">6.8</sys:Double>
  <sys:Double x:Key="Space.3">10.2</sys:Double>
  <sys:Double x:Key="Space.4">13.6</sys:Double>
  <sys:Double x:Key="Space.6">20.4</sys:Double>
  <sys:Double x:Key="Space.8">27.2</sys:Double>

  <!-- 字体 -->
  <FontFamily x:Key="Font.Heading">pack://application:,,,/Fonts/#Barlow Condensed, Noto Sans JP, Yu Gothic UI</FontFamily>
  <FontFamily x:Key="Font.Body">pack://application:,,,/Fonts/#Barlow, Noto Sans JP, Yu Gothic UI</FontFamily>
  <sys:Double x:Key="FontSize.Body">15</sys:Double>
  <sys:Double x:Key="FontSize.Control">14</sys:Double>
  <sys:Double x:Key="FontSize.Small">13</sys:Double>
  <sys:Double x:Key="FontSize.Caption">11</sys:Double>
  <sys:Double x:Key="FontSize.H4">20</sys:Double>
  <sys:Double x:Key="FontSize.H5">16</sys:Double>
</ResourceDictionary>
```

- [ ] **Step 3: コンバータを書く**

`src/MoTask.App/Converters/RampBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MoTask.Core.Model;

namespace MoTask.App.Converters;

/// <summary>"accent-300" のようなランプ名を "Brush.Accent.300" リソースに解決する。</summary>
public sealed class RampBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? Label.DefaultColor;
        var parts = name.Split('-');
        var key = parts.Length == 2 && parts[1].Length > 0
            ? $"Brush.{char.ToUpperInvariant(parts[0][0])}{parts[0][1..]}.{parts[1]}"
            : "Brush.Accent.300";
        return Application.Current?.TryFindResource(key) as Brush
               ?? Application.Current?.TryFindResource("Brush.Accent.300") as Brush
               ?? Brushes.LightSteelBlue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
```

`src/MoTask.App/Converters/DueStatusBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MoTask.Core.Filtering;

namespace MoTask.App.Converters;

/// <summary>期限超過は赤、当日はアクセント、それ以外は控えめな文字色。</summary>
public sealed class DueStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DueStatus.Overdue => "Brush.Danger",
            DueStatus.Today => "Brush.Accent",
            _ => "Brush.TextMuted",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
```

`src/MoTask.App/Converters/BoolToVisibilityConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MoTask.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
```

`src/MoTask.App/Converters/NullToVisibilityConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MoTask.App.Converters;

/// <summary>null または空文字なら Collapsed。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value is string s && s.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
```

- [ ] **Step 4: コントロールのスタイルを書く**

`src/MoTask.App/Themes/Controls.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:conv="clr-namespace:MoTask.App.Converters">

  <conv:RampBrushConverter x:Key="RampBrush" />
  <conv:DueStatusBrushConverter x:Key="DueStatusBrush" />
  <conv:BoolToVisibilityConverter x:Key="BoolToVisibility" />
  <conv:BoolToVisibilityConverter x:Key="BoolToVisibilityInverse" Invert="True" />
  <conv:NullToVisibilityConverter x:Key="NullToVisibility" />

  <!-- キーボードフォーカス: 2px のアクセント枠 -->
  <Style x:Key="Focus.Ring">
    <Setter Property="Control.Template">
      <Setter.Value>
        <ControlTemplate>
          <Rectangle Margin="-2" Stroke="{StaticResource Brush.Accent}" StrokeThickness="2" SnapsToDevicePixels="True" />
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 文字 -->
  <Style x:Key="Text.Heading" TargetType="TextBlock">
    <Setter Property="FontFamily" Value="{StaticResource Font.Heading}" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.H5}" />
  </Style>
  <Style x:Key="Text.Brand" TargetType="TextBlock" BasedOn="{StaticResource Text.Heading}">
    <Setter Property="FontSize" Value="{StaticResource FontSize.H4}" />
    <Setter Property="Foreground" Value="{StaticResource Brush.Accent.700}" />
  </Style>
  <Style x:Key="Text.Small" TargetType="TextBlock">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
    <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
  </Style>
  <Style x:Key="Text.Caption" TargetType="TextBlock">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Caption}" />
    <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
  </Style>
  <Style x:Key="Text.Label" TargetType="TextBlock" BasedOn="{StaticResource Text.Caption}">
    <Setter Property="FontWeight" Value="Medium" />
  </Style>

  <!-- Button 既定 = secondary（塗りなし・1px 枠） -->
  <Style TargetType="Button">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{StaticResource Brush.Text}" />
    <Setter Property="BorderBrush" Value="{StaticResource Brush.Divider}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="10.2,5" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
    <Setter Property="FocusVisualStyle" Value="{StaticResource Focus.Ring}" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.100}" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.200}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.45" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 唯一の塗りあり: primary -->
  <Style x:Key="Btn.Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="{StaticResource Brush.Accent}" />
    <Setter Property="BorderBrush" Value="{StaticResource Brush.Accent}" />
    <Setter Property="Foreground" Value="{StaticResource Brush.Bg}" />
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.600}" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.700}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.45" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Btn.Ghost" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Foreground" Value="{StaticResource Brush.Accent.700}" />
    <Setter Property="Padding" Value="6.8,3.4" />
  </Style>

  <!-- TextBox -->
  <Style TargetType="TextBox">
    <Setter Property="Background" Value="{StaticResource Brush.Bg}" />
    <Setter Property="Foreground" Value="{StaticResource Brush.Text}" />
    <Setter Property="BorderBrush" Value="{StaticResource Brush.Divider}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="6.8,5" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
    <Setter Property="CaretBrush" Value="{StaticResource Brush.Accent}" />
    <Setter Property="SelectionBrush" Value="{StaticResource Brush.Accent.300}" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ScrollViewer x:Name="PART_ContentHost" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Brush.Neutral.600}" />
            </Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Brush.Accent}" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ラベル／フィルタ用チップ -->
  <Style x:Key="Chip.Toggle" TargetType="ToggleButton">
    <Setter Property="Foreground" Value="{StaticResource Brush.Text}" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
    <Setter Property="FocusVisualStyle" Value="{StaticResource Focus.Ring}" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Bd" Background="Transparent" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="1"
                  Padding="6.8,2" SnapsToDevicePixels="True">
            <ContentPresenter VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.100}" />
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{StaticResource Brush.Accent.200}" />
              <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Brush.Accent.400}" />
              <Setter Property="Foreground" Value="{StaticResource Brush.Accent.900}" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 既定テンプレートのまま字体だけ揃えるもの -->
  <Style TargetType="ComboBox">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
    <Setter Property="Padding" Value="6.8,4" />
    <Setter Property="FocusVisualStyle" Value="{StaticResource Focus.Ring}" />
  </Style>
  <Style TargetType="CheckBox">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
    <Setter Property="FocusVisualStyle" Value="{StaticResource Focus.Ring}" />
  </Style>
  <Style TargetType="DatePicker">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
  </Style>
  <Style TargetType="ContextMenu">
    <Setter Property="FontFamily" Value="{StaticResource Font.Body}" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.Control}" />
  </Style>
</ResourceDictionary>
```

- [ ] **Step 5: 文言 resx とラッパーを書く**

`src/MoTask.App/Resources/Strings.resx`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="AppTitle" xml:space="preserve"><value>MoTask</value></data>
  <data name="Brand" xml:space="preserve"><value>TASKS</value></data>
  <data name="ViewBoard" xml:space="preserve"><value>ボード</value></data>
  <data name="FilterAllProjects" xml:space="preserve"><value>すべてのプロジェクト</value></data>
  <data name="DueAll" xml:space="preserve"><value>期限: すべて</value></data>
  <data name="DueToday" xml:space="preserve"><value>期限: 今日</value></data>
  <data name="DueThisWeek" xml:space="preserve"><value>期限: 今週</value></data>
  <data name="DueOverdue" xml:space="preserve"><value>期限切れ</value></data>
  <data name="Search" xml:space="preserve"><value>検索</value></data>
  <data name="ShowDeleted" xml:space="preserve"><value>削除済みを表示</value></data>
  <data name="NewTask" xml:space="preserve"><value>＋ タスク</value></data>
  <data name="AddColumn" xml:space="preserve"><value>＋ 列を追加</value></data>
  <data name="AddTaskInline" xml:space="preserve"><value>＋ 追加</value></data>
  <data name="Rename" xml:space="preserve"><value>改名</value></data>
  <data name="ChangeRole" xml:space="preserve"><value>種別を変更</value></data>
  <data name="SetWip" xml:space="preserve"><value>WIP 制限を設定</value></data>
  <data name="ClearWip" xml:space="preserve"><value>WIP 制限をなくす</value></data>
  <data name="DeleteColumn" xml:space="preserve"><value>列を削除</value></data>
  <data name="RoleBacklog" xml:space="preserve"><value>未着手</value></data>
  <data name="RoleActive" xml:space="preserve"><value>進行中</value></data>
  <data name="RoleReview" xml:space="preserve"><value>確認待ち</value></data>
  <data name="RoleDone" xml:space="preserve"><value>完了</value></data>
  <data name="Delete" xml:space="preserve"><value>削除</value></data>
  <data name="Restore" xml:space="preserve"><value>復元</value></data>
  <data name="Close" xml:space="preserve"><value>閉じる</value></data>
  <data name="Deleted" xml:space="preserve"><value>削除済み</value></data>
  <data name="Description" xml:space="preserve"><value>説明</value></data>
  <data name="Project" xml:space="preserve"><value>プロジェクト</value></data>
  <data name="Labels" xml:space="preserve"><value>ラベル</value></data>
  <data name="DueDate" xml:space="preserve"><value>期限</value></data>
  <data name="Column" xml:space="preserve"><value>列</value></data>
  <data name="History" xml:space="preserve"><value>履歴</value></data>
  <data name="NoProject" xml:space="preserve"><value>（なし）</value></data>
  <data name="NewProjectHint" xml:space="preserve"><value>新しいプロジェクト名を入力して Enter</value></data>
  <data name="NewLabelHint" xml:space="preserve"><value>新しいラベル名を入力して Enter</value></data>
  <data name="HistoryCreatedFormat" xml:space="preserve"><value>{0} に作成</value></data>
  <data name="HistoryMovedFormat" xml:space="preserve"><value>{0} → {1}</value></data>
  <data name="HistoryEditedFormat" xml:space="preserve"><value>{0} を変更</value></data>
  <data name="HistoryDeleted" xml:space="preserve"><value>削除</value></data>
  <data name="HistoryRestored" xml:space="preserve"><value>復元</value></data>
  <data name="FieldTitle" xml:space="preserve"><value>タイトル</value></data>
  <data name="FieldDescription" xml:space="preserve"><value>説明</value></data>
  <data name="FieldProject" xml:space="preserve"><value>プロジェクト</value></data>
  <data name="FieldDueDate" xml:space="preserve"><value>期限</value></data>
  <data name="FieldLabels" xml:space="preserve"><value>ラベル</value></data>
  <data name="UnknownColumn" xml:space="preserve"><value>（不明な列）</value></data>
  <data name="DbOpenFailedFormat" xml:space="preserve"><value>データベースを開けませんでした。
{0}

バックアップを作成して新しく作り直しますか？
「いいえ」を選ぶと終了します。</value></data>
  <data name="DbRecreatedFormat" xml:space="preserve"><value>以前のデータベースを次の場所に保存し、新しく作成しました。
{0}</value></data>
  <data name="DbRecreateFailedFormat" xml:space="preserve"><value>データベースを作り直せませんでした。終了します。
{0}</value></data>
</root>
```

`src/MoTask.App/Resources/Strings.cs`:

```csharp
using System.Globalization;
using System.Resources;

namespace MoTask.App.Resources;

public static class Strings
{
    private static readonly ResourceManager Rm =
        new("MoTask.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string AppTitle => Get(nameof(AppTitle));
    public static string Brand => Get(nameof(Brand));
    public static string ViewBoard => Get(nameof(ViewBoard));
    public static string FilterAllProjects => Get(nameof(FilterAllProjects));
    public static string DueAll => Get(nameof(DueAll));
    public static string DueToday => Get(nameof(DueToday));
    public static string DueThisWeek => Get(nameof(DueThisWeek));
    public static string DueOverdue => Get(nameof(DueOverdue));
    public static string Search => Get(nameof(Search));
    public static string ShowDeleted => Get(nameof(ShowDeleted));
    public static string NewTask => Get(nameof(NewTask));
    public static string AddColumn => Get(nameof(AddColumn));
    public static string AddTaskInline => Get(nameof(AddTaskInline));
    public static string Rename => Get(nameof(Rename));
    public static string ChangeRole => Get(nameof(ChangeRole));
    public static string SetWip => Get(nameof(SetWip));
    public static string ClearWip => Get(nameof(ClearWip));
    public static string DeleteColumn => Get(nameof(DeleteColumn));
    public static string RoleBacklog => Get(nameof(RoleBacklog));
    public static string RoleActive => Get(nameof(RoleActive));
    public static string RoleReview => Get(nameof(RoleReview));
    public static string RoleDone => Get(nameof(RoleDone));
    public static string Delete => Get(nameof(Delete));
    public static string Restore => Get(nameof(Restore));
    public static string Close => Get(nameof(Close));
    public static string Deleted => Get(nameof(Deleted));
    public static string Description => Get(nameof(Description));
    public static string Project => Get(nameof(Project));
    public static string Labels => Get(nameof(Labels));
    public static string DueDate => Get(nameof(DueDate));
    public static string Column => Get(nameof(Column));
    public static string History => Get(nameof(History));
    public static string NoProject => Get(nameof(NoProject));
    public static string NewProjectHint => Get(nameof(NewProjectHint));
    public static string NewLabelHint => Get(nameof(NewLabelHint));
    public static string HistoryCreatedFormat => Get(nameof(HistoryCreatedFormat));
    public static string HistoryMovedFormat => Get(nameof(HistoryMovedFormat));
    public static string HistoryEditedFormat => Get(nameof(HistoryEditedFormat));
    public static string HistoryDeleted => Get(nameof(HistoryDeleted));
    public static string HistoryRestored => Get(nameof(HistoryRestored));
    public static string FieldTitle => Get(nameof(FieldTitle));
    public static string FieldDescription => Get(nameof(FieldDescription));
    public static string FieldProject => Get(nameof(FieldProject));
    public static string FieldDueDate => Get(nameof(FieldDueDate));
    public static string FieldLabels => Get(nameof(FieldLabels));
    public static string UnknownColumn => Get(nameof(UnknownColumn));
    public static string DbOpenFailedFormat => Get(nameof(DbOpenFailedFormat));
    public static string DbRecreatedFormat => Get(nameof(DbRecreatedFormat));
    public static string DbRecreateFailedFormat => Get(nameof(DbRecreateFailedFormat));
}
```

- [ ] **Step 6: App.xaml / App.xaml.cs とウィンドウの骨組みを書く**

`git mv src/MoTask.App/MainWindow.xaml src/MoTask.App/Views/MainWindow.xaml` と `.xaml.cs` も同様に移動（`Views/` フォルダを作る）。

`src/MoTask.App/App.xaml`（`StartupUri` は置かない）:

```xml
<Application x:Class="MoTask.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Themes/Industry.xaml" />
        <ResourceDictionary Source="Themes/Controls.xaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

`src/MoTask.App/App.xaml.cs`:

```csharp
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoTask.App.Resources;
using MoTask.App.Views;
using MoTask.Core.Abstractions;
using MoTask.Core.Services;
using MoTask.Data;

namespace MoTask.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dbPath = DbPaths.DefaultDatabase;
        Directory.CreateDirectory(DbPaths.DefaultDirectory);

        _host = BuildHost(dbPath);
        if (!await TryInitializeDatabaseAsync(dbPath))
        {
            Shutdown();
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static IHost BuildHost(string dbPath)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMoTaskData(dbPath);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IBoardService, BoardService>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    /// <summary>仕様 §8: 開けない／壊れている DB はバックアップしてから再作成するか、終了するかを尋ねる。</summary>
    private async Task<bool> TryInitializeDatabaseAsync(string dbPath)
    {
        try
        {
            await _host!.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            var answer = MessageBox.Show(
                string.Format(Strings.DbOpenFailedFormat, ex.Message), Strings.AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer != MessageBoxResult.Yes) return false;
        }

        _host!.Dispose();
        var backup = DatabaseRecovery.BackupAndReset(dbPath, DateTime.Now);
        _host = BuildHost(dbPath);
        try
        {
            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            MessageBox.Show(string.Format(Strings.DbRecreatedFormat, backup), Strings.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.DbRecreateFailedFormat, ex.Message), Strings.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
```

`src/MoTask.App/Views/MainWindow.xaml`（この時点ではブランドだけ。Task 17 で本体を組む）:

```xml
<Window x:Class="MoTask.App.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:res="clr-namespace:MoTask.App.Resources"
        Title="{x:Static res:Strings.AppTitle}" Width="1280" Height="800" MinWidth="900" MinHeight="600"
        Background="{StaticResource Brush.Bg}" Foreground="{StaticResource Brush.Text}"
        FontFamily="{StaticResource Font.Body}" FontSize="{StaticResource FontSize.Body}">
  <Border BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,0,0,1" Padding="20.4,10.2" VerticalAlignment="Top">
    <TextBlock Text="{x:Static res:Strings.Brand}" Style="{StaticResource Text.Brand}" />
  </Border>
</Window>
```

`src/MoTask.App/Views/MainWindow.xaml.cs`:

```csharp
using System.Windows;

namespace MoTask.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: 文言のテストを書いて通す**

`tests/MoTask.App.Tests/StringsTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.Resources;
using Xunit;

namespace MoTask.App.Tests;

public class StringsTests
{
    [Fact]
    public void Strings_ResolveFromResx()
    {
        Strings.Brand.Should().Be("TASKS");
        string.Format(Strings.HistoryMovedFormat, "未着手", "進行中").Should().Be("未着手 → 進行中");
    }
}
```

Run: `dotnet build MoTask.sln && dotnet test tests/MoTask.App.Tests`
Expected: ビルド成功、`Passed! - Failed: 0, Passed: 1`

- [ ] **Step 8: 起動を手動確認する**

Run: `dotnet run --project src/MoTask.App`
Expected: 薄いグレーの地に「TASKS」が Barlow Condensed（細長い見出し字体）で表示される。閉じると終了する。`%LOCALAPPDATA%\MoTask\motask.db` が作られている。フォントが既定の字体で出る場合は `Font.Heading` の pack URI を `pack://application:,,,/MoTask;component/Fonts/#Barlow Condensed` に変えて再確認する。

- [ ] **Step 9: コミット**

```bash
git add src/MoTask.App tests/MoTask.App.Tests
git commit -m "feat(app): add Industry theme, fonts, strings, converters and host bootstrap

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 15: ViewModel（カード・列・フィルタ・ボード）

**Files:**
- Create: `src/MoTask.App/ViewModels/Options.cs`, `TaskCardViewModel.cs`, `ColumnViewModel.cs`, `FilterViewModel.cs`, `BoardViewModel.cs`
- Create: `tests/MoTask.App.Tests/TestBoards.cs`
- Test: `tests/MoTask.App.Tests/BoardViewModelTests.cs`

**Interfaces:**
- Consumes: `IBoardService`, `IClock`, `TaskFilter`, `DueStatuses`, `Strings`。
- Produces: `BoardViewModel(IBoardService, IClock)` と `Columns`, `Filter`, `SelectedCard`, `BannerMessage`, `LoadAsync()`, `ReloadAsync()`, `SelectCard(card)`, `MoveCardAsync(card, target, position)`, `CreateTaskAsync(column, title)`, `DeleteTaskAsync/RestoreTaskAsync/UpdateTaskAsync/SetTaskLabelsAsync(card, ...)`, `CreateProjectAsync(name)`, `CreateLabelAsync(name)`, `RenameColumnAsync/SetColumnRoleAsync/SetWipLimitAsync/DeleteColumnAsync(column, ...)`, `ReorderColumnsAsync(order)`, `GetHistoryAsync(taskId)`, `ColumnName(id)`, `ProjectName(id)`, `Today`。`ColumnViewModel.Cards`（表示）と `AllCards`（全件、Position 順）。Task 16 が `Detail` を、Task 18 が D&D ハンドラのプロパティを追加する。

- [ ] **Step 1: テストデータと失敗するテストを書く**

`tests/MoTask.App.Tests/TestBoards.cs`:

```csharp
using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.App.Tests;

public static class TestBoards
{
    public static Project ProjectA() => new() { Id = 100, Name = "顧客A対応" };
    public static Label Urgent() => new() { Id = 200, Name = "至急", Color = "accent-500" };

    /// <summary>未着手(1): 10,11 / 進行中(2, WIP 1): 12 / 完了(3): なし</summary>
    public static Board Sample(Label? urgent = null)
    {
        urgent ??= Urgent();
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var backlog = new Column { Id = 1, BoardId = 1, Name = "未着手", Order = 0, Role = ColumnRole.Backlog };
        var active = new Column { Id = 2, BoardId = 1, Name = "進行中", Order = 1, Role = ColumnRole.Active, WipLimit = 1 };
        var done = new Column { Id = 3, BoardId = 1, Name = "完了", Order = 2, Role = ColumnRole.Done };
        backlog.Tasks.Add(new TaskItem
        {
            Id = 10, Title = "請求先情報を更新する", ColumnId = 1, Position = 0, ProjectId = 100,
            DueDate = new DateOnly(2026, 9, 8), CreatedAt = t, UpdatedAt = t, Labels = { urgent },
        });
        backlog.Tasks.Add(new TaskItem { Id = 11, Title = "求人票の文面を見直す", ColumnId = 1, Position = 1, CreatedAt = t, UpdatedAt = t });
        active.Tasks.Add(new TaskItem { Id = 12, Title = "週次レポートを作成する", ColumnId = 2, Position = 0, CreatedAt = t, UpdatedAt = t });
        var board = new Board { Id = 1, Name = "テスト" };
        board.Columns.AddRange(new[] { backlog, active, done });
        return board;
    }
}

public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 9, 4);
}
```

`tests/MoTask.App.Tests/BoardViewModelTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.ViewModels;
using MoTask.Core;
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

    [Fact]
    public async Task MoveCard_WhenServiceFails_RollsBackAndShowsBanner()
    {
        _service.MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail("だめ")));
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeFalse();
        _vm.BannerMessage.Should().Be("だめ");
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
            var task = backlog.Tasks.Single(t => t.Id == 10);
            backlog.Tasks.Remove(task);
            active.Tasks.Insert(0, task);
            task.ColumnId = 2;
            for (var i = 0; i < backlog.Tasks.Count; i++) backlog.Tasks[i].Position = i;
            for (var i = 0; i < active.Tasks.Count; i++) active.Tasks[i].Position = i;
            return Task.FromResult(Result.Ok());
        });
        await _vm.LoadAsync();
        var card = _vm.Columns[0].Cards[0];
        _vm.SelectCard(card);

        var ok = await _vm.MoveCardAsync(card, _vm.Columns[1], 0);

        ok.Should().BeTrue();
        _vm.BannerMessage.Should().BeNull();
        Ids(_vm.Columns[0]).Should().Equal(11);
        Ids(_vm.Columns[1]).Should().Equal(10, 12);
        _vm.Columns[1].IsOverWip.Should().BeTrue();
        _vm.Columns[1].CountText.Should().Be("2 / 1");
        _vm.SelectedCard.Should().BeSameAs(card);
        _vm.Columns[1].SelectedCard.Should().BeSameAs(card);
        await _service.Received(1).GetBoardAsync(Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task DeleteColumn_WhenServiceFails_ShowsReasonAndKeepsColumn()
    {
        _service.DeleteColumnAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail(Messages.ColumnHasTasks)));
        await _vm.LoadAsync();

        await _vm.Columns[0].DeleteColumnCommand.ExecuteAsync(null);

        _vm.BannerMessage.Should().Be(Messages.ColumnHasTasks);
        _vm.Columns.Should().HaveCount(3);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.App.Tests`
Expected: コンパイルエラー（`BoardViewModel` 未定義）

- [ ] **Step 3: Options と TaskCardViewModel を書く**

`src/MoTask.App/ViewModels/Options.cs`:

```csharp
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

public sealed record ProjectOption(int? Id, string Name);
public sealed record DueOption(DueFilter Value, string Name);
public sealed record LabelChip(int Id, string Name, string Color);
```

`src/MoTask.App/ViewModels/TaskCardViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>カード1枚。Model は BoardService と共有する追跡済みエンティティで、変更後に Refresh で取り込む。</summary>
public sealed partial class TaskCardViewModel : ObservableObject
{
    public TaskItem Model { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private string _dueText = "";
    [ObservableProperty] private DueStatus _dueStatus;
    [ObservableProperty] private bool _isDeleted;

    public ObservableCollection<LabelChip> Labels { get; } = new();

    public TaskCardViewModel(TaskItem model)
    {
        Model = model;
    }

    public int Id => Model.Id;
    public int ColumnId => Model.ColumnId;
    public int Position => Model.Position;

    public void Refresh(Func<int?, string?> projectName, DateOnly today)
    {
        Title = Model.Title;
        ProjectName = projectName(Model.ProjectId);
        DueText = Model.DueDate is DateOnly d ? $"{d.Month}/{d.Day}" : "";
        DueStatus = DueStatuses.Of(Model, today);
        IsDeleted = Model.IsDeleted;
        Labels.Clear();
        foreach (var label in Model.Labels.OrderBy(l => l.Name))
        {
            Labels.Add(new LabelChip(label.Id, label.Name, label.Color));
        }
        OnPropertyChanged(nameof(ColumnId));
        OnPropertyChanged(nameof(Position));
    }
}
```

- [ ] **Step 4: ColumnViewModel を書く**

`src/MoTask.App/ViewModels/ColumnViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

public sealed partial class ColumnViewModel : ObservableObject
{
    private readonly BoardViewModel _board;

    public Column Model { get; }
    public BoardViewModel Board => _board;
    public int Id => Model.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int? _wipLimit;
    [ObservableProperty] private ColumnRole _role;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private bool _isOverWip;
    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private bool _isAddingTask;
    [ObservableProperty] private string _newTaskTitle = "";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isEditingWip;
    [ObservableProperty] private string _wipText = "";
    [ObservableProperty] private TaskCardViewModel? _selectedCard;

    /// <summary>列内の全カード（Position 順、削除済み含む）。</summary>
    public List<TaskCardViewModel> AllCards { get; } = new();

    /// <summary>フィルタ適用後の表示カード。ListBox にバインドする。</summary>
    public ObservableCollection<TaskCardViewModel> Cards { get; } = new();

    public ColumnViewModel(Column model, BoardViewModel board)
    {
        Model = model;
        _board = board;
        RefreshHeader();
    }

    public void RefreshHeader()
    {
        Name = Model.Name;
        WipLimit = Model.WipLimit;
        Role = Model.Role;
        IsDone = Role == ColumnRole.Done;
        ActiveCount = Model.ActiveCount;
        IsOverWip = Model.IsOverWip;
        CountText = WipLimit is int limit ? $"{ActiveCount} / {limit}" : ActiveCount.ToString();
    }

    /// <summary>Model.Tasks から AllCards を組み直す。既存のカード VM は再利用する。</summary>
    public void SyncCardsFromModel(Func<int?, string?> projectName, DateOnly today)
    {
        var existing = AllCards.ToDictionary(c => c.Id);
        AllCards.Clear();
        foreach (var task in Model.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id))
        {
            var card = existing.TryGetValue(task.Id, out var e) && ReferenceEquals(e.Model, task)
                ? e
                : new TaskCardViewModel(task);
            card.Refresh(projectName, today);
            AllCards.Add(card);
        }
    }

    public void ApplyFilter(TaskFilter filter, DateOnly today)
    {
        var visible = filter.Apply(AllCards.Select(c => c.Model), today)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var selected = SelectedCard;
        Cards.Clear();
        foreach (var card in AllCards)
        {
            if (visible.Contains(card.Model)) Cards.Add(card);
        }
        if (selected is not null && Cards.Contains(selected)) SelectedCard = selected;
    }

    partial void OnSelectedCardChanged(TaskCardViewModel? value)
    {
        if (value is not null) _board.SelectCard(value);
    }

    // ---- インライン作成 ----

    [RelayCommand]
    private void BeginAddTask()
    {
        NewTaskTitle = "";
        IsAddingTask = true;
    }

    [RelayCommand]
    private async Task CommitAddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle)) return; // 空は拒否し入力欄に留まる
        if (await _board.CreateTaskAsync(this, NewTaskTitle))
        {
            NewTaskTitle = "";
            IsAddingTask = false;
        }
    }

    [RelayCommand]
    private void CancelAddTask()
    {
        NewTaskTitle = "";
        IsAddingTask = false;
    }

    // ---- ヘッダーメニュー ----

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        await _board.RenameColumnAsync(this, RenameText);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task SetRoleAsync(ColumnRole role) => _board.SetColumnRoleAsync(this, role);

    [RelayCommand]
    private void BeginEditWip()
    {
        WipText = WipLimit?.ToString() ?? "";
        IsEditingWip = true;
    }

    [RelayCommand]
    private async Task CommitWipAsync()
    {
        if (!IsEditingWip) return;
        IsEditingWip = false;
        int? limit = int.TryParse(WipText, out var n) ? n : null;
        await _board.SetWipLimitAsync(this, limit);
    }

    [RelayCommand]
    private void CancelEditWip() => IsEditingWip = false;

    [RelayCommand]
    private Task ClearWipAsync() => _board.SetWipLimitAsync(this, null);

    [RelayCommand]
    private Task DeleteColumnAsync() => _board.DeleteColumnAsync(this);
}
```

- [ ] **Step 5: FilterViewModel を書く**

`src/MoTask.App/ViewModels/FilterViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

public sealed partial class FilterViewModel : ObservableObject
{
    public ObservableCollection<ProjectOption> Projects { get; } = new();
    public ObservableCollection<LabelFilterItem> Labels { get; } = new();
    public IReadOnlyList<DueOption> DueOptions { get; } = new[]
    {
        new DueOption(DueFilter.All, Strings.DueAll),
        new DueOption(DueFilter.Today, Strings.DueToday),
        new DueOption(DueFilter.ThisWeek, Strings.DueThisWeek),
        new DueOption(DueFilter.Overdue, Strings.DueOverdue),
    };

    [ObservableProperty] private ProjectOption? _selectedProject;
    [ObservableProperty] private DueOption _selectedDue;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showDeleted;

    public event EventHandler? Changed;

    public FilterViewModel()
    {
        _selectedDue = DueOptions[0];
        SetProjects(Array.Empty<Project>());
    }

    public void SetProjects(IEnumerable<Project> projects)
    {
        var keep = SelectedProject?.Id;
        Projects.Clear();
        Projects.Add(new ProjectOption(null, Strings.FilterAllProjects));
        foreach (var p in projects.Where(p => !p.Archived).OrderBy(p => p.Name))
        {
            Projects.Add(new ProjectOption(p.Id, p.Name));
        }
        SelectedProject = Projects.FirstOrDefault(o => o.Id == keep) ?? Projects[0];
    }

    public void SetLabels(IEnumerable<Label> labels)
    {
        var keep = Labels.Where(l => l.IsSelected).Select(l => l.Id).ToHashSet();
        Labels.Clear();
        foreach (var l in labels.OrderBy(l => l.Name))
        {
            Labels.Add(new LabelFilterItem(l, RaiseChanged) { IsSelected = keep.Contains(l.Id) });
        }
    }

    public TaskFilter ToFilter() => new(
        ProjectId: SelectedProject?.Id,
        LabelIds: Labels.Where(l => l.IsSelected).Select(l => l.Id).ToHashSet(),
        Due: SelectedDue.Value,
        SearchText: SearchText,
        ShowDeleted: ShowDeleted);

    partial void OnSelectedProjectChanged(ProjectOption? value) => RaiseChanged();
    partial void OnSelectedDueChanged(DueOption value) => RaiseChanged();
    partial void OnSearchTextChanged(string value) => RaiseChanged();
    partial void OnShowDeletedChanged(bool value) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed partial class LabelFilterItem : ObservableObject
{
    private readonly Action _changed;

    public Label Model { get; }
    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;

    [ObservableProperty] private bool _isSelected;

    public LabelFilterItem(Label model, Action changed)
    {
        Model = model;
        _changed = changed;
    }

    partial void OnIsSelectedChanged(bool value) => _changed();
}
```

- [ ] **Step 6: BoardViewModel を書く**

`src/MoTask.App/ViewModels/BoardViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// ボード全体。操作は BoardService に即時コミットし、成功したら差分（Refresh）で表示を更新する。
/// 失敗したらバナーを出し、GetBoard で全体を読み直して巻き戻す。
/// </summary>
public sealed partial class BoardViewModel : ObservableObject
{
    private static readonly string[] LabelColors =
        { "accent-300", "accent-500", "accent-200", "accent-700", "accent-400" };

    private readonly IBoardService _service;
    private readonly IClock _clock;
    private Board? _board;
    private bool _syncingSelection;

    public ObservableCollection<ColumnViewModel> Columns { get; } = new();
    public FilterViewModel Filter { get; } = new();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<Label> Labels { get; private set; } = Array.Empty<Label>();

    [ObservableProperty] private TaskCardViewModel? _selectedCard;
    [ObservableProperty] private string? _bannerMessage;
    [ObservableProperty] private bool _isAddingColumn;
    [ObservableProperty] private string _newColumnName = "";
    [ObservableProperty] private bool _isLoaded;

    public BoardViewModel(IBoardService service, IClock clock)
    {
        _service = service;
        _clock = clock;
        Filter.Changed += (_, _) => ApplyFilter();
    }

    public DateOnly Today => _clock.Today;

    // ---------- 読み込み ----------

    [RelayCommand]
    public Task LoadAsync() => ReloadAsync();

    /// <summary>起動時と、失敗からの復帰時にだけ呼ぶ。</summary>
    public async Task ReloadAsync()
    {
        var result = await _service.GetBoardAsync();
        if (!result.IsSuccess)
        {
            BannerMessage = result.Error;
            return;
        }
        _board = result.Value!;
        Projects = await _service.GetProjectsAsync();
        Labels = await _service.GetLabelsAsync();
        Filter.SetProjects(Projects);
        Filter.SetLabels(Labels);

        var selectedId = SelectedCard?.Id;
        Columns.Clear();
        foreach (var column in _board.Columns.OrderBy(c => c.Order))
        {
            var vm = new ColumnViewModel(column, this);
            vm.SyncCardsFromModel(ProjectName, Today);
            Columns.Add(vm);
        }
        ApplyFilter();
        SelectCard(selectedId is int id ? AllCards().FirstOrDefault(c => c.Id == id) : null);
        IsLoaded = true;
    }

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId) => _service.GetHistoryAsync(taskId);

    public string? ProjectName(int? projectId)
        => projectId is int id ? Projects.FirstOrDefault(p => p.Id == id)?.Name : null;

    public string ColumnName(int columnId)
        => _board?.Columns.FirstOrDefault(c => c.Id == columnId)?.Name ?? Strings.UnknownColumn;

    // ---------- 選択 ----------

    public void SelectCard(TaskCardViewModel? card)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            SelectedCard = card;
            foreach (var column in Columns)
            {
                column.SelectedCard = card is not null && column.AllCards.Contains(card) ? card : null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    [RelayCommand]
    private void CloseDetail() => SelectCard(null);

    [RelayCommand]
    private void DismissBanner() => BannerMessage = null;

    [RelayCommand]
    private void NewTask()
    {
        var column = (SelectedCard is { } card ? ColumnOf(card) : null) ?? Columns.FirstOrDefault();
        if (column is null) return;
        foreach (var other in Columns.Where(c => !ReferenceEquals(c, column))) other.IsAddingTask = false;
        column.BeginAddTaskCommand.Execute(null);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedCard is { IsDeleted: false } card) await DeleteTaskAsync(card);
    }

    // ---------- 列の追加 ----------

    [RelayCommand]
    private void BeginAddColumn()
    {
        NewColumnName = "";
        IsAddingColumn = true;
    }

    [RelayCommand]
    private async Task CommitAddColumnAsync()
    {
        if (string.IsNullOrWhiteSpace(NewColumnName)) return;
        var result = await _service.AddColumnAsync(NewColumnName);
        if (!await HandleAsync(result)) return;
        IsAddingColumn = false;
        NewColumnName = "";
        var vm = new ColumnViewModel(result.Value!, this);
        vm.ApplyFilter(Filter.ToFilter(), Today);
        Columns.Add(vm);
    }

    [RelayCommand]
    private void CancelAddColumn() => IsAddingColumn = false;

    // ---------- タスク操作（ColumnViewModel / TaskDetailViewModel / D&D から呼ぶ） ----------

    public async Task<bool> CreateTaskAsync(ColumnViewModel column, string title)
    {
        var result = await _service.CreateTaskAsync(column.Id, title);
        if (!await HandleAsync(result)) return false;
        RefreshColumn(column);
        var card = column.AllCards.FirstOrDefault(c => c.Id == result.Value!.Id);
        if (card is not null) SelectCard(card);
        return true;
    }

    /// <param name="position">移動先列で、移動カードを除いた全カード列での挿入位置。</param>
    public async Task<bool> MoveCardAsync(TaskCardViewModel card, ColumnViewModel target, int position)
    {
        var source = ColumnOf(card);
        if (source is null) return false;

        // 楽観的更新: 先に表示を動かす
        var wasSelected = ReferenceEquals(SelectedCard, card);
        source.AllCards.Remove(card);
        position = Math.Clamp(position, 0, target.AllCards.Count);
        target.AllCards.Insert(position, card);
        var filter = Filter.ToFilter();
        source.ApplyFilter(filter, Today);
        target.ApplyFilter(filter, Today);

        var result = await _service.MoveTaskAsync(card.Id, target.Id, position);
        if (!await HandleAsync(result)) return false;

        RefreshColumn(source);
        if (!ReferenceEquals(source, target)) RefreshColumn(target);
        if (wasSelected) SelectCard(card);
        AfterTaskChanged();
        return true;
    }

    public Task<bool> DeleteTaskAsync(TaskCardViewModel card)
        => RunTaskChangeAsync(card, () => _service.DeleteTaskAsync(card.Id));

    public Task<bool> RestoreTaskAsync(TaskCardViewModel card)
        => RunTaskChangeAsync(card, () => _service.RestoreTaskAsync(card.Id));

    public Task<bool> UpdateTaskAsync(TaskCardViewModel card, TaskUpdate update)
        => RunTaskChangeAsync(card, () => _service.UpdateTaskAsync(update));

    public Task<bool> SetTaskLabelsAsync(TaskCardViewModel card, IReadOnlyCollection<int> labelIds)
        => RunTaskChangeAsync(card, () => _service.SetTaskLabelsAsync(card.Id, labelIds));

    public async Task<Project?> CreateProjectAsync(string name)
    {
        var result = await _service.CreateProjectAsync(name);
        if (!await HandleAsync(result)) return null;
        Projects = await _service.GetProjectsAsync();
        Filter.SetProjects(Projects);
        return result.Value;
    }

    public async Task<Label?> CreateLabelAsync(string name)
    {
        var color = LabelColors[Labels.Count % LabelColors.Length];
        var result = await _service.CreateLabelAsync(name, color);
        if (!await HandleAsync(result)) return null;
        Labels = await _service.GetLabelsAsync();
        Filter.SetLabels(Labels);
        return result.Value;
    }

    // ---------- 列操作 ----------

    public Task<bool> RenameColumnAsync(ColumnViewModel column, string name)
        => RunColumnChangeAsync(column, () => _service.RenameColumnAsync(column.Id, name));

    public Task<bool> SetColumnRoleAsync(ColumnViewModel column, ColumnRole role)
        => RunColumnChangeAsync(column, () => _service.SetColumnRoleAsync(column.Id, role));

    public Task<bool> SetWipLimitAsync(ColumnViewModel column, int? limit)
        => RunColumnChangeAsync(column, () => _service.SetWipLimitAsync(column.Id, limit));

    public async Task<bool> DeleteColumnAsync(ColumnViewModel column)
    {
        var result = await _service.DeleteColumnAsync(column.Id);
        if (!await HandleAsync(result)) return false;
        Columns.Remove(column);
        return true;
    }

    public async Task<bool> ReorderColumnsAsync(IReadOnlyList<ColumnViewModel> order)
    {
        // 楽観的更新
        for (var i = 0; i < order.Count; i++)
        {
            var current = Columns.IndexOf(order[i]);
            if (current >= 0 && current != i) Columns.Move(current, i);
        }
        var result = await _service.ReorderColumnsAsync(order.Select(c => c.Id).ToList());
        return await HandleAsync(result);
    }

    // ---------- 内部 ----------

    private async Task<bool> RunTaskChangeAsync(TaskCardViewModel card, Func<Task<Result>> operation)
    {
        var result = await operation();
        if (!await HandleAsync(result)) return false;
        var column = ColumnOf(card);
        if (column is not null)
        {
            card.Refresh(ProjectName, Today);
            column.RefreshHeader();
            column.ApplyFilter(Filter.ToFilter(), Today);
        }
        AfterTaskChanged();
        return true;
    }

    private async Task<bool> RunColumnChangeAsync(ColumnViewModel column, Func<Task<Result>> operation)
    {
        var result = await operation();
        if (!await HandleAsync(result)) return false;
        column.RefreshHeader();
        return true;
    }

    /// <summary>失敗ならバナーを出して全体を読み直す（巻き戻し）。WIP 警告は赤表示だけなのでバナーに出さない。</summary>
    private async Task<bool> HandleAsync(Result result)
    {
        if (result.IsSuccess) return true;
        BannerMessage = result.Error;
        await ReloadAsync();
        return false;
    }

    private void RefreshColumn(ColumnViewModel column)
    {
        column.SyncCardsFromModel(ProjectName, Today);
        column.RefreshHeader();
        column.ApplyFilter(Filter.ToFilter(), Today);
    }

    /// <summary>Task 16 で詳細パネルの Refresh を呼ぶ差し込み口。</summary>
    private void AfterTaskChanged()
    {
    }

    private void ApplyFilter()
    {
        var filter = Filter.ToFilter();
        foreach (var column in Columns) column.ApplyFilter(filter, Today);
    }

    private IEnumerable<TaskCardViewModel> AllCards() => Columns.SelectMany(c => c.AllCards);

    private ColumnViewModel? ColumnOf(TaskCardViewModel card)
        => Columns.FirstOrDefault(c => c.AllCards.Contains(card));
}
```

- [ ] **Step 7: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.App.Tests`
Expected: `Passed! - Failed: 0, Passed: 12`

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.App tests/MoTask.App.Tests
git commit -m "feat(app): add board, column, card and filter view models

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 16: TaskDetailViewModel と履歴の整形

**Files:**
- Create: `src/MoTask.App/ViewModels/HistoryFormatter.cs`, `TaskDetailViewModel.cs`
- Modify: `src/MoTask.App/ViewModels/BoardViewModel.cs`（`Detail` プロパティ、`OnSelectedCardChanged`、`AfterTaskChanged`）
- Test: `tests/MoTask.App.Tests/HistoryFormatterTests.cs`, `TaskDetailViewModelTests.cs`

**Interfaces:**
- Produces: `BoardViewModel.Detail`（選択中のみ非 null）、`TaskDetailViewModel` の `Title/Description/SelectedProject/DueDate/SelectedColumn/Labels/History/IsDeleted/HasTitleError/PendingSave`、コマンド `Delete/Restore/Close/CreateProject/CreateLabel`。`HistoryFormatter.Format(entry, columnName, timeZone)`。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/HistoryFormatterTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class HistoryFormatterTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static string Name(int id) => id switch { 1 => "未着手", 2 => "進行中", _ => "?" };

    [Fact]
    public void Moved_ShowsLocalTimeAndArrow()
    {
        var entry = new HistoryEntry
        {
            At = new DateTime(2026, 9, 3, 23, 40, 0, DateTimeKind.Utc), Kind = HistoryKind.Moved,
            FromColumnId = 1, ToColumnId = 2,
        };
        HistoryFormatter.Format(entry, Name, Tokyo).Should().Be("9/4 8:40 未着手 → 進行中");
    }

    [Fact]
    public void Created_Edited_Deleted_Restored()
    {
        var at = new DateTime(2026, 9, 4, 0, 5, 0, DateTimeKind.Utc);
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Created, ToColumnId = 1 }, Name, Tokyo)
            .Should().Be("9/4 9:05 未着手 に作成");
        var detail = HistoryDetail.Serialize(new Dictionary<string, FieldChange>
        {
            ["Title"] = new("a", "b"), ["DueDate"] = new(null, "2026-09-10"),
        });
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Edited, Detail = detail }, Name, Tokyo)
            .Should().Be("9/4 9:05 タイトル、期限 を変更");
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Deleted }, Name, Tokyo)
            .Should().Be("9/4 9:05 削除");
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Restored }, Name, Tokyo)
            .Should().Be("9/4 9:05 復元");
    }
}
```

`tests/MoTask.App.Tests/TaskDetailViewModelTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class TaskDetailViewModelTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Board _board;
    private readonly BoardViewModel _vm;

    public TaskDetailViewModelTests()
    {
        _board = TestBoards.Sample();
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(new[]
            {
                new HistoryEntry { TaskId = 10, At = DateTime.UtcNow, Kind = HistoryKind.Created, ToColumnId = 1 },
            }));
        _service.UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _vm = new BoardViewModel(_service, new TestClock());
    }

    private async Task<TaskDetailViewModel> OpenAsync(int taskId)
    {
        await _vm.LoadAsync();
        var card = _vm.Columns.SelectMany(c => c.AllCards).Single(c => c.Id == taskId);
        _vm.SelectCard(card);
        var detail = _vm.Detail!;
        await detail.PendingSave;
        return detail;
    }

    [Fact]
    public async Task Open_LoadsFieldsAndHistory()
    {
        var detail = await OpenAsync(10);

        detail.Title.Should().Be("請求先情報を更新する");
        detail.SelectedProject!.Name.Should().Be("顧客A対応");
        detail.DueDate.Should().Be(new DateTime(2026, 9, 8));
        detail.SelectedColumn!.Id.Should().Be(1);
        detail.Labels.Single().IsSelected.Should().BeTrue();
        detail.History.Should().ContainSingle().Which.Should().EndWith("未着手 に作成");
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingTitle_SavesExactlyOnce()
    {
        var detail = await OpenAsync(10);

        detail.Title = "新しい題";
        await detail.PendingSave;

        await _service.Received(1).UpdateTaskAsync(
            Arg.Is<TaskUpdate>(u => u.TaskId == 10 && u.Title == "新しい題" && u.ProjectId == 100
                                   && u.DueDate == new DateOnly(2026, 9, 8)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettingSameTitle_DoesNotSave()
    {
        var detail = await OpenAsync(10);

        detail.Title = "請求先情報を更新する";
        await detail.PendingSave;

        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyTitle_IsRejectedLocally()
    {
        var detail = await OpenAsync(10);

        detail.Title = "   ";
        await detail.PendingSave;

        detail.HasTitleError.Should().BeTrue();
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingColumn_MovesToEndOfThatColumn()
    {
        var detail = await OpenAsync(10);

        detail.SelectedColumn = _vm.Columns[1];
        await detail.PendingSave;

        // int.MaxValue は移動先の件数（1）に丸められてから BoardService へ渡る
        await _service.Received(1).MoveTaskAsync(10, 2, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglingLabel_CallsSetTaskLabels()
    {
        _service.SetTaskLabelsAsync(10, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        var detail = await OpenAsync(10);

        detail.Labels[0].IsSelected = false;
        await detail.PendingSave;

        await _service.Received(1).SetTaskLabelsAsync(10, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_ThenRestore_UpdatesIsDeleted()
    {
        var task = _board.Columns[0].Tasks[0];
        _service.DeleteTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ => { task.DeletedAt = DateTime.UtcNow; return Task.FromResult(Result.Ok()); });
        _service.RestoreTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ => { task.DeletedAt = null; return Task.FromResult(Result.Ok()); });
        var detail = await OpenAsync(10);

        await detail.DeleteCommand.ExecuteAsync(null);
        detail.IsDeleted.Should().BeTrue();
        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(11, "削除済みは既定で隠れる");

        await detail.RestoreCommand.ExecuteAsync(null);
        detail.IsDeleted.Should().BeFalse();
        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(10, 11);
    }

    [Fact]
    public async Task Close_ClearsDetail()
    {
        var detail = await OpenAsync(10);
        detail.CloseCommand.Execute(null);
        _vm.Detail.Should().BeNull();
        _vm.SelectedCard.Should().BeNull();
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.App.Tests`
Expected: コンパイルエラー（`HistoryFormatter`, `Detail` 未定義）

- [ ] **Step 3: HistoryFormatter を書く**

`src/MoTask.App/ViewModels/HistoryFormatter.cs`:

```csharp
using System.Globalization;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>「9/4 8:40 未着手 → 進行中」の形に整形する。</summary>
public static class HistoryFormatter
{
    public static string Format(HistoryEntry entry, Func<int, string> columnName, TimeZoneInfo? timeZone = null)
    {
        var utc = DateTime.SpecifyKind(entry.At, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? TimeZoneInfo.Local);
        var stamp = local.ToString("M/d H:mm", CultureInfo.InvariantCulture);

        var body = entry.Kind switch
        {
            HistoryKind.Created => string.Format(Strings.HistoryCreatedFormat, Name(entry.ToColumnId)),
            HistoryKind.Moved => string.Format(Strings.HistoryMovedFormat, Name(entry.FromColumnId), Name(entry.ToColumnId)),
            HistoryKind.Edited => string.Format(Strings.HistoryEditedFormat,
                string.Join("、", HistoryDetail.Deserialize(entry.Detail).Keys.Select(FieldName))),
            HistoryKind.Deleted => Strings.HistoryDeleted,
            HistoryKind.Restored => Strings.HistoryRestored,
            _ => entry.Kind.ToString(),
        };
        return $"{stamp} {body}";

        string Name(int? id) => id is int i ? columnName(i) : Strings.UnknownColumn;
    }

    public static string FieldName(string key) => key switch
    {
        "Title" => Strings.FieldTitle,
        "Description" => Strings.FieldDescription,
        "Project" => Strings.FieldProject,
        "DueDate" => Strings.FieldDueDate,
        "Labels" => Strings.FieldLabels,
        _ => key,
    };
}
```

- [ ] **Step 4: TaskDetailViewModel を書く**

`src/MoTask.App/ViewModels/TaskDetailViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// 詳細パネル。各項目は値が変わった時点（TextBox はフォーカスアウト）で即保存する。保存ボタンは置かない。
/// PendingSave はテストが保存完了を待つためのハンドル。
/// </summary>
public sealed partial class TaskDetailViewModel : ObservableObject
{
    private readonly BoardViewModel _board;
    private bool _loading;

    public TaskCardViewModel Card { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private ProjectOption? _selectedProject;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private ColumnViewModel? _selectedColumn;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _hasTitleError;
    [ObservableProperty] private string _newProjectName = "";
    [ObservableProperty] private string _newLabelName = "";

    public ObservableCollection<ProjectOption> Projects { get; } = new();
    public ObservableCollection<LabelToggleViewModel> Labels { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public ObservableCollection<ColumnViewModel> Columns => _board.Columns;

    public Task PendingSave { get; private set; } = Task.CompletedTask;

    public TaskDetailViewModel(TaskCardViewModel card, BoardViewModel board)
    {
        Card = card;
        _board = board;
        Refresh();
        PendingSave = LoadHistoryAsync();
    }

    /// <summary>Card.Model の現在値を表示に取り込む。保存は起こさない。</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var m = Card.Model;
            Title = m.Title;
            HasTitleError = false;
            Description = m.Description;

            Projects.Clear();
            Projects.Add(new ProjectOption(null, Strings.NoProject));
            foreach (var p in _board.Projects.Where(p => !p.Archived || p.Id == m.ProjectId).OrderBy(p => p.Name))
            {
                Projects.Add(new ProjectOption(p.Id, p.Name));
            }
            SelectedProject = Projects.FirstOrDefault(o => o.Id == m.ProjectId) ?? Projects[0];

            DueDate = m.DueDate?.ToDateTime(TimeOnly.MinValue);
            SelectedColumn = _board.Columns.FirstOrDefault(c => c.Id == m.ColumnId);
            IsDeleted = m.IsDeleted;

            Labels.Clear();
            foreach (var l in _board.Labels.OrderBy(l => l.Name))
            {
                Labels.Add(new LabelToggleViewModel(l, m.Labels.Any(x => x.Id == l.Id), OnLabelToggled));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public async Task LoadHistoryAsync()
    {
        var entries = await _board.GetHistoryAsync(Card.Id);
        History.Clear();
        foreach (var e in entries) History.Add(HistoryFormatter.Format(e, _board.ColumnName));
    }

    partial void OnTitleChanged(string value) => QueueSave();
    partial void OnDescriptionChanged(string value) => QueueSave();
    partial void OnSelectedProjectChanged(ProjectOption? value) => QueueSave();
    partial void OnDueDateChanged(DateTime? value) => QueueSave();

    partial void OnSelectedColumnChanged(ColumnViewModel? value)
    {
        if (_loading || value is null || value.Id == Card.Model.ColumnId) return;
        PendingSave = MoveAsync(value);
    }

    private void QueueSave()
    {
        if (_loading) return;
        PendingSave = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            HasTitleError = true; // 空タイトルは拒否し、入力欄に留まる
            return;
        }
        HasTitleError = false;
        var update = new TaskUpdate(
            Card.Id, Title, Description, SelectedProject?.Id,
            DueDate is DateTime d ? DateOnly.FromDateTime(d) : null);
        if (await _board.UpdateTaskAsync(Card, update)) await LoadHistoryAsync();
    }

    private async Task MoveAsync(ColumnViewModel target)
    {
        if (await _board.MoveCardAsync(Card, target, int.MaxValue)) await LoadHistoryAsync();
    }

    private void OnLabelToggled(LabelToggleViewModel _)
    {
        if (_loading) return;
        var ids = Labels.Where(l => l.IsSelected).Select(l => l.Id).ToList();
        PendingSave = SaveLabelsAsync(ids);
    }

    private async Task SaveLabelsAsync(IReadOnlyCollection<int> ids)
    {
        if (await _board.SetTaskLabelsAsync(Card, ids)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await _board.DeleteTaskAsync(Card)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (await _board.RestoreTaskAsync(Card)) await LoadHistoryAsync();
    }

    [RelayCommand]
    private void Close() => _board.CloseDetailCommand.Execute(null);

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0) return;
        var project = await _board.CreateProjectAsync(name);
        if (project is null) return;
        NewProjectName = "";
        Refresh();
        SelectedProject = Projects.First(o => o.Id == project.Id); // 変更として保存される
    }

    [RelayCommand]
    private async Task CreateLabelAsync()
    {
        var name = NewLabelName.Trim();
        if (name.Length == 0) return;
        var label = await _board.CreateLabelAsync(name);
        if (label is null) return;
        NewLabelName = "";
        var ids = Card.Model.Labels.Select(l => l.Id).Append(label.Id).ToList();
        if (await _board.SetTaskLabelsAsync(Card, ids)) await LoadHistoryAsync();
    }
}

public sealed partial class LabelToggleViewModel : ObservableObject
{
    private readonly Action<LabelToggleViewModel> _toggled;

    public Label Model { get; }
    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;

    [ObservableProperty] private bool _isSelected;

    public LabelToggleViewModel(Label model, bool isSelected, Action<LabelToggleViewModel> toggled)
    {
        Model = model;
        _isSelected = isSelected;
        _toggled = toggled;
    }

    partial void OnIsSelectedChanged(bool value) => _toggled(this);
}
```

- [ ] **Step 5: BoardViewModel に Detail を足す**

`BoardViewModel.cs` を次のように変更する:

1. フィールド群に追加:

```csharp
    [ObservableProperty] private TaskDetailViewModel? _detail;
```

2. `SelectCard` の直後に追加:

```csharp
    partial void OnSelectedCardChanged(TaskCardViewModel? value)
    {
        Detail = value is null ? null : new TaskDetailViewModel(value, this);
    }
```

3. `AfterTaskChanged` の本体を置き換え:

```csharp
    private void AfterTaskChanged() => Detail?.Refresh();
```

- [ ] **Step 6: テストが通ることを確認する**

Run: `dotnet test tests/MoTask.App.Tests`
Expected: `Passed! - Failed: 0, Passed: 22`

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.App tests/MoTask.App.Tests
git commit -m "feat(app): add task detail view model and history formatting

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 17: View（メインウィンドウ・フィルタバー・ボード・列・カード）とキーボード

**Files:**
- Modify: `src/MoTask.App/Views/MainWindow.xaml(.cs)`, `src/MoTask.App/App.xaml.cs`（`BoardViewModel` の登録）
- Create: `src/MoTask.App/Views/FilterBar.xaml(.cs)`, `BoardView.xaml(.cs)`, `ColumnView.xaml(.cs)`, `TaskCardView.xaml(.cs)`

**Interfaces:**
- Consumes: Task 15/16 の ViewModel、Task 14 のスタイルとコンバータ。
- Produces: 起動して DB のボードが表示され、タスクの作成・選択・フィルタ・列の追加／改名／Role／WIP／削除が動く。D&D（Task 18）と詳細パネル（Task 19）は未接続。

- [ ] **Step 1: DI に BoardViewModel を登録する**

`App.xaml.cs` の `BuildHost` で `builder.Services.AddSingleton<MainWindow>();` の前に追加:

```csharp
        builder.Services.AddSingleton<ViewModels.BoardViewModel>();
```

- [ ] **Step 2: MainWindow を書く**

`src/MoTask.App/Views/MainWindow.xaml`:

```xml
<Window x:Class="MoTask.App.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:res="clr-namespace:MoTask.App.Resources"
        xmlns:vm="clr-namespace:MoTask.App.ViewModels"
        xmlns:views="clr-namespace:MoTask.App.Views"
        mc:Ignorable="d" d:DataContext="{d:DesignInstance vm:BoardViewModel}"
        Title="{x:Static res:Strings.AppTitle}" Width="1280" Height="800" MinWidth="900" MinHeight="600"
        Background="{StaticResource Brush.Bg}" Foreground="{StaticResource Brush.Text}"
        FontFamily="{StaticResource Font.Body}" FontSize="{StaticResource FontSize.Body}"
        Loaded="OnLoaded" PreviewKeyDown="OnPreviewKeyDown">
  <DockPanel>
    <!-- トップバー -->
    <Border DockPanel.Dock="Top" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,0,0,1" Padding="20.4,10.2">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="{x:Static res:Strings.Brand}" Style="{StaticResource Text.Brand}" VerticalAlignment="Center" />
        <Border Margin="27.2,0,0,0" BorderBrush="{StaticResource Brush.Accent}" BorderThickness="0,0,0,2" Padding="6.8,3.4">
          <TextBlock Text="{x:Static res:Strings.ViewBoard}" Foreground="{StaticResource Brush.Accent.700}" FontWeight="Medium" />
        </Border>
      </StackPanel>
    </Border>

    <!-- 保存失敗などの非モーダルバナー -->
    <Border DockPanel.Dock="Top" Background="{StaticResource Brush.Accent.100}" BorderBrush="{StaticResource Brush.Danger}"
            BorderThickness="0,0,0,1" Padding="20.4,6.8"
            Visibility="{Binding BannerMessage, Converter={StaticResource NullToVisibility}}">
      <DockPanel>
        <Button DockPanel.Dock="Right" Content="×" Style="{StaticResource Btn.Ghost}" Command="{Binding DismissBannerCommand}" />
        <TextBlock Text="{Binding BannerMessage}" Foreground="{StaticResource Brush.Danger}" VerticalAlignment="Center" TextWrapping="Wrap" />
      </DockPanel>
    </Border>

    <views:FilterBar x:Name="FilterBar" DockPanel.Dock="Top" />

    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
      </Grid.ColumnDefinitions>
      <views:BoardView Grid.Column="0" />
      <!-- 詳細パネルは Task 19 でここに置く -->
    </Grid>
  </DockPanel>
</Window>
```

`src/MoTask.App/Views/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _vm;

    public MainWindow(BoardViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _vm.LoadAsync();

    /// <summary>仕様 §6 キーボード: N=新規、Delete=論理削除、Esc=詳細を閉じる、Ctrl+F=検索。文字入力中は N/Delete を奪わない。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBar.FocusSearch();
            e.Handled = true;
            return;
        }

        var typing = Keyboard.FocusedElement is TextBoxBase or ComboBox { IsEditable: true } or DatePicker;
        if (typing) return; // インライン編集中の Enter/Esc は各 TextBox が処理する

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
}
```

- [ ] **Step 3: FilterBar を書く**

`src/MoTask.App/Views/FilterBar.xaml`:

```xml
<UserControl x:Class="MoTask.App.Views.FilterBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:res="clr-namespace:MoTask.App.Resources">
  <Border BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,0,0,1" Padding="20.4,6.8">
    <DockPanel>
      <Button DockPanel.Dock="Right" Content="{x:Static res:Strings.NewTask}" Style="{StaticResource Btn.Primary}"
              Command="{Binding NewTaskCommand}" />
      <StackPanel Orientation="Horizontal">
        <ComboBox Width="180" ItemsSource="{Binding Filter.Projects}" DisplayMemberPath="Name"
                  SelectedItem="{Binding Filter.SelectedProject}" />
        <ItemsControl ItemsSource="{Binding Filter.Labels}" Margin="10.2,0,0,0" VerticalAlignment="Center">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <ToggleButton Content="{Binding Name}" IsChecked="{Binding IsSelected}" Style="{StaticResource Chip.Toggle}" Margin="0,0,3.4,0" />
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <ComboBox Width="120" Margin="10.2,0,0,0" ItemsSource="{Binding Filter.DueOptions}" DisplayMemberPath="Name"
                  SelectedItem="{Binding Filter.SelectedDue}" />
        <TextBlock Text="{x:Static res:Strings.Search}" Style="{StaticResource Text.Small}" Margin="13.6,0,3.4,0" VerticalAlignment="Center" />
        <TextBox x:Name="SearchBox" Width="220" Text="{Binding Filter.SearchText, UpdateSourceTrigger=PropertyChanged}" />
        <CheckBox Margin="13.6,0,0,0" VerticalAlignment="Center" Content="{x:Static res:Strings.ShowDeleted}"
                  IsChecked="{Binding Filter.ShowDeleted}" />
      </StackPanel>
    </DockPanel>
  </Border>
</UserControl>
```

`src/MoTask.App/Views/FilterBar.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace MoTask.App.Views;

public partial class FilterBar : UserControl
{
    public FilterBar()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
```

- [ ] **Step 4: BoardView を書く**

`src/MoTask.App/Views/BoardView.xaml`:

```xml
<UserControl x:Class="MoTask.App.Views.BoardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:res="clr-namespace:MoTask.App.Resources"
             xmlns:views="clr-namespace:MoTask.App.Views">
  <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled" Padding="20.4,13.6">
    <StackPanel Orientation="Horizontal">
      <ItemsControl x:Name="ColumnsHost" ItemsSource="{Binding Columns}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <views:ColumnView Margin="0,0,13.6,0" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <!-- 列を追加 -->
      <Border Width="280" VerticalAlignment="Top" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="1" Padding="10.2">
        <Grid>
          <Button Content="{x:Static res:Strings.AddColumn}" Style="{StaticResource Btn.Ghost}" HorizontalAlignment="Left"
                  Command="{Binding BeginAddColumnCommand}"
                  Visibility="{Binding IsAddingColumn, Converter={StaticResource BoolToVisibilityInverse}}" />
          <TextBox Text="{Binding NewColumnName, UpdateSourceTrigger=PropertyChanged}"
                   Visibility="{Binding IsAddingColumn, Converter={StaticResource BoolToVisibility}}"
                   KeyDown="NewColumnBox_KeyDown" IsVisibleChanged="EditBox_IsVisibleChanged" />
        </Grid>
      </Border>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

`src/MoTask.App/Views/BoardView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class BoardView : UserControl
{
    public BoardView()
    {
        InitializeComponent();
    }

    private void NewColumnBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BoardViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitAddColumnCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelAddColumnCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => FocusWhenVisible(sender, e);

    /// <summary>インライン入力欄が表示されたらフォーカスを移す。ColumnView からも使う。</summary>
    internal static void FocusWhenVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }
}
```

- [ ] **Step 5: ColumnView を書く**

`src/MoTask.App/Views/ColumnView.xaml`:

```xml
<UserControl x:Class="MoTask.App.Views.ColumnView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:res="clr-namespace:MoTask.App.Resources"
             xmlns:model="clr-namespace:MoTask.Core.Model;assembly=MoTask.Core"
             xmlns:views="clr-namespace:MoTask.App.Views">
  <Border Width="280" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="1">
    <DockPanel>
      <!-- ヘッダー（Task 18 で列ドラッグのハンドルにする） -->
      <Border x:Name="Header" DockPanel.Dock="Top" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,0,0,1"
              Padding="10.2,6.8" Background="Transparent">
        <DockPanel>
          <Button DockPanel.Dock="Right" Content="⋯" Style="{StaticResource Btn.Ghost}" Click="MenuButton_Click">
            <Button.ContextMenu>
              <ContextMenu DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}">
                <MenuItem Header="{x:Static res:Strings.Rename}" Command="{Binding BeginRenameCommand}" />
                <MenuItem Header="{x:Static res:Strings.ChangeRole}"
                          Visibility="{Binding IsDone, Converter={StaticResource BoolToVisibilityInverse}}">
                  <MenuItem Header="{x:Static res:Strings.RoleBacklog}" Command="{Binding SetRoleCommand}"
                            CommandParameter="{x:Static model:ColumnRole.Backlog}" />
                  <MenuItem Header="{x:Static res:Strings.RoleActive}" Command="{Binding SetRoleCommand}"
                            CommandParameter="{x:Static model:ColumnRole.Active}" />
                  <MenuItem Header="{x:Static res:Strings.RoleReview}" Command="{Binding SetRoleCommand}"
                            CommandParameter="{x:Static model:ColumnRole.Review}" />
                </MenuItem>
                <MenuItem Header="{x:Static res:Strings.SetWip}" Command="{Binding BeginEditWipCommand}" />
                <MenuItem Header="{x:Static res:Strings.ClearWip}" Command="{Binding ClearWipCommand}" />
                <Separator />
                <MenuItem Header="{x:Static res:Strings.DeleteColumn}" Command="{Binding DeleteColumnCommand}"
                          Visibility="{Binding IsDone, Converter={StaticResource BoolToVisibilityInverse}}" />
              </ContextMenu>
            </Button.ContextMenu>
          </Button>
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="{Binding Name}" Style="{StaticResource Text.Heading}" VerticalAlignment="Center"
                       Visibility="{Binding IsRenaming, Converter={StaticResource BoolToVisibilityInverse}}" />
            <TextBox Text="{Binding RenameText, UpdateSourceTrigger=PropertyChanged}" MinWidth="120"
                     Visibility="{Binding IsRenaming, Converter={StaticResource BoolToVisibility}}"
                     KeyDown="RenameBox_KeyDown" LostFocus="RenameBox_LostFocus" IsVisibleChanged="EditBox_IsVisibleChanged" />
            <TextBlock Text="{Binding CountText}" Margin="6.8,0,0,0" VerticalAlignment="Center">
              <TextBlock.Style>
                <Style TargetType="TextBlock" BasedOn="{StaticResource Text.Small}">
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding IsOverWip}" Value="True">
                      <Setter Property="Foreground" Value="{StaticResource Brush.Danger}" />
                      <Setter Property="FontWeight" Value="Bold" />
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </TextBlock.Style>
            </TextBlock>
            <TextBox Text="{Binding WipText, UpdateSourceTrigger=PropertyChanged}" Width="48" Margin="6.8,0,0,0"
                     Visibility="{Binding IsEditingWip, Converter={StaticResource BoolToVisibility}}"
                     KeyDown="WipBox_KeyDown" LostFocus="WipBox_LostFocus" IsVisibleChanged="EditBox_IsVisibleChanged" />
          </StackPanel>
        </DockPanel>
      </Border>

      <!-- インライン作成 -->
      <Border DockPanel.Dock="Bottom" Padding="10.2,6.8">
        <Grid>
          <Button Content="{x:Static res:Strings.AddTaskInline}" Style="{StaticResource Btn.Ghost}" HorizontalAlignment="Left"
                  Command="{Binding BeginAddTaskCommand}"
                  Visibility="{Binding IsAddingTask, Converter={StaticResource BoolToVisibilityInverse}}" />
          <TextBox Text="{Binding NewTaskTitle, UpdateSourceTrigger=PropertyChanged}"
                   Visibility="{Binding IsAddingTask, Converter={StaticResource BoolToVisibility}}"
                   KeyDown="NewTaskBox_KeyDown" LostFocus="NewTaskBox_LostFocus" IsVisibleChanged="EditBox_IsVisibleChanged" />
        </Grid>
      </Border>

      <!-- カード一覧（Task 18 で D&D を付ける） -->
      <ListBox x:Name="CardList" ItemsSource="{Binding Cards}" SelectedItem="{Binding SelectedCard}"
               Background="Transparent" BorderThickness="0" Padding="10.2,6.8"
               ScrollViewer.HorizontalScrollBarVisibility="Disabled" FocusVisualStyle="{x:Null}">
        <ListBox.ItemContainerStyle>
          <Style TargetType="ListBoxItem">
            <Setter Property="Margin" Value="0,0,0,6.8" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                  <Grid>
                    <Border x:Name="Frame" Background="Transparent" BorderBrush="{StaticResource Brush.Divider}"
                            BorderThickness="1" Padding="10.2" SnapsToDevicePixels="True">
                      <ContentPresenter />
                    </Border>
                    <Rectangle x:Name="Dashed" Stroke="{StaticResource Brush.Neutral.500}" StrokeThickness="1"
                               StrokeDashArray="3 2" Visibility="Collapsed" IsHitTestVisible="False" />
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Frame" Property="Background" Value="{StaticResource Brush.Accent.100}" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource Brush.Accent}" />
                      <Setter TargetName="Frame" Property="BorderThickness" Value="2" />
                      <Setter TargetName="Frame" Property="Padding" Value="9.2" />
                    </Trigger>
                    <DataTrigger Binding="{Binding IsDeleted}" Value="True">
                      <Setter TargetName="Frame" Property="BorderThickness" Value="0" />
                      <Setter TargetName="Frame" Property="Padding" Value="11.2" />
                      <Setter TargetName="Frame" Property="Opacity" Value="0.55" />
                      <Setter TargetName="Dashed" Property="Visibility" Value="Visible" />
                    </DataTrigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ListBox.ItemContainerStyle>
        <ListBox.ItemTemplate>
          <DataTemplate>
            <views:TaskCardView />
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </DockPanel>
  </Border>
</UserControl>
```

`src/MoTask.App/Views/ColumnView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class ColumnView : UserControl
{
    public ColumnView()
    {
        InitializeComponent();
    }

    private ColumnViewModel? Vm => DataContext as ColumnViewModel;

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitAddTaskCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelAddTaskCommand.Execute(null); e.Handled = true; }
    }

    private void NewTaskBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 空のまま離れたら閉じる。入力途中なら残す（Enter で確定、Esc で取り消し）
        if (Vm is { IsAddingTask: true } vm && string.IsNullOrWhiteSpace(vm.NewTaskTitle)) vm.CancelAddTaskCommand.Execute(null);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelRenameCommand.Execute(null); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => Vm?.CommitRenameCommand.Execute(null);

    private void WipBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitWipCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelEditWipCommand.Execute(null); e.Handled = true; }
    }

    private void WipBox_LostFocus(object sender, RoutedEventArgs e) => Vm?.CommitWipCommand.Execute(null);

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => BoardView.FocusWhenVisible(sender, e);
}
```

- [ ] **Step 6: TaskCardView を書く**

`src/MoTask.App/Views/TaskCardView.xaml`:

```xml
<UserControl x:Class="MoTask.App.Views.TaskCardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- 子要素はヒットテストしない: クリックと D&D は ListBoxItem が受ける -->
  <StackPanel IsHitTestVisible="False">
    <DockPanel>
      <TextBlock DockPanel.Dock="Right" Text="{Binding DueText}" Style="{StaticResource Text.Caption}"
                 Foreground="{Binding DueStatus, Converter={StaticResource DueStatusBrush}}" FontWeight="Medium" />
      <Border Background="{StaticResource Brush.Neutral.200}" Padding="5,1" HorizontalAlignment="Left"
              Visibility="{Binding ProjectName, Converter={StaticResource NullToVisibility}}">
        <TextBlock Text="{Binding ProjectName}" Style="{StaticResource Text.Caption}" Foreground="{StaticResource Brush.Neutral.800}" />
      </Border>
    </DockPanel>
    <TextBlock Text="{Binding Title}" TextWrapping="Wrap" Margin="0,3.4,0,0" />
    <ItemsControl ItemsSource="{Binding Labels}" Margin="0,6.8,0,0">
      <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
      </ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Border Background="{Binding Color, Converter={StaticResource RampBrush}}" Padding="5,1" Margin="0,0,3.4,3.4">
            <TextBlock Text="{Binding Name}" Style="{StaticResource Text.Caption}" Foreground="{StaticResource Brush.Accent.900}" />
          </Border>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
  </StackPanel>
</UserControl>
```

`src/MoTask.App/Views/TaskCardView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace MoTask.App.Views;

public partial class TaskCardView : UserControl
{
    public TaskCardView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: ビルドとテストを通し、手動確認する**

Run: `dotnet build MoTask.sln && dotnet test MoTask.sln`
Expected: ビルド成功、全テスト成功（Core 64 / Data 11 / App 22）

Run: `dotnet run --project src/MoTask.App`
確認項目:
- 4 列（未着手／進行中／確認待ち／完了）が枠線付きで横に並ぶ。
- 「＋ 追加」→ タイトル入力 → Enter でカードができ、選択枠（アクセント 2px）が付く。空のまま Enter では何も起きない。Esc で閉じる。
- `N` キーで先頭列（選択中があればその列）の入力欄が開く。TextBox にフォーカスがある間は `N` が文字として入力される。
- 検索欄に文字を入れるとカードが絞り込まれる。Ctrl+F で検索欄にフォーカスが移る。
- 列ヘッダーの「⋯」→ 改名／種別／WIP 設定／削除。完了列では「種別を変更」「列を削除」が出ない。WIP を 1 にして 2 件入れると件数が赤く `2 / 1` になる。タスクが残る列の削除はバナーで拒否理由が出る。
- 「＋ 列を追加」で列が末尾に増える。
- Delete キーで選択中のカードが消え、「削除済みを表示」をオンにすると点線枠・薄い表示で戻る。

- [ ] **Step 8: コミット**

```bash
git add src/MoTask.App
git commit -m "feat(app): add main window, filter bar, board, column and card views

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 18: ドラッグ＆ドロップ（カードの並び替え・列間移動・列の並び替え）

**Files:**
- Create: `src/MoTask.App/DragDrop/DropPositionCalculator.cs`, `CardDropHandler.cs`, `ColumnDragHandler.cs`, `ColumnDropHandler.cs`
- Modify: `src/MoTask.App/ViewModels/BoardViewModel.cs`（ハンドラのプロパティ）、`Views/ColumnView.xaml`（ListBox とヘッダーの dd 属性）、`Views/BoardView.xaml`（列 ItemsControl の dd 属性）
- Test: `tests/MoTask.App.Tests/DropPositionCalculatorTests.cs`

**Interfaces:**
- Consumes: `BoardViewModel.MoveCardAsync(card, target, position)`, `ReorderColumnsAsync(order)`、GongSolutions の `IDropTarget`, `DefaultDragHandler`, `IDropInfo.InsertIndex / TargetCollection / Data`。
- Produces: `DropPositionCalculator.ToPosition(visible, all, moving, insertIndex)` と `Reorder<T>(current, moving, insertIndex)`。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/DropPositionCalculatorTests.cs`:

```csharp
using FluentAssertions;
using MoTask.App.DragDrop;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class DropPositionCalculatorTests
{
    private static TaskCardViewModel Card(int id) => new(new TaskItem { Id = id, Title = id.ToString() });

    private static readonly TaskCardViewModel A = Card(1), B = Card(2), C = Card(3), X = Card(9);
    private static readonly TaskCardViewModel[] All = { A, B, C };

    [Theory]
    [InlineData(0, null)]  // 自分の上 → 変化なし
    [InlineData(1, 0)]     // A と B の間 → 位置 0（変化なし相当）
    [InlineData(2, 1)]     // B と C の間 → [B, A, C]
    [InlineData(3, 2)]     // 末尾 → [B, C, A]
    public void SameColumn_MovingFirstCard(int insertIndex, int? expected)
    {
        DropPositionCalculator.ToPosition(All, All, A, insertIndex).Should().Be(expected);
    }

    [Fact]
    public void SameColumn_MovingLastCardToTop()
    {
        DropPositionCalculator.ToPosition(All, All, C, 0).Should().Be(0);
    }

    [Fact]
    public void OtherColumn_InsertsBeforeAnchor_OrAtEnd()
    {
        DropPositionCalculator.ToPosition(All, All, X, 1).Should().Be(1);
        DropPositionCalculator.ToPosition(All, All, X, 3).Should().Be(3);
        DropPositionCalculator.ToPosition(Array.Empty<TaskCardViewModel>(), Array.Empty<TaskCardViewModel>(), X, 0).Should().Be(0);
    }

    [Fact]
    public void HiddenCards_AreCountedInAllButNotInVisible()
    {
        var visible = new[] { A, C }; // B はフィルタで非表示
        DropPositionCalculator.ToPosition(visible, All, X, 1).Should().Be(2, "C の前 = 全件では index 2");
        DropPositionCalculator.ToPosition(visible, All, A, 2).Should().Be(2, "A を除いた [B, C] の末尾");
    }

    [Fact]
    public void Reorder_MovesItemToInsertIndex()
    {
        var items = new[] { "a", "b", "c", "d" };
        DropPositionCalculator.Reorder(items, "a", 3).Should().Equal("b", "c", "a", "d");
        DropPositionCalculator.Reorder(items, "d", 0).Should().Equal("d", "a", "b", "c");
        DropPositionCalculator.Reorder(items, "b", 1).Should().Equal("a", "b", "c", "d");
        DropPositionCalculator.Reorder(items, "b", 4).Should().Equal("a", "c", "d", "b");
    }
}
```

- [ ] **Step 2: テストが失敗することを確認する**

Run: `dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~DropPositionCalculatorTests"`
Expected: コンパイルエラー

- [ ] **Step 3: 計算とハンドラを書く**

`src/MoTask.App/DragDrop/DropPositionCalculator.cs`:

```csharp
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

public static class DropPositionCalculator
{
    /// <summary>
    /// Gong の InsertIndex（表示カード列での挿入位置）を、Core の position
    /// （移動カードを除いた列内全カード列での挿入位置）に変換する。null は「自分の上に落とした」= 変化なし。
    /// </summary>
    public static int? ToPosition(
        IReadOnlyList<TaskCardViewModel> visible,
        IReadOnlyList<TaskCardViewModel> all,
        TaskCardViewModel moving,
        int insertIndex)
    {
        var others = all.Where(c => c.Id != moving.Id).ToList();
        if (insertIndex >= visible.Count) return others.Count;
        var anchor = visible[Math.Max(0, insertIndex)];
        if (anchor.Id == moving.Id) return null;
        return others.IndexOf(anchor);
    }

    /// <summary>列の並び替え: current から moving を抜き、InsertIndex に差し込んだ新しい順序を返す。</summary>
    public static IReadOnlyList<T> Reorder<T>(IReadOnlyList<T> current, T moving, int insertIndex) where T : class
    {
        var list = current.ToList();
        var from = list.IndexOf(moving);
        if (from < 0) return current;
        list.RemoveAt(from);
        if (insertIndex > from) insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, list.Count);
        list.Insert(insertIndex, moving);
        return list;
    }
}
```

`src/MoTask.App/DragDrop/CardDropHandler.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>カードの列内並び替えと列間移動。ドロップ時に MoveTask を1回呼ぶ。</summary>
public sealed class CardDropHandler : IDropTarget
{
    private readonly BoardViewModel _board;

    public CardDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not TaskCardViewModel) return;
        if (dropInfo.TargetCollection is not ObservableCollection<TaskCardViewModel>) return;
        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        dropInfo.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not TaskCardViewModel card) return;
        if (dropInfo.TargetCollection is not ObservableCollection<TaskCardViewModel> cards) return;
        var target = _board.Columns.FirstOrDefault(c => ReferenceEquals(c.Cards, cards));
        if (target is null) return;

        var position = DropPositionCalculator.ToPosition(target.Cards, target.AllCards, card, dropInfo.InsertIndex);
        if (position is null) return;
        _ = _board.MoveCardAsync(card, target, position.Value);
    }
}
```

`src/MoTask.App/DragDrop/ColumnDragHandler.cs`:

```csharp
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>列ヘッダー（ItemsControl ではない要素）からのドラッグ。Data に ColumnViewModel を載せる。</summary>
public sealed class ColumnDragHandler : DefaultDragHandler
{
    public override bool CanStartDrag(IDragInfo dragInfo)
        => (dragInfo.VisualSource as FrameworkElement)?.DataContext is ColumnViewModel;

    public override void StartDrag(IDragInfo dragInfo)
    {
        var column = (dragInfo.VisualSource as FrameworkElement)?.DataContext as ColumnViewModel;
        dragInfo.Data = column;
        dragInfo.Effects = column is null ? DragDropEffects.None : DragDropEffects.Move;
    }
}
```

`src/MoTask.App/DragDrop/ColumnDropHandler.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

public sealed class ColumnDropHandler : IDropTarget
{
    private readonly BoardViewModel _board;

    public ColumnDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not ColumnViewModel) return;
        if (dropInfo.TargetCollection is not ObservableCollection<ColumnViewModel>) return;
        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        dropInfo.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not ColumnViewModel moving) return;
        if (dropInfo.TargetCollection is not ObservableCollection<ColumnViewModel>) return;
        var order = DropPositionCalculator.Reorder(_board.Columns.ToList(), moving, dropInfo.InsertIndex);
        _ = _board.ReorderColumnsAsync(order);
    }
}
```

- [ ] **Step 4: BoardViewModel にハンドラを持たせる**

`BoardViewModel.cs` に `using MoTask.App.DragDrop;` を追加し、`Filter` プロパティの下に追加:

```csharp
    public CardDropHandler CardDropHandler { get; }
    public ColumnDragHandler ColumnDragHandler { get; } = new();
    public ColumnDropHandler ColumnDropHandler { get; }
```

コンストラクタ内に追加:

```csharp
        CardDropHandler = new CardDropHandler(this);
        ColumnDropHandler = new ColumnDropHandler(this);
```

- [ ] **Step 5: XAML に D&D を付ける**

`ColumnView.xaml` のルート `UserControl` に名前空間を追加:

```xml
             xmlns:dd="urn:gong-wpf-dragdrop"
```

ヘッダー `Border x:Name="Header"` に属性を追加（列の並び替えはヘッダーのドラッグ）:

```xml
              dd:DragDrop.IsDragSource="True"
              dd:DragDrop.DragHandler="{Binding Board.ColumnDragHandler}"
              dd:DragDrop.UseDefaultDragAdorner="True"
```

`ListBox x:Name="CardList"` に属性を追加:

```xml
               dd:DragDrop.IsDragSource="True"
               dd:DragDrop.IsDropTarget="True"
               dd:DragDrop.DropHandler="{Binding Board.CardDropHandler}"
               dd:DragDrop.UseDefaultDragAdorner="True"
```

`BoardView.xaml` のルートに `xmlns:dd="urn:gong-wpf-dragdrop"` を追加し、`ItemsControl x:Name="ColumnsHost"` に追加:

```xml
                    dd:DragDrop.IsDropTarget="True"
                    dd:DragDrop.DropHandler="{Binding ColumnDropHandler}"
```

- [ ] **Step 6: テストを通し、手動確認する**

Run: `dotnet test tests/MoTask.App.Tests`
Expected: `Passed! - Failed: 0, Passed: 30`

Run: `dotnet run --project src/MoTask.App`
確認項目（仕様 §9 手動確認）:
- 列内でカードを上下にドラッグすると挿入位置に線（インジケータ）が出て、離すと並びが変わる。アプリを再起動しても並びが保たれる。
- 列間でドラッグすると移動し、詳細パネル（Task 19 以降）の履歴に「未着手 → 進行中」が残る。完了列へ入れると CompletedAt が入る（Task 19 のパネルで所属列を確認）。
- 空の列にもドロップできる。
- 列ヘッダーをドラッグすると列の順序が変わり、再起動後も保たれる。
- ヘッダーのドラッグが始まらない場合の代替: `ColumnsHost` に `dd:DragDrop.IsDragSource="True"` を付け、`CardList` と「インライン作成」の `Border` に `dd:DragDrop.DragSourceIgnore="True"` を付ける（このとき `ColumnDragHandler` と `Header` の dd 属性は外す）。

- [ ] **Step 7: コミット**

```bash
git add src/MoTask.App tests/MoTask.App.Tests
git commit -m "feat(app): add drag and drop for cards and columns

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 19: 詳細パネルの View

**Files:**
- Create: `src/MoTask.App/Views/TaskDetailPanel.xaml(.cs)`
- Modify: `src/MoTask.App/Views/MainWindow.xaml`（パネルを右側に配置）

**Interfaces:**
- Consumes: `TaskDetailViewModel`（Task 16）。

- [ ] **Step 1: パネルを書く**

`src/MoTask.App/Views/TaskDetailPanel.xaml`:

```xml
<UserControl x:Class="MoTask.App.Views.TaskDetailPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:res="clr-namespace:MoTask.App.Resources">
  <UserControl.Resources>
    <Style x:Key="Field.Label" TargetType="TextBlock" BasedOn="{StaticResource Text.Label}">
      <Setter Property="Margin" Value="0,13.6,0,3.4" />
    </Style>
  </UserControl.Resources>
  <Border BorderBrush="{StaticResource Brush.Divider}" BorderThickness="1,0,0,0" Padding="20.4,13.6"
          Background="{StaticResource Brush.Bg}">
    <DockPanel>
      <DockPanel DockPanel.Dock="Top">
        <Button DockPanel.Dock="Right" Content="×" Style="{StaticResource Btn.Ghost}" Command="{Binding CloseCommand}"
                ToolTip="{x:Static res:Strings.Close}" />
        <TextBlock Text="{Binding SelectedColumn.Name}" Style="{StaticResource Text.Caption}" VerticalAlignment="Center" />
      </DockPanel>

      <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,13.6,0,0">
        <Button Content="{x:Static res:Strings.Delete}" Command="{Binding DeleteCommand}"
                Visibility="{Binding IsDeleted, Converter={StaticResource BoolToVisibilityInverse}}" />
        <Button Content="{x:Static res:Strings.Restore}" Style="{StaticResource Btn.Primary}" Command="{Binding RestoreCommand}"
                Visibility="{Binding IsDeleted, Converter={StaticResource BoolToVisibility}}" />
        <TextBlock Text="{x:Static res:Strings.Deleted}" Foreground="{StaticResource Brush.Danger}" Margin="10.2,0,0,0"
                   VerticalAlignment="Center" Visibility="{Binding IsDeleted, Converter={StaticResource BoolToVisibility}}" />
      </StackPanel>

      <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel>
          <!-- タイトル: フォーカスアウトで保存、Esc で取り消し -->
          <TextBox Text="{Binding Title, UpdateSourceTrigger=LostFocus}" Margin="0,6.8,0,0"
                   FontFamily="{StaticResource Font.Heading}" FontSize="{StaticResource FontSize.H4}" FontWeight="SemiBold"
                   KeyDown="EditBox_KeyDown">
            <TextBox.Style>
              <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                <Style.Triggers>
                  <DataTrigger Binding="{Binding HasTitleError}" Value="True">
                    <Setter Property="BorderBrush" Value="{StaticResource Brush.Danger}" />
                  </DataTrigger>
                </Style.Triggers>
              </Style>
            </TextBox.Style>
          </TextBox>

          <TextBlock Text="{x:Static res:Strings.Description}" Style="{StaticResource Field.Label}" />
          <TextBox Text="{Binding Description, UpdateSourceTrigger=LostFocus}" AcceptsReturn="True" TextWrapping="Wrap"
                   MinHeight="80" VerticalScrollBarVisibility="Auto" KeyDown="EditBox_KeyDown" />

          <TextBlock Text="{x:Static res:Strings.Project}" Style="{StaticResource Field.Label}" />
          <ComboBox ItemsSource="{Binding Projects}" DisplayMemberPath="Name" SelectedItem="{Binding SelectedProject}" />
          <TextBlock Text="{x:Static res:Strings.NewProjectHint}" Style="{StaticResource Text.Caption}" Margin="0,6.8,0,2" />
          <TextBox Text="{Binding NewProjectName, UpdateSourceTrigger=PropertyChanged}" KeyDown="NewProjectBox_KeyDown" />

          <TextBlock Text="{x:Static res:Strings.Labels}" Style="{StaticResource Field.Label}" />
          <ItemsControl ItemsSource="{Binding Labels}">
            <ItemsControl.ItemsPanel>
              <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <ToggleButton Content="{Binding Name}" IsChecked="{Binding IsSelected}" Style="{StaticResource Chip.Toggle}" Margin="0,0,3.4,3.4" />
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
          <TextBlock Text="{x:Static res:Strings.NewLabelHint}" Style="{StaticResource Text.Caption}" Margin="0,3.4,0,2" />
          <TextBox Text="{Binding NewLabelName, UpdateSourceTrigger=PropertyChanged}" KeyDown="NewLabelBox_KeyDown" />

          <TextBlock Text="{x:Static res:Strings.DueDate}" Style="{StaticResource Field.Label}" />
          <DatePicker SelectedDate="{Binding DueDate}" />

          <TextBlock Text="{x:Static res:Strings.Column}" Style="{StaticResource Field.Label}" />
          <ComboBox ItemsSource="{Binding Columns}" DisplayMemberPath="Name" SelectedItem="{Binding SelectedColumn}" />

          <TextBlock Text="{x:Static res:Strings.History}" Style="{StaticResource Field.Label}" />
          <ItemsControl ItemsSource="{Binding History}">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <TextBlock Text="{Binding}" Style="{StaticResource Text.Small}" Margin="0,0,0,3.4" TextWrapping="Wrap" />
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </StackPanel>
      </ScrollViewer>
    </DockPanel>
  </Border>
</UserControl>
```

`src/MoTask.App/Views/TaskDetailPanel.xaml.cs`:

```csharp
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class TaskDetailPanel : UserControl
{
    public TaskDetailPanel()
    {
        InitializeComponent();
    }

    private TaskDetailViewModel? Vm => DataContext as TaskDetailViewModel;

    /// <summary>Esc: 編集を取り消して VM の値に戻す。Enter（単一行）: フォーカスを移して確定。</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void NewProjectBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.CreateProjectCommand.Execute(null); e.Handled = true; }
    }

    private void NewLabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.CreateLabelCommand.Execute(null); e.Handled = true; }
    }
}
```

- [ ] **Step 2: MainWindow に配置する**

`MainWindow.xaml` の `<!-- 詳細パネルは Task 19 でここに置く -->` を置き換え:

```xml
      <views:TaskDetailPanel Grid.Column="1" Width="360" DataContext="{Binding Detail}"
                             Visibility="{Binding DataContext.Detail, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource NullToVisibility}}" />
```

- [ ] **Step 3: ビルドして手動確認する**

Run: `dotnet build MoTask.sln && dotnet test MoTask.sln && dotnet run --project src/MoTask.App`
確認項目:
- カードをクリックすると右に幅 360 のパネルが開き、× か Esc で閉じる。
- タイトルを書き換えて Tab で離れると保存され、履歴に「タイトル を変更」が増える。空にして離れると赤枠になり保存されない。Esc で元に戻る。
- 説明・プロジェクト・期限・ラベル・所属列の変更がそれぞれ即保存される。所属列を「完了」にするとカードが完了列へ移り、履歴に「未着手 → 完了」が出る。
- 「新しいプロジェクト名を入力して Enter」でプロジェクトができ、そのタスクに割り当たり、フィルタバーの選択肢にも増える。ラベルも同様。
- 「削除」で点線表示になり「復元」に切り替わる。復元で戻る。
- 履歴は新しい順で「9/4 8:40 未着手 → 進行中」の形。

- [ ] **Step 4: コミット**

```bash
git add src/MoTask.App
git commit -m "feat(app): add task detail panel

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 20: 最終確認・README・DB 復旧の手動確認

**Files:**
- Create: `README.md`
- Modify: `.gitignore`（`design-time.db`）

- [ ] **Step 1: README を書く**

`README.md`:

```markdown
# MoTask

個人用カンバン（v1）。Windows 11 / .NET 10 / WPF / SQLite。

## 使い方

```bash
dotnet run --project src/MoTask.App
```

データは `%LOCALAPPDATA%\MoTask\motask.db` に保存されます。

| キー | 動作 |
| --- | --- |
| N | 新規タスク（選択中の列、なければ先頭の列） |
| Delete | 選択中のタスクを論理削除 |
| Esc | 詳細パネルを閉じる／インライン編集を取り消す |
| Ctrl+F | 検索欄にフォーカス |

## 開発

```bash
dotnet test MoTask.sln
dotnet ef migrations add <Name> --project src/MoTask.Data --output-dir Migrations
```

構成は `docs/superpowers/specs/2026-09-04-motask-kanban-v1-design.md` を参照。
```

`.gitignore` に追記:

```
design-time.db
```

- [ ] **Step 2: 全テストと起動を通す**

Run: `dotnet build MoTask.sln -warnaserror:CS8600,CS8602,CS8603 && dotnet test MoTask.sln`
Expected: ビルド成功、全テスト成功（Core 64 / Data 11 / App 30）

- [ ] **Step 3: DB 復旧を手動確認する**

アプリを終了した状態で:

```bash
cp "$LOCALAPPDATA/MoTask/motask.db" /tmp/motask-good.db
printf 'broken' > "$LOCALAPPDATA/MoTask/motask.db"
dotnet run --project src/MoTask.App
```

Expected: 「データベースを開けませんでした…バックアップを作成して新しく作り直しますか？」のダイアログ。「いいえ」で終了する。再度起動して「はい」を選ぶと `motask.db.bak-<日時>` ができ、既定の4列で起動する。確認後 `/tmp/motask-good.db` を戻す。

- [ ] **Step 4: 仕様 §9 の手動確認を一通り行う**

- 列内／列間／列の並び替えの D&D（Task 18 の確認項目）
- フォント: 見出しが Barlow Condensed、本文が Barlow、日本語が Yu Gothic UI にフォールバックしている
- 期限超過のカードが赤、当日がアクセント色

- [ ] **Step 5: コミット**

```bash
git add README.md .gitignore
git commit -m "docs: add README and ignore design-time db

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 仕様との対応（自己チェック）

| 仕様 | タスク |
| --- | --- |
| §3 技術選定、DB の場所、resx | 1, 2, 10, 14 |
| §4.1 Core エンティティ・ユースケース・TaskFilter・Result | 2〜9 |
| §4.2 Data（DbContext、マイグレーション、起動時投入） | 10〜12 |
| §4.3 App（ホスト、ViewModel、View、テーマ、フォント） | 14〜19 |
| §5 Done 列のルール、WIP 警告、Position 再採番、CompletedAt、履歴の同一トランザクション | 4, 7, 12 |
| §6 レイアウト、列、カード、詳細パネル、D&D、キーボード、見た目 | 17, 18, 19 |
| §7 即時コミット、楽観的更新と巻き戻し、GetBoard は起動時と復帰時のみ | 15 |
| §8 DB 作成・復旧、保存失敗バナー、空タイトル、列削除拒否 | 13, 14, 15, 16, 20 |
| §9 Core／Data／App のテスト、手動確認 | 各タスク、17, 18, 19, 20 |

意図的に UI に出していないもの: `ArchiveProject`（ユースケースとテストは Task 8 にあるが、v1 の画面に導線がない。仕様 §6 に該当画面がないため）。
