# MoTask 朝の実行プラン（2/2）プランの確定と画面（追補設計仕様）

日付: 2026-09-09
状態: 設計承認済み（実装計画は未作成）
親仕様: `2026-09-07-motask-morning-plan-triage-design.md`（§15 の「2本目」がこの文書）
前提: 1本目「取り込みと仕分け」は実装済み・master にマージ済み
（`docs/superpowers/plans/2026-09-07-motask-morning-triage-intake.md`）
参照: `Incomplete design request/Morning Plan Wireframes.dc.html` の 4a / 4b / 4c

この文書は親仕様に**足す**ものである。親仕様と矛盾する記述はここには無い。
親仕様が決めていなかったことだけを決める。

## 1. 何を作るか

1本目で「候補を取り込んでタスクにする」までは動く。`MorningRun.PlanJson` には
検証済みの `plan.json` が保存されているが、**まだ誰も読んでいない**。
この2本目は、その `PlanJson` を画面の行に解決し、ワイヤー 4a / 4b の画面に仕上げる。

1本目の計画が明示的に残した7項目がそのまま範囲である。

1. `MorningPlanResolver` — `PlanJson` の `taskId` / `externalId` を実タスクと候補に解決する
2. 左パネルのモード切替 — 仕分け中（4a）⇄ 最初にやる1件（4b）
3. 右カラム — 候補キュー（ソースチップ・推奨バッジ）と4区分のプラン
4. ボード画面との導線 — 候補件数のバッジ
5. キーボード `T` / `E` / `X` / `L`（編集欄にフォーカスがある間は無効）
6. 「推奨をまとめて適用」（確認ダイアログを1枚挟む）と「すべて後で」
7. 統合先を人が選ぶ UI

## 2. スコープ

### 含む

上の7項目。加えて、それらに必要な最小限の追加:

- `IMorningRepository` / `IMorningService` に「実行の候補を状態を問わず全部返す」照会
- `IMorningService` に一括操作2つ
- `BoardViewModel` に「Id でタスクを選択する」操作（朝の画面からボードへ飛ぶため）

### 含まない

- **契約の変更。** `candidates.jsonl` / `plan.json` / `instruction.md` / `board.json` の形は変えない。
  `MorningResultReader` と `MorningInstruction` にも触らない
- `TaskItem` / `TriageCandidate` / `MorningRun` への列追加。マイグレーションは作らない
- ワイヤーにあるが契約に無いもの: 「所要」「次の一手」「必要情報」「着手する」
  「Claude に相談」「今日じゃない」「再計算」「共有」「履歴を見る」「AI に準備を依頼する」
- プランの手編集（親仕様 §2 のとおり。プランは毎朝まるごと差し替わる）
- ボード画面のヘッダー右に別途「候補 N ／ 朝のプランへ →」を置くこと（§7 で既存タブに統合する）

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| 状態2の左パネル | タイトル・選定理由・「ボードで開く」の3つだけ | `plan.json` の `firstThing` にあるのは `taskId` / `externalId` と `reason` だけ。契約を変えずに済む |
| ボードからの導線 | 既存の「朝の実行プラン」タブに件数バッジを付ける | 同じ画面への導線を2つ作らない |
| 統合先の選択 | 盤面の未完了タスクから選ぶコンボボックス。推薦があれば初期選択 | 全候補で `E`=統合 が効くようになり、親仕様 §11 のキー割当と一致する |
| 解決の置き場 | Core の純関数 `MorningPlanResolver` | `MorningResultReader` と同じ構え。fixture で固定でき、UI 無しで全ルールを試せる |
| 一括操作の置き場 | `MorningService`（Core） | 既存の4アクションを内側で順に呼ぶだけ。Core のテストで「止まらない・見送り理由が出る」を固定できる |
| 一括登録の列 | 完了以外の先頭の列に固定 | 候補ごとに列を聞いたら一括にならない。確認ダイアログの文言に明記する |
| 確認ダイアログ | VM の `Func<string, bool> Confirm`。既定は `MessageBox`、テストでは差し替え | `OpenPath` と同じ流儀 |
| 手動確認の置き場 | この文書の §11 | README のチェックリストは 2026-09-08 に外された。戻さない |

## 4. MorningPlanResolver — 解決ルール

親仕様 §8 の要（「各項目が `taskId` か `externalId` のどちらかを指す」）を画面に落とす純関数。
`src/MoTask.Core/Morning/MorningPlanResolver.cs`。

### 入力

