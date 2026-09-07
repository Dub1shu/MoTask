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

## 手動確認チェックリスト

自動テスト（Core / Data / App / Mcp、531 本）ではカバーできない項目です。リリース前、
または D&D・フォント・詳細パネル周りを変更した後に、上から順に確認してください。

### 1. 列の並び替え（ドラッグ＆ドロップ）

列ヘッダーを掴んで別の位置へドラッグします。**これが最も未検証な項目です**
（実装時は合成入力での自動確認ができず、起動確認のみで済ませています）。

- [x] 列ヘッダーをドラッグすると、ドラッグが実際に始まる（カーソルにアドーナーが付く、
      または挿入位置の線が出る）。
- [x] 列を別の位置にドロップすると並びが変わり、再起動しても保たれる。
- [x] 列ヘッダーを別の列の上に運んでいる間、その列のカード一覧が誤ってドロップを
      横取りしない（カードの並びが変わらない）。
- [x] もし列ヘッダーのドラッグがそもそも始まらない場合は、フォールバック実装
      （列の `ItemsControl` 自体を drag source にする方式）へ切り替えます。XAML の属性を
      3 か所差し替えるだけで、`ColumnDropHandler` と `DropPositionCalculator` は
      そのまま使えます。

      1. `src/MoTask.App/Views/ColumnView.xaml` の `<Border x:Name="Header" ...>` から
         `dd:DragDrop.IsDragSource="True"` と `dd:DragDrop.UseDefaultDragAdorner="True"`
         の 2 属性を外す。
      2. `src/MoTask.App/Views/BoardView.xaml` の
         `<ItemsControl x:Name="ColumnsHost" ...>` に `dd:DragDrop.IsDragSource="True"`
         を足す（`dd:DragDrop.IsDropTarget="True"` はそのまま残す）。
      3. `src/MoTask.App/Views/ColumnView.xaml` の `<ListBox x:Name="CardList" ...>` と、
         その下の「インライン作成」の `<Border DockPanel.Dock="Bottom" ...>` に
         `dd:DragDrop.DragSourceIgnore="True"` を足す。これがないと、カードや入力欄を
         掴んだつもりが列のドラッグになる。

### 2. カードのドラッグ＆ドロップ

- [x] 同じ列内でカードを上下にドラッグすると、挿入線が出て離すと並びが変わる。
      再起動後も順序が保たれる。
- [x] カードを別の列にドラッグして移動できる。詳細パネルの履歴に「移動」の記録が
      1件残る。
- [x] 完了列（Done）にカードを移動すると、完了日時（CompletedAt 相当）が設定される
      （詳細パネルに反映される）。
- [x] 空の列にカードをドロップできる。
- [x] 列の最後尾（既存カードの下の余白）にドロップして末尾に追加できる。
- [x] 列ヘッダーの「⋯」ボタン・列名の編集欄・WIP 上限の入力欄の上で操作しても、
      誤って列やカードのドラッグが始まらない。

### 3. 詳細パネル

- [x] カードをクリックすると詳細パネルが開く。
- [x] タイトル・説明・期限・プロジェクト・ラベルなど各フィールドを編集すると、
      即座に自動保存される（明示的な保存ボタンなし）。
- [x] プロジェクト欄・ラベル欄で、既存にない名前を入力するとその場で新規作成される。
- [x] タスクの削除（論理削除）と、削除したタスクの復元ができる。
- [x] 履歴一覧に、これまでの操作（作成・移動・編集・削除など）が新しい順（または
      仕様どおりの順序）で、内容も正しく表示される。
- [x] Esc で詳細パネルが閉じる。インライン編集中に Esc で編集が取り消される。

### 4. プロジェクトとラベルの管理ダイアログ

このダイアログは起動時ではなくボタンを押した時点で初めて生成されるため、起動確認では
XAML の実体が検証されない。最初に一度開くこと。

- [x] トップバー右端の「プロジェクトとラベル」を押すとダイアログが開く。
- [x] プロジェクトとラベルが一覧され、それぞれ「N 件で使用中」の件数が実態と合っている
      （論理削除済みのタスクは数に入らない）。
