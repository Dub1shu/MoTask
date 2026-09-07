# MoTask 朝の実行プランと受信箱の仕分け（設計仕様）

日付: 2026-09-07
状態: 設計承認済み（実装計画は未作成）
前提: `2026-09-04-motask-kanban-v1-design.md`（実装済み）、
`2026-09-05-motask-terminal-ai-design.md`（実装済み・master にマージ済み）
参照: `Incomplete design request/Morning Plan Wireframes.dc.html` の 4a / 4b / 4c

## 1. 何を作るか

ロードマップの Morning Plan と Inbox triage を、ワイヤーフレーム 4a / 4b のとおり
**1つの画面**として作る。朝、ボタンを1つ押すと Claude が受信箱を見て回り、
タスクの候補と今日のプランを持って帰る。人は候補を1件ずつ「登録・統合・あとで・却下」で
片づけ、片づけ終わるとプランが確定する。

この画面には独立した2つのサブシステムが同居する。

1. **候補の取り込みと仕分け** — 受信箱からタスク候補を作り、人が判断してタスクにする
2. **プランの確定** — 今日やることを「最初にやる1件」と4区分に並べる

設計としては1本の画面だが、**実装計画は上の2つに割る**。1本にすると1プランが肥大しすぎる。

## 2. スコープ

### 含む

- 朝の実行を Claude Code のセッションとして発注し、その成果物ファイルを取り込む
- 候補のテーブルと、`ExternalId` による取り込みの重複排除
- 仕分けの4アクション（登録・統合・あとで・却下）と、それぞれの盤面への反映
- 「朝の実行プラン」画面（左パネルのモード切替、候補キュー、5区分のプラン）
- ボード画面との相互の導線（候補件数バッジ、「朝のプランへ」）

### 含まない

- 取り込み元ごとの実装。**MoTask はソースを列挙しない**（§4）
- MoTask 自身による OAuth・トークン保管・メール API の呼び出し
- 朝の実行の自動起動・スケジュール実行（起動は常に人が押す）
- 見積もり時間（ワイヤーの「所要」欄）。`TaskItem` に概念がなく、今回足す理由がない
- プランの手編集。プランは毎朝まるごと差し替わる

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| 取り込みの経路 | Claude のセッションに取りに行かせる | MoTask 側に OAuth・トークン保管・API クライアントが一切要らない。根拠文と判断も同じセッションが書ける |
| 取り込み元の指定 | しない。Claude が自分のコネクタで届く範囲を集める | コネクタが増減してもアプリを直さずに済む。`Source` は自由文字列 |
| プランを決めるのは | Claude（区分・順序・理由のすべて） | 盤面だけでは分からない文脈（今朝の候補との関係など）を使わせる |
| 朝の実行の起動 | 人がボタンを押す。結果は保存して次に作るまで表示 | 朝以外の起動で勝手に端末が開くのを避ける。日中に何度見ても同じプランが出る |
| ジョブの器 | `AiJob` に相乗りせず `MorningRun` を新設 | `AiJob.TaskId` は非 null の必須 FK で、完了時に対象タスクを確認待ち列へ動かす。朝の実行には対象タスクがない（§5） |
| 候補の置き場 | DB のテーブル | 「あとで」の翌朝再掲と、却下したメールを二度と拾わないことが成立する |
| プランの持ち方 | 検証済みの生 JSON を1カラムに持つ | 毎朝まるごと差し替わり、行単位で編集されない。正規化しても得るものがない |
| 承認 | MoTask は関与しない | 既存 AI 遂行と同じ。利用者の `settings.json` が唯一の方針 |
| プロセスの所有 | 所有しない | 既存 AI 遂行と同じ。「追跡をやめる」は監視を降りるだけ |

## 4. 取り込み元を指定しないということ

