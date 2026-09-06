# MoTask TODO 操作 I/F（MCP） — 設計仕様

日付: 2026-09-05
状態: 実装済み（本書は実装に合わせた改訂版。節番号はコード中の `仕様 §…` 参照が指す先なので動かさない）
前提: `2026-09-04-motask-kanban-v1-design.md`（v1 カンバン。実装済み）、
`2026-09-05-motask-terminal-ai-design.md`（AI 遂行のターミナル実行。実装済み・master にマージ済み）

## 1. 目的と位置づけ

Claude Code から MoTask の TODO を読み書きできるようにする。手元のターミナルや VSCode で
動かしている普段の Claude Code に「今の TODO を見せて」「これタスクにしといて」「終わったから
Done にして」と頼めるようにするのが狙い。

初版の設計では、AI 遂行が持っていたアプリ内の承認 MCP サーバ（`ApprovalMcpServer`）に
ボード操作ツールを相乗りさせる想定だった。その後 AI 遂行はターミナル実行へ作り替えられ
（前提仕様）、承認サーバ・承認ツール・ジョブの子プロセスへ渡すトークンは一式削除された。
そのため本書の対象は **外部セッション向けの MCP I/F だけ** になる。MoTask は
この I/F のためだけに MCP サーバを 1 つ立て、認証は起動ごとの board トークン 1 本にする。

## 2. スコープ

### 含む

- ボード構成（列・プロジェクト・ラベル）の取得
- タスクの一覧取得（列・プロジェクト・ラベル・期日・文字列で絞り込み）
- タスク1件の詳細取得
- タスクの追加
- タスクの更新（タイトル・本文・プロジェクト・期日・ラベル）
- タスクの移動（Done ロールの列へ動かすことが「完了」にあたる）
- MoTask 未起動時のアプリ自動起動
- アプリの多重起動防止（自動起動を安全にするための前提）

### 含まない（後続で検討）

- タスクの削除・復元
- 列・プロジェクト・ラベルそのものの作成／改名／並び替え
- AI ジョブの起動・停止（`IAiJobService` の MCP 公開）
- 履歴（`HistoryEntry`）の取得
- 書き込みに対する人の承認（決定の理由は §3）
- リモート／他端末からの接続、認証基盤

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| 誰が呼ぶか | 手元の Claude Code / AI エージェント（外部セッション） | 普段使っている Claude に TODO を触らせたい。AI 遂行は端末で走るので、そちらから使いたければ同じ登録を使えばよい |
| 公開形態 | stdio ブリッジ exe ＋ アプリ内 HTTP MCP サーバ | Claude Code に stdio で登録できて、未起動時にアプリを起動できる。ボード操作の実体はアプリ内に置けるので、履歴・WIP・排他・画面更新が既存のまま効く |
| アプリ未起動時 | ブリッジがアプリを自動起動して待つ | 「アプリを開いていないと TODO が見えない」を避ける |
| SQLite を直接開くか | 開かない。書き手はアプリ1プロセスだけ | 複数プロセス書き込み（WAL・busy_timeout）と、起動中アプリの画面が更新されない問題を丸ごと回避できる |
| 書き込みの承認 | 承認ツールは持たず即反映 | 自分1人のボードで、操作は `HistoryEntry` に残り後から戻せる。承認ダイアログは外部セッション中にデスクトップへ注意を奪う。MoTask 側で許可を聞き直さない方針は AI 遂行の作り替えとも揃う |
| 認証 | アプリの起動ごとに board トークンを 1 本だけ発行する | 相手は同じ端末の自分の Claude Code だけ。トークンの種別を分ける相手が居ない |
| 操作の範囲 | 参照・追加・更新・移動まで。削除と構造変更は出さない | 取り返しの付きにくい操作を最初から渡さない |
| 列やラベルの指定 | id と名前の両方を受け付ける | Claude は人の言葉（「進行中に入れて」）のまま呼べる。id は曖昧さが無い |
| ツールの説明文 | resx に置かず、コードに直書きする | 人に見える文言ではなくモデルが読む文字列。翻訳の対象ではない |

## 4. アーキテクチャ

