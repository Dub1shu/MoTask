# MoTask 「朝のプラン」を「計画」にする（設計仕様）

日付: 2026-09-21
状態: 設計承認済み（実装計画は未作成）
前提: `2026-09-07-motask-morning-plan-triage-design.md`（実装済み）、
`2026-09-09-motask-morning-plan-view-design.md`（実装済み）、
`2026-09-13-motask-morning-mcp-handoff-design.md`（実装済み・master にマージ済み）

## 1. 何をするか

画面と語彙を「朝の実行プラン」から**「計画」**に付け替える。あわせて、その呼び名に
コードの内部名（型・名前空間・リソースキー・MCP ツール名・テーブル名）を揃える。

裏の仕組みは変えない。計画は今までどおり Claude のセッションが 1 日 1 回作り、
まるごと差し替わり、人は行単位で編集できない。**振る舞いを変える変更はこの仕様に含まれない。**

## 2. スコープ

### 含む

- 利用者に見える文言から「朝」を外す（§4）
- 「朝の実行」に由来する制御ボタンを、計画本体より下の補助的な位置へ退ける（§5）
- 内部名の全面改名（§3）。MCP ツール名とテーブル名を含む
- 既存マイグレーションの書き換えと、手元 DB のその場移行（§6）

### 含まない

- 計画の持ち主を人に移すこと。手編集・区分の並べ替え・「今日やるに追加」は入らない
- 区分（今日中／余裕があれば／AI 準備完了／待ち）の整理。分類軸の混在はそのまま残す
- 「最初にやる1件」の常駐、所要時間、着手ボタン、要約の作り直し
- 既存の設計仕様・実装計画の書き換え（§8）
- 設定 JSON のカスタム指示文の自動移行。旧キー `MorningInstruction` は読まない（§6）。
  DB の行は逆に、その場の SQL 移行で残す（§6）

## 3. 命名の規則

3 つだけ。

| 対象 | 語 |
| --- | --- |
| 実行とライフサイクル（セッション 1 回、その状態、格納、MCP 契約） | `Planning*` |
| 計画の中身と、それを見せる画面 | `Plan*` |
| 候補の仕分け | `Triage*`（既存のまま） |

`PlanRun` にしないのは、「計画を実行する（タスクをこなす）」と読めるため。計画を**作る**
実行は `PlanningRun` と綴る。

### Core

| 現在 | 改名後 |
| --- | --- |
| 名前空間 `MoTask.Core.Morning` | `MoTask.Core.Planning` |
| `MorningRun` / `MorningRunStatus` | `PlanningRun` / `PlanningRunStatus` |
| `IMorningRepository` | `IPlanningRepository` |
| `IMorningService` / `MorningService` | `IPlanningService` / `PlanningService` |
| `MorningRunChangedEventArgs` | `PlanningRunChangedEventArgs` |
| `MorningRunDescriptor` | `PlanningRunDescriptor` |
| `MorningInstruction` | `PlanningInstruction` |
| `MorningOutcome` | `PlanningOutcome` |
| `MorningPlanResolver` / `MorningPlanValidator` | `PlanResolver` / `PlanValidator` |
| `JobFolderPaths.MorningDirectoryName` = `"morning"` | `PlanningDirectoryName` = `"planning"` |
| `AiSettings.MorningInstruction` | `AiSettings.PlanningInstruction` |
| `ResolvedPlan` / `PlanRow` / `PlanGroup*` / `BoardSnapshot` / `Candidate*` | 変更なし（名前空間だけ移動） |

`AiSettings.PlanningInstruction`（利用者が書く収集方針）と `PlanningInstruction`
（instruction.md を組み立てる静的クラス）が同名で並ぶ。これは改名前も
`AiSettings.MorningInstruction` と `MorningInstruction` で同じ形だったので、読みにくさは増えない。

### Data

| 現在 | 改名後 |
| --- | --- |
| `MorningRepository` | `PlanningRepository` |
| `DbSet MorningRuns` / `ToTable("MorningRuns")` | `PlanningRuns` / `"PlanningRuns"` |
| `20260907135320_AddMorningRuns` | 同 ID のまま `AddPlanningRuns` に書き換え（§6） |
| `TriageCandidates` | 変更なし（「朝」由来ではない） |

### App