MoTask は「どこから取るか」を知らない。`instruction.md` は Claude に対して
**「あなたが今アクセスできる受信箱・チャット・カレンダーを見て、今日のタスク候補を出せ」**
と頼むだけで、Outlook なのか Teams なのか Gmail なのかを列挙しない。

したがって:

- `TriageCandidate.Source` は enum ではなく**自由文字列**（`"Outlook"` / `"Teams"` / `"Gmail"` …）。
  UI はこれをそのままチップに出す
- 利用者がコネクタを増やしても MoTask は無改修
- 逆に、**認証済みのコネクタが1つも無ければ候補は0件になる**。これは失敗ではなく
  正常な結果として扱い、画面は「候補はありませんでした」を出す（§11）

MoTask と Claude の契約は「どこから取ったか」ではなく、次章の2ファイルの形だけである。

## 5. なぜ `AiJob` に相乗りしないか

`AiJob` は仕様上「タスク1件に対する AI の1回の依頼」であり、`TaskId` は非 null の必須 FK、
`GetJobsForTaskAsync` もカードのバッジも完了時の列移動も、対象タスクがあることを前提にしている。
朝の実行には対象タスクがない。

相乗りさせるには `TaskId` を nullable にしたうえで、`AiJobService` の完了パス・照会・
UI のバッジすべてに null 分岐を入れることになる。**AI 遂行はマージ直後で実機検証が1度しか
通っておらず、そこへ回帰リスクを持ち込む価値がない。**

代わりに、本当に再利用できる抽象だけを共有する。

| 共有するもの | 共有しないもの |
| --- | --- |
| `IJobFolder`（`Create` / `WriteJobJson` / `ReadTail`） | `AiJob` / `AiJobService` / `AiJobStatus` |
| `ISessionLauncher` / `IJobEventSource` | ジョブの意味論（終了時の副作用が本質的に違う） |
| `HookEventParser` / `JobFolderPaths` / `HooksJson` | — |
| `OperationGate`（DB 直列化） | — |

両者の終了時の副作用は本当に違う。片方はタスクを確認待ち列へ動かし、
もう片方はプランを差し替える。共通化しないほうが正直である。

## 6. アーキテクチャとデータフロー

```
[朝のプランを作る] ボタン
        ↓
  MorningService.StartAsync
        ↓
  ジョブフォルダを作る（instruction.md / hooks.json / board.json / run.json / result/）
        ↓
  MorningRun を DB に保存（フォルダ確定後。§12）
        ↓
  TerminalLauncher で claude を対話起動 → MoTask は手放す
        ↓
  events.jsonl を 500ms ポーリングで追従（既存機構）
        ↓
  Stop フックが来るたび result/ を読みに行く
        ↓
  MorningResultReader が検証 → TriageCandidate 群と PlanJson を保存 → Ingested
        ↓
  画面が候補キューと暫定プランを表示 → 人が仕分け → プラン確定
```

**cwd はジョブフォルダ自身**にする。既存の AI 遂行と違い、朝の実行はソースツリーに用がない。
（`TerminalLauncher` は `--add-dir <ジョブフォルダ>` を常に付けるが、cwd と同じなので無害。）

### ジョブフォルダ

`<既定ワークフォルダ>/morning/0007-2026-09-07/`

| ファイル | 書く人 | 中身 |
| --- | --- | --- |
| `instruction.md` | MoTask | 収集と判断の指示文。AI 設定ダイアログで編集可能 |
| `hooks.json` | MoTask | 既存 `HooksJson.Build` をそのまま流用 |
| `board.json` | MoTask | 未完了タスクのスナップショット（§7） |
| `run.json` | MoTask | `job.json` 相当。DB が壊れてもフォルダだけで何の実行か分かる保険 |
| `events.jsonl` | フック exe | 既存と同じ |
| `result/candidates.jsonl` | Claude | 候補（§8） |
| `result/plan.json` | Claude | プラン（§8） |

## 7. board.json — Claude に渡す盤面

