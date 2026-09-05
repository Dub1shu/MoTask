# MoTask AI 遂行 — ターミナル実行への作り替え（設計仕様）

日付: 2026-09-05
状態: 設計承認済み（実装計画は未作成）
前提: `2026-09-05-motask-ai-execution-design.md`（ヘッドレス実行版。実装済み・master にマージ済み）

## 1. なぜ作り替えるか

現行実装は `claude -p --output-format stream-json` を隠しウィンドウで起動し、MoTask 自身が
承認 MCP サーバを立ててツール1回ごとに WPF ダイアログを出す。動くが、次の点で仕事にならない。

- **途中で止められない・方向転換できない。** 走り出したら終わるまで待つか殺すかの二択で、
  「そっちじゃない、こっちを調べて」が言えない
- **Claude からの確認に答える口が無い。** モデルが人に聞きたいことがあっても、聞く先が無い
- **承認を二重に持っている。** 利用者は自分の `settings.json` で許可方針を決めているのに、
  MoTask が `--setting-sources ""` でそれを無効化し、自前のルールで聞き直している
- **成果物がどこにあるか散らかる。** cwd に混ざって出るうえ、`... > report.md` のように
  Write/Edit を経由しないファイルは一覧に出ない

作り替えの方針は一言でいうと、**MoTask はエージェントを走らせるホストをやめ、
セッションの発注者兼観測者になる**。対話は本物のターミナルで人と Claude が直接行う。

## 2. スコープ

### 含む

- ジョブごとのフォルダを作り、指示文・フック定義・イベントログ・成果物をそこに集める
- Claude Code を Windows Terminal で対話起動し、MoTask は手放す
- Claude Code の hooks が書くイベントを MoTask が追従し、盤面と詳細パネルに反映する
- セッション終了でタスクを確認待ち列へ送る
- 中断したセッションを `--resume` で開き直す
- 現行の承認サブシステム一式の削除

### 含まない

- ツール承認の UI（利用者の Claude Code 設定に委ねる）
- 費用の表示（後述 §9）
- MoTask 内へのターミナル埋め込み（ConPTY）
- 複数タスクの一括依頼、スケジュール実行

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| 追跡の手段 | ジョブフォルダ＋hooks でファイルに書かせ、MoTask は読むだけ | MoTask を閉じても記録が続く。プロセス間の口を持たずに済む |
| ツール承認 | MoTask は一切関与しない | 利用者の `settings.json` が唯一の方針。二重管理をやめる |
| 権限モード | `--permission-mode auto` を既定、設定で変更可 | 既定で止まらず走り、危険な操作は端末で人に聞かれる |
| プロセスの所有 | 所有しない | 対話中の端末をアプリが殺すのは乱暴。閉じても走り続けてよい |
| 完了の判定 | `SessionEnd` フック | 対話なので「モデルが黙った」は完了ではない。人がセッションを終えた時が完了 |
| 作業ディレクトリ | cwd はプロジェクト、出力先はジョブフォルダ | コードを直す仕事も調査の仕事も同じ形で扱える |
| ジョブフォルダの位置 | 既定ワークフォルダ配下 | リポジトリを汚さない。プロジェクト未設定のタスクでも同じ形 |
| フックの実体 | 専用の小さな exe | シェルのワンライナーで JSON を追記するのは引用符と文字コードで壊れやすい |

## 4. 実機で確認した CLI の挙動

Claude Code 2.1.260 で検証済み。設計はこれらの事実に依存する。

### 4.1 `--settings <file>` は利用者の設定に「足す」

`--settings` は追加の設定ファイルを読む。`--setting-sources` を指定しない限り利用者の
user / project / local 設定はそのまま効く。**利用者の許可方針を壊さずに MoTask のフックだけ
差し込める。**

### 4.2 対話起動でも `--session-id` が効く

MoTask 側で GUID を採番して渡せる。再開のために出力から ID を拾う必要が無い。

### 4.3 `--permission-mode` の有効値

`acceptEdits` / `auto` / `bypassPermissions` / `manual` / `dontAsk` / `plan`。

### 4.4 フックの発火とペイロード