```
Claude Code ──stdio──> MoTask.Mcp.exe ──HTTP(JSON-RPC)──> MoTask.exe（MoTask.App）
                            │                                  │
                    endpoint.json を読む                MoTaskMcpServer
                    無ければ App を起動して待つ         └ McpProtocol（JSON-RPC ＋ ツール登録）
                                                               │
                                                        BoardToolHost（board ツール 6 本）
                                                               │
                                                        IBoardService
```

### 新規・変更するプロジェクト

| プロジェクト | 変更 |
| --- | --- |
| `src/MoTask.Mcp`（新規） | .NET 10 コンソール exe。stdio ⇄ HTTP の中継とアプリ自動起動だけを持つ。**参照は `MoTask.Core` のみ** |
| `src/MoTask.Core` | `AppPaths`（`%LOCALAPPDATA%\MoTask` のパス）と `Ai/McpEndpointFile`（endpoint.json の読み書き）を追加 |
| `src/MoTask.Data` | `DbPaths.DefaultDirectory` を `AppPaths` 参照へ置き換え（重複を作らない） |
| `src/MoTask.App` | `Ai/BoardTools/` 一式と `Ai/McpProtocol`・`Ai/McpTool`・`Ai/MoTaskMcpServer` を追加。`App.OnStartup` に単一インスタンス保護と endpoint.json の書き出しを、`OnExit` に削除を追加。`BoardViewModel` が外部変更を購読 |
| `tests/MoTask.Mcp.Tests`（新規） | ブリッジの endpoint 解決と stdio 中継 |

依存方向は App → Data → Core、Mcp → Core。`MoTask.Mcp` が `MoTask.Core` だけを参照するのは、
ブリッジに EF Core を持ち込まないため。ブリッジが Core から使うのは `AppPaths`・`McpEndpointFile`・
`Messages`（resx）だけ。新規 NuGet パッケージは足さない。

`MoTask.App` の `AssemblyName` は `MoTask` なので、出来上がる実行ファイルは **`MoTask.exe`**。
ブリッジはこの名前で探す（§5.2）。

## 5. エンドポイントの受け渡しと自動起動

### endpoint.json

場所は `%LOCALAPPDATA%\MoTask\endpoint.json`（DB と同じフォルダ。パスは `AppPaths.EndpointFile`）。
アプリは MCP サーバを起動した直後に書き、終了時に消す。

```json
{
  "url": "http://127.0.0.1:52341/mcp",
  "token": "3F2A...（64桁の16進数）",
  "pid": 12345
}
```

- `token` はアプリ起動ごとの乱数（32 バイトを大文字 16 進で 64 桁）。board ツール専用の 1 本だけ。
- ファイルの保護は `%LOCALAPPDATA%` のユーザー ACL に任せる（DB 本体と同じ扱い）。
- 読めない・壊れている・`url` / `token` が空・`pid` が 0 以下ならアプリ未起動として扱う。
- 異常終了でファイルが残った場合は `pid` の生存確認と疎通確認で検出する（§5.2）。
- Mutex を取れずに終わった 2 つ目のインスタンスは、1 つ目のファイルを消さない（§5.3）。

### 5.1 ブリッジの動作

1. stdin から JSON-RPC のメッセージを1件（1行）読む。空行は読み飛ばす。
2. endpoint を解決する（§5.2）。
3. 解決した URL へ `Authorization: Bearer <token>` を付けて POST し、応答本文をそのまま stdout へ1行で流す。
4. 通知（`id` 無し）は応答本文が無いので、アプリが 202 を返したらブリッジも何も書かない。

**stdout は JSON-RPC 専用。ログ・診断はすべて stderr へ出す**（Claude Code が拾う）。
ブリッジ側で失敗したときも、stdout に出るのは JSON-RPC エラー（`-32603` ＋ resx の日本語）だけで、
例外の中身は stderr にしか出さない。`id` の無い要求（通知）が失敗したときは何も書かない。
`id` は生の JSON のまま echo するので、数値でも文字列でも壊さない。

### 5.2 endpoint の解決と自動起動