| 引数 | 何か |
| --- | --- |
| `planJson` | `MorningRun.PlanJson`（検証済みなので `groups[].key` は4値のどれか、`items[]` は配列） |
| `candidates` | その実行の `TriageCandidate` を**状態を問わず全部** |
| `board` | `IBoardService.GetBoardAsync` の `Board`（列とタスク） |
| `projects` | プロジェクト名の解決用 |

`PlanJson` が空文字（未取り込み）なら、行の無い `ResolvedPlan` を返す。

### 出力

```
ResolvedPlan
  FirstThing: PlanRow?      — 最初にやる1件
  FirstThingReason: string  — plan.json の firstThing.reason。無ければ ""
  FirstThingIsFallback: bool — firstThing が解決できず today の先頭に繰り下げたか
  Groups: PlanGroup[4]      — today / ifTime / aiReady / waiting の順で必ず4つ
  Summary: TriageSummary    — 候補の総数と、登録・統合・却下・あとで・未処理の各件数

PlanGroup
  Key: PlanGroupKey         — 4値の enum
  Rows: PlanRow[]
  TaskCount: int            — タスク行の数
  CandidateCount: int       — 候補行（仕分け待ち）の数

PlanRow は2種類のどちらか
  TaskRow: TaskId, Title, ProjectName, DueDate, ColumnName, IsDone, Origin
    Origin は None / RegisteredThisMorning / MergedThisMorning
  CandidateRow: CandidateId, Title, Source, SuggestedDueDate, SuggestedProject
```

### ルール

| 項目の指す先 | 結果 |
| --- | --- |
| `taskId` が盤面にある（論理削除されていない） | `TaskRow`。完了列（`ColumnRole.Done`）にあれば `IsDone = true`（取り消し線で残す） |
| `taskId` が盤面に無い、または論理削除済み | 行を落とす |
| `externalId` の候補が `Pending` / `Later` | `CandidateRow` |
| 候補が `Registered` / `Merged` | `ResultTaskId` のタスクで `TaskRow`。`Origin` に印。そのタスクが盤面に無ければ落とす |
| 候補が `Rejected` | 行を落とす |
| `externalId` に一致する候補が無い（取り込み時に捨てられた行） | 行を落とす |
| `taskId` と `externalId` の両方を持つ項目 | `taskId` を優先する（契約は「どちらか一方」だが、両方来ても落とさない） |
| どちらも持たない項目 | 行を落とす |
| 同じタスクが複数回現れる | today → ifTime → aiReady → waiting の順に走査し、**最初の1回だけ**出す。同じ区分の中でも同じ |
| 同じ候補が複数回現れる | 同上 |
| `firstThing` が解決できる | その行。重複排除には参加しない（区分の中にも出る） |
| `firstThing` が解決できない、または無い | `today` の先頭行に繰り下げ、`FirstThingIsFallback = true`。`today` も空なら `FirstThing = null` |

「解決できない」は上の表で「行を落とす」に該当する場合すべてを指す。

`Summary` は `candidates` の `Status` を数えるだけ。`Registered` / `Merged` / `Rejected` / `Later` / `Pending` を
それぞれ数える。今日の `Later` はキューに乗らない（キューは今日の `Pending` ＋ 過去の `Later`）ので、
「あとで」は決着済みとして数える。`Pending` が 0 でないのは仕分け中だけ。

### 何を保証しないか

- 区分の中の順序は `plan.json` の順序をそのまま使う。並べ替えない
- `aiReady` の行に AI ジョブの状態は載せない（`board.json` の `hasActiveAiJob` は Claude への入力であって、画面の出力ではない）

## 5. Core / Data の追加

### IMorningRepository

```
Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);
```

`MorningRunId == runId` を状態を問わず Id 昇順で。既存の `GetQueueAsync` は Pending ＋ 過去の Later
だけなので、解決には使えない。

### IMorningService

```
Task<IReadOnlyList<TriageCandidate>> GetCandidatesOfRunAsync(int runId, CancellationToken ct = default);
Task<Result<BulkOutcome>> ApplySuggestionsAsync(int runId, int registerColumnId, CancellationToken ct = default);
Task<Result<BulkOutcome>> PostponeAllAsync(int runId, CancellationToken ct = default);
```

```
BulkOutcome(int Applied, IReadOnlyList<string> Skipped)
```

`ApplySuggestionsAsync` は `GetQueueAsync(runId)` の候補を Id 昇順に、`SuggestedAction` どおりに処理する。

