# MoTask AI ログ — DB 記録の廃止と直近 3 行表示（設計仕様）

前提: `docs/superpowers/specs/2026-09-05-motask-terminal-ai-design.md`（ターミナル実行への作り替え）

## 1. なぜ変えるか

ターミナル実行に作り替えたあと、`events.jsonl` の 1 行 = `AiJobEvents` の 1 行として全量を DB に積んでいる。
ジョブ 1 本でツール呼び出しが数十回走るので、詳細パネルのログが読めない長さになり、DB も膨らみ続ける。

そもそも進行状況は端末で見えている。MoTask 側に全量を持つ必要が無い。

## 2. スコープ

### 含む

- `AiJobEvents` テーブルと、そこへの読み書きの廃止
- 詳細パネルのログを直近 3 行に限定する
- 追従の再開位置を、保存済みイベント件数から `AiJob` 上のカウンタへ移す

### 含まない

- `AiJob` 行そのものの廃止（状態・ターン数・ジョブフォルダは今までどおり残す）
- カードのバッジ、完了時の列移動、端末の開き直し、起動時復旧の挙動（すべて現状維持）
- `HistoryEntry`（履歴）の扱い
- `events.jsonl` の書式・フック配線・起動コマンド

## 3. 決定事項とその理由

| 決定 | 理由 |
| --- | --- |
| ログ行は DB に記録しない。表示は直近 3 行だけ | 進行状況は端末で見える。MoTask は「いま何をしているか」が分かれば足りる |
| `AiJob` 行は残す | バッジ・列移動・再開・起動時復旧はすべてこの行に依存しており、失うものが大きい |
| 3 行は `events.jsonl` の末尾を読んで作る | フックが書くファイルが実質の記録。MoTask は何も記録せずに、再起動後も過去ジョブでも同じ見た目を保てる |
| 再開位置は `AiJob.ProcessedLines`（int）で持つ | ログ本文は残さずカウンタだけ。既存の復旧挙動をそのまま保てる |
| 現在のファイル行数を再開位置に使わない | アプリを閉じている間に届いた行を読み飛ばし、`SessionEnd` を取りこぼす |
| 結果ペインと成果物一覧は残す | 最終回答と artifacts は AI に任せた成果そのもので、ログの冗長さとは別の話 |

## 4. データモデルの変更

削除:

- テーブル `AiJobEvents`（マイグレーションで DROP。既存のログ行も消える）
- `MoTaskDbContext.AiJobEvents` と、その `Entity<AiJobEvent>` 設定
- `IAiJobRepository.AddEvent(AiJobEvent)` と `IAiJobRepository.GetEventsAsync(int, CancellationToken)`

追加:

- `AiJob.ProcessedLines`（`int`、既定 `0`）。MoTask がその `events.jsonl` から取り込み済みの行数
- `IJobFolder.ReadTail(string root, int lines)`（§5）
- `IAiJobService.GetResultTextAsync(int jobId, CancellationToken)`（§5）

変更:

- `AiJobEvent` は型としては残すが、EF から外して純粋なメモリ上の DTO にする。`Id` を落とす
  （`Seq` / `At` / `Kind` / `ToolName` / `Payload` は残す。整形と `JobChanged` がそのまま使えるため）

`Down` マイグレーションは `AiJobEvents` を空のまま復元し、`ProcessedLines` を落とす。消えたログ行は戻らない。

## 5. 直近 3 行の作り方

新しい口を `IJobFolder` に 1 つ足す:

```csharp
/// <summary>events.jsonl の末尾 lines 行。フォルダやファイルが無ければ空。</summary>
IReadOnlyList<string> ReadTail(string root, int lines);
```

`AiJobService.GetEventsAsync(jobId)` は DB を引くのをやめ、次の形にする:

1. ジョブの `JobFolder` が空、またはフォルダが無ければ空を返す
2. `IJobFolder.ReadTail(root, 3)` で末尾 3 行を取る
3. 各行を `HookEventParser.Parse` に通し、`AiJobEvent` に組み立てて返す
   （`Seq` は「そのジョブで何行目か」を保てないので、返す 3 件に先頭から 1, 2, 3 を振る。表示順にしか使わない）

