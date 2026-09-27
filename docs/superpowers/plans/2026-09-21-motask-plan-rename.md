# 「朝のプラン」を「計画」にする 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 画面と語彙を「朝の実行プラン」から「計画」に付け替え、型・名前空間・リソースキー・MCP ツール名・テーブル名をその呼び名に揃える。振る舞いは変えない。

**Architecture:** 3 コミット。(1) §3 の機械的改名を `git mv` ＋ 順序を決めた `sed` の一括置換で行い、ビルドと全テストが緑であることを証明にする。(2) §4 の利用者文言とコード内コメントの語彙を同じやり方で置き換える。(3) §5 のレイアウトを `PlanView.xaml` だけで直す。ViewModel・サービス・DB の振る舞いには一切触れない。

**Tech Stack:** .NET 10 / WPF / EF Core 10 (SQLite) / xunit + FluentAssertions / dotnet-ef（ローカルツール）

**Spec:** `docs/superpowers/specs/2026-09-21-motask-plan-rename-design.md`

## Global Constraints

- **振る舞いを変えない。** 既存テストのアサーションの中身は変えない。名前と文言だけを変える。例外は本計画が明示する箇所に限る。
- **新しいテストは足さない。** 仕様 §7 の決定。
- **既存の設計仕様・実装計画（`docs/superpowers/specs/` と `docs/superpowers/plans/` の他ファイル）は書き換えない。** 仕様 §8。本計画の `sed` はすべて `src` と `tests` だけを対象にする。`docs` と `Incomplete design request/` には触れない。
- **コード内の `仕様 §6` のような節番号参照はそのまま残す。** 旧文書の節を指し続ける（仕様 §8）。
- 命名の 3 語（仕様 §3）: 実行とライフサイクル = `Planning*` / 計画の中身と画面 = `Plan*` / 候補の仕分け = `Triage*`。
- 残す語: 「仕分け」「候補キュー」「最初にやる1件」「今日中／余裕があれば／AI 準備完了／待ち」。
- ベースライン（着手前に実測済み・2026-09-21）: `dotnet test MoTask.sln` は **882 件すべて緑**。内訳は MoTask.Core.Tests 353 / MoTask.App.Tests 465 / MoTask.Data.Tests 42 / MoTask.Mcp.Tests 22。**改名後も件数は 882 のまま**でなければならない（テストを消していない証明）。
- 作業ディレクトリは worktree `D:\source\cs\MoTask\.worktrees\worktree-plan-rename`、ブランチは `worktree-plan-rename`。以降のコマンドはすべてこのディレクトリの Bash から実行する。

### 仕様に無い判断（この計画で決めたこと）

仕様 §3 は「内部名の全面改名」と言うが、次の 3 つは表に載っていない。`Morning` を 1 つも残さないという §2 の要求に従い、こう決める。

| 対象 | 決定 | 理由 |
| --- | --- | --- |
| `TriageCandidate.MorningRunId`（と DB の同名カラム・FK・索引） | `PlanningRunId` | `TriageCandidates` テーブル名は据え置き（仕様 §3 Data）だが、カラムは実行への参照なので `Planning*`。マイグレーションは書き直すので改名の代償は無い |
| `TaskRowOrigin.RegisteredThisMorning` / `MergedThisMorning` | `RegisteredThisRun` / `MergedThisRun` | 「この実行の候補から生まれた」の意。仕様 §4 が `CandidateAlreadyInThisRun` の文言を「この実行ですでに積んだ」に変えるのと同じ語 |
| `TerminalLauncher.MorningTemplate` | `PlanningTemplate` | 計画づくりの起動テンプレート＝ライフサイクル側 |

---

## ファイル構成

### 移動・改名するファイル（Task 1 で `git mv`）

**src/MoTask.Core**

| 現在 | 改名後 |
| --- | --- |
| `Abstractions/IMorningRepository.cs` | `Abstractions/IPlanningRepository.cs` |
| `Model/MorningRun.cs` | `Model/PlanningRun.cs` |
| `Model/MorningRunStatus.cs` | `Model/PlanningRunStatus.cs` |
| `Morning/`（ディレクトリ） | `Planning/` |
| `Morning/MorningInstruction.cs` | `Planning/PlanningInstruction.cs` |
| `Morning/MorningOutcomes.cs` | `Planning/PlanningOutcomes.cs` |
| `Morning/MorningPlanResolver.cs` | `Planning/PlanResolver.cs` |
| `Morning/MorningPlanValidator.cs` | `Planning/PlanValidator.cs` |
| `Morning/MorningRunDescriptor.cs` | `Planning/PlanningRunDescriptor.cs` |
| `Morning/{BoardSnapshot,CandidateInput,CandidateNote,CandidateRecord,CandidateValidator,ResolvedPlan}.cs` | `Planning/` へ移動のみ（名前は据え置き） |
| `Services/IMorningService.cs` | `Services/IPlanningService.cs` |
| `Services/MorningService.cs` | `Services/PlanningService.cs` |
| `Services/MorningRunChangedEventArgs.cs` | `Services/PlanningRunChangedEventArgs.cs` |

**src/MoTask.Data**

| 現在 | 改名後 |
| --- | --- |
| `Repositories/MorningRepository.cs` | `Repositories/PlanningRepository.cs` |
| `Migrations/20260907135320_AddMorningRuns.cs` | `Migrations/20260907135320_AddPlanningRuns.cs` |
| `Migrations/20260907135320_AddMorningRuns.Designer.cs` | `Migrations/20260907135320_AddPlanningRuns.Designer.cs` |

**src/MoTask.App**