| `SuggestedAction` | 呼ぶもの |
| --- | --- |
| `Register` | `RegisterAsync(new CandidateDecision(id, Title, SuggestedDueDate, SuggestedProject, registerColumnId))` |
| `Merge` | `MergeAsync(id, SuggestedMergeTaskId)`。`SuggestedMergeTaskId` が null なら見送り |
| `Later` | `PostponeAsync(id)` |
| `Reject` | `RejectAsync(id)` |

- **1件失敗しても止めない。** 失敗した候補は `Skipped` に「タイトル: 理由」で積み、次へ進む
- 4アクションの `Warnings` は `Skipped` には入れない（成功しているので）。まとめて `Result.Warnings` に載せる
- 既存の4アクションを呼ぶだけなので、**`OperationGate` の外から `IBoardService` を呼ぶ規律は自動的に守られる。**
  一括操作自身はゲートを取らない
- 全件見送りでも `Result` は成功。画面は `Applied` / `Skipped` を見て文言を出す

`PostponeAllAsync` は同じ形で `PostponeAsync` を順に呼ぶ。

### 触らないもの

`AiJob*` 一式、`MorningResultReader`、`MorningInstruction`、`BoardSnapshot`、既存の4アクションの中身。

## 6. App — ViewModel の構成

今の `MorningPlanViewModel` は「実行の状態 ＋ 候補キュー ＋ 編集フォーム ＋ 4アクション」を1つで持っている。
左パネルをモード切替にするため、**編集フォームと4アクションを `TriagePanelViewModel` へ移す。**

```
MorningPlanViewModel（画面）
  ├ 実行の状態（今のまま: CanStart / IsRunning / IsFailed / Progress / バナー）
  ├ Candidates（候補キュー・右カラム）
  ├ Plan: ResolvedPlan の表示用（右カラム）
  │   ├ FirstThingText / FirstThingReason（暫定表示にも使う）
  │   └ Sections: PlanSectionViewModel ×4（Heading、CountText、Rows: PlanRowViewModel）
  ├ LeftPanel: object?  — TriagePanelViewModel か FirstThingViewModel か null
  ├ PendingCount: int   — タブのバッジ。Candidates.Count と同じ値
  ├ 一括操作: ApplySuggestionsCommand / PostponeAllCommand
  ├ Confirm: Func<string, bool>（既定 MessageBox.Show YesNo、テストで差し替え）
  └ event NavigateToTask(int taskId)

TriagePanelViewModel（左・状態1）
  ├ Selected: CandidateItemViewModel（画面の Selected と同じもの）
  ├ HeadingText / PositionText（「00 ／ タスク候補の仕分け」「1 / 5」）
  ├ 編集フォーム: EditTitle / EditDueDate / EditProjectName / EditColumnId / ColumnChoices
  ├ 統合先: MergeTargets（TaskChoice(Id, Title, ColumnName) の一覧）/ EditMergeTargetId
  ├ Register / Merge / Postpone / Reject / OpenLink の各コマンド
  └ CanMerge: EditMergeTargetId が選ばれているか

FirstThingViewModel（左・状態2）
  ├ Title / Reason / IsFallback（繰り下げなら「（暫定）」を付ける）
  ├ HasFirstThing（無ければ「最初にやる1件はありません」）
  └ OpenOnBoardCommand → NavigateToTask
```

### LeftPanel の決まり方

| 状態 | `LeftPanel` |
| --- | --- |
| 実行が無い / 実行中 / 失敗 | `null`（上部バーが案内とボタンを担う。左は空） |
| 取り込み済みで候補キューが1件以上 | `TriagePanelViewModel` |
| 取り込み済みで候補キューが0件 | `FirstThingViewModel` |

候補キューは親仕様 §9 のとおり「今日の実行の Pending ＋ 過去の Later」なので、
過去の「あとで」が残っている朝は仕分けから始まる。これは意図どおり。

### 統合先の一覧

`IBoardService.GetBoardAsync` の全列から、論理削除されておらず `ColumnRole.Done` でないタスク。
候補を選ぶたびに `EditMergeTargetId` を `SuggestedMergeTaskId` で初期化する（一覧に無ければ未選択）。
`MergeCommand` は `EditMergeTargetId` が選ばれているときだけ実行できる。
今の `CandidateItemViewModel.CanMerge`（推薦の有無）は「統合が推奨」バッジの判定に用途を変える。

### プランの再解決のタイミング

`ReloadQueueAsync` と同じ場所で、`GetCurrentRunAsync` → `GetCandidatesOfRunAsync` → `GetBoardAsync` →
`MorningPlanResolver` を通す。仕分けの1件ごとに解決し直すので、登録した候補の行はその場で
実タスクに変わり、却下した行はその場で消える。

### ボードとの往復