統合先の推薦とプラン作成には現在の盤面が要る。SQLite を直接読ませず、
MoTask がスナップショットを書く。

```json
{"date":"2026-09-07",
 "columns":[{"id":1,"name":"やること","role":"Backlog"},{"id":2,"name":"今日中","role":"Active"}],
 "tasks":[{"id":45,"title":"Q4企画書の内容を確定する","columnId":2,"columnRole":"Active",
           "project":"プロジェクトQ4","due":"2026-09-08","labels":["社内"],
           "updatedAt":"2026-09-05T10:00:00Z","hasActiveAiJob":false}]}
```

論理削除済みのタスクは含めない。`hasActiveAiJob` はプランの「AI 準備完了」区分に要る。

## 8. 出力の契約

MoTask と Claude の唯一の接点。**この2つの形を知るコードは `MorningResultReader`（Core）
1クラスに閉じる。** `HookEventParser` が CLI 出力形状への依存を1点に閉じているのと同じ役割で、
同じく実キャプチャの fixture でピン留めする。

### result/candidates.jsonl

1行1候補の JSON Lines。

```json
{"externalId":"outlook:AAMkAD...","source":"Outlook","receivedAt":"2026-09-07T07:42:00+09:00",
 "from":"顧客A 山本さん","title":"請求先情報を更新する",
 "evidence":"「9月8日までに新しい請求先へ…」","link":"https://outlook.office.com/...",
 "reasoning":"依頼が明確で期限の記述あり。既存タスクとの重複なし。",
 "suggestedDueDate":"2026-09-08","suggestedProject":"顧客A",
 "suggestedAction":"register","mergeTargetTaskId":null}
```

| 項目 | 必須 | 備考 |
| --- | --- | --- |
| `externalId` | ○ | 重複排除の鍵。`<source>:<元のID>` の形を推奨するが強制はしない |
| `source` | ○ | 自由文字列（§4） |
| `title` | ○ | 人が編集してから登録できる |
| `evidence` | ○ | 元の文面からの引用。**これが無い候補は判断できないので捨てる** |
| `suggestedAction` | ○ | `register` / `merge` / `later` / `reject` の4値 |
| `mergeTargetTaskId` | `merge` のとき | `board.json` に載っていた `id` |
| その他 | — | 欠けていれば空扱い |

`suggestedAction` は**推薦であって実行ではない**。決めるのは常に人である。

### result/plan.json

```json
{"date":"2026-09-07",
 "firstThing":{"taskId":45,"reason":"送付前に部長の確認が必要。今朝の候補『数値の差し替え』を統合したため確認範囲が1つ増えた"},
 "groups":[{"key":"today","items":[{"taskId":45},{"externalId":"outlook:AAMkAD..."}]},
           {"key":"ifTime","items":[{"taskId":52}]},
           {"key":"aiReady","items":[{"taskId":61}]},
           {"key":"waiting","items":[{"taskId":70}]}]}
```

`groups[].key` は `today` / `ifTime` / `aiReady` / `waiting` の4つに固定。
`firstThing` は `taskId` または `externalId` のどちらかを指す。

**プランの各項目が `taskId` か `externalId` のどちらかを指す、というのが本設計の要である。**
これにより「仕分けが終わったらプランを作り直す」ための2回目の Claude 実行が要らなくなる。
候補を登録すればその行が実タスクに解決し、却下・あとでにすればその行がプランから消えるだけで、
ワイヤーの「候補5件を仕分け中 — 終わるとプランが確定します」が機械的に成立する。

### 検証と壊れた出力

- **行単位で検証し、不正な行は捨てて残りを取り込む。** 1行の JSON 崩れで朝が全滅しない
- **捨てた件数は画面に出す**（「5件のうち1件は読めませんでした」）。黙って減らさない
- `plan.json` が読めない、または `SessionEnd` まで候補0件かつ `plan.json` 無し
  → `Failed`。画面に理由とジョブフォルダを開くリンク