| 現在 | 改名後 |
| --- | --- |
| `Ai/MorningTools/` | `Ai/PlanningTools/` |
| `MorningArgs` / `MorningToolHost` | `PlanningArgs` / `PlanningToolHost` |
| `MorningKeyMap` | `TriageKeyMap`（中身は既に `TriageKeyAction`） |
| `Views/MorningPlanView.xaml(.cs)` | `Views/PlanView.xaml(.cs)` |
| `MainWindow.xaml` の `x:Name="MorningHost"` | `PlanHost` |
| `ViewModels/MorningPlanViewModel` | `PlanViewModel` |
| `Strings.Morning*`（約 50 キー） | `Plan*`（`ViewMorningPlan` → `ViewPlan`、`MorningStart` → `PlanStart` など） |
| `Strings.McpMorning*` | `McpPlanning*` |
| `Messages.MorningRun*` / `MorningInstructionDefault` / `MorningInstructionContractFormat` | `PlanningRun*` / `PlanningInstructionDefault` / `PlanningInstructionContractFormat` |

### MCP 契約

| 現在 | 改名後 |
| --- | --- |
| `morning_get_context` | `planning_get_context` |
| `morning_add_candidate` | `planning_add_candidate` |
| `morning_submit_plan` | `planning_submit_plan` |
| `morning_complete` | `planning_complete` |

この 4 本を知っているのは `PlanningToolHost` と既定 instruction テンプレートの 2 か所だけで、
外部の利用者はいない。実行中のセッションは MoTask が閉じるので、据え置く理由がない。

## 4. 利用者に見える文言

| キー（改名後） | 現在 | 変更後 |
| --- | --- | --- |
| `ViewPlan` | 朝の実行プラン | 計画 |
| `PlanStart` | 朝のプランを作る | 計画を作る |
| `PlanDateHeadingFormat` | {0} の実行プラン | {0} の計画 |
| `PlanNoRun` | 今日のプランはまだありません | 今日の計画はまだありません |
| `PlanHeading` | プラン | 計画 |
| `PlanTriagingFormat` | 候補 {0} 件を仕分け中 — 終わるとプランが確定します | …終わると計画が確定します |
| `PlanNoFirstThing` ほか「プラン」を含む文言 | プラン | 計画 |
| `SettingsPlanningInstruction` | 朝の実行の指示文（…） | 計画づくりの指示文（…） |
| `PlanTemplateFallsBackToDefault` | …朝の実行では既定の起動… | …計画づくりでは既定の起動… |
| `Messages.PlanningRunAlreadyRunning` ほか 4 件 | 朝の実行が… | 計画づくりが… |
| `Messages.CandidateAlreadyInThisRun` | 今朝すでに積んだ externalId です | この実行ですでに積んだ externalId です |
| `Messages.PlanningInstructionContractFormat` | この朝の実行の runId は… | この計画づくりの runId は… |
| `Messages.PlanningInstructionDefault` | `# 朝のタスク候補の収集`／「候補が 0 件の朝もある」 | `# タスク候補の収集`／「候補が 0 件の日もある」 |

`PlanningInstructionDefault` には §3 のツール名の差し替えも同時に入る。

コード内の日本語コメントと XML doc の「朝の実行」も「計画づくり」に直す。
既存仕様への `仕様 §6` といった節番号参照はそのまま残す（§8 の対応表が旧称を引き受ける）。

**残す語**: 「仕分け」「候補キュー」「最初にやる1件」「今日中／余裕があれば／AI 準備完了／待ち」。
「朝」由来ではないので触らない。

## 5. 朝由来の要素の置き方

いま画面最上部の 1 行に、主操作と実行の制御が混ざって並んでいる。

```
[計画を作る] [Claude が受信箱を見ています] [3 ターン] [完了にする] [追跡をやめる] [ジョブフォルダを開く]
```

後ろの 3 つは端末セッションの後始末であって、計画そのものの操作ではない。ここを分ける。

| 位置 | 出すもの | 条件 |
| --- | --- | --- |
| 上部 | 「計画を作る」 | `CanStart` |
| 上部 | 「Claude が受信箱を見ています」＋ターン数 | `IsRunning` |
| 下部ストリップ | 「完了にする」「追跡をやめる」 | `CanControl` |
| 下部ストリップ | 「ジョブフォルダを開く」 | `IsFailed` |

下部ストリップは `Text.Caption` 相当の控えめな見た目にし、条件を満たさないときは行ごと出さない。

