# MoTask AI 遂行 — 設計仕様

日付: 2026-09-05
状態: 設計承認済み（実装計画は未作成）
前提: `2026-09-04-motask-kanban-v1-design.md`（v1 カンバン。実装済み・master にマージ済み）

## 1. 目的と位置づけ

v1 カンバンの上に、**タスクを AI に遂行させる仕組み**を載せる。ロードマップ上は
Morning Plan → Inbox triage の後だったが、今回はそこを飛ばして AI 遂行を先に作る。

ワイヤーフレーム `Incomplete design request/Morning Plan Wireframes.dc.html` の
turn 3 案 **3c（ボード＋詳細パネル）** が UI の参照元。v1 spec が「4c のボード部分だけを作り、
AI 詳細パネルは後続」と決めていたので、その後続がこれにあたる。

盤面は 4 列のまま増やさない。選択したタスクの右パネルで AI に調査／遂行を指示し、
実行ログ・成果物・権限までそこで面倒を見る。

## 2. スコープ

### 含む

- タスクを AI に渡して調査（Research）／遂行（Execute）させる
- 実行中の進捗・ツール実行ログ・成果物の表示
- ツール呼び出し1回ごとの承認ダイアログと、「今後も許可」の記憶
- ジョブの停止、アプリ終了による中断、次回起動時の再開
- 完了したジョブのタスクを「確認待ち」（Review ロールの列）へ自動で送る
- 同時実行数の上限
- ジョブごとの所要ターン数と費用の表示

### 含まない（後続で検討）

- Morning Plan、Inbox triage（ロードマップ上は先だが今回は作らない）
- 複数タスクの一括依頼、AI 同士の連携
- スケジュール実行、アプリを閉じた状態での実行
- 成果物の外部共有・送信（メール、Teams）
- ジョブ結果の差分表示（ワイヤーフレームの「差分を見る」）

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| AI がどこまで手を出せるか | 任意のコマンド実行まで | 知識労働の代行には調査・ファイル操作・コマンド実行が要る |
| エージェントループを誰が持つか | Claude Code CLI を子プロセス起動 | Claude Agent SDK は Python / TypeScript のみで C# には無い。自前実装ならサンドボックス・コンテキスト管理・中断再開を全部作ることになる |
| 人の割り込み粒度 | ツール1回ごとに承認ダイアログ | 任意コマンド実行を許す以上、ここが唯一の安全弁 |
| 承認の配線 | `--permission-prompt-tool` + MoTask が立てる HTTP MCP サーバ | MCP は公開プロトコルなので CLI 更新で壊れにくい。stream-json の制御チャネルは Agent SDK ホストの内部ワイヤ形式で、ドキュメント化されていない |
| 作業ディレクトリ | Project に作業フォルダを紐づける | 継続的な仕事に成果物が蓄積し、CLAUDE.md も置ける |
| アプリ終了時 | 中断して次回起動時に再開できる | ここまでのログと成果物を捨てない |

## 4. 実機で確認した CLI の挙動

Claude Code 2.1.260 で検証済み。設計はこれらの事実に依存する。

### 4.1 承認ツールの入出力

`--permission-prompt-tool mcp__<server>__<tool>` を指定すると、承認が要るツール呼び出しの前に
その MCP ツールが呼ばれる。受け取る引数:

```json
{
  "name": "approve",
  "arguments": {
    "tool_name": "Write",
    "input": { "file_path": "...", "content": "..." },
    "tool_use_id": "toolu_01PpinL6oZi2oiXoKdEHgJrN"
  },
  "_meta": { "claudecode/toolUseId": "toolu_...", "progressToken": 2 }
}
```

返す値は、テキストコンテンツブロックに JSON 文字列を入れる形:

```json
{ "content": [ { "type": "text", "text": "{\"behavior\":\"allow\",\"updatedInput\":{...}}" } ] }
```

- `{"behavior":"allow","updatedInput":{...}}` — 許可。`updatedInput` で引数を差し替えられる
- `{"behavior":"deny","message":"..."}` — 拒否

拒否した場合、ツールは実行されず、結果 JSON の `permission_denials[]` に記録され、
モデルは拒否を理解して停止する（勝手に別経路を試みたりはしない）。

### 4.2 HTTP トランスポートの MCP サーバが使える

`--mcp-config` に以下を渡すと `status:"connected"` になり、`Authorization` ヘッダーも届く。