| 現在 | 改名後 |
| --- | --- |
| `Ai/MorningTools/`（ディレクトリ） | `Ai/PlanningTools/` |
| `Ai/MorningTools/MorningArgs.cs` | `Ai/PlanningTools/PlanningArgs.cs` |
| `Ai/MorningTools/MorningToolHost.cs` | `Ai/PlanningTools/PlanningToolHost.cs` |
| `MorningKeyMap.cs` | `TriageKeyMap.cs` |
| `ViewModels/MorningPlanViewModel.cs` | `ViewModels/PlanViewModel.cs` |
| `Views/MorningPlanView.xaml` | `Views/PlanView.xaml` |
| `Views/MorningPlanView.xaml.cs` | `Views/PlanView.xaml.cs` |

**tests**（15 ファイル ＋ フィクスチャ 1）

| 現在 | 改名後 |
| --- | --- |
| `MoTask.App.Tests/Fakes/FakeMorningService.cs` | `Fakes/FakePlanningService.cs` |
| `MoTask.App.Tests/MorningKeyMapTests.cs` | `TriageKeyMapTests.cs` |
| `MoTask.App.Tests/MorningPlanViewModelTests.cs` | `PlanViewModelTests.cs` |
| `MoTask.App.Tests/MorningToolHostTests.cs` | `PlanningToolHostTests.cs` |
| `MoTask.Core.Tests/MorningInstructionTests.cs` | `PlanningInstructionTests.cs` |
| `MoTask.Core.Tests/MorningModelTests.cs` | `PlanningModelTests.cs` |
| `MoTask.Core.Tests/MorningPlanResolverTests.cs` | `PlanResolverTests.cs` |
| `MoTask.Core.Tests/MorningPlanValidatorTests.cs` | `PlanValidatorTests.cs` |
| `MoTask.Core.Tests/MorningRunDescriptorTests.cs` | `PlanningRunDescriptorTests.cs` |
| `MoTask.Core.Tests/MorningServiceBulkTests.cs` | `PlanningServiceBulkTests.cs` |
| `MoTask.Core.Tests/MorningServiceLifecycleTests.cs` | `PlanningServiceLifecycleTests.cs` |
| `MoTask.Core.Tests/MorningServiceMcpTests.cs` | `PlanningServiceMcpTests.cs` |
| `MoTask.Core.Tests/MorningServiceStartTests.cs` | `PlanningServiceStartTests.cs` |
| `MoTask.Core.Tests/MorningServiceTriageTests.cs` | `PlanningServiceTriageTests.cs` |
| `MoTask.Data.Tests/MorningRepositoryTests.cs` | `PlanningRepositoryTests.cs` |
| `MoTask.Core.Tests/Fixtures/morning-plan.json` | `Fixtures/plan.json` |

フィクスチャは `MoTask.Core.Tests.csproj` の `<None Include="Fixtures\**">` で丸ごと出力へコピーされるので、csproj の変更は要らない。

### 内容だけ変わるファイル（`sed` が届く範囲）

`src` と `tests` の `*.cs` / `*.xaml` / `*.resx` / `*.json` すべて（`bin` / `obj` を除く）。Task 1 の置換は対象ファイルを指定せず、この範囲に一括で当てる。名前の一貫性はこの「全部まとめて 1 回」が保証する。

### 手で書き換えるファイル

| ファイル | Task | 理由 |
| --- | --- | --- |
| `src/MoTask.App/Views/MainWindow.xaml.cs` | 1 | フィールド `_morning` とコンストラクタ引数 `morning` の改名先が `_plan` / `plan` で、一括規則に載らない |
| `src/MoTask.App/Views/PlanView.xaml` | 3 | §5 のレイアウト |

---

## Task 1: §3 の機械的改名

型・名前空間・ファイル名・リソースキー・MCP ツール名・テーブル名・マイグレーションを一度に改名する。仕様 §9 のコミット 1。ここは完全に機械的で、レビューは「`Morning` が 1 つも残っていない」「882 件が緑」の 2 点だけを見る。

**Files:**
- Move: 上の「移動・改名するファイル」表のすべて
- Modify: `src` と `tests` の `*.cs` / `*.xaml` / `*.resx`（`bin` / `obj` を除く）
- Modify by hand: `src/MoTask.App/Views/MainWindow.xaml.cs`
- Test: 既存テスト全件（`dotnet test MoTask.sln`）

**Interfaces:**
- Consumes: なし（最初のタスク）
- Produces: 以降のタスクが使う名前。主なもの —
  - `MoTask.Core.Planning` 名前空間、`PlanningInstruction.Build(string? template, DateOnly date, int runId) : string`、`PlanningInstruction.DefaultTemplate : string`、`PlanResolver.Resolve(...)`、`PlanValidator.Validate(string) : Result<string>`、`PlanValidator.PlanGroupKeys`
  - `MoTask.Core.Model.PlanningRun`、`PlanningRunStatus`、`TriageCandidate.PlanningRunId : int`
  - `MoTask.Core.Services.IPlanningService` / `PlanningService`、`PlanningRunChangedEventArgs`、`PlanningRunSnapshot`、`PlanningOutcome`
  - `MoTask.Core.Abstractions.IPlanningRepository`、`MoTask.Data.Repositories.PlanningRepository`、`MoTaskDbContext.PlanningRuns`、テーブル `PlanningRuns`
  - `MoTask.Core.Ai.JobFolderPaths.PlanningDirectoryName = "planning"`、`AiSettings.PlanningInstruction`
  - `MoTask.App.Ai.PlanningTools.PlanningToolHost`（定数 `GetContext = "planning_get_context"` / `AddCandidate = "planning_add_candidate"` / `SubmitPlan = "planning_submit_plan"` / `Complete = "planning_complete"`）、`PlanningArgs`
  - `MoTask.App.ViewModels.PlanViewModel`、`MoTask.App.Views.PlanView`、`MoTask.App.TriageKeyMap`
  - `Strings.Plan*` / `Strings.McpPlanning*` / `Strings.SettingsPlanningInstruction` / `Strings.ViewPlan`、`Messages.Planning*` / `Messages.PlanNotSubmitted`