- 候補0件でも `plan.json` が妥当なら成功（コネクタ未認証や、単に候補が無い朝）

## 9. データモデル

新テーブルは2本。`Tasks` にも `AiJobs` にも列を足さない。

### MorningRun

| 列 | 型 | 備考 |
| --- | --- | --- |
| `Id` | int | |
| `Date` | DateOnly | 対象日（ローカル） |
| `Status` | string | `Pending` / `Running` / `Ingested` / `Failed` / `Cancelled` |
| `SessionId` | Guid | `--session-id` に渡す |
| `Instruction` | string | 実行時の指示文のスナップショット |
| `JobFolder` | string | 絶対パス |
| `StartedAt` / `EndedAt` | DateTime? | |
| `ErrorMessage` | string? | |
| `ProcessedLines` | int | `events.jsonl` を読んだ行数。再開位置（§10） |
| `PlanJson` | string | 検証済み `plan.json` の生データ。未取り込みは `""` |

**イベントを DB に持つテーブルは作らない。** `AiJobEvents` は 2026-09-07 に削除された
（ツールの入出力を丸ごと抱えて DB の半分を占めていた）。記録は `events.jsonl` が持ち、
表示は `IJobFolder.ReadTail` が末尾を読む。再開位置は行数カウンタが持つ
——`AiJob.ProcessedLines` と同じ形にする。朝の実行でも同じ構えを最初から採る。

### TriageCandidate

| 列 | 型 | 備考 |
| --- | --- | --- |
| `Id` | int | |
| `MorningRunId` | int | 取り込んだ実行 |
| `ExternalId` | string | **一意インデックス** |
| `Source` / `From` / `Title` / `Evidence` / `Link` / `Reasoning` | string | |
| `ReceivedAt` | DateTime? | |
| `SuggestedDueDate` | DateOnly? | |
| `SuggestedProject` | string | |
| `SuggestedAction` | string | `Register` / `Merge` / `Later` / `Reject` |
| `Status` | string | `Pending` / `Registered` / `Merged` / `Later` / `Rejected` |
| `ResultTaskId` | int? | 登録先または統合先 |
| `DecidedAt` | DateTime? | |

インデックス: `ExternalId`（一意）、`Status`、`MorningRunId`。

enum は既存の流儀どおり `.HasConversion<string>()` で文字列永続化する。
**既知の制約**: メンバーを削除する場合は保存済みの行を書き換えるマイグレーションが要る
（`AiJobStatus` と同じ）。

### 候補キューに何が並ぶか

**今日の `MorningRun` の候補 ＋ 過去の `Status == Later` の候補。**
これが「あとで」の定義である。`ExternalId` の一意制約により、
`Registered` / `Merged` / `Rejected` 済みのものは翌朝 Claude が再提出しても
取り込み時に黙って捨てられる。

### 履歴

`HistoryKind` に `CandidateRegistered` と `CandidateMerged` を追加する。
enum メンバーの追加は既存行に影響しないのでマイグレーションは不要。

## 10. レイヤの分担

### Core（UI・EF 非依存を維持）

- `Model/MorningRun.cs`, `MorningRunStatus.cs`, `TriageCandidate.cs`, `TriageAction.cs`, `TriageStatus.cs`
- `Morning/MorningResultReader.cs` — `result/` の形を知る唯一の場所。純関数
- `Morning/BoardSnapshot.cs` — `board.json` の組み立て
- `Morning/MorningInstruction.cs` — `instruction.md` のテンプレート生成
- `Morning/MorningPlanResolver.cs` — `PlanJson` と候補・タスクを突き合わせて表示用の行に解決する純関数
- `Abstractions/IMorningRepository.cs`
- `Services/IMorningService.cs` / `MorningService.cs`

### Data

`Repositories/MorningRepository.cs`、`MoTaskDbContext` に2エンティティ、マイグレーション1本。

### App