```json
{"mcpServers":{"motask":{"type":"http","url":"http://127.0.0.1:<port>/mcp",
  "headers":{"Authorization":"Bearer <token>"}}}}
```

**MoTask.App がプロセス内で承認サーバをホストでき、承認ハンドラから直接 WPF ダイアログを
出せる。** 別 exe や named pipe による橋渡しは不要。

### 4.3 読み取り専用の Bash は承認ツールに来ない

`echo` のような副作用のないコマンドは CLI 側の判定で自動許可され、承認ツールを経由しない。

**承認ツールは「危険な呼び出しのゲート」であって「全ツール呼び出しの記録係」ではない。**
完全な実行ログは stream-json のイベントから別途取る。

### 4.4 `--setting-sources ""` が必須

指定しないと、実行ユーザーの `settings.json` の allowlist が MoTask のジョブにも効いてしまい、
承認ダイアログを黙って素通りする（検証中に実際に発生した）。MoTask のジョブは MoTask 自身の
許可ルールだけで動かす。

### 4.5 その他

- `--session-id <uuid>` でセッション ID を MoTask 側から採番できる。再開のために ID を
  出力からパースして拾う必要がない
- `--resume <session-id>` で中断からの再開
- `--output-format json` / stream-json の `result` イベントに `session_id`, `total_cost_usd`,
  `usage`, `permission_denials`, `num_turns`, `is_error`, `terminal_reason` が入る
- `rate_limit_event` 型のイベントが流れてくることがある

## 5. アーキテクチャ

依存方向は v1 のまま。App → Data → Core、App → Core。Core は他に依存しない。

| 層 | 追加するもの |
| --- | --- |
| MoTask.Core | `AiJob` 系エンティティ、`AiJobService`、`IPermissionPolicy`、`IAgentRunner`（インターフェースのみ） |
| MoTask.Data | リポジトリ実装、マイグレーション |
| MoTask.App | `ClaudeCodeRunner : IAgentRunner`、`ApprovalMcpServer`、ViewModel / View |

承認の判断は Core と App で割る。`IPermissionPolicy.Evaluate(job, request)` が
`Allow` / `Deny` / `AskHuman` を返す。ルール照合というテストできるロジックは Core に置き、
`AskHuman` のときだけ App がダイアログを出す。「今後も許可」を選んだら Core に
`AiPermissionRule` を作らせる。

## 6. データモデル

### AiJob

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| TaskId | int | FK → TaskItem |
| Kind | AiJobKind | Research / Execute |
| Status | AiJobStatus | Pending / Running / AwaitingApproval / Suspended / Succeeded / Failed / Cancelled |
| SessionId | Guid | MoTask が採番し `--session-id` に渡す。再開は `--resume` |
| Instruction | string | 人が確認・編集した指示文 |
| WorkingDirectory | string | 実行時に解決した cwd のスナップショット |
| StartedAt | DateTime? | |
| EndedAt | DateTime? | |
| NumTurns | int? | result イベントから |
| TotalCostUsd | decimal? | result イベントから |
| ErrorMessage | string? | |

`WorkingDirectory` を実行時のスナップショットとして持つのは、後から Project の作業フォルダを
変えても、過去のジョブがどこで走ったかが分かるようにするため。

### AiJobEvent（追記のみ）

既存の `HistoryEntry` と同じ思想で、更新も削除もしない。

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| JobId | int | FK |
| Seq | int | ジョブ内の順序 |
| At | DateTime | |
| Kind | AiJobEventKind | AssistantText / ToolUse / ToolResult / PermissionAsked / PermissionDecided / Error / Result |
| ToolName | string? | ToolUse / PermissionAsked のとき |
| Payload | string | stream-json の1行を生のまま |

`Payload` に生 JSON を残す理由: CLI のイベント形式は今後増える。パースして正規化した列だけ
持つと、新しいイベント型が来たときに情報が黙って消える。表示に要る最小限（`Kind`、`ToolName`）
だけを列に抜き出し、原文は必ず残す。

### AiPermissionRule

「今後も許可」の記憶。

| 列 | 型 | 備考 |
| --- | --- | --- |
| Id | int | PK |
| Scope | RuleScope | Global / Project |
| ProjectId | int? | Scope が Project のとき |
| ToolName | string | Bash, Write, Edit, … |
| Pattern | string? | 下記の規則で作る文字列。null は「そのツール全部」 |
| Decision | RuleDecision | Allow / Deny |
| CreatedAt | DateTime | |