---

- [ ] **Step 1: 一括置換のヘルパーを定義する**

以降のステップはすべてこのシェル関数を使う。`src` と `tests` だけを見る（`docs` は仕様 §8 で対象外）。

```bash
rn() {  # rn <sed の検索パターン> <置換後>
  grep -rlZ --include='*.cs' --include='*.xaml' --include='*.resx' --include='*.json' \
       --exclude-dir=bin --exclude-dir=obj -- "$1" src tests |
  xargs -0 -r sed -i "s/$1/$2/g"
}
```

このシェルセッションを閉じると関数も消える。Task 1 は 1 つの Bash セッションの中で通して実行すること。

- [ ] **Step 2: ファイルとディレクトリを `git mv` で移動する**

C# はファイル名と型名が無関係で、WPF も `x:Class` で partial class を決めるので、この時点ではビルドは通ったままになる。

```bash
git mv src/MoTask.Core/Abstractions/IMorningRepository.cs src/MoTask.Core/Abstractions/IPlanningRepository.cs
git mv src/MoTask.Core/Model/MorningRun.cs               src/MoTask.Core/Model/PlanningRun.cs
git mv src/MoTask.Core/Model/MorningRunStatus.cs         src/MoTask.Core/Model/PlanningRunStatus.cs
git mv src/MoTask.Core/Morning                           src/MoTask.Core/Planning
git mv src/MoTask.Core/Planning/MorningInstruction.cs    src/MoTask.Core/Planning/PlanningInstruction.cs
git mv src/MoTask.Core/Planning/MorningOutcomes.cs       src/MoTask.Core/Planning/PlanningOutcomes.cs
git mv src/MoTask.Core/Planning/MorningPlanResolver.cs   src/MoTask.Core/Planning/PlanResolver.cs
git mv src/MoTask.Core/Planning/MorningPlanValidator.cs  src/MoTask.Core/Planning/PlanValidator.cs
git mv src/MoTask.Core/Planning/MorningRunDescriptor.cs  src/MoTask.Core/Planning/PlanningRunDescriptor.cs
git mv src/MoTask.Core/Services/IMorningService.cs             src/MoTask.Core/Services/IPlanningService.cs
git mv src/MoTask.Core/Services/MorningService.cs              src/MoTask.Core/Services/PlanningService.cs
git mv src/MoTask.Core/Services/MorningRunChangedEventArgs.cs  src/MoTask.Core/Services/PlanningRunChangedEventArgs.cs

git mv src/MoTask.Data/Repositories/MorningRepository.cs src/MoTask.Data/Repositories/PlanningRepository.cs
git mv src/MoTask.Data/Migrations/20260907135320_AddMorningRuns.cs \
       src/MoTask.Data/Migrations/20260907135320_AddPlanningRuns.cs
git mv src/MoTask.Data/Migrations/20260907135320_AddMorningRuns.Designer.cs \
       src/MoTask.Data/Migrations/20260907135320_AddPlanningRuns.Designer.cs

git mv src/MoTask.App/Ai/MorningTools                     src/MoTask.App/Ai/PlanningTools
git mv src/MoTask.App/Ai/PlanningTools/MorningArgs.cs     src/MoTask.App/Ai/PlanningTools/PlanningArgs.cs
git mv src/MoTask.App/Ai/PlanningTools/MorningToolHost.cs src/MoTask.App/Ai/PlanningTools/PlanningToolHost.cs
git mv src/MoTask.App/MorningKeyMap.cs                    src/MoTask.App/TriageKeyMap.cs
git mv src/MoTask.App/ViewModels/MorningPlanViewModel.cs  src/MoTask.App/ViewModels/PlanViewModel.cs
git mv src/MoTask.App/Views/MorningPlanView.xaml          src/MoTask.App/Views/PlanView.xaml
git mv src/MoTask.App/Views/MorningPlanView.xaml.cs       src/MoTask.App/Views/PlanView.xaml.cs

git mv tests/MoTask.App.Tests/Fakes/FakeMorningService.cs   tests/MoTask.App.Tests/Fakes/FakePlanningService.cs
git mv tests/MoTask.App.Tests/MorningKeyMapTests.cs         tests/MoTask.App.Tests/TriageKeyMapTests.cs
git mv tests/MoTask.App.Tests/MorningPlanViewModelTests.cs  tests/MoTask.App.Tests/PlanViewModelTests.cs
git mv tests/MoTask.App.Tests/MorningToolHostTests.cs       tests/MoTask.App.Tests/PlanningToolHostTests.cs
git mv tests/MoTask.Core.Tests/MorningInstructionTests.cs   tests/MoTask.Core.Tests/PlanningInstructionTests.cs
git mv tests/MoTask.Core.Tests/MorningModelTests.cs         tests/MoTask.Core.Tests/PlanningModelTests.cs
git mv tests/MoTask.Core.Tests/MorningPlanResolverTests.cs  tests/MoTask.Core.Tests/PlanResolverTests.cs
git mv tests/MoTask.Core.Tests/MorningPlanValidatorTests.cs tests/MoTask.Core.Tests/PlanValidatorTests.cs
git mv tests/MoTask.Core.Tests/MorningRunDescriptorTests.cs tests/MoTask.Core.Tests/PlanningRunDescriptorTests.cs
git mv tests/MoTask.Core.Tests/MorningServiceBulkTests.cs      tests/MoTask.Core.Tests/PlanningServiceBulkTests.cs
git mv tests/MoTask.Core.Tests/MorningServiceLifecycleTests.cs tests/MoTask.Core.Tests/PlanningServiceLifecycleTests.cs
git mv tests/MoTask.Core.Tests/MorningServiceMcpTests.cs       tests/MoTask.Core.Tests/PlanningServiceMcpTests.cs
git mv tests/MoTask.Core.Tests/MorningServiceStartTests.cs     tests/MoTask.Core.Tests/PlanningServiceStartTests.cs
git mv tests/MoTask.Core.Tests/MorningServiceTriageTests.cs    tests/MoTask.Core.Tests/PlanningServiceTriageTests.cs
git mv tests/MoTask.Data.Tests/MorningRepositoryTests.cs       tests/MoTask.Data.Tests/PlanningRepositoryTests.cs
git mv tests/MoTask.Core.Tests/Fixtures/morning-plan.json      tests/MoTask.Core.Tests/Fixtures/plan.json
```

