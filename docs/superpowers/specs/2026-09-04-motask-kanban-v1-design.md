# MoTask v1 — 個人用カンバン 設計仕様

日付: 2026-09-04
状態: 設計承認済み（実装計画は未作成）

## 1. 目的と位置づけ

MoTask は「朝の実行プラン」「受信箱からのタスク候補仕分け」「AI によるタスク遂行」を段階的に
載せていくタスク管理アプリである。v1 はその土台となる **個人用カンバン** だけを作る。

v1 の役割は次の2つ。

1. 日常のタスク管理として単体で使えること。
2. 後続機能（Morning Plan → Inbox triage → AI 遂行）が UI を触らずに載せられる
   ドメイン層とデータ（特に状態変更の履歴）を用意すること。

`Incomplete design request/` にある Claude Design のワイヤーフレーム（turn 3c / 4c）が
画面の参照元。v1 は 4c のボード部分だけを対象とし、AI 詳細パネル・朝プランへの導線は含めない。

## 2. スコープ

### 含む

- タスクの作成・編集・論理削除・復元
- 列間のドラッグ移動と列内の並び替え
- 期限（日付のみ）
- プロジェクト（1タスク1件）とラベル（複数）、それらによるフィルタとテキスト検索
- 列の追加・改名・並び替え・削除、WIP 制限
- 状態変更ログ（履歴）と詳細パネルでの表示
- Industry デザインシステムの色とフォントの適用

### 含まない（後続で検討）

- サブタスク、所要時間、添付、コメント、依存関係
- リスト／カレンダービュー、複数選択
- 複数ユーザー、同期、ログイン
- Claude 連携、メール／Teams／カレンダー連携
- Morning Plan、候補仕分け、AI 遂行の各画面

## 3. 前提と技術選定

| 項目 | 決定 |
| --- | --- |
| 対象 OS | Windows 11 |
| ランタイム | .NET 10（LTS）。開発機には .NET 7 までしか入っていないため SDK 導入が前提 |
| UI | WPF、CommunityToolkit.Mvvm |
| DI / ホスト | Microsoft.Extensions.DependencyInjection + Microsoft.Extensions.Hosting |
| 永続化 | SQLite、EF Core（Microsoft.EntityFrameworkCore.Sqlite） |
| ドラッグ＆ドロップ | GongSolutions.WPF.DragDrop |
| テスト | xUnit、FluentAssertions |
| DB の場所 | `%LOCALAPPDATA%\MoTask\motask.db` |
| 表示言語 | 日本語のみ。文言は resx に置く |

## 4. アーキテクチャ

```
MoTask.sln
├─ src/
│  ├─ MoTask.Core      ドメイン・ユースケース。WPF にも EF にも依存しない
│  ├─ MoTask.Data      EF Core + SQLite。マイグレーションとリポジトリ実装
│  └─ MoTask.App       WPF。ViewModel、View、スタイル、DI 構成
└─ tests/
   ├─ MoTask.Core.Tests
   ├─ MoTask.Data.Tests
   └─ MoTask.App.Tests
```

依存方向は App → Data → Core、App → Core。Core は他に依存しない。

### 4.1 Core

- エンティティ: `Board`, `Column`, `Task`, `Project`, `Label`, `HistoryEntry`
- 列挙: `ColumnRole`, `HistoryKind`
- インターフェース: `IBoardRepository`, `IHistoryRepository`, `IUnitOfWork`, `IClock`
- ユースケース `BoardService`
  - タスク: `CreateTask`, `UpdateTask`, `MoveTask(taskId, toColumnId, position)`, `DeleteTask`, `RestoreTask`
  - 列: `AddColumn`, `RenameColumn`, `SetColumnRole`, `ReorderColumns`, `SetWipLimit`, `DeleteColumn`
  - 分類: `CreateProject`, `ArchiveProject`, `CreateLabel`, `SetTaskLabels`
  - 照会: `GetBoard()`（列とタスクをまとめて返す）、`GetHistory(taskId)`
- フィルタ条件 `TaskFilter`（プロジェクト、ラベル集合、期限区分、検索文字列、削除済み表示）と
  それを適用する純関数 `TaskFilter.Apply(IEnumerable<Task>)`。フィルタは Core に置き、
  ViewModel からもテストからも同じロジックを使う。
- 各ユースケースは `Result` 型を返す。成功時は `Warnings`（WIP 超過など）を持ち、
  失敗時はユーザー向けの理由を持つ。例外は想定外の障害にだけ使う。

後続機能はすべて Core のユースケースとして追加する。Claude 呼び出しは Core に
インターフェースを置き、App 側で実装する。

### 4.2 Data