`Pattern` の作り方:

| ツール | Pattern | 一致条件 |
| --- | --- | --- |
| Bash | コマンド文字列の先頭2トークンまで（`git push`、`dotnet`） | 実行しようとするコマンドが Pattern で始まる |
| Write / Edit | ディレクトリの絶対パス | 対象ファイルがそのディレクトリ配下にある |
| その他 | null のみ | ツール名だけで一致 |

Bash を先頭2トークンまでにするのは、`git` 全体を一度に許すのは広すぎ、コマンド全文の完全一致は
引数が変わるたびに聞かれて役に立たないため。実際に記憶する Pattern はダイアログに明示する。

照合順序は、Deny が Allow に優先し、Project スコープが Global に優先する。どのルールにも
当たらなければ `AskHuman`。

### 既存エンティティの変更

- `Project` に `WorkingDirectory` (string?) を追加
- `HistoryKind` に `AiJobStarted` / `AiJobFinished` を追加
- `TaskItem` は変更しない。「AI 稼働中」バッジはジョブ側から引く

### 成果物

専用テーブルは作らない。`AiJobEvent` の ToolUse（Write / Edit）から触れたファイルパスを
拾って詳細パネルに出す。ファイルの実体は cwd にあり、DB に二重管理を持ち込まない。

**既知の限界**: Bash のリダイレクト（`... > report.md`）で作られたファイルは、Write / Edit の
ToolUse を伴わないので一覧に出ない。cwd をスキャンして差分を取る手もあるが、無関係なファイルを
拾う割に得るものが少ないので今回はやらない。詳細パネルには成果物一覧と併せて
「作業フォルダを開く」を置き、そこから辿れるようにする。

## 7. ジョブのライフサイクル

```
Pending → Running ⇄ AwaitingApproval
                 ├→ Succeeded   … タスクを Review ロールの列へ移動、履歴に AiJobFinished
                 ├→ Failed
                 ├→ Cancelled   … 人が「停止」を押した
                 └→ Suspended   … アプリ終了。--resume で Running へ戻せる
```

- **完了時**: `BoardService.MoveTask` を再利用して Review ロールの列の末尾へ送る。
  Review ロールの列が盤面に無ければタスクは動かさず、`Result.Warnings` に載せる
- **同時実行**: 既定 3。`AiJobService` が上限を持ち、超過したら Result の失敗を返す
- **再開**: 同じ引数に `--resume <SessionId>` を足し、「中断したところから続けてください」を
  プロンプトとして渡す

## 8. 起動引数

```
claude -p "<Instruction>"
  --output-format stream-json --verbose
  --session-id <AiJob.SessionId>
  --setting-sources ""
  --strict-mcp-config
  --mcp-config <ジョブごとの一時ファイル>
  --permission-prompt-tool mcp__motask__approve
  --permission-mode default
  --tools <Kind 別>
  --max-turns <上限>
```

Kind 別のツール面:

| Kind | `--tools` | 意図 |
| --- | --- | --- |
| Research | `Read,Glob,Grep,WebSearch,WebFetch` | 書き込みができないので承認ダイアログはほぼ出ない |
| Execute | `default` | 任意のコマンド実行を含む |

cwd は、タスクにプロジェクトがあればその `Project.WorkingDirectory`、無ければ設定の
既定ワークフォルダ（初期値 `%USERPROFILE%\MoTask`、初回のジョブ開始時に作る）。
プロジェクトに作業フォルダが設定されていてもそのフォルダが存在しなければ、既定へは
フォールバックせずジョブを開始しない — 意図した場所と違うところで任意コマンドを走らせないため。

`--model` と `--max-turns` は設定で変更できるようにする。

## 9. 承認サーバ

MoTask.App の起動時に 127.0.0.1 へ HTTP サーバを1つだけ立てる（ポートは OS 任せ）。

- ジョブごとにランダムな Bearer トークンを発行し、`--mcp-config` の一時ファイルに書く
- トークンでジョブを特定し、そのジョブの承認ダイアログを出す
- 認証は 127.0.0.1 バインドと Bearer トークンの二重
- 一時ファイルはジョブ終了時に削除する