- [ ] **Step 3: 移動後にファイル名が残っていないことを確かめる**

```bash
find src tests -iname '*morning*' -not -path '*/bin/*' -not -path '*/obj/*'
```

期待: 出力なし。

- [ ] **Step 4: `Strings` のリソースキーを改名する（参照側）**

`Strings.*` は必ず `Strings.` 付きで書かれている（XAML も `res:Strings.X`）ので、宣言側と分けて安全に置換できる。**このステップは Core の型改名より先に行うこと** — `Strings.MorningRunning` が `MorningRun` の規則に巻き込まれるのを避けるため。

順序が意味を持つ。上から順に実行する。

```bash
rn 'Strings\.McpMorning'                 'Strings.McpPlanning'
rn 'Strings\.SettingsMorningInstruction' 'Strings.SettingsPlanningInstruction'
rn 'Strings\.ViewMorningPlan'            'Strings.ViewPlan'
rn 'Strings\.MorningPlanHeading'         'Strings.PlanHeading'
rn 'Strings\.Morning'                    'Strings.Plan'
```

- [ ] **Step 5: `Strings` のリソースキーを改名する（宣言側）**

`Strings.cs` と `Strings.resx` の 2 ファイルだけ。値（日本語）には `Morning` が現れないので、キー名だけが変わる。

```bash
sed -i -e 's/McpMorning/McpPlanning/g' \
       -e 's/SettingsMorningInstruction/SettingsPlanningInstruction/g' \
       -e 's/ViewMorningPlan/ViewPlan/g' \
       -e 's/MorningPlanHeading/PlanHeading/g' \
       -e 's/Morning/Plan/g' \
       src/MoTask.App/Resources/Strings.cs src/MoTask.App/Resources/Strings.resx
```

結果は 57 キー。抜粋（全キーが `Morning` → `Plan` で、例外は次の 4 つだけ）:

| 現在 | 改名後 |
| --- | --- |
| `SettingsMorningInstruction` | `SettingsPlanningInstruction` |
| `ViewMorningPlan` | `ViewPlan` |
| `MorningPlanHeading` | `PlanHeading` |
| `McpMorningRunIdRequired` / `McpMorningPlanRequired` / `McpMorningSuggestedDueDateInvalid` | `McpPlanningRunIdRequired` / `McpPlanningPlanRequired` / `McpPlanningSuggestedDueDateInvalid` |
| `MorningStart` / `MorningNoRun` / `MorningRunning` / `MorningComplete` / … 他 50 キー | `PlanStart` / `PlanNoRun` / `PlanRunning` / `PlanComplete` / … |

- [ ] **Step 6: `Messages` のリソースキーを改名する（参照側・宣言側）**

`Messages.MorningPlanNotSubmitted` だけ `PlanNotSubmitted`（計画の中身の話で、`Messages` には既に `PlanNotAnObject` / `PlanGroupsInvalid` / `PlanFirstThingNeedsId` が並んでいる）。残りは `Planning*`。

```bash
rn 'Messages\.MorningPlanNotSubmitted' 'Messages.PlanNotSubmitted'
rn 'Messages\.Morning'                 'Messages.Planning'

sed -i -e 's/MorningPlanNotSubmitted/PlanNotSubmitted/g' \
       -e 's/Morning/Planning/g' \
       src/MoTask.Core/Resources/Messages.cs src/MoTask.Core/Resources/Messages.resx
```

結果（11 キー全部）:

| 現在 | 改名後 |
| --- | --- |
| `MorningStartPromptFormat` | `PlanningStartPromptFormat` |
| `MorningInstructionDefault` | `PlanningInstructionDefault` |
| `MorningInstructionContractFormat` | `PlanningInstructionContractFormat` |
| `MorningRunAlreadyRunning` | `PlanningRunAlreadyRunning` |
| `MorningRunNotFound` | `PlanningRunNotFound` |
| `MorningRunAlreadyFinished` | `PlanningRunAlreadyFinished` |
| `MorningPlanNotSubmitted` | `PlanNotSubmitted` |
| `MorningCompleteMissing` | `PlanningCompleteMissing` |
| `MorningCompleteWithoutPlan` | `PlanningCompleteWithoutPlan` |
| `MorningRunNotRunningFormat` | `PlanningRunNotRunningFormat` |
| `MorningTerminalClosed` | `PlanningTerminalClosed` |

- [ ] **Step 7: MCP ツール名 4 本を改名する**

`morning_` で始まるトークンは MCP ツール名しかない。`Messages.resx` の既定 instruction テンプレートに書かれた `mcp__motask__morning_*`、`PlanningToolHost` の定数、テストのアサーションが一度に変わる。

```bash
rn 'morning_' 'planning_'
```

- [ ] **Step 8: 名前空間と型名を改名する**

順序が意味を持つ。上から順に実行する（長いものが先）。