`SessionStart` / `PostToolUse` / `Stop` / `SessionEnd` の 4 つが発火することを実測した。
各ペイロードは 1 行の JSON として stdin に来る。共通で `session_id`, `transcript_path`,
`cwd`, `hook_event_name` を持ち、イベント別に次が入る。

| イベント | 追加のキー |
| --- | --- |
| SessionStart | `source`（`startup` など） |
| PostToolUse | `tool_name`, `tool_input`, `tool_response`, `tool_use_id`, `duration_ms`, `permission_mode` |
| Stop | `last_assistant_message`, `stop_hook_active`, `background_tasks` |
| SessionEnd | `reason` |

フックコマンドの中で `$CLAUDE_PROJECT_DIR` が展開される。

**費用（`total_cost_usd`）はどのフックにも来ない。** ヘッドレス実行の `result` イベント固有の
情報だった。

## 5. アーキテクチャ

依存方向は変わらない。App → Data → Core、App → Core。Core は他に依存しない。

```
MoTask ──(1) ジョブフォルダを作る
       ──(2) instruction.md を書く（人が起動前に編集できる）
       ──(3) hooks.json を書く
       ──(4) wt.exe で claude を起動して手放す
                                  |
                        人と Claude が対話する（承認は利用者の設定どおり）
                                  |
                        hooks が events.jsonl に追記する
       ──(5) FileSystemWatcher で events.jsonl を追従し、盤面と詳細パネルに反映
       ──(6) SessionEnd を見たら Succeeded にして確認待ち列へ
```

| 層 | 追加するもの |
| --- | --- |
| MoTask.Core | `ISessionLauncher`、`IJobFolder`、`HookEventParser` |
| MoTask.App | `JobFolder`、`TerminalLauncher`、`JobEventWatcher` |
| MoTask.Hooks | 新規プロジェクト。stdin を 1 行追記するだけの exe |

`HookEventParser` は「フックの 1 行 → `AiJobEvent`」の純粋関数として Core に置く。ここが
CLI との唯一の形の依存点なので、テストで固定して黙って壊れないようにする。

## 6. ジョブフォルダ

場所は既定ワークフォルダ配下。`<既定ワークフォルダ>\jobs\<JobId>-<タスク名のスラグ>\`。

```
jobs/0042-請求書の突合/
  job.json          MoTask が書く: JobId, SessionId, Kind, cwd, 起動コマンド, 開始時刻
  instruction.md    指示文。起動前に人が編集できる
  hooks.json        --settings に渡すフック定義
  events.jsonl      hooks が追記する。MoTask は読むだけ
  artifacts/        「調査メモと成果物はここへ」と指示文で指定する出力先
```

cwd をここにしないのは、既存リポジトリを直す仕事ができなくなるため。cwd はタスクの
プロジェクトの `WorkingDirectory`（無ければ既定ワークフォルダ）のままにし、ジョブフォルダは
`--add-dir` で読み書きを許す。

成果物一覧は `artifacts/` の実ファイルを列挙する。現行の「ToolUse から拾うので
`... > report.md` が見えない」という既知の限界はこれで消える。

`job.json` を置くのは、MoTask の DB が壊れてもフォルダだけで何のジョブか分かるようにするため。

## 7. フック配線と起動

### hooks.json

4 イベントすべてで同じ exe を呼ぶ。引数は追記先のパス 1 つ。

```json
{"hooks":{
  "SessionStart":[{"hooks":[{"type":"command","command":"\"<MoTask.Hooks.exe>\" \"<events.jsonl>\""}]}],
  "PostToolUse": [{"hooks":[{"type":"command","command":"\"<MoTask.Hooks.exe>\" \"<events.jsonl>\""}]}],
  "Stop":        [{"hooks":[{"type":"command","command":"\"<MoTask.Hooks.exe>\" \"<events.jsonl>\""}]}],
  "SessionEnd":  [{"hooks":[{"type":"command","command":"\"<MoTask.Hooks.exe>\" \"<events.jsonl>\""}]}]
}}
```

`MoTask.Hooks.exe` は stdin を読み切り、末尾に改行を足して追記モードで 1 行書く。それだけ。
MoTask.App.exe に兼務させないのは、フックのたびに WPF ランタイムが立ち上がるのを避けるため。

### 起動コマンド

```
wt.exe -d <cwd> cmd /k <claude> --settings <hooks.json> --session-id <SessionId>
       --permission-mode <設定値> --add-dir <ジョブフォルダ>
       "@instruction.md を読んで作業を始めてください。調査に使ったファイルと成果物は
        <ジョブフォルダ>/artifacts/ に出してください。"
