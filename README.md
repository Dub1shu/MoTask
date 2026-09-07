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

# dotnet-ef はローカルツール（.config/dotnet-tools.json）なので、
# クローン直後は先に復元が必要です。
dotnet tool restore
dotnet ef migrations add <Name> --project src/MoTask.Data --output-dir Migrations
```

構成は `docs/superpowers/specs/2026-09-04-motask-kanban-v1-design.md` を参照。

## MoTask.Mcp（Claude Code から TODO を操作する）

手元の Claude Code に MoTask の TODO を読み書きさせるための stdio ブリッジ。

### 発行と登録

ブリッジは既定で **自分と同じフォルダの `MoTask.exe`** を探すので、2 つを同じ場所へ発行する
（`MoTask.App` の `AssemblyName` が `MoTask` なので、出来上がる exe は `MoTask.exe`）。

```bash
dotnet publish src/MoTask.App -c Release -o publish
dotnet publish src/MoTask.Mcp -c Release -o publish
```

登録するときは、**ブリッジの絶対パスを直接書く**。Claude Code はコマンド文字列をそのまま
保存するので、`$(pwd)` のようなシェルの展開はここでは効かない（展開されないまま保存され、
`Failed to reconnect to motask` になる）。

```bash
claude mcp add motask -- "D:\source\cs\MoTask\publish\MoTask.Mcp.exe"
```

登録内容は `claude mcp get motask` で確認できる。`command` が実在する exe を指していること。

開発中に `bin` の exe を使いたい場合は、環境変数 `MOTASK_APP_EXE` にアプリの
実行ファイルの絶対パスを入れる。

### セッション開始時に MoTask が起動する

Claude Code は stdio の MCP サーバをセッション開始時に起動し、`initialize` と `tools/list` を
送ってツールを列挙する。ブリッジは受け取った行ごとに endpoint を解決し、MoTask が起動して
いなければそこで起動するので、**`motask` をユーザースコープで登録すると、TODO と無関係な
プロジェクトで `claude` を起動しただけで MoTask のウィンドウが開く**。

MoTask が既に起動していれば何も起きない（単一インスタンスの Mutex により二重起動はしない）。

常時開いておきたくない場合は、ユーザースコープではなく MoTask を使うプロジェクトでだけ
登録する（そのプロジェクトのフォルダで `claude mcp add` を実行する）。

### 既知の制限：同時更新は後勝ち

`update_task` は現在値を読んでから省略された項目を埋めて書き戻す（read-modify-write）。
各サービス呼び出しは直列化されるが、「読み」と「書き」の間は保護されていないため、2 つの
Claude セッションが同じタスクへ別々の項目の `update_task` を投げると、後から書いたほうが勝ち、
もう一方の変更は消える（画面で編集中のカードと衝突した場合も同じ）。個人用途では実害が
小さいため、根本的な修正（楽観的同時実行制御など）はスコープ外とした。

### 使えるツール

`get_board`（列・プロジェクト・ラベル）／`list_tasks`／`get_task`／`add_task`／
`update_task`／`move_task`。完了させるには `get_board` の `role` が `Done` の列へ
`move_task` する。列・プロジェクト・ラベルは id でも名前でも指定できる。