```bash
rn 'MoTask\.Core\.Morning'        'MoTask.Core.Planning'
rn 'MoTask\.App\.Ai\.MorningTools' 'MoTask.App.Ai.PlanningTools'

rn 'MorningPlanResolver'   'PlanResolver'
rn 'MorningPlanValidator'  'PlanValidator'
rn 'MorningPlanViewModel'  'PlanViewModel'
rn 'MorningPlanView'       'PlanView'
rn 'MorningKeyMap'         'TriageKeyMap'
rn 'MorningToolHost'       'PlanningToolHost'
rn 'MorningTools'          'PlanningTools'
rn 'MorningArgs'           'PlanningArgs'
rn 'MorningRun'            'PlanningRun'
rn 'IMorningRepository'    'IPlanningRepository'
rn 'IMorningService'       'IPlanningService'
rn 'MorningRepository'     'PlanningRepository'
rn 'MorningService'        'PlanningService'
rn 'MorningOutcome'        'PlanningOutcome'
rn 'MorningDirectoryName'  'PlanningDirectoryName'
rn 'MorningInstruction'    'PlanningInstruction'
rn 'MorningTemplate'       'PlanningTemplate'
rn 'RegisteredThisMorning' 'RegisteredThisRun'
rn 'MergedThisMorning'     'MergedThisRun'
rn 'MorningHost'           'PlanHost'
rn 'MorningTabButton'      'PlanTabButton'
rn 'OnShowMorningClick'    'OnShowPlanClick'
rn '_morningInstruction'   '_planningInstruction'
rn 'morningInstruction'    'planningInstruction'
rn 'morningShape'          'planningShape'
```

`MorningRun` の 1 行で `MorningRunStatus` / `MorningRunId` / `MorningRunDescriptor` / `MorningRunChangedEventArgs` / `MorningRunSnapshot` / `MorningRunStatusExtensions` / `MorningRuns`（DbSet とテーブル名）/ `AddMorningRuns`（マイグレーションのクラス名と ID の名前部分）がまとめて変わる。

- [ ] **Step 9: 残りのテストメソッド名とパス文字列を改名する**

```bash
rn 'morning-plan\.json'            'plan.json'
rn 'ForTheMorning'                 'ForPlanning'
rn 'UnderMorning'                  'UnderPlanning'
rn 'AnEmptyMorningIsNotAFailure'   'AnEmptyDayIsNotAFailure'
rn 'MorningRequest'                'PlanningRequest'
rn 'Morning'                       'Planning'
rn 'morning'                       'planning'
```

1 行目は必須。`PlanValidatorTests` が `Fixture("morning-plan.json")` でフィクスチャを読んでいて、Step 2 でファイルを `plan.json` に改名している。ここを飛ばすと最後の `rn 'morning' 'planning'` が `"planning-plan.json"` という存在しない名前にしてしまい、テストが `FileNotFoundException` で落ちる。

最後の 2 行は取りこぼしの受け皿。`JobFolderPaths.PlanningDirectoryName = "planning"` の値、テストの `Path.Combine("planning", "0007-2026-09-07")`、`@"C:\work\planning\0001-2026-09-07"`、because 文字列の `仕様 §6 の planning/0007-2026-09-07`、`TerminalLauncherTests` のローカル変数 `morning` がここで揃う。

- [ ] **Step 10: `MainWindow.xaml.cs` のフィールドと引数を手で直す**

一括規則だと `_morning` が `_planning` になってしまうが、持っているのは `PlanViewModel` なので `_plan` が正しい。Step 9 まで終えた時点で次の 3 か所が `_planning` / `planning` になっているので、手で `_plan` / `plan` に直す。

```csharp
// 修正前（Step 9 直後の状態）
private readonly PlanViewModel _planning;

public MainWindow(BoardViewModel vm, PlanViewModel planning, IAiSettingsStore settings)
{
    ...
    _planning = planning;
    ...
    PlanHost.DataContext = planning;
    planning.NavigateToTask += OnNavigateToTask;
```

```csharp
// 修正後
private readonly PlanViewModel _plan;

public MainWindow(BoardViewModel vm, PlanViewModel plan, IAiSettingsStore settings)
{
    ...
    _plan = plan;
    ...
    PlanHost.DataContext = plan;
    plan.NavigateToTask += OnNavigateToTask;
```

同じファイルの `await _planning.LoadAsync();`（2 か所）と `_planning.LeftPanel is TriagePanelViewModel triage` も `_plan` に直す。

```bash
sed -i -e 's/_planning\b/_plan/g' -e 's/\bplanning\b/plan/g' src/MoTask.App/Views/MainWindow.xaml.cs
grep -n '_plan\|PlanViewModel\|PlanHost' src/MoTask.App/Views/MainWindow.xaml.cs
```

- [ ] **Step 11: `Morning` が 1 つも残っていないことを確かめる**

```bash
grep -rn -i 'morning' --include='*.cs' --include='*.xaml' --include='*.resx' --include='*.json' \
     --exclude-dir=bin --exclude-dir=obj src tests
find src tests -iname '*morning*' -not -path '*/bin/*' -not -path '*/obj/*'
```

期待: どちらも出力なし。

- [ ] **Step 12: リソースキーの宣言と resx が食い違っていないことを確かめる**

`Messages` には `Strings` と違って全プロパティ走査のテストが無い（`Strings` は `StringsTests.AllProperties_ResolveToNonEmptyValuesDistinctFromTheirNames` が守っている）。`.cs` 側だけ改名して `.resx` を取りこぼすと、`Get` がキー名をそのまま返すフォールバックに落ちて黙って通ってしまうので、ここで直接突き合わせる。

```bash
diff <(grep -o 'nameof([A-Za-z]*)' src/MoTask.Core/Resources/Messages.cs | sed 's/nameof(//;s/)//' | sort) \
     <(grep -o '<data name="[A-Za-z]*"' src/MoTask.Core/Resources/Messages.resx | sed 's/<data name="//;s/"//' | sort)
```

期待: 出力なし（両者が完全に一致）。

- [ ] **Step 13: 古い生成物を捨ててビルドする**

`MorningPlanView.g.cs` のような旧名の XAML 生成物が `obj/` に残っていると読み違えのもとになる。

```bash
dotnet clean MoTask.sln
dotnet build MoTask.sln
```