- [x] 使用中のものを「アーカイブ」すると、フィルタバーの選択肢と詳細パネルの選択肢から消える。
      一方で、**そのラベル／プロジェクトが付いているタスクからは外れない**（カードのチップと
      詳細パネルの表示が残る）。
- [x] アーカイブ済みの行は淡く表示され、ボタンが「復元」に変わる。押すと一覧に戻る。
- [x] 絞り込みに使っていたラベルをアーカイブすると、隠れていたカードが出てくる。
- [x] Esc と「閉じる」でダイアログが閉じる。

### 5. フォントと見た目

- [x] 見出し（列名など）が Barlow Condensed **SemiBold**（600）で表示されている
      ことを目視で確認する。単なる Barlow Condensed Regular を太字表示（合成ボールド）
      しているだけではないか、隣に通常の太字テキストと並べて比べる、または
      Windows の「フォント」アプリで実際に埋め込まれた `BarlowCondensed-SemiBold.ttf`
      のグリフと見比べる。
- [x] 本文が Barlow（Regular/Medium）で表示されている。
- [x] 日本語テキストが Yu Gothic UI にフォールバックして表示されている（文字化けや
      極端に細い／太いフォールバックになっていない）。
- [x] 期限超過のカードが赤系の強調表示、当日期限のカードがアクセント色
      （`#5980a6` 系）で表示されている。

### 6. WPF バインディングエラーの確認（デバッガ必須）

`dotnet run` 単体ではバインディングエラーは Output ウィンドウに出ません。
Visual Studio または `devenv`／VS Code のデバッガをアタッチしてアプリを起動し、
起動直後・カード操作時・詳細パネル表示時に Output（デバッグ）ウィンドウを確認して
ください。

- [x] `System.Windows.Data Error` が一件も出ていない。
- [x] 出ている場合は、対象のバインディングパス・要素名を記録し、該当する
      ViewModel のプロパティ名／XAML の `Binding` パスを見直す。

### 7. DB 復旧（破損データベースからの回復）

アプリを終了した状態で実行します。

```bash
cp "$LOCALAPPDATA/MoTask/motask.db" /tmp/motask-good.db
printf 'broken' > "$LOCALAPPDATA/MoTask/motask.db"
dotnet run --project src/MoTask.App
```

- [x] 「データベースを開けませんでした…バックアップを作成して新しく作り直しますか？」
      というダイアログが出る。
- [x] 「いいえ」を選ぶとアプリが終了する（DB はまだ壊れたまま）。
- [x] 再度 `dotnet run --project src/MoTask.App` を実行し、今度は「はい」を選ぶと、
      `%LOCALAPPDATA%\MoTask\motask.db.bak-<日時>` が作成され、既定の4列
      （未着手・進行中・レビュー・完了など仕様どおりの構成）で新しいボードが
      起動する。
- [x] 確認後、`/tmp/motask-good.db` を `%LOCALAPPDATA%\MoTask\motask.db` に
      戻し、作成された `.bak-*` ファイルや余分な `motask.db-shm` / `motask.db-wal`
      を削除して、確認前の状態に戻す。

### 8. AI 遂行（ターミナル実行）

前提: `claude --version` が 2.1.x を返し、ログイン済み。設定は `%LOCALAPPDATA%\MoTask\settings.json`。
仕様は `docs/superpowers/specs/2026-09-05-motask-terminal-ai-design.md`。

- [x] `MoTask.App` をビルドすると、出力フォルダの隣に `hooks\MoTask.Hooks.exe` が配置される。
      これが無いジョブは `フックの実行ファイル（hooks\MoTask.Hooks.exe）が見つかりません` で
      開始できないので、見当たらないときはビルドをやり直す。
- [x] 「AI 設定」ダイアログで既定の作業フォルダ・claude のパス・モデル・権限モード・端末の起動コマンドを
      変えて保存し、再起動後も残っている。
- [x] タスクを選び「遂行させる」→ 指示文を確認して「開始」。Windows Terminal が開き、
      `instruction.md` を読んで作業が始まる。
- [x] タスクを選び「調査させる」→ 指示文を確認して「開始」。端末の開き方・コマンドは
      「遂行させる」と全く同じで、バッジ表示と履歴の文言だけが変わる。