`BoardViewModel.QueryAiEventsAsync` と `AiJobEventFormatter` は変えない。返る件数が 3 件になるだけ。

### 結果テキスト

`AiJobEventFormatter.ResultText` は与えられたイベント列から最後の `TurnEnded` の `last_assistant_message` を拾う。
末尾 3 行に `TurnEnded` が無いと結果ペインが空になるので、結果は別の口で取る。

`IAiJobService` に `Task<string> GetResultTextAsync(int jobId, CancellationToken ct = default)` を足し、
`events.jsonl` を末尾から遡って最初に見つかった `TurnEnded` の `last_assistant_message` を返す。
遡るのは末尾から最大 200 行まで。見つからなければ空文字。

これに伴い変わる呼び出し側は 2 か所だけ:

- `BoardViewModel` に `QueryAiResultTextAsync(int jobId)` を足す
- `TaskAiPanelViewModel` は、結果ペインの文字列を「読み込んだイベント列から `ResultText` で導く」のをやめ、
  この新しい口から取る

### ライブで届く行

`JobChanged` の `NewEvent` は今までどおり 1 件ずつ上がる。詳細パネルは受け取るたびに
`Log` へ追記し、3 件を超えたら先頭を捨てる。

## 6. 追従と再開

`AiJobService` は 1 行取り込むごとに `ProcessedLines` を 1 進め、状態遷移・ターン数の更新と同じ保存に相乗りさせる。

購読を張るときの `JobEventSubscription.SkipLines` は `job.ProcessedLines` を渡す。対象は 3 か所:

- `StartJobAsync`（新規なので 0）
- `ReopenTerminalAsync`
- `RecoverOnStartupAsync`

これにより「アプリを閉じている間に届いた行」は次回起動時に読み直され、`SessionEnd` を取りこぼさない。

## 7. UI

詳細パネルの AI セクションは構成を変えない。状態バッジ、ログ、結果、成果物一覧、操作ボタンの並びはそのまま。
変わるのはログが常に最大 3 行になることだけ。「以前のログは端末で見てください」といった注記は置かない
（端末が開いていることは利用者が知っている）。

## 8. エラー処理

- `events.jsonl` が読めない（フォルダごと消された、権限が無い）ときは、ログを空にして落ちない。
  既存の `Messages` に理由を足さない（パネルが空になるだけで、ジョブの状態は `AiJob` 行が持っている）
- 壊れた行は `HookEventParser` が `System` として返す。今までどおり捨てない

## 9. テスト方針

- `JobFolder.ReadTail`: 空 / 1 行 / 3 行未満 / 3 行超 / 末尾に改行が無い / フォルダが無い / 巨大な最終行
- `AiJobService.GetEventsAsync`: 3 件に収まること、`JobFolder` が空のジョブで空が返ること
- `AiJobService.GetResultTextAsync`: 末尾 3 行の外にある `TurnEnded` から結果を拾えること、
  200 行の上限を超えたら空になること
- 再開: `ProcessedLines` を進めたジョブで、閉じている間に届いた `SessionEnd` を起動時に拾って完了にすること
  （既存の `Recover_FinishesAJobWhoseSessionEndArrivedWhileTheAppWasClosed` を、DB のイベント件数から
  `ProcessedLines` へ置き換える）
- マイグレーション: `AiJobEvents` が消え、`AiJob.ProcessedLines` が既定 `0` で入ること
- 実起動（端末を開く、実セッションを走らせる）は自動テストしない。これは前計画からの継続

## 10. グローバル制約（前提仕様から継続）

- 依存方向は App → Data → Core、App → Core。`MoTask.Core` は他に依存しない
- 利用者向けの文言は日本語で resx に置く。コード直書きの日本語文字列を作らない
- コメントと XML ドキュメントは日本語
- 列挙の既存番号は動かさない。`AiJobStatus` / `AiJobEventKind` / `AiJobKind` は EF で文字列保存
- MoTask は端末のプロセスを所有しない。プロセスを殺すコードを書かない
- マイグレーションは `dotnet ef` で生成する
- テスト実行は `dotnet test MoTask.sln`