```
endpoint.json を読む
  ├ ファイルが無い・壊れている ───┐
  ├ pid のプロセスが存在しない ───┤
  ├ ping が 200 で返らない ───────┤
  │                               └→ MoTask.exe を起動し、
  │                                  endpoint.json が読めて ping が通るまで
  │                                  最大 30 秒ポーリング（250ms 間隔）
  └ ping が通った ──────────────────→ そのまま使う
```

- 疎通確認は `{"jsonrpc":"2.0","id":0,"method":"ping"}` の POST。トークンが合っていれば 200 が返る。
  トークンが古ければ 401 になるので、この確認は認証の確認も兼ねる。
- `MoTask.exe` の場所は、ブリッジ自身の実行ファイルと同じフォルダ（`AppContext.BaseDirectory`）を
  既定にする（発行時に並べて置く）。環境変数 **`MOTASK_APP_EXE`** に絶対パスを入れると
  そちらを使う（開発中に `bin` の exe を指すための逃げ道）。見つからなければ、探したパスを
  含めたエラーメッセージを返す。
- 30 秒待っても駄目なら、そのツール呼び出しを JSON-RPC エラーで返す。次の呼び出しでは
  また最初から解決を試みる（プロセスは常駐しないので状態を引きずらない）。
- 起動待ちの間にアプリが DB 修復ダイアログを出して止まることがある。その場合はタイムアウトとして
  扱い、「MoTask の起動を確認してください」と返す。

### 5.3 多重起動の防止