- [x] 「調査させる」の指示文はファイルを書かないよう求めているだけで、`--tools` などで
      強制してはいない。権限モード次第では実際に書き込みもできてしまう。
- [x] 走っている途中で人が割り込み、「そっちじゃない、こっちを調べて」と方向転換できる。
- [x] Claude が人に質問してきたとき、端末でそれに答えられる。
- [x] ツール承認が自分の `~/.claude/settings.json` の方針どおりに出る。MoTask のダイアログは出ない。
- [x] モデルの応答が終わるたびにカードに「入力待ち」バッジが付き、次に道具を使うと消える。
- [x] 端末で `/exit` すると、タスクが「確認待ち」列へ移り、履歴に「AI 遂行が完了」が残る。
- [x] 端末を × で強制終了すると、ジョブは「AI 遂行中」のまま残る。詳細パネルの「完了にする」を押すと
      「確認待ち」列へ移る。
- [x] MoTask を閉じたまま端末で作業を続け、MoTask を開き直すと、その間のやりとりが進行状況に
      追いついている。
- [ ] 「端末を開き直す」を押すと `--resume` で同じセッションが開き、会話の続きから始まる。
- [x] 「追跡をやめる」を押すと「AI 停止」になるが、**端末は開いたまま**（プロセスは殺さない）。
- [x] `<既定ワークフォルダ>\jobs\<番号>-<タスク名>\` に `job.json` / `instruction.md` / `hooks.json` /
      `events.jsonl` / `artifacts\` が揃っている。「ジョブフォルダを開く」で開ける。
- [x] 成果物一覧に `artifacts\` の実ファイルが出る。`... > report.md` のようにリダイレクトで作った
      ファイルも出る（Write / Edit を通らなくても見える）。クリックで既定のアプリが開く。
- [x] 存在しないパスをプロジェクトの作業フォルダに設定したタスクで開始すると、バナーに
      「プロジェクトの作業フォルダが見つかりません: …」と出て開始しない（既定へ逃げない）。
- [x] `wt.exe` が無い環境（PATH から外して確認）では `cmd.exe` のウィンドウで開く。
- [x] 端末の起動コマンドを `pwsh.exe -NoExit -Command {command}` にすると PowerShell で開く。

### 9. TODO 操作 I/F（MCP）

前提: 下の「発行と登録」を済ませ、`claude mcp list` で `motask` が **Connected** になって
いること（登録さえすれば一覧には出るので、接続まで確認する）。
仕様は `docs/superpowers/specs/2026-09-05-motask-mcp-interface-design.md`。

- [x] `claude` を起動して `/mcp` を開くと `motask` に 6 つのツールが並ぶ。
- [x] MoTask を閉じた状態で「MoTask の TODO を見せて」と頼むと、MoTask が立ち上がり
      一覧が返る（初回は 30 秒以内）。
- [x] MoTask を開いた状態で「〇〇をタスクにしておいて」と頼むと、進行中の列に
      カードが増え、**ボードが即座に更新される**（手で更新しなくてよい）。
- [x] 「〇〇は終わったから完了にして」と頼むと、Done ロールの列へ移り、カードが
      完了表示になる。履歴に「移動」が残る。
- [x] 存在しない列名（例:「保留中」）を指定すると、候補の列名が並んだエラーが返る。
- [x] `motask` をユーザースコープで登録したまま、TODO と無関係なプロジェクトで `claude` を
      起動すると MoTask のウィンドウが開く（下の「セッション開始時に MoTask が起動する」）。
      既に起動していれば何も起きない。開かれたくないならプロジェクト単位で登録し直す。
- [x] MoTask を 2 回起動しても 2 つ目のウィンドウが出ない（Mutex）。
      タスクマネージャの `MoTask.exe` も 1 つのまま。
- [x] MoTask を終了すると `%LOCALAPPDATA%\MoTask\endpoint.json` が消える。
      タスクマネージャで強制終了した場合はファイルが残るが、次の呼び出しで
      アプリが起動し直して復帰する。
- [x] 論理削除したタスクは `list_tasks` に出ず、`get_task` で id を指定しても
      「タスクが見つかりません」になる。

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