**承認要求はタイムアウトさせない。** 夜に放置して翌朝答えられるべきだから。
ただしアプリ終了時だけは、保留中の要求に `deny` を返して子プロセスを畳み、`Suspended` にする。
このときの `message` は「MoTask が終了したため中断しました」とする — 再開したセッションの
会話履歴には拒否として残るので、人が判断して拒否したのだと誤解させないため。再開時のプロンプトも
「中断されたところから続けてください。直前の拒否はアプリの終了によるものです」とする。

ダイアログには、ツール名・引数（Bash ならコマンド全文、Write ならパスと内容）・
タスク名・作業ディレクトリを出す。選択肢は「許可」「拒否」「今後も許可」「今後も拒否」。
「今後も」を選んだときのスコープ（このプロジェクトだけ／全体）もそこで選ばせる。

## 10. UI

盤面は 4 列のまま。追加するのは詳細パネルの中身とカードのバッジだけ。

### タスクカード

- AI 稼働中のタスクに「AI 調査中」「AI 遂行中」バッジと進捗（`num_turns` ベース）
- 承認待ちのタスクにはそれと分かるバッジ。クリックで詳細パネルの承認へ飛ぶ

### 詳細パネル（既存の `TaskDetailPanel` を拡張）

ワイヤーフレーム 3c の右パネルに対応する。

- **AI に任せる** — 「調査させる」「遂行させる」の2ボタン。押すと指示文の確認・編集ができる
- **進行状況** — ツール実行ログを時系列で。`AiJobEvent` から生成
- **成果物** — 触れたファイルの一覧。クリックで既定のアプリで開く
- **停止 / 再開** — 状態に応じて出し分け
- **費用** — `total_cost_usd` と `num_turns`

`TaskDetailViewModel` は既に 200 行あり、ここに AI の面倒まで足すと肥大する。
AI 関連は `TaskAiPanelViewModel` として分け、詳細パネルが子として持つ。

### 設定

- 既定ワークフォルダ
- 同時実行の上限（既定 3）
- `claude` 実行ファイルのパス（未設定なら PATH から探す）
- モデルと `--max-turns`
- 記憶した許可ルールの一覧と削除

## 11. エラー処理

| 事象 | 扱い |
| --- | --- |
| `claude` が見つからない | ジョブ開始時に検出し Result の失敗。設定でパスを指定できる |
| cwd が未設定・存在しない | ジョブを開始せず Result の失敗 |
| 非 0 終了、または `is_error:true` | `Failed`。result の内容を `ErrorMessage` へ |
| `rate_limit_event` | イベントとして記録し UI に表示。ジョブは止めない |
| stream-json の行がパースできない | その行を生のまま `Payload` に残して続行。1行壊れてもジョブは殺さない |
| 同時実行が上限 | Result の失敗。何件動いているかを文言に含める |
| 再開しようとしたセッションが CLI 側に無い | `Failed` にし、新しいジョブとしてやり直せる旨を出す |

`total_cost_usd` は詳細パネルに出す。任意コマンド実行を許す以上、いくら使ったかが
見えないのは筋が悪い。

## 12. テスト方針

| 対象 | やり方 |
| --- | --- |
| `AiJobService` | `IAgentRunner` を NSubstitute で差し替え、状態遷移・同時実行上限・完了時の列移動を検証。既存 Core テストと同じ形 |
| `IPermissionPolicy` | ルール照合を純粋な単体テストで。Deny 優先、Project スコープ優先、パターン一致 |
| Data | マイグレーションとリポジトリの往復 |
| `ClaudeCodeParser` | 検証時に採取した stream-json を固定サンプルにして単体テスト。プロセス起動そのものはテストしない |
| `ApprovalMcpServer` | HTTP で叩いて allow / deny の JSON 形を検証 |

承認ツールの入出力形（4.1）をテストに固定しておけば、CLI 側が形を変えたときに赤で気づける。
ここは黙って壊れると承認ダイアログが素通りしかねない場所なので、テストで抑える価値が高い。

## 13. 手動確認が要る項目

自動テストで届かない範囲。実装後に README のチェックリストへ追加する。

- 承認ダイアログが実際に出て、許可・拒否がジョブに反映されるか
- ジョブ実行中にアプリを閉じ、再起動して `--resume` で続きから走るか
- 同時に 3 件走らせ、4 件目が上限で弾かれるか
- Research ジョブで書き込み系ツールが本当に使えないか
- `--setting-sources ""` により、個人の settings.json の allowlist が効いていないこと
  （承認ダイアログが出るべき操作で実際に出ること）