`ViewModels/MorningPlanViewModel.cs` / `TriagePanelViewModel.cs`、
`Views/MorningPlanView.xaml` ほか。既存 `JobEventWatcher` を朝の実行にも向ける薄い配線。

### MorningService の操作

| 操作 | やること |
| --- | --- |
| `StartAsync` | フォルダを作り端末を開いて手放す。`Pending` で返る |
| `RegisterAsync(candidateId, 編集後の値)` | `IBoardService` でタスクを作り、候補を `Registered`、履歴に「候補から登録」 |
| `MergeAsync(candidateId, targetTaskId)` | 対象タスクの説明末尾に根拠を追記し、候補側にだけ期限があれば設定。候補を `Merged`、履歴に「統合」 |
| `PostponeAsync` / `RejectAsync` | 候補の `Status` を変えるだけ。タスクは作らない |
| `StopTrackingAsync` | 追跡をやめて `Cancelled`。端末は殺さない |
| `RecoverOnStartupAsync` | 未完了の `MorningRun` の `events.jsonl` を読み直して追いつく |

**既存実装の規律をそのまま持ち込む。** `MorningService` は `BoardService` / `AiJobService` と
**同じ `OperationGate`** で直列化し、**ゲートの中から `IBoardService` を呼ばない**
（`AiJobService` のコメントに残っている既知のデッドロック）。登録・統合はゲートの外で呼ぶ。

## 11. UI

既存 `MainWindow` に画面切替を足し、「ボード」⇄「朝の実行プラン」の2画面にする。
ボード側のヘッダー右に `候補 N` バッジと「朝のプランへ →」を置いて2画面を接続する（ワイヤー 4c）。

### 朝の実行プラン画面

2カラム。左が固定パネル、右がプラン全体。

**左パネルはモードを持つ。** ワイヤーの核心はここで、候補が残っている間と片づいた後で
*同じ場所*が切り替わる。目線を動かさないための設計なので、別パネルの表示/非表示ではなく
1つの `ContentControl` の `DataTemplate` 切替として実装する。

- **状態1・仕分け中**（ワイヤー 4a）: 「00 ／ タスク候補の仕分け」と `1 / 5`、
  ソースチップ＋差出人＋受信時刻、タイトル、**根拠**（引用文と「元のメールを開く」＝
  `Link` を既定ブラウザで開く）、**AI の判断**（`Reasoning`）、
  編集フォーム（タイトル／期限／プロジェクト／登録先の区分）、アクション4つ
- **状態2・仕分け完了**（ワイヤー 4b）: 「最初にやる1件」＝ `plan.json` の
  `firstThing` とその理由

**右カラム**: 日付見出しと「候補 5件を仕分け中」、候補キュー
（ソースチップと推奨バッジ「統合が推奨」「今日中の候補」など）、確定待ちのプラン4区分。
プランの各行は §8 のとおり `taskId` / `externalId` を解決して描画し、仕分けの結果に追随する。

### 状態ごとの表示

| 状態 | 画面 |
| --- | --- |
| プラン未生成 | 「今日のプランはまだありません（前回: 9/5）」＋「朝のプランを作る」 |
| 実行中 | 既存 AI 実行と同じ進捗表示（ターン数と直近のツール使用） |
| 取り込み済み・候補あり | 状態1 |
| 取り込み済み・候補なし | 状態2。候補キューは「候補はありませんでした」 |
| 失敗 | 理由と「ジョブフォルダを開く」 |

### キーボード

`T` 登録 / `E` 統合 / `X` 却下 / `L` あとで（ワイヤーどおり）。
**編集欄にフォーカスがある間は無効化する。**

### 一括操作

「推奨をまとめて適用」は `suggestedAction` どおりに一括処理する。
**確認ダイアログを1枚挟む** — 複数件が一度に動き、統合はタスク本文を書き換えるため。
「すべて後で」は全候補を `Later` にする（こちらは取り消しが容易なので確認不要）。