変えるのは `PlanView.xaml` のレイアウトだけ。VM のプロパティ（`CanStart` / `IsRunning` /
`CanControl` / `IsFailed`）とコマンドは触らない。既存の VM テストはそのまま通る。

## 6. データとマイグレーション

**この節はレビュー後に訂正した。** もともとは「旧データは引き継がない。手元の DB を
作り直す前提で進める」としていたが、これは誤りだった。`%LOCALAPPDATA%\MoTask\motask.db`
には計画づくりの実行ログ（`MorningRuns` / `TriageCandidates`）だけでなく、盤面・タスク・
プロジェクト・ラベル・履歴・AI ジョブという利用者の全データが同居している（`tests/MoTask.Data.Tests/MigrationTests.cs`
参照）。DB を作り直す前提はこれらすべてを消す前提と同じで、正しくない。以下は
**その場の SQL 移行で DB を残す**方針に置き換える。

- 最後のマイグレーション `20260907135320_AddMorningRuns` をその場で書き換え、`AddPlanningRuns`
  にする。タイムスタンプ ID は維持し、ファイル名・クラス名・`CreateTable` のテーブル名を変える。
  `.Designer.cs` と `MoTaskDbContextModelSnapshot.cs` は再生成する
- 書き換えた `20260907135320_AddPlanningRuns.Up()` は `PlanningRuns` と `TriageCandidates` の
  両方を `CreateTable` する（改名前の元マイグレーションがそうだったのを引き継いでいるだけで、
  こちらは変えていない）。そのため、旧 DB にそのまま起動すると `TriageCandidates` が既に
  存在していて `CreateTable` が例外を投げる。`App.xaml.cs` の `TryInitializeDatabaseAsync`
  がこれを捕まえて「バックアップして作り直しますか？」（`Strings.DbOpenFailedFormat`）を出し、
  Yes を選ぶと `DatabaseRecovery.BackupAndReset` が `.bak-yyyyMMdd-HHmmss` を残して DB を
  空で作り直す（`Strings.DbRecreatedFormat`）。**これ自体が、利用者が気づかないうちに盤面を
  失う経路になる。** だから手動確認の先頭で、起動する前に次の SQL 移行を済ませておく
  （§7 の手動確認 0・1）。
- **手元 DB の移行手順。** MoTask を終了した状態で、`%LOCALAPPDATA%\MoTask\motask.db` に対して
  次の 3 文を実行する（念のため実行前に `motask.db` をコピーして退避しておくとよい）。

  ```sql
  ALTER TABLE MorningRuns RENAME TO PlanningRuns;
  ALTER TABLE TriageCandidates RENAME COLUMN MorningRunId TO PlanningRunId;
  UPDATE __EFMigrationsHistory SET MigrationId='20260907135320_AddPlanningRuns'
    WHERE MigrationId='20260907135320_AddMorningRuns';
  ```

  盤面・タスク・プロジェクト・ラベル・履歴・AI ジョブ・過去の計画づくりの行はすべて残る。
  索引と FK の制約名は旧名（`IX_MorningRuns_Date` / `IX_MorningRuns_Status` /
  `IX_TriageCandidates_MorningRunId` / `FK_TriageCandidates_MorningRuns_MorningRunId` など）
  のまま残るが、EF は起動時に `__EFMigrationsHistory` のマイグレーション ID しか見ず、
  実行時の SQL は表名・列名で組み立てるので実害は無い。この 3 文は、改名前のスキーマで
  作った DB にサンプル行を入れ、実際に実行して `dotnet ef database update` が
  「すでに最新です」と報告し、`PlanningRuns` と `PlanningRunId` で結合したクエリが
  元のデータをそのまま返すところまで確認済み。
