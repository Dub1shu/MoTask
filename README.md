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

自動テスト（Core / Data / App、173 本）ではカバーできない項目です。リリース前、
または D&D・フォント・詳細パネル周りを変更した後に、上から順に確認してください。

### 1. 列の並び替え（ドラッグ＆ドロップ）

列ヘッダーを掴んで別の位置へドラッグします。**これが最も未検証な項目です**
（実装時は合成入力での自動確認ができず、起動確認のみで済ませています）。

- [ ] 列ヘッダーをドラッグすると、ドラッグが実際に始まる（カーソルにアドーナーが付く、
      または挿入位置の線が出る）。
- [ ] 列を別の位置にドロップすると並びが変わり、再起動しても保たれる。
- [ ] 列ヘッダーを別の列の上に運んでいる間、その列のカード一覧が誤ってドロップを
      横取りしない（カードの並びが変わらない）。
- [ ] もし列ヘッダーのドラッグがそもそも始まらない場合は、フォールバック実装
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

- [ ] 同じ列内でカードを上下にドラッグすると、挿入線が出て離すと並びが変わる。
      再起動後も順序が保たれる。
- [ ] カードを別の列にドラッグして移動できる。詳細パネルの履歴に「移動」の記録が
      1件残る。
- [ ] 完了列（Done）にカードを移動すると、完了日時（CompletedAt 相当）が設定される
      （詳細パネルに反映される）。
- [ ] 空の列にカードをドロップできる。
- [ ] 列の最後尾（既存カードの下の余白）にドロップして末尾に追加できる。
- [ ] 列ヘッダーの「⋯」ボタン・列名の編集欄・WIP 上限の入力欄の上で操作しても、
      誤って列やカードのドラッグが始まらない。

### 3. 詳細パネル

- [ ] カードをクリックすると詳細パネルが開く。
- [ ] タイトル・説明・期限・プロジェクト・ラベルなど各フィールドを編集すると、
      即座に自動保存される（明示的な保存ボタンなし）。
- [ ] プロジェクト欄・ラベル欄で、既存にない名前を入力するとその場で新規作成される。
- [ ] タスクの削除（論理削除）と、削除したタスクの復元ができる。
- [ ] 履歴一覧に、これまでの操作（作成・移動・編集・削除など）が新しい順（または
      仕様どおりの順序）で、内容も正しく表示される。
- [ ] Esc で詳細パネルが閉じる。インライン編集中に Esc で編集が取り消される。

### 4. フォントと見た目

- [ ] 見出し（列名など）が Barlow Condensed **SemiBold**（600）で表示されている
      ことを目視で確認する。単なる Barlow Condensed Regular を太字表示（合成ボールド）
      しているだけではないか、隣に通常の太字テキストと並べて比べる、または
      Windows の「フォント」アプリで実際に埋め込まれた `BarlowCondensed-SemiBold.ttf`
      のグリフと見比べる。
- [ ] 本文が Barlow（Regular/Medium）で表示されている。
- [ ] 日本語テキストが Yu Gothic UI にフォールバックして表示されている（文字化けや
      極端に細い／太いフォールバックになっていない）。
- [ ] 期限超過のカードが赤系の強調表示、当日期限のカードがアクセント色
      （`#5980a6` 系）で表示されている。

### 5. WPF バインディングエラーの確認（デバッガ必須）

`dotnet run` 単体ではバインディングエラーは Output ウィンドウに出ません。
Visual Studio または `devenv`／VS Code のデバッガをアタッチしてアプリを起動し、
起動直後・カード操作時・詳細パネル表示時に Output（デバッグ）ウィンドウを確認して
ください。

- [ ] `System.Windows.Data Error` が一件も出ていない。
- [ ] 出ている場合は、対象のバインディングパス・要素名を記録し、該当する
      ViewModel のプロパティ名／XAML の `Binding` パスを見直す。

### 6. DB 復旧（破損データベースからの回復）

アプリを終了した状態で実行します。

```bash
cp "$LOCALAPPDATA/MoTask/motask.db" /tmp/motask-good.db
printf 'broken' > "$LOCALAPPDATA/MoTask/motask.db"
dotnet run --project src/MoTask.App
```

- [ ] 「データベースを開けませんでした…バックアップを作成して新しく作り直しますか？」
      というダイアログが出る。
- [ ] 「いいえ」を選ぶとアプリが終了する（DB はまだ壊れたまま）。
- [ ] 再度 `dotnet run --project src/MoTask.App` を実行し、今度は「はい」を選ぶと、
      `%LOCALAPPDATA%\MoTask\motask.db.bak-<日時>` が作成され、既定の4列
      （未着手・進行中・レビュー・完了など仕様どおりの構成）で新しいボードが
      起動する。
- [ ] 確認後、`/tmp/motask-good.db` を `%LOCALAPPDATA%\MoTask\motask.db` に
      戻し、作成された `.bak-*` ファイルや余分な `motask.db-shm` / `motask.db-wal`
      を削除して、確認前の状態に戻す。