自動起動を入れると同じ SQLite に2つの書き手ができるため、`App.OnStartup` の先頭で
名前付き Mutex（`Local\MoTask.SingleInstance`）を取る。取れなかった 2 つ目のインスタンスは
何も表示せずに終了する。`Local\` 接頭辞でログオンセッション内に閉じ、他ユーザーの起動は邪魔しない。
既存ウィンドウを前面に出す処理は入れない（別途の小さな改善として切り出せる）。

## 6. 公開するツール

サーバ名は `motask`。ツール名は Claude 側で `mcp__motask__list_tasks` のようになる。

### 共通の約束

- **タスクの指定**（`task`）は id のみ。タイトルは重複しうるので名前引きは受け付けない。
  キーが無ければ「必須です」、あるのに整数でなければ「整数で指定してください」と言い分ける
  （区別しないとモデルが自力で直せない）。
- **列・プロジェクト・ラベルの指定**は id（整数）でも名前（文字列）でもよい。名前は前後の空白を
  無視し、大文字小文字を区別しない完全一致。一致が無い／複数ある場合はエラーを返し、
  メッセージに既存の候補を並べる。id でも名前でもない値（真偽値など）は「id か名前で指定してください」。
- **返り値**は JSON をひとつ。`IBoardService` の `Result.Warnings`（WIP 超過など）は
  同じ JSON に `"warnings": ["..."]` として同梱する（警告が無ければキー自体を出さない）。
  日本語は `\uXXXX` に潰さずそのまま出す。
- **失敗**は MCP のツールエラー（`isError: true`）とし、本文に `Result.Error` などの日本語メッセージを入れる。
- **論理削除済みのタスクは、どのツールからも見えないし触れない。** 一覧に出ず、id を指定しても
  「タスクが見つかりません」になる。
- 日付は `YYYY-MM-DD`、日時は UTC の ISO 8601（`o` 書式）。DB から戻る `DateTime` は Kind が
  Unspecified のことがあるので UTC と決め打って表記を揃える。

### 6.1 `get_board`

引数なし。Claude が語彙を知るための最初の呼び出し。

```json
{
  "columns": [
    { "id": 1, "name": "バックログ", "role": "Backlog", "wipLimit": null, "taskCount": 12, "overWip": false },
    { "id": 3, "name": "進行中", "role": "Active", "wipLimit": 3, "taskCount": 4, "overWip": true }
  ],
  "projects": [{ "id": 2, "name": "MoTask", "archived": false }],
  "labels": [{ "id": 5, "name": "bug", "color": "#D33", "archived": false }]
}
```

列は `Order` 順。`taskCount` は論理削除を除いた件数、`overWip` は WIP 上限超過。
アーカイブ済みのプロジェクト・ラベルも `archived: true` を付けて返す（既存タスクに付いたままの
ことがあるため）。

### 6.2 `list_tasks`

| 引数 | 型 | 既定 |
| --- | --- | --- |
| `column` | id または名前 | 全列 |
| `project` | id または名前 | 全プロジェクト |
| `label` | id または名前。配列で複数指定すると AND（すべて付いているものだけ） | 条件なし |
| `due` | `all` / `today` / `this_week` / `overdue` | `all` |
| `search` | 文字列（タイトルと本文を部分一致） | なし |

`MoTask.Core.Filtering.TaskFilter` をそのまま使う（**`ShowDeleted` は常に false**）。`due` に
上の 4 語以外が来たらツールエラー。結果は列ごとにまとめ、列内は `Position` 順。

```json
{
  "columns": [
    {
      "id": 3, "name": "進行中",
      "tasks": [
        {
          "id": 42, "title": "MCP I/F を作る", "column": "進行中",
          "project": "MoTask", "labels": ["feature"],
          "dueDate": "2026-09-08", "completedAt": null,
          "description": "先頭200字まで…"
        }
      ]
    }
  ]
}
```

`description` は 200 字を超えたら切り詰め、末尾に `…` を付ける（サロゲートペアの途中で切らない）。
全文は `get_task` で取る。

### 6.3 `get_task`

引数 `task`（id、必須）。`list_tasks` の1件と同じ形で、`description` は全文。`createdAt` /
`updatedAt` / `position` も返す。履歴は含めない。

### 6.4 `add_task`

| 引数 | 型 | 既定 |
| --- | --- | --- |
| `title` | 文字列（必須） | — |
| `column` | id または名前 | 先頭の Active ロール列。無ければボードの先頭列 |
| `description` | 文字列 | 空 |
| `project` | id または名前 | なし |
| `labels` | id または名前の配列 | なし |
| `due` | `YYYY-MM-DD` | なし |

`CreateTaskAsync` → 必要なら `UpdateTaskAsync` / `SetTaskLabelsAsync` の順に呼ぶ。返すのは
**作成されたタスクを読み直した姿**（`get_task` と同じ形。書き込み系はすべてこの形で返す）。
WIP 超過の警告は `warnings` に載るが、追加自体は成功する（既存の UI と同じ扱い）。

列の既定を「先頭の Active 列」にするのは、アプリの N キーの規則（選択中の列、なければ先頭）に
合わせたもの。MCP には「選択中」が無いので Active を優先する。ボードに列が1つも無ければエラー。

作成には成功したが続く更新（本文・プロジェクト・期日・ラベル）で失敗した場合は、`isError` を
立てたうえで**本文に作成済みのタスク id を明示する**。これが無いとモデルは作成ごと失敗したと読んで
再試行し、タイトルだけのタスクが重複して増える。

### 6.5 `update_task`

引数 `task`（id、必須）と、変えたい項目だけ（`title` / `description` / `project` / `due` / `labels`）。
省略した項目は現在値を保持する。`project` と `due` は明示的に `null` を渡すと外れる
（**省略と `null` を区別する**）。`labels` は渡された配列で置き換え、空配列なら全部外す。

実装は「現在値を読む → 指定された項目だけ差し替える → `UpdateTaskAsync`」。`labels` が
指定されたときだけ `SetTaskLabelsAsync` も呼ぶ。返すのは更新後のタスク（§6.4 と同じ読み直し）。

### 6.6 `move_task`

| 引数 | 型 | 既定 |
| --- | --- | --- |
| `task` | id（必須） | — |
| `column` | id または名前（必須） | — |
| `position` | 整数（0 始まり） | 末尾 |

`MoveTaskAsync` をそのまま呼ぶ（範囲外の位置はサービス側が端へ丸めるので、既定は十分大きい値を渡す）。
Done ロールの列へ入れば `CompletedAt` が入り、そこから出れば消える（既存の挙動）。
`complete_task` は作らない。ツールの説明文に「完了させるには Done ロールの列へ move する」と書き、
`get_board` の `role` から Claude が特定できるようにする。

## 7. アプリ側の受け口

### 7.1 `BoardToolHost`

ボード操作の知識をこの1ファイルに閉じ込める。責務は4つ。

1. ツール定義（`tools/list` に出す JSON Schema）を提供する
2. 引数の名前↔id 解決と省略値の補完
3. `IBoardService` の呼び出し
4. 結果の JSON 整形（`Result` → ツール応答）

`IBoardService` と `IClock`（`due` の `today` 判定用）だけに依存し、HTTP も JSON-RPC も知らない。
補助として `BoardArgs`（arguments の読み取り。省略と明示 `null` の区別）、`McpRef`（id か名前）、
`BoardLookup`（一意解決）、`BoardJson`（返り値の整形）に分けてある。

### 7.2 `McpProtocol`

MCP（Streamable HTTP）の JSON-RPC 部分。ツールは配列で受け取り、`tools/list` はその定義を並べ、
`tools/call` は名前で引く。HTTP と認証は持たない。

| メソッド | 応答 |
| --- | --- |
| `initialize` | `protocolVersion` は要求をそのまま返す（無ければ `2025-06-18`）。`serverInfo.name` は `motask` |
| `ping` | 空の result |
| `tools/list` | 渡されたツールの `name` / `description` / `inputSchema` |
| `tools/call` | ツールを引いて実行し、`content[0].text` と `isError` を返す |
| `id` 無し（通知） | 202 Accepted・本文なし |
| 不明なメソッド | `-32601` |
| params が不正・未知のツール名 | `-32602` |

ツールが `ArgumentException` を投げたら「引数の形が不正」として `-32602`。それ以外の
想定外の例外は `-32603`（§8）で、例外の内容は呼び出し側へ漏らさない。

### 7.3 `MoTaskMcpServer`

アプリ内に 1 つだけ立てる HTTP MCP サーバ。`127.0.0.1` の空きポートへバインドし、パスは `/mcp`。
`HttpListener` は 127.0.0.1 への非管理者バインドが可能なことを実機で確認済み（urlacl の登録は不要）。

**認証はアプリの起動ごとに 1 つ発行する board トークンだけ**（初版にあったジョブ用トークンとの
2 種類の使い分けは、承認サーバの廃止に伴い無くなった）。トークンは `Start()` 時に採番し、
`endpoint.json` 経由でブリッジへ渡す。提供するツールは `BoardToolHost` の board ツール 6 本のみ。

- `/mcp` 以外は 404、POST 以外は 405。
- **認証はメソッドで分岐する前に効く。** トークンが無い／違えば `tools/list` でも `tools/call` でも 401 で、
  ツールは一切走らない。
- リクエストにサーバ側のタイムアウトは設けない。ツールの実処理（DB 操作）は短く、詰まったときは
  呼び出し側（ブリッジ / Claude Code）が打ち切るのが筋。ここで勝手に切ると
  「書き込みは通ったのに応答だけ落ちた」という一番始末の悪い状態を作りかねない。
- `Dispose` は、進行中のリクエスト（応答を書き込む途中のものを含む）が完了するのを待ってから
  listener を止める。`HttpListener` は `Stop()`/`Close()` を呼ぶと書き込み中の応答まで打ち切るため。

### 7.4 画面への反映

`BoardToolHost` は MCP 経由の書き込みが成功するたびに `IBoardChangeSource.BoardChanged` を上げる。
`BoardViewModel` はそれを購読し、`SynchronizationContext` へ Post して `ReloadAsync()` を呼ぶ。
選択中タスクや詳細パネルの扱いは既存の `ReloadAsync` の挙動に従う。
`BoardToolHost` は DI で singleton にし、`MoTaskMcpServer` と `IBoardChangeSource` に同じ
インスタンスを配る（別々になると通知が届かない）。

### 7.5 並行性

MCP 経由の操作も、UI や AI ジョブと同じ singleton の `IBoardService` → `OperationGate` を
通る。DbContext への同時アクセスは既存の仕組みで直列化されるので、追加の排他は要らない。

## 8. エラー処理

| 起きること | 返し方 |
| --- | --- |
| アプリが起動していない・起動できない | ブリッジが JSON-RPC エラー。メッセージに探した exe のパスを含める |
| 30 秒待っても endpoint が来ない | 同上。「MoTask の起動を確認してください」 |
| 接続が途中で切れた（アプリ終了など） | 次の呼び出しで再解決。切れた1件はエラーを返す |
| トークンが合わない | アプリが 401。ブリッジは endpoint.json を読み直して1度だけ再試行し、なお 401 ならエラー |
| アプリが想定外の HTTP ステータスを返した | ブリッジがステータス番号を含むエラーを返す |
| ブリッジ内の想定外の例外 | その1件だけツールエラーにする（プロセスごと落とさない）。詳細は stderr のみ |
| 名前が一致しない／複数一致 | ツールエラー。候補を並べる |
| 引数の型・書式が違う（`due` の形式、id でも名前でもない値） | ツールエラー。何をどう直せばよいかを日本語で言う |
| `Result.Fail`（WIP 制限以外の業務エラー） | ツールエラー。`Result.Error` の日本語をそのまま |
| 想定外の例外 | アプリ側で捕捉して `-32603`。例外の内容は呼び出し側へ漏らさない |

利用者に見える文言はすべて resx（`MoTask.Core` の `Messages`、`MoTask.App` の `Strings`）。
ツールの `description` はモデルが読む文字列なので resx に置かない。

**ブリッジは stdout に JSON-RPC 以外を絶対に書かない。**

## 9. テスト方針

TDD で進める。既存のテストプロジェクトの流儀に合わせる。

| 対象 | 見るもの |
| --- | --- |
| `BoardToolHost`（参照系・書き込み系） | ツールごとの正常系、名前↔id 解決（一致なし・複数一致）、`add_task` の列既定と部分失敗、`update_task` の省略と明示 `null` の違い、`warnings` の同梱、`Result.Fail` → `isError`、論理削除済みが見えないこと |
| `BoardArgs` / `BoardLookup` / `BoardJson` | 省略と `null` の区別、一意解決の失敗メッセージ、切り詰めと日付・日時の書式 |
| `McpProtocol` | `initialize` / `ping` / `tools/list` / `tools/call`、通知の 202、未知のメソッドとツール名、例外の落とし先 |
| `MoTaskMcpServer` | 実際に HTTP を立てて叩く。ツール 6 本が並ぶこと、401（トークン違い・トークン無し。`tools/call` だけでなく `tools/list` でも）、トークンの形、`Dispose` の drain |
| `McpEndpointFile` | 書き込み・読み込み・壊れた JSON・項目欠けの扱い |
| `SingleInstance` | 1 つ目は取れて 2 つ目は取れないこと |
| ブリッジの endpoint 解決（`EndpointResolver`） | ファイル無し／pid が死んでいる／接続失敗／正常、の分岐とタイムアウト。プロセス起動・接続・時間を差し替え可能にして純関数として試験する |
| ブリッジの中継（`StdioBridge`） | 応答の素通し、通知に何も書かないこと、401 の 1 度だけ再試行、失敗時に stdout へ出るのは JSON-RPC エラーだけであること |
| DI 配線（`HostWiringTests`） | `MoTaskMcpServer` と `BoardViewModel` が同じ `BoardToolHost` を受け取ること |
| `BoardViewModel` | 外部変更イベントで `ReloadAsync` が呼ばれること |

実際のプロセス起動と Claude Code との結合はテストしない（§10 の手動確認に回す）。

## 10. 手動確認が要る項目

自動化できないので README のチェックリスト「9. TODO 操作 I/F（MCP）」に置く。

- [ ] `claude mcp add motask -- <発行先>\MoTask.Mcp.exe` で登録し、`/mcp` に 6 つのツールが並ぶ
- [ ] MoTask を閉じた状態で「TODO を見せて」と頼むとアプリが立ち上がり、一覧が返る（30 秒以内）
- [ ] MoTask を開いた状態でタスクを追加すると、ボードが即座に更新される
- [ ] Done ロールの列へ move すると、カードが完了表示になり履歴に「移動」が残る
- [ ] 存在しない列名を指定すると、候補の列名が並んだエラーが返る
- [ ] アプリを 2 回起動しても 2 つ目のウィンドウが出ない（Mutex）
- [ ] アプリを終了すると endpoint.json が消える。強制終了後も次の呼び出しで復帰する
- [ ] 論理削除したタスクは `list_tasks` に出ず、`get_task` でも見えない