- `MoTaskDbContext` と EF Core マイグレーション
- `IBoardRepository` / `IHistoryRepository` / `IUnitOfWork` の実装
- 起動時に `Database.Migrate()` を実行し、ボードが0件なら既定のボードと4列を投入する

### 4.3 App

- `App.xaml.cs` でホストを組み立て、`MainWindow` を DI から解決する
- ViewModel: `BoardViewModel`, `ColumnViewModel`, `TaskCardViewModel`,
  `TaskDetailViewModel`, `FilterViewModel`
- View: `MainWindow`, `BoardView`, `ColumnView`, `TaskCardView`, `TaskDetailPanel`, `FilterBar`
- `Themes/Industry.xaml` にトークンを ResourceDictionary として移植
- `Fonts/` に Barlow と Barlow Condensed を同梱（OFL）

## 5. データモデル

### Board

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| Name | string | v1 では1件固定 |

### Column

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| BoardId | int | FK |
| Name | string | 自由に改名可 |
| Order | int | 表示順 |
| WipLimit | int? | null は制限なし |
| Role | ColumnRole | `Backlog`, `Active`, `Review`, `Done` |

ルール:

- ボードには `Done` の列が必ず1つ存在する。`Done` の列は削除できず、Role も変更できない。
  他の列を `Done` に変更することもできない。
- `Done` 以外の Role は任意の数の列に付けられる。列の追加時は Role を選ぶ（既定 `Active`）。
- タスク（論理削除済みを含む）が残る列は削除できない。
- 初期投入: 未着手 (`Backlog`) / 進行中 (`Active`) / 確認待ち (`Review`) / 完了 (`Done`)。
- WIP 制限は **警告** であり、超過しても移動を拒否しない。超過中は列ヘッダーの件数を赤で表示する。
  件数には論理削除済みのタスクを含めない。

### Project

| 列 | 型 |
| --- | --- |
| Id | int |
| Name | string |
| Archived | bool |

### Label

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | |
| Name | string | |
| Color | string | アクセントランプの段（`accent-300` など）を名前で保持 |

### Task

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| Title | string | 空は拒否 |
| Description | string | 空文字可 |
| ColumnId | int | FK |
| Position | int | 列内の順序。移動のたびに列内で 0..n-1 に再採番 |
| ProjectId | int? | FK |
| DueDate | DateOnly? | 日付のみ |
| CreatedAt | DateTime | UTC |
| UpdatedAt | DateTime | UTC |
| CompletedAt | DateTime? | `Done` 列に入った時刻。`Done` から出たら null に戻す |
| DeletedAt | DateTime? | 論理削除 |

`TaskLabel(TaskId, LabelId)` で多対多。

### HistoryEntry（追記専用）

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | long | PK |
| TaskId | int | FK |
| At | DateTime | UTC |
| Kind | HistoryKind | `Created`, `Moved`, `Edited`, `Deleted`, `Restored` |
| FromColumnId | int? | `Moved` のとき |
| ToColumnId | int? | `Created`, `Moved` のとき |
| Detail | string | `Edited` のとき、変更した項目名と前後の値を JSON で保持。それ以外は空文字 |

履歴はタスクの変更と **同じトランザクション** で書く。更新も削除もしない。
列内の並び替えだけ（列が変わらない `MoveTask`）は履歴に残さない。

## 6. 画面と操作

### レイアウト

1ウィンドウ。上から順に:

1. **トップバー**: ブランド「TASKS」、ビュータブ（v1 は「ボード」のみ）。
2. **フィルタバー**: プロジェクト選択（すべて／各プロジェクト）、ラベルチップ（複数選択、AND）、
   期限（すべて／今日／今週／期限切れ）、テキスト検索（タイトルと説明の部分一致）、
   「削除済みを表示」トグル。右端に「＋ タスク」。
3. **ボード**: 横スクロールする列の並び。末尾に「＋ 列を追加」。
4. **詳細パネル**: カード選択で右側に開く。幅固定、Esc または × で閉じる。

### 列

- ヘッダーに名前、件数、WIP 制限（設定時は `4 / 5` の形）。件数が制限を超えると赤。
- ヘッダーのメニューから改名、Role 変更（Done 以外）、WIP 設定、削除。
- 列の並び替えはヘッダーのドラッグ。
- 列末尾の「＋ 追加」でタイトルだけのインライン作成。Enter で確定、Esc で取り消し。

### カード

- 上段: プロジェクトチップ、期限。期限超過は赤、当日はアクセント色。
- 中段: タイトル。
- 下段: ラベルチップ。
- 選択中はアクセント色の枠。削除済みは点線枠で薄く表示。

### 詳細パネル

- タイトル（インライン編集）、説明（複数行）、プロジェクト、ラベル、期限、所属列。
- 変更はフォーカスを外した時点で即保存。保存ボタンは置かない。
- 下部に履歴を新しい順に表示（「9/4 8:40 未着手 → 進行中」の形）。
- 「削除」で論理削除。削除済みタスクを選択すると「復元」が出る。