```

- `wt.exe` が見つからなければ `cmd.exe /k` にフォールバックする
- 起動コマンドのテンプレートは AI 設定で上書きできる（PowerShell 派・WSL 派の逃げ道）
- 指示文をコマンドラインに丸ごと渡さないのは、長文の引用符・改行・文字数制限を
  コマンドラインに持ち込まないため。起動プロンプトは `instruction.md` を指す短文にする
- 再開は同じ形で `--session-id` の代わりに `--resume <SessionId>` を渡す
- `--setting-sources`、`--permission-prompt-tool`、`--tools`、`--max-turns`、`--strict-mcp-config`
  はいずれも渡さない

`--model` は設定に値があるときだけ渡す。

## 8. ジョブのライフサイクル

```
Pending ──SessionStart──> Running <=> WaitingForInput   Stop で入力待ち、次の発話で Running
                             └─SessionEnd──> Succeeded  確認待ち列へ移動、履歴に AiJobFinished
Pending ──起動失敗──────> Failed
（人が「追跡をやめる」）─> Cancelled
```

- `AwaitingApproval` と `Suspended` は廃止する。承認は MoTask を通らず、プロセスを所有しないので
  中断という状態が無い
- 同時実行の上限は廃止する。端末を開くのは人であり、アプリが数を絞る意味が無い
- **「追跡をやめる」はプロセスを殺さない。** `wt.exe` 越しに孫プロセスを追えないため、MoTask 側で
  `Cancelled` にして追従をやめるだけにする。端末は人が閉じる。ボタンの文言もそのように書く
- **端末を × で強制終了すると `SessionEnd` は来ない。** ジョブは `Running` のまま残るので、
  詳細パネルに「完了にする」を常設する。これは `SessionEnd` を受け取ったのと同じ扱いにし、
  `Succeeded` にして確認待ち列へ移す。無音タイムアウトによる自動判定はしない
  （夜間の長考と区別がつかないため）
- MoTask 起動時は、未完了ジョブの `events.jsonl` を記録済みオフセットから読み直して追いつく。
  その過程で `SessionEnd` を見つければそこで完了させる

## 9. データモデルの変更

| 対象 | 変更 |
| --- | --- |
| `AiJob` | `JobFolder` (string) を追加。`TotalCostUsd` を削除。`NumTurns` は Stop フックの回数 |
| `AiJobStatus` | `AwaitingApproval` / `Suspended` を廃止し `WaitingForInput = 7` を追加 |
| `AiJobEventKind` | `PermissionAsked` / `PermissionDecided` を廃止し `SessionStarted` / `SessionEnded` / `TurnEnded` を追加 |
| `AiPermissionRule` | テーブルごと削除 |
| `AiSettings` | `MaxConcurrentJobs` / `MaxTurns` を削除、`PermissionMode` / `TerminalCommandTemplate` を追加 |

列挙の既存番号は動かさない。廃止する値の番号を空けたまま新しい値を後ろに足し、マイグレーションで
`AwaitingApproval` / `Suspended` のまま残っている行を `Cancelled` に寄せる。

`AiJobEvent` は現行のまま（`Payload` に元の 1 行をそのまま残す）。フックのペイロードは今後
増えるので、表示に要る最小限だけ列に抜き出して原文を残す方針は変えない。

`TotalCostUsd` を落とすのは §4.4 のとおりフックに費用が来ないため。人は端末で `/cost` を見られる。
盤面に出すなら `transcript_path` の JSONL を読む必要があり、得るものに対して重い。

## 10. 削除するもの

コードとテストの両方を消す。

`ApprovalMcpServer` / `McpProtocol` / `McpConfigFile` / `PermissionGate` / `WpfPermissionPrompt` /
`PermissionDialog`（+ ViewModel）/ `ClaudeCodeParser` / `ClaudeCodeRunner` / `ClaudeCodeArguments` /
`PermissionPolicy` / `PermissionPattern` / `PermissionRequest` / `PermissionDecision` /
`IPermissionPolicy` / `IPermissionPrompt` / `IAgentRunner` / `AgentRunRequest` / `AgentRunOutcome` /
`AiPermissionRule` / `IPermissionRuleRepository` / `PermissionRuleRepository`

対応するテスト（`PermissionPolicyTests`、`PermissionGateTests`、`PermissionDialogViewModelTests`、
`McpConfigFileTests`、`AiJobServicePermissionTests`、`FakePermissionPrompt`、`FakeAgentRunner`）も
併せて消す。

`ClaudeLocator` はそのまま流用する。

## 11. UI

盤面は 4 列のまま。

### タスクカード

- 「AI 調査中」「AI 遂行中」に加えて「入力待ち」バッジ。承認待ちバッジは消える

### 詳細パネル

- **AI に任せる** — 「調査させる」「遂行させる」。押すと指示文の確認・編集、そこから端末が開く
- **進行状況** — `events.jsonl` 由来の時系列
- **成果物** — `artifacts/` の実ファイル一覧。クリックで既定のアプリで開く
- **操作** — 「ジョブフォルダを開く」「端末を開き直す（`--resume`）」「完了にする」「追跡をやめる」

`TaskAiPanelViewModel` は残すが、承認まわりが消えて小さくなる。

### 設定

- 既定ワークフォルダ
- `claude` 実行ファイルのパス（未設定なら PATH から探す）
- モデル
- `--permission-mode`（既定 `auto`）
- 端末の起動コマンドテンプレート

許可ルールの一覧、同時実行の上限、`--max-turns` は設定から消える。

## 12. エラー処理

| 事象 | 扱い |
| --- | --- |
| `claude` が見つからない | ジョブ開始時に検出し Fail。設定でパスを指定できる |
| cwd が未設定・存在しない | ジョブを開始せず Fail。既定へフォールバックしない |
| 端末の起動に失敗 | `Failed` にし、組み立てたコマンドを `ErrorMessage` に入れる |
| `events.jsonl` の行がパースできない | 生のまま `Payload` に残して `System` として続行。1 行壊れても追従を止めない |
| `events.jsonl` が消された | 追従を止め、詳細パネルにその旨を出す。ジョブの状態は変えない |
| `SessionEnd` が来ないまま端末が消えた | `Running` のまま。「完了にする」で人が閉じる |
| `--resume` 先のセッションが CLI 側に無い | 端末側にエラーが出る。MoTask は追従を続けるだけで状態を変えない |

## 13. テスト方針

| 対象 | やり方 |
| --- | --- |
| `HookEventParser` | 実機で採取した 4 種のペイロードを fixture に固定。CLI が形を変えたら赤くなる |
| `JobFolder` | 一時ディレクトリでレイアウト生成、`artifacts/` の列挙、スラグ生成を検証 |
| hooks.json の生成 | 生成した JSON の形を固定する回帰テスト |
| `JobEventWatcher` | 一時ファイルに追記して追従と、オフセットからの再開を検証 |
| `AiJobService` | `ISessionLauncher` を差し替え、SessionStart→Running、Stop→WaitingForInput、SessionEnd→Succeeded と確認待ち列への移動を検証 |
| `TerminalLauncher` | コマンド組み立て（`wt` あり／なし、テンプレート上書き）を文字列として検証。実起動はしない |

プロセスの実起動と端末の描画はテストしない。

## 14. 手動確認が要る項目

実装後に README のチェックリストへ追加する。

- 「遂行させる」で端末が開き、指示文どおりに走り出すこと
- 走っている途中で人が割り込み、方向転換できること
- Claude が人に質問し、端末でそれに答えられること
- ツール承認が利用者の `settings.json` の方針どおりに出る（MoTask のダイアログが出ない）こと
- Stop のたびに「入力待ち」バッジが付き、次の発話で消えること
- `/exit` でタスクが確認待ち列へ移ること
- 端末を × で閉じたとき `Running` のまま残り、「完了にする」で閉じられること
- MoTask を閉じてから端末で作業を続け、MoTask を開き直すと追いついていること
- 「端末を開き直す」で `--resume` が効き、会話の続きから始まること
- `wt.exe` が無い環境で `cmd.exe` にフォールバックすること