## 12. エラー処理

既存 AI 基盤の失敗モードをそのまま引き継ぐ。

- `claude` が見つからない、hooks exe が配置されていない → **開始時点で止める**。黙って走らせない
- 端末を × で閉じられて `SessionEnd` が来ない → 人が「完了にする」を押す
- 「追跡をやめる」はプロセスを殺さない

朝の実行に固有のもの。

- `result/` が壊れている → §8 の方針（行単位で捨てる、件数を出す、全滅なら `Failed`）
- **二重起動の防止** → 未完了の `MorningRun` があれば新規開始を拒否

### 既知の穴をこちらでは作らない

`AiJobService.StartJobAsync` には「`JobFolder = ""` のまま `Pending` で保存し、
その窓でクラッシュすると `RecoverOnStartupAsync` が `JobFolder == ""` の行を飛ばすため
復旧できない」という、意図的に据え置かれた欠陥がある。

`MorningService` では**フォルダを先に作り、パスが確定してから初めて DB に保存**する順序にして、
この窓自体をなくす。

## 13. テスト方針

### Core

- `MorningResultReader` を**実キャプチャの fixture でピン留め**する
  （`HookEventParser` が4つの実 fixture で守られているのと同じやり方）。
  正常系、壊れた行の混入、重複 `ExternalId`、未知の `source` 値、`evidence` 欠落
- `BoardSnapshot` / `MorningInstruction` の生成
- `MorningPlanResolver` の解決（`taskId` / `externalId` の混在、登録済み候補の実タスクへの解決、
  却下された候補の行が消えること）
- `MorningService` の状態遷移、二重起動の拒否、
  登録・統合が `OperationGate` の外で `IBoardService` を呼ぶこと

### Data

マイグレーションの往復、`ExternalId` 一意制約。

### App

左パネルのモード切替、キーボードショートカット（編集中は無効）、候補バッジの件数。

### 自動テストで届かない領域

既存基盤と同じく、**実際に端末を開いて `claude` を走らせるテストは1本もない。**
したがって次は §14 の手動確認に回る。

`--add-dir` が可変長でプロンプトを飲み込み「端末は開くが何も始まらない」に至った前回の事故は、
まさにこの隙間で起きた。**起動引数に触る変更は、組み立てた文字列ではなく実 CLI で確認すること。**

## 14. 手動確認が要る項目

README のチェックリストに追記する。

- [ ] 「朝のプランを作る」で端末が開き、Claude がコネクタから候補を集めて
      `result/candidates.jsonl` と `result/plan.json` を書く
- [ ] `instruction.md` の指示が意図どおり効く（根拠の引用が入る、推奨が4値に収まる）
- [ ] 認証済みコネクタが1つも無いときに、失敗ではなく「候補なし」として扱われる
- [ ] `Stop` が複数回来る実行で、result が揃った時点で1度だけ取り込まれる
- [ ] 端末を × で閉じたあと「完了にする」で取り込みが走る
- [ ] 却下した候補が翌朝の実行で再提出されても候補キューに出てこない
- [ ] 「あとで」にした候補が翌朝の候補キューに残っている
- [ ] 統合でタスクの説明末尾に根拠が追記され、履歴に1件残る

## 15. 実装計画の分割

このスコープは1本のプランに収まらない。2本に割る。

1. **取り込みと仕分け** — §9 のデータモデル、`MorningResultReader`、`BoardSnapshot`、
   `MorningInstruction`、`MorningService` の起動と取り込みと4アクション、
   最小の UI（候補一覧とアクション）
2. **プラン確定と画面** — `PlanJson` の解決、ワイヤー 4a / 4b の左パネルのモード切替、
   右カラムの候補キューと4区分、ボード画面との導線、キーボード、一括操作

1 が終わった時点で「候補を取り込んでタスクにする」までは動く。2 は見せ方を仕上げる。