期待: `ビルドに成功しました` / warning 0・error 0。

- [ ] **Step 14: マイグレーションとモデルが一致していることを確かめる**

`20260907135320_AddPlanningRuns.cs` の `CreateTable("PlanningRuns")` / `PK_PlanningRuns` / `IX_PlanningRuns_Date` / `IX_PlanningRuns_Status` / `FK_TriageCandidates_PlanningRuns_PlanningRunId` / `IX_TriageCandidates_PlanningRunId`、`.Designer.cs` の `[Migration("20260907135320_AddPlanningRuns")]` と `modelBuilder.Entity("MoTask.Core.Model.PlanningRun")` / `b.ToTable("PlanningRuns", ...)`、`MoTaskDbContextModelSnapshot.cs` の同じ 3 か所が Step 8 の `MorningRun` 規則で揃っているはず。EF に確認させる。

```bash
dotnet tool restore
dotnet ef migrations list --project src/MoTask.Data --startup-project src/MoTask.Data
dotnet ef migrations has-pending-model-changes --project src/MoTask.Data --startup-project src/MoTask.Data
```

期待:
- `migrations list` の最後が `20260907135320_AddPlanningRuns`（タイムスタンプは据え置き）
- `has-pending-model-changes` が `No changes have been made to the model since the last migration.`

- [ ] **Step 15: 全テストを走らせる**

```bash
dotnet test MoTask.sln
```

期待: 失敗 0・合計 **882**（Core 353 / App 465 / Data 42 / Mcp 22）。件数が減っていたらテストを消してしまっている。

仕様 §7 が「名前そのものを書いている」と挙げた 4 か所に加えて、**`JobFolderTests.Create_ForPlanning_PutsTheFolderUnderPlanningWithNoOutputFolder` の `Path.Combine("planning", "0007-2026-09-07")` が 5 か所目**。これは Step 9 の `rn 'morning' 'planning'` で自動的に直る。ここが赤いままなら Step 9 を取りこぼしている。

- [ ] **Step 16: コミットする**

```bash
git add -A
git commit -m "$(cat <<'EOF'
refactor(plan): 「朝の実行」由来の内部名を Planning / Plan に改名する

型・名前空間・ファイル名・リソースキー・MCP ツール名 4 本・テーブル名・
マイグレーションを機械的に付け替える。文言と画面の配置は触っていない。
アサーションの中身を変えずに 882 件が緑であることが、振る舞いを変えて
いない証明になる。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

`git commit` が Anti-Virus のロックでオブジェクト書き込みに失敗したら、そのまま同じコマンドをもう一度実行する。

---

## Task 2: §4 の文言とコード内の語彙

利用者に見える文言と、コード内の日本語コメント・XML doc から「朝」を外す。仕様 §9 のコミット 2。

**Files:**
- Modify: `src` と `tests` の `*.cs` / `*.xaml` / `*.resx`（`bin` / `obj` を除く）。実体は `Strings.resx` / `Messages.resx` の値と、コメント 89 行
- Test: 既存テスト全件

**Interfaces:**
- Consumes: Task 1 が作った名前すべて（`Strings.ViewPlan`、`Messages.PlanningInstructionContractFormat` など）
- Produces: 文言だけ。API は変わらない

---

- [ ] **Step 1: Task 1 と同じヘルパーを定義する**

```bash
rn() {
  grep -rlZ --include='*.cs' --include='*.xaml' --include='*.resx' --include='*.json' \
       --exclude-dir=bin --exclude-dir=obj -- "$1" src tests |
  xargs -0 -r sed -i "s/$1/$2/g"
}
```

- [ ] **Step 2: 「朝」を含む語をまとめて置き換える**

順序が意味を持つ。上から順に実行する。

```bash
rn '今朝すでに'     'この実行ですでに'
rn '朝のタスク候補' 'タスク候補'
rn 'この朝のプラン' 'この計画'
rn '朝の実行プラン' '計画'
rn '今朝の実行'     'この計画づくり'
rn '今朝の対象日'   '今日の対象日'
rn '今朝の候補'     'この実行の候補'
rn '朝の実行1回'    '計画づくり 1 回'
rn '朝の実行'       '計画づくり'
rn '朝のプラン'     '計画'
rn '朝プラン'       '計画'
rn '朝の画面'       '計画の画面'
rn '朝の仕事'       '計画づくり'
rn '0 件の朝'       '0 件の日'
rn '翌朝の実行'     '次の実行'
rn '翌朝また'       '次の実行でまた'
rn '翌朝 Claude'    '次の実行で Claude'
rn '翌朝'           '次の実行'
rn '今朝'           'この実行'
rn '朝もある'       '日もある'
```

これで仕様 §4 の表のうち「朝」に由来する行がすべて片づく。対応を確かめておく:

| キー | 変更後の値 |
| --- | --- |
| `Strings.ViewPlan` | 計画 |
| `Strings.PlanStart` | 計画を作る |
| `Strings.SettingsPlanningInstruction` | 計画づくりの指示文（空なら既定の文面。出力先と JSON の形は MoTask が自動で付け足します） |
| `Strings.PlanTemplateFallsBackToDefault` | …計画づくりでは既定の起動（cmd.exe /s /c）を使います。… |
| `Messages.PlanningRunAlreadyRunning` | 計画づくりがまだ終わっていません。先に取り込むか、追跡をやめてください |
| `Messages.PlanningRunNotFound` | 計画づくりが見つかりません |
| `Messages.PlanningRunAlreadyFinished` | この計画づくりはもう終わっています |
| `Messages.PlanningRunNotRunningFormat` | runId {0} の計画づくりは動いていません |
| `Messages.CandidateAlreadyInThisRun` | この実行ですでに積んだ externalId です |
| `Messages.PlanningInstructionDefault` | `# タスク候補の収集` で始まる |
| `Messages.PlanningInstructionContractFormat` | この計画づくりの runId は {1} です／候補が 0 件の日もある |