- `NavigateToTask` は `FirstThingViewModel.OpenOnBoardCommand` と `PlanRowViewModel`（タスク行）の
  クリックから上がる。候補行のクリックは、その候補を `Selected` にするだけ
- `MainWindow` が購読し、`ShowBoard(true)` → `BoardViewModel.SelectTask(taskId)` の順に呼ぶ
- `BoardViewModel.SelectTask(int taskId)` を新設する。`Columns` の `AllCards` から探して
  `SelectCard` を呼ぶだけ。見つからなければ何もしない（フィルタで隠れている場合を含む）

### タブのバッジ

- `MainWindow` の「朝の実行プラン」タブに `PendingCount` を束縛し、0 のときは非表示
- 起動時（`OnLoaded`）にも `_morning.LoadAsync()` を呼んで件数を出す。今はタブを押すまで読まない
- `RunChanged`（候補の取り込み）と仕分けのたびに `PendingCount` が動く

### キーボード

`MainWindow.OnPreviewKeyDown` に足す。既にある「`TextBoxBase` / `ComboBox` / `DatePicker` に
フォーカスがあれば奪わない」判定の**後ろ**で、朝の画面が表示中かつ `LeftPanel` が
`TriagePanelViewModel` のときだけ効く。

| キー | 操作 |
| --- | --- |
| `T` | 登録 |
| `E` | 統合（`CanMerge` が偽なら何もしない） |
| `X` | 却下 |
| `L` | あとで |

キーから操作への対応は `MorningKeyMap`（App、純関数）に切り出し、
`Key` と修飾キーから `TriageKeyAction?` を返す形にしてテストする。修飾キー付きは対象外。

### 一括操作

- 「推奨をまとめて適用」: `Confirm` に「候補 N 件を推奨どおりに処理します。登録は『<列名>』へ入ります。
  統合は対象タスクの説明を書き換えます。」を渡し、真のときだけ `ApplySuggestionsAsync`
- 「すべて後で」: 確認なしで `PostponeAllAsync`（親仕様 §11 のとおり）
- 終わったら `Applied` / `Skipped` を警告バナーに1行で出す（「4 件を処理、1 件を見送り: …」）。
  その後キューとプランを読み直す
- どちらもキューが0件なら実行できない

## 7. App — 画面（ワイヤー 4a / 4b）

2カラム。左 360px の固定パネル、右はスクロールするプラン全体。上部バー（バナー、
「プラン未生成」の案内、開始・完了・追跡をやめる・ジョブフォルダを開く）は今のまま。

### 左パネル

`ContentControl Content="{Binding LeftPanel}"` に、型ごとの `DataTemplate` を2つ。

- **仕分け中**（4a）: 見出しと `1 / 5`、ソースチップ＋差出人＋受信時刻、タイトル、根拠（引用と
  「元のメールを開く」）、AI の判断、編集フォーム（タイトル／期限／プロジェクト／登録先の列／統合先）、
  「タスクに登録」（主）と「統合」「あとで」「却下」、キーの案内文
- **仕分け完了**（4b）: 「01 ／ 最初にやる1件」、タイトル、選定理由、「ボードで開く」。
  繰り下げなら見出しに「（暫定）」

### 右カラム

上から順に。

1. 見出し: 「9月9日（火）の実行プラン」。その下に状態1なら「候補 5件を仕分け中 — 終わるとプランが
   確定します」、状態2なら「仕分け完了 候補5件 → 登録2・統合1・却下1・あとで1」
2. 一括操作のボタン2つ（状態1のみ）
3. 候補キュー（状態1のみ）: 1件がソースチップ、タイトル、差出人／期限の小さな1行、推奨バッジ。
   推奨バッジは `SuggestedAction` を「登録が推奨」「統合が推奨」「あとでが推奨」「却下が推奨」に写すだけ。
   選択中は枠がアクセント色（今のスタイルを流用）。候補0件の取り込み済みは「候補はありませんでした」
4. プラン: 「01 最初にやる1件」（状態1では「（暫定）」を付ける）、
   「02 今日中 3件（+候補2件）」「03 余裕があれば」「04 AI 準備完了」「05 待ち」。
   タスク行はタイトル、プロジェクト／期限の小さな1行、`Origin` に応じて「新規」「統合」の印、
   完了なら取り消し線。候補行はソースチップと「仕分け待ち」。空の区分は見出しだけ残して「なし」

### 状態ごとの表示（親仕様 §11 の表に左右を足したもの）