### ドラッグ＆ドロップ

- カードは列内の並び替えと列間移動の両方に対応。ドロップ先の位置をインジケータで示す。
- ドロップ時に `MoveTask` を1回呼ぶ。

### キーボード

| キー | 動作 |
| --- | --- |
| N | 新規タスク（選択中の列、なければ先頭の列） |
| Delete | 選択中のタスクを論理削除 |
| Esc | 詳細パネルを閉じる／インライン編集を取り消す |
| Ctrl+F | 検索欄にフォーカス |

### 見た目

- `_ds/.../styles.css` の `:root` トークンを `Themes/Industry.xaml` に移植する。
  色は `--color-bg` #f2f2f3、`--color-text` #1d1f20、`--color-accent` #5980a6 と
  各 100〜900 ランプ、`--color-divider`。間隔と字体サイズもトークン名を揃える。
- 見出しは Barlow Condensed、本文は Barlow。日本語は Noto Sans JP にフォールバック。
- 角は直角、枠線は 1px の `divider` 色。カードと列は塗りなし。
- v1 では「＋」の隅マーク、デュオトーン画像などの装飾は入れない。
- フォーカスは 2px のアクセント色枠。ホバーは `accent-100` の塗り。

## 7. データの流れ

```
View ──(コマンド)──▶ ViewModel ──▶ BoardService (Core)
                                   │
                                   ▼
                          IBoardRepository / IHistoryRepository (Data)
                                   │
                                   ▼
                                 SQLite
```

- 操作は即時コミット。バッチや保存ボタンはない。
- ViewModel は楽観的に自分の状態を先に更新し、`BoardService` が失敗を返したら巻き戻す。
- `GetBoard()` は起動時と失敗からの復帰時にだけ呼ぶ。通常の操作は差分で ViewModel を更新する。

## 8. エラー処理とバリデーション

| 状況 | 挙動 |
| --- | --- |
| DB ファイルがない | 作成し、マイグレーションと既定列の投入を行う |
| マイグレーションが必要 | 起動時に自動適用 |
| DB が開けない／壊れている | `motask.db.bak-<日時>` にコピーしてから再作成するか、終了するかを尋ねる |
| 保存に失敗 | ViewModel を巻き戻し、トップバー直下に非モーダルのバナーで通知。再試行は不要（次の操作で再度試みる） |
| 空タイトル | 拒否し、入力欄に留まる |
| タスクが残る列の削除 | 拒否し、理由を表示 |
| `Done` 列の削除・Role 変更 | メニューに出さない |
| WIP 超過 | 許可し、赤表示のみ |

## 9. テスト

### Core（xUnit）

- `Done` 列は必ず1つ: 削除・Role 変更が拒否される。他の列を `Done` にできない
- `MoveTask`: 列内の Position が 0..n-1 に再採番される。`Done` への移動で `CompletedAt` が入り、
  `Done` から出ると null に戻る。列が変わる移動で `Moved` の履歴が1件追記され、
  列内の並び替えでは追記されない
- `UpdateTask`: 変更した項目だけが `Edited` の Detail に入る。変更なしなら履歴を書かない
- `DeleteTask` / `RestoreTask`: `DeletedAt` と履歴
- WIP 超過時に `MoveTask` の結果に警告が立つが、移動は成功する
- `TaskFilter.Apply`: プロジェクト・ラベル AND・期限区分・検索・削除済み表示の組み合わせ

### Data（xUnit、一時ファイルの SQLite）

- マイグレーションが空 DB に適用でき、既定のボードと列が投入される
- タスク変更と履歴追記が同一トランザクションで、片方が失敗すれば両方ロールバックする
- `GetBoard()` が列順・Position 順で返す

### App（xUnit）

- `BoardViewModel`: フィルタ適用で表示カードが変わる。保存失敗時に巻き戻る
- `TaskDetailViewModel`: フォーカスアウトで保存が1回呼ばれる

### 手動確認

- ドラッグ＆ドロップ（列内、列間、列の並び替え）
- フォントとテーマの見え方

## 10. 後続機能への接続点

この仕様が意識している差し込み口を記録しておく。v1 では実装しない。

- **Morning Plan**: `Column.Role` で「今日中」「待ち」「確認待ち」の在庫を取れる。
  `HistoryEntry` から「受信からの経過時間」「停滞日数」を算出できる。
- **Inbox triage**: `Task` に `Source`（メール／Teams／手入力）と根拠文を持つ列を
  マイグレーションで足す。
- **AI 遂行**: `Task` に `Assignee`（自分／AI）と実行ログのテーブルを足す。
  `Review` の Role が「確認待ち」の受け皿になる。