- 設定 JSON（`%USERPROFILE%\MoTask\`）の旧キー `MorningInstruction` は読まない。
  カスタム指示文を書いていた場合は既定テンプレートに戻る。書いていた指示文を残したい
  場合の手順は §7 の手動確認 0 を参照
- 既定ワークフォルダ下の旧 `morning/` ジョブフォルダは放置する。新しい実行は `planning/` の下に作る。
  過去の実行の DB 行は上の移行で残るが、ジョブフォルダ自体の中身（instruction.md やログ）は
  誰も参照しない

## 7. テストと検証

ファイル名に `Morning` を含むテスト 15 ファイルは同じ規則で改名する。改名したファイルも、
改名した型を参照するだけの他のテスト（`MoTaskMcpServerTests` / `JobFolderTests` など）も、
**アサーションの中身は変えない**。名前だけ変えて全部緑になることが、振る舞いを変えていない
証明になる。

例外は、名前そのものを書いている次の 4 か所だけ。

| ファイル（改名後） | 変わるもの |
| --- | --- |
| `PlanningToolHostTests` | ツール名 4 本の一覧と、`先に planning_submit_plan を呼んでください` の文言 |
| `MoTaskMcpServerTests` | サーバに載るツール名の一覧 |
| `PlanningInstructionTests` | `mcp__motask__planning_*` の出現順アサーション |
| `PlanningServiceStartTests` | `Category` が `"planning"` であること |

フィクスチャ `tests/MoTask.Core.Tests/Fixtures/morning-plan.json` は `plan.json` に改名する。

新しいテストは足さない。振る舞いを変えないので足すものがなく、§5 は XAML のレイアウトだけで
テスト対象がない。

### 手動確認

0. `%USERPROFILE%\MoTask\settings.json` を開き、`MorningInstruction` に値があれば控えておく。
   MoTask を起動したあと、設定画面の「計画づくりの指示文」へ貼り直す。**AI 設定を一度でも
   保存すると、このキーはファイルから消えて戻せない。** 新しい設定 DTO にこのキーは無く、
   読み込み時は未知のキーを黙って捨て、保存時はそのキーを持たない形でファイルを丸ごと
   書き直すため（`JsonAiSettingsStore`）
1. §6 の SQL 移行を先に済ませてから起動する（MoTask を終了した状態で 3 文を実行し、
   その後で起動する）→ 例外なく盤面が出て、既存のタスク・プロジェクト・ラベル・履歴・
   AI ジョブがすべて残っている
2. タブが「計画」になっている。候補件数バッジは従来どおり出る
3. 「計画を作る」で端末が起動し、既定ワークフォルダ下に `planning/` のジョブフォルダができる
4. `planning_get_context` / `planning_add_candidate` / `planning_submit_plan` / `planning_complete`
   の 4 本が通り、`planning_complete` で端末が閉じる
5. 実行中だけ下部ストリップに「完了にする」「追跡をやめる」が出る。失敗時だけ
   「ジョブフォルダを開く」が出る。どれも該当しないとき、下部ストリップは出ない
6. 設定画面のラベルが「計画づくりの指示文」になっている
7. 仕分け（T / E / X / L）が従来どおり効く

## 8. 既存文書の扱い

「朝の実行プラン」語彙で書かれた既存の設計仕様・実装計画は**書き換えない**。
それぞれが書かれた時点の記録として残す。現行の語彙はこの文書が引き受ける。

| 既存文書の語 | 現在の語 |
| --- | --- |
| 朝の実行プラン（画面） | 計画（画面） |
| 朝の実行 / 朝の実行 1 回 | 計画づくり / 計画づくりの実行 1 回 |
| 朝のプラン | 計画 |
| `MorningRun` / `MorningService` / `IMorningRepository` | `PlanningRun` / `PlanningService` / `IPlanningRepository` |
| `MorningPlanResolver` / `MorningPlanValidator` | `PlanResolver` / `PlanValidator` |
| `MorningPlanView` / `MorningPlanViewModel` | `PlanView` / `PlanViewModel` |
| `morning_*`（MCP ツール 4 本） | `planning_*` |
| テーブル `MorningRuns` | `PlanningRuns` |
| ジョブフォルダの `morning/` | `planning/` |

コード内に残る `仕様 §6` のような節番号参照は、引き続き旧文書の節を指す。旧称に出会ったら
この表を引く。

## 9. コミットの刻み

1 本のブランチで、3 コミットに分ける。

| # | 内容 | 検証 |
| --- | --- | --- |
| 1 | §3 の機械的改名（型・名前空間・ファイル・リソースキー・MCP ツール名・テーブル名・マイグレーション書き換え）と、§7 の例外 4 か所 | ビルドが通り、全テストが緑 |
| 2 | §4 の文言と、コード内コメントの語彙 | 全テストが緑 |
| 3 | §5 の配置 | 全テストが緑。§7 の手動確認 |

1 が完全に機械的なので、レビューは 2 と 3 に集中できる。語彙が混ざった中間状態は生まれない。