| 状態 | 上部バー | 左 | 右 |
| --- | --- | --- | --- |
| プラン未生成 | 案内と「朝のプランを作る」 | 空 | 空 |
| 実行中 | 進捗、完了にする、追跡をやめる | 空 | 空 |
| 失敗 | 理由、ジョブフォルダを開く | 空 | 空 |
| 取り込み済み・候補あり | — | 仕分け | 候補キュー ＋ 暫定プラン |
| 取り込み済み・候補なし | — | 最初にやる1件 | 仕分け完了の要約 ＋ 確定プラン |

### 文言

新しい文言はすべて `src/MoTask.App/Resources/Strings.resx`（アクセサ `Strings.cs`）に置く。
Core 側の見送り理由は既存の `Messages.resx` の文言をそのまま使う（4アクションの `Error` を転記するだけ）。

## 8. エラー処理

- 解決に必要な `GetBoardAsync` が失敗したら、プランは行なし・左パネルは今の状態のまま・
  警告バナーに理由。候補キューと仕分けは盤面が無くても動く
- `PlanJson` が空なのに `Ingested`（親仕様 §8 では起こらない）→ 行なしとして扱い、落とさない
- 一括操作の途中の失敗は §5 のとおり止めない。バナーで件数と理由を出す
- `NavigateToTask` で対象がフィルタに隠れていて選べない → ボードに切り替えるだけで、エラーにしない

## 9. テスト方針

### Core

- `MorningPlanResolver`: §4 の表の全行を1ケースずつ。加えて、`PlanJson` が空、`groups` が4つ未満
  （検証は `key` の値しか見ないので、欠けた区分は空で補う）、`firstThing` が候補を指していて
  登録済み、`firstThing` が却下済みで `today` の先頭に繰り下がる
- `MorningService.ApplySuggestionsAsync`: 4種が対応する操作を呼ぶ、統合先が消えていて見送り、
  1件目が失敗しても2件目が処理される、`Warnings` が集約される、ゲートを取らない
  （既存の「ゲートの中から `IBoardService` を呼ばない」テストと同じ手筋）
- `MorningService.PostponeAllAsync`: 全件 `Later`

### Data

`GetCandidatesOfRunAsync` が状態を問わず、他の実行の候補を含めずに返す。

### App

- `MorningPlanViewModel`: `LeftPanel` の型が状態で切り替わる、`PendingCount`、仕分け後にプランの
  行が実タスクに変わる／消える、`Confirm` が偽なら一括適用が呼ばれない、
  一括の結果がバナーに出る
- `TriagePanelViewModel`: 統合先の初期選択、未選択なら `MergeCommand` が動かない
- `MorningKeyMap`: 4キーの対応、修飾キー付きは null
- `BoardViewModel.SelectTask`: 見つかれば選択、無ければ何もしない

### 自動テストで届かない領域

`MainWindow` のキー処理・タブ切替・バッジの束縛は code-behind なので手動確認（§11）。

## 10. 実装計画への注意

- 1本目と同じく、**`AiJob*` 一式とテストが1行も壊れないこと**を完了条件に入れる
- `MorningPlanViewModelTests` は VM の分割で書き直しになる。既存のケース（バナーの消し方、
  古い通知の無視、`RefreshAsync` の例外がバナーへ回ること）は**落とさずに移す**
- 文言を `resx` に足したら同じ名前のプロパティを `.cs` に足す（`StringsTests` が守っている）

## 11. 手動確認が要る項目

README には足さない。実施したらここにチェックを入れてコミットする。

- [ ] 取り込み後、左に仕分けパネル、右に候補キューと暫定プランが出る
- [ ] 候補を登録すると、右のプランのその行がその場で実タスク（「新規」）に変わる
- [ ] 候補を却下すると、右のプランのその行が消える
- [ ] 最後の候補を片づけると、左が「最初にやる1件」に切り替わる
- [ ] `firstThing` が却下した候補を指していたとき、`today` の先頭が「（暫定）」で出る
- [ ] 「ボードで開く」でボードに切り替わり、そのタスクが選択されている
- [ ] 「朝の実行プラン」タブのバッジが起動直後から出て、仕分けのたびに減る
- [ ] `T` / `E` / `X` / `L` が効く。タイトル欄にカーソルがある間は効かない
- [ ] 統合先のコンボボックスに推薦が初期選択され、別のタスクに変えて統合できる
- [ ] 「推奨をまとめて適用」で確認ダイアログが出て、「いいえ」なら何も起きない
- [ ] 「すべて後で」で候補が全部消え、翌朝の候補キューに残っている

- 2026-09-09: 自動テスト 749 件は緑。実機確認は MoTask.exe が起動中のため未実施（次回のセッションで実施する）