仕様 §4 は「候補が 0 件の朝もある」を `PlanningInstructionDefault` の行に載せているが、実際に入っているのは `PlanningInstructionContractFormat` の末尾。置換は文字列に当たるので、どちらに入っていても直る。

- [ ] **Step 3: 「プラン」を「計画」に置き換える**

Step 2 を先に済ませてあるので、ここで残っている「プラン」はすべて単独の語。

```bash
rn 'プラン' '計画'
```

これで仕様 §4 の残り 4 行が片づく:

| キー | 変更前 | 変更後 |
| --- | --- | --- |
| `Strings.PlanNoRun` | 今日のプランはまだありません | 今日の計画はまだありません |
| `Strings.PlanDateHeadingFormat` | {0} の実行プラン | {0} の計画 |
| `Strings.PlanHeading` | プラン | 計画 |
| `Strings.PlanTriagingFormat` | 候補 {0} 件を仕分け中 — 終わるとプランが確定します | 候補 {0} 件を仕分け中 — 終わると計画が確定します |
| `Messages.PlanningCompleteWithoutPlan` | まだプランが出ていないので、完了にできません | まだ計画が出ていないので、完了にできません |

`Strings.PlanNoFirstThing`（最初にやる1件はありません）には「プラン」も「朝」も無いので変わらない。仕様 §4 の「`PlanNoFirstThing` ほか『プラン』を含む文言」という見出しは大づかみな書き方で、実際に「プラン」を含むのは上の 4 キーだけ。

- [ ] **Step 4: 死んだクラス名への参照を落とす**

`tests/MoTask.Core.Tests/CandidateValidatorTests.cs` の冒頭コメントが、もう存在しないテストクラス `MorningResultReaderTests` を名指ししている。Step 2・3 では変わらない（`Morning` は Task 1 で `Planning` になっているので `PlanningResultReaderTests` という存在しない名前になっている）。過去の名前を作り替えても意味がないので、参照ごと落とす。

```csharp
// 変更前
/// PlanningResultReaderTests の候補まわりをここへ移植したもの（JSON Lines 読みの分だけ落ちている）。

// 変更後
/// 候補 1 件の検証（JSON Lines 読みはもう無い）。
```

- [ ] **Step 5: 「朝」も「プラン」も残っていないことを確かめる**

```bash
grep -rn '朝\|プラン' --include='*.cs' --include='*.xaml' --include='*.resx' \
     --exclude-dir=bin --exclude-dir=obj src tests
```

期待: 出力なし。残っていたら仕様 §8 の対応表を引いて手で直す。

- [ ] **Step 6: テストの日本語リテラルが追随していることを確かめる**

日本語リテラルを直接アサートしているのは次の 2 ファイル。どちらも Step 2・3 の置換が両側（アサーションと resx／Fake の返り値）を同時に直すので、**手で直すところは無い**。ここでは中身を目で確かめるだけ。

```bash
grep -n '候補が 0 件' tests/MoTask.Core.Tests/PlanningInstructionTests.cs
grep -n 'runId 9999' tests/MoTask.App.Tests/PlanningToolHostTests.cs
```

期待:
- `PlanningInstructionTests`: メソッド名が `Build_TellsThatAnEmptyDayIsNotAFailure`、アサートが `Contain("候補が 0 件の日もある")`
- `PlanningToolHostTests`: `Result.Fail<string>("runId 9999 の計画づくりは動いていません")` と `text.Should().Be("runId 9999 の計画づくりは動いていません")`（Fake が返す文字列なので `Messages` とは独立だが、語彙は揃える）

- [ ] **Step 7: ビルドと全テスト**

```bash
dotnet build MoTask.sln
dotnet test MoTask.sln
```

期待: 失敗 0・合計 882。

- [ ] **Step 8: コミットする**

```bash
git add -A
git commit -m "$(cat <<'EOF'
refactor(plan): 画面の文言とコード内の語彙を「計画」に付け替える

「朝の実行プラン」を「計画」、「朝の実行」を「計画づくり」にする。
既定の instruction テンプレートと MCP ツールの説明文も同じ語彙に揃える。
コメントと XML doc も直す。振る舞いは変わっていない。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: §5 の朝由来の要素の置き方

いま画面最上部に 1 行で並んでいる「計画を作る／実行中の表示／完了にする／追跡をやめる／ジョブフォルダを開く」を、主操作と端末セッションの後始末に分ける。仕様 §9 のコミット 3。

**Files:**
- Modify: `src/MoTask.App/Views/PlanView.xaml`（上部のボタン行のみ）
- Test: 既存テスト全件（VM は触らないので、既存の VM テストがそのまま通ることが「壊していない」証明）

**Interfaces:**
- Consumes: `PlanViewModel` の既存プロパティ `CanStart` / `IsRunning` / `ProgressText` / `CanControl` / `IsFailed` と、コマンド `StartCommand` / `CompleteCommand` / `StopTrackingCommand` / `OpenJobFolderCommand`。**ViewModel には一切触らない**（仕様 §5）
- Produces: なし（画面のレイアウトだけ）

`PlanViewModel.UpdateCounters()` が `CanControl = IsRunning;` と置いていて、`IsFailed` は終了状態（`PlanningRunStatus.Failed`）でしか立たない。つまり `CanControl` と `IsFailed` は排他なので、下部ストリップを 2 本の独立した行に分けても同時には出ない。新しい VM プロパティを足さずに「どれも該当しないときは行ごと出さない」を満たせるのはこのため。

---

- [ ] **Step 1: 上部のボタン行から後始末の 3 つを外す**

`src/MoTask.App/Views/PlanView.xaml` の `<!-- 実行のボタン -->` の `StackPanel` を丸ごと次で置き換える。

```xml
    <!-- 上部は計画そのものの主操作だけ（仕様 §5）。 -->
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="{StaticResource Gap.Top.4}">
      <Button Content="{x:Static res:Strings.PlanStart}" Style="{StaticResource Btn.Primary}"
              Command="{Binding StartCommand}"
              Visibility="{Binding CanStart, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{x:Static res:Strings.PlanRunning}" VerticalAlignment="Center"
                 Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressText}" Style="{StaticResource Text.Caption}"
                 VerticalAlignment="Center" Margin="{StaticResource Gap.Left.2}"
                 Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
```

- [ ] **Step 2: 下部ストリップを足す**

Step 1 で置き換えた `StackPanel` の直後、`<Grid Margin="{StaticResource Gap.Top.4}">` の**前**に次を入れる。`DockPanel` は最後の子が残りを埋めるので、`Grid` より前に置いた `Dock="Bottom"` が計画本体の下に並ぶ。

```xml
    <!--
      端末セッションの後始末（仕様 §5）。計画そのものの操作ではないので計画本体より下に退け、
      控えめな見た目にする。Btn.Ghost は既に Brush.TextMuted なので、字の大きさだけ
      Text.Caption に合わせる。
      CanControl（= IsRunning）と IsFailed は排他なので、行を 2 本に分けても同時には出ない。
      どちらの条件も満たさないときは Border ごと消えるため、空の行は残らない。
    -->
    <Border DockPanel.Dock="Bottom" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,1,0,0"
            Padding="{StaticResource Pad.SectionHeader}" Margin="{StaticResource Gap.Top.4}"
            Visibility="{Binding CanControl, Converter={StaticResource BoolToVisibility}}">
      <StackPanel Orientation="Horizontal">
        <Button Content="{x:Static res:Strings.PlanComplete}" Style="{StaticResource Btn.Ghost}"
                FontSize="{StaticResource FontSize.Caption}"
                Command="{Binding CompleteCommand}" />
        <Button Content="{x:Static res:Strings.PlanStopTracking}" Style="{StaticResource Btn.Ghost}"
                FontSize="{StaticResource FontSize.Caption}"
                Command="{Binding StopTrackingCommand}" Margin="{StaticResource Gap.Left.2}" />
      </StackPanel>
    </Border>
    <Border DockPanel.Dock="Bottom" BorderBrush="{StaticResource Brush.Divider}" BorderThickness="0,1,0,0"
            Padding="{StaticResource Pad.SectionHeader}" Margin="{StaticResource Gap.Top.4}"
            Visibility="{Binding IsFailed, Converter={StaticResource BoolToVisibility}}">
      <Button Content="{x:Static res:Strings.PlanOpenJobFolder}" Style="{StaticResource Btn.Ghost}"
              FontSize="{StaticResource FontSize.Caption}"
              Command="{Binding OpenJobFolderCommand}" HorizontalAlignment="Left" />
    </Border>
```

- [ ] **Step 3: ビルドと全テスト**

```bash
dotnet build MoTask.sln
dotnet test MoTask.sln
```

期待: 失敗 0・合計 882。XAML のレイアウトだけを変えたので、ここが赤くなったらバインド名を打ち間違えている。

- [ ] **Step 4: DB を消してアプリを起動する（手動確認 1・2）**

```bash
# MoTask が動いていないことを確かめてから
rm -f "$LOCALAPPDATA/MoTask/motask.db" "$LOCALAPPDATA/MoTask/motask.db-wal" "$LOCALAPPDATA/MoTask/motask.db-shm"
dotnet run --project src/MoTask.App
```

既存 DB の `__EFMigrationsHistory` には旧 ID `20260907135320_AddMorningRuns` が残っていて、消さずに起動すると `MorningRuns` が孤児として残ったまま `PlanningRuns` が追加される中途半端な状態になる（仕様 §6）。**必ず先に消す。**

確認:
1. 例外なく盤面が出る
2. タブが「計画」になっている。候補件数バッジは従来どおり出る

- [ ] **Step 5: 計画を作って MCP ツール 4 本を通す（手動確認 3・4）**

3. 「計画を作る」で端末が起動し、既定ワークフォルダ（`%USERPROFILE%\MoTask\`）の下に `planning/0001-YYYY-MM-DD` ができる
4. `planning_get_context` / `planning_add_candidate` / `planning_submit_plan` / `planning_complete` の 4 本が通り、`planning_complete` で端末が閉じる

旧 `morning/` のジョブフォルダが残っていても放置してよい。過去の実行は DB ごと消えたので誰も参照しない（仕様 §6）。

- [ ] **Step 6: 下部ストリップの出方を確かめる（手動確認 5）**

5. 実行中だけ下部ストリップに「完了にする」「追跡をやめる」が出る。失敗時だけ「ジョブフォルダを開く」が出る。どれも該当しないとき、下部ストリップは出ない（区切り線も出ない）

- [ ] **Step 7: 設定と仕分けを確かめる（手動確認 6・7）**

6. 設定画面のラベルが「計画づくりの指示文」になっている。設定 JSON の旧キー `MorningInstruction` は読まれず、カスタム指示文を書いていた場合は既定テンプレートに戻る（仕様 §6・意図どおり）
7. 仕分け（T / E / X / L）が従来どおり効く

- [ ] **Step 8: コミットする**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(plan): 端末セッションの後始末を計画本体の下へ退ける

「完了にする」「追跡をやめる」「ジョブフォルダを開く」は計画そのものの
操作ではないので、上部の主操作から外して下部の控えめなストリップに移す。
CanControl と IsFailed は排他なので行を 2 本に分け、どちらでもないときは
行ごと出さない。ViewModel は触っていない。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## 完了後

3 コミットがブランチに載り、全テストが緑で、手動確認 7 項目が通っていること。マージは superpowers:finishing-a-development-branch に従う。
