# MoTask 朝の実行 — MCP による受け渡しと端末の自動終了（設計仕様）

日付: 2026-09-13
状態: 設計承認済み（実装計画は未作成）
親仕様: `2026-09-07-motask-morning-plan-triage-design.md`（朝の実行プランと仕分け）
関連: `2026-09-05-motask-terminal-ai-design.md`（端末での AI 実行）、
`2026-09-05-motask-mcp-interface-design.md`（MCP の I/F と自動起動）、
`2026-09-09-motask-morning-plan-view-design.md`（プランの確定と画面）

## 1. 何を作るか

朝の実行を起動すると端末が開き、Claude が仕事を終えても**端末は開いたまま残る**。
この文書は、その端末が仕事の完了とともに自分で閉じるようにする。

閉じない原因は 2 つあり、片方だけ直しても閉じない。

1. **`cmd /k` でシェルを残している。** `TerminalLauncher.WindowsTerminalTemplate` は
   `wt.exe -d "{cwd}" cmd /k {command}` である。`/k` は「コマンドが終わってもシェルを残す」指定なので、
   claude が終了しても窓は残る。
2. **そもそも claude が終了しない。** 起動プロンプトを `--` の後に位置引数として渡しているので、
   claude は**対話 REPL** として起動する。`plan.json` を書き終えても REPL は次の入力を待って座ったままである。

一方で `MorningService` は `Stop` フックのたびに `result/` を覗き、揃っていればその場で `Ingested` にしている。
つまり **MoTask 側の仕事は終わっているのに端末だけが残る**。完了はすでに検知できているので、
「完了をどう知るか」ではなく「知った後どうやって窓を閉じるか」が本題である。

窓を閉じるには MoTask が claude のプロセスを掴んでいる必要があり、そのためには起動の形を変える必要がある。
ついでに、Claude から MoTask への成果の受け渡しを**ファイルから MCP に移す**。この 2 つは独立に見えて、
同じところに効く。MCP に移すと「候補が 1 件届いた」が画面にそのまま出せるようになり、
親仕様 §11 が置いていた「実行中の進捗」が別途作らずに満たされる。

## 2. スコープ

### 含む

- 朝の実行の起動を `wt.exe` 経由から `cmd.exe` 直接に変え、MoTask がプロセスを所有する
- 起動テンプレートの既定を `cmd.exe` 系に統一し、`wt.exe` の分岐を落とす
- 朝の実行の成果（盤面の取得・候補・プラン・完了）を MCP ツール 4 本に移す
- `result/candidates.jsonl` / `result/plan.json` / `board.json` のファイル契約を廃止する
- `instruction.md` の契約節を、ファイルの形の説明からツールの呼び出し手順に差し替える
- 完了時・セッション終了時・端末が先に死んだときの、端末とプロセスの後始末
- 朝の実行の MCP サーバ登録を `--mcp-config` で保証する（利用者の手動登録に依存しない）

### 含まない

- AI 遂行（タスク単位の AI 実行）の挙動の変更。`Launch`（所有しない）のままで、端末は閉じない
- `MoTask.Hooks` / `HookEventParser` / `JobEventWatcher` の変更。`events.jsonl` の契約はそのまま
- `BoardToolHost` の 6 本の変更
- `TriageCandidate` / `MorningRun` への列追加。マイグレーションは作らない
- `MorningPlanResolver` / `ResolvedPlan` / 朝の画面のレイアウトの変更
- headless 実行（`--print`）。理由は §3
- MoTask が承認を聞く仕組み（`--permission-prompt-tool`）。理由は §3
- 朝の実行を MoTask 内に埋め込むこと（端末を出さない案）。理由は §3

## 3. 決定事項とその理由

| 論点 | 決定 | 理由 |
| --- | --- | --- |
| 実行形態 | 対話 REPL のまま。headless にしない | headless は承認プロンプトに答えられない。`--permission-mode auto`（現在の既定）のままだとコネクタ呼び出しが軒並み拒否され、朝の実行が空振りする。回避するには許可リストか `bypassPermissions` の設定が要り、そこまでの手間を掛けたくない |
| 承認の扱い | 利用者の `settings.json` の方針どおり端末で出す | `--permission-prompt-tool` で MoTask が聞く道はあるが、それは `2026-09-05-motask-terminal-ai-design.md` が「承認の二重管理をやめる」として意図的に削除した仕組みの復活になる |
| 端末を残すか | 残す。ただし補助に落とす | 承認プロンプトに答える場所と、詰まったときに覗く場所が要る。進捗は MoTask の画面に出るので、端末を見続ける必要はなくなる |
| 誰が claude を終わらせるか | MoTask。対話 REPL は自分では終われない | 対話モードの claude を外から止める以外に道がない。Stop フックに親を殺させる案もあるが、「中身を解釈しない追記係」というフックの規律を崩す |
| プロセスの掴み方 | MoTask が `cmd.exe` を直接起動し、`Process` ハンドルを保持する | `wt.exe` を挟むと `Process.Start` が返すのは即座に終了する起動役の pid で、掛けどころを失う。`cmd` 自体は外せない（§5.1） |
| 起動テンプレート | 既定を `cmd.exe` 系に統一し、`wt.exe` の分岐を落とす | 分岐・`{cwd}` の埋め込み・テストの構築子引数が丸ごと消える。朝の実行が「テンプレートを無視する」例外も要らなくなる（§5.2） |
| 成果の受け渡し | 候補・プラン・完了をすべて MCP ツールで受ける | 候補が 1 件ずつ画面に出せる。今は形が不正でも `MorningResultUnreadable` で実行ごと失敗し、**Claude はそれを知らないまま終わる**。MCP ならその場で不備を返せる |
| ツールの粒度 | 候補は 1 件ずつ積む | 進捗表示が別途要らなくなる。1 件の不備で全体が弾かれない。呼び出し回数は増えるが、朝 1 回・数件〜十数件の規模では問題にならない |
| ツールの宛先 | すべて `runId` を必須にする | 利用者が普段使っている Claude Code も同じ MCP サーバに繋がる。そちらが誤って朝の実行を完了させ、端末を殺す事故を防ぐ |
| 不備の返し方 | ツールエラーではなく通常の結果で `accepted:false` と理由を返す | Claude は「1 件弾かれた」だけを受け取って次へ進めばよい。セッションを失敗させる話ではない |
| 閉じる瞬間 | `morning_complete` の直後ではなく、次に来る `Stop` フックで閉じる | ツール結果を返した直後に殺すと Claude の最後の一言が切れる。`Stop` はそのターンが終わった合図なので、言い終えてから消える |
| AI 遂行 | 変更しない。`Launch`（所有しない）のまま | 「MoTask を閉じても端末は走り続ける」は意図した設計である。所有すると理由もなくその約束が揺れる |
| MoTask 終了時 | ハンドルを解放するだけ。端末は殺さない | 実行の途中でアプリを閉じただけで仕事を潰さない。MCP 呼び出しはブリッジが MoTask を起動し直して届く |
| 手動確認の置き場 | この文書の §11 | README のチェックリストは 2026-09-08 に外された。戻さない |

## 4. アーキテクチャとデータフロー

```
MoTask.exe (MoTask.App)
 │
 ├─ Process.Start("cmd.exe", "/c <claude …>")   ← Process ハンドルごと保持する
 │    └─ claude（対話 REPL・permission-mode は設定どおり）
 │         ├─ MoTask.Hooks.exe ──> events.jsonl（SessionStart / PostToolUse / Stop / SessionEnd）
 │         └─ MoTask.Mcp.exe ──HTTP──> MoTaskMcpServer
 │                                       ├─ BoardToolHost   （既存 6 本・無変更）
 │                                       └─ MorningToolHost （新規 4 本）
 │                                            └─ IMorningService
 └─ JobEventWatcher ──> events.jsonl を追う（進捗表示と終了検知）
```

**経路が 2 本に分かれる。** MCP は「成果」（盤面・候補・プラン・完了）を運び、
フックは「営み」（どのツールを使ったか・ターンが終わったか）を運ぶ。
現状は成果がファイル、営みがフックだが、**成果の側だけ**が MCP に移る。

ジョブフォルダの中身は `instruction.md` / `hooks.json` / `events.jsonl` / `run.json` / `mcp.json` になる。
`result/` と `board.json` は作られなくなる。

### 変更するもの

| 場所 | 変更 |
| --- | --- |
| `src/MoTask.App/Ai/MorningTools/`（新規） | `MorningToolHost` — ツール 4 本の定義・引数の検証・`IMorningService` 呼び出し。`BoardToolHost` と同じ構え |
| `src/MoTask.Core/Morning/` | `MorningResultReader` を解体し `CandidateValidator`（1 件分）と `MorningPlanValidator`（プラン 1 本）に分ける |
| `src/MoTask.Core/Morning/MorningInstruction.cs` | 契約節を「ファイルに書け」から「ツールを呼べ」に差し替え |
| `src/MoTask.Core/Services/MorningService.cs` | `Stop` での `result/` 読みを撤去。`AddCandidateAsync` / `SubmitPlanAsync` / `GetContextAsync` を追加。`CompleteAsync` が端末を閉じる |
| `src/MoTask.Core/Services/IMorningService.cs` | 上の 3 つを公開 |
| `src/MoTask.Core/Ai/ISessionLauncher.cs` | `LaunchOwned(ownerId, command)` / `CloseOwned(ownerId)` を追加。既存 `Launch` はそのまま |
| `src/MoTask.App/Ai/TerminalLauncher.cs` | テンプレートの統一と所有起動の実装 |
| `src/MoTask.Core/Ai/MorningRunDescriptor.cs` | `run.json` に `processId` と `processStartedAt` を足す |
| 削除 | `MorningResultReader`、`JobFolderPaths.CandidatesRelativePath` / `PlanRelativePath` / `ResultDirectoryName` / `BoardJsonName`、`TerminalLauncher.WindowsTerminalTemplate` と `FindWindowsTerminal` |

依存方向は変わらない。新規 NuGet パッケージは足さない。P/Invoke も使わない。

## 5. 端末の起動と所有

### 5.1 `cmd` は外せない

`ClaudeLocator.Find` は `claude.exe` だけでなく**拡張子なしの `claude`** も拾う（npm 経由のシム）。
拡張子なしのパスは `CreateProcess` では起動できず、PATHEXT を補って解決しているのは `cmd` である。
したがって「`claude.exe` を直接起動して pid を持つ」案は採れない。
選択は `wt + cmd` か `cmd` 単独かの二択で、後者を採る。

### 5.2 テンプレートの統一

```
DefaultTemplate  = "cmd.exe /s /k \"{command}\""   AI 遂行（対話を続ける）
MorningTemplate  = "cmd.exe /s /c \"{command}\""   朝の実行（claude が終われば窓も畳む）
```

`WindowsTerminalTemplate` と `FindWindowsTerminal()` と `_hasWindowsTerminal` を落とす。
`TerminalLauncher` の内部テスト用構築子から `Func<bool> hasWindowsTerminal` 引数も消える。

`{command}` を引用符で包み `/s` を付けるのは省略できない。`cmd /?` の規則により、`/c` や `/k` の
後ろに引用符が 3 個以上あると cmd は**先頭の 1 個と末尾の 1 個を剥がして**残りを解釈し直す。
MoTask は引数を 1 つずつ引用符で囲むので十数個になり、包まないと実行ファイルのパスが壊れて
claude が一度も起動しない（2026-09-15 の手動確認で実際に踏んだ）。`/s` はこの剥がす規則を
無条件にして、引用符の個数に依存させないための指定である。

既定テンプレートに `{cwd}` が現れなくなるので、末尾バックスラッシュで `CommandLineToArgvW` が壊れる問題
（現行 `TerminalLauncher` のコメント付きの細工）を**既定の道では踏まなくなる**。
利用者定義テンプレートは `{cwd}` を使えるので、置換のコード自体は残す。

利用者定義の `TerminalCommandTemplate` は AI 遂行でも朝の実行でも効く。
ただし朝の実行では、**先頭トークンのファイル名が `wt.exe` のときだけ既定に落とし**
（`C:\…\wt.exe` のようなフルパスも同じ扱い。判定は大文字小文字を区別しない）、
「wt を経由すると完了時に端末を閉じられないので、朝の実行では既定の起動を使いました」と AI 設定画面に注意を出す。
`powershell.exe -NoExit -Command {command}` や WSL のテンプレートは、
先頭プロセスがウィンドウの持ち主なので朝の実行でもそのまま使える。

Windows 11 では `cmd.exe` も既定で Windows Terminal の中に開くので、見た目はほぼ変わらない。
ただし既存の WT ウィンドウのタブとしては開かず、必ず新しいウィンドウになる。
「既定のターミナル アプリケーション」を conhost に戻している環境では昔ながらの窓になる。
どちらも §11 の手動確認で見る。

### 5.3 所有起動

```csharp
public interface ISessionLauncher
{
    Result CheckAvailable();
    Result<TerminalCommand> BuildCommand(SessionLaunchRequest request);

    /// <summary>所有しない起動。ハンドルはその場で捨てる（AI 遂行）。</summary>
    Result Launch(TerminalCommand command);

    /// <summary>所有する起動。ownerId に紐づけて Process を保持する（朝の実行）。</summary>
    Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command);

    /// <summary>プロセスツリーごと終了させる。知らない ownerId は黙って無視する。</summary>
    void CloseOwned(int ownerId);

    /// <summary>再起動後に掛け直す。生きていて開始時刻が一致すれば true。</summary>
    bool TryReattach(int ownerId, int processId, DateTime startedAt);

    /// <summary>所有しているプロセスが終わった。渡すのは ownerId だけで、理由は問わない。</summary>
    event EventHandler<int>? OwnedSessionExited;
}

public sealed record OwnedSession(int ProcessId, DateTime StartedAt);
```

`TerminalLauncher` は `ConcurrentDictionary<int, Process>` でハンドルを保持する。
**ハンドルを開いたままにするのが要点**で、Windows は開いているハンドルのある pid を再利用しないため、
「死んだ後に同じ pid の別プロセスを殺す」事故が起きない。
`EnableRaisingEvents = true` にして `Exited` を購読し、`OwnedSessionExited` に流す。

`CloseOwned` は `Process.Kill(entireProcessTree: true)` で `cmd` / `claude` / `MoTask.Mcp.exe` をまとめて落とす。
終了コードに依存しないので確実に窓が消える。`Kill` の後にハンドルを閉じて辞書から外す。
`InvalidOperationException`（すでに終了）と `Win32Exception` は握り潰す — 目的は窓が消えることで、
すでに消えているならそれでよい。

`MoTask.App` の `OnExit` では `CloseOwned` を呼ばず、ハンドルを閉じるだけにする（§3 の決定）。

### 5.4 起動コマンド

```
cmd.exe /s /c "<claude> --settings <ジョブフォルダ>/hooks.json --session-id <SessionId>
               --permission-mode <設定値> --add-dir <ジョブフォルダ>
               --mcp-config <ジョブフォルダ>/mcp.json
               -- <ジョブフォルダ>/instruction.md を読んで作業を始めてください。"
```

実際には引数は 1 つずつ引用符で囲まれ、その全体をさらに 1 組の引用符で包む（§5.2）。
`/s` と外側の引用符が無いと cmd が先頭と末尾の引用符を剥がしてパスを壊す。

`--mcp-config` が新しい。`MoTask.Mcp.exe` の絶対パスを書いた `mcp.json` をジョブフォルダに生成して渡す。
これで朝の実行は利用者の手動 MCP 登録に依存しなくなる。
`--strict-mcp-config` は渡さないので、利用者のコネクタ（Gmail・カレンダーなど）はそのまま生きる。

起動プロンプトから出力先の案内が消える。成果はファイルに出さないので伝えるものが無い。
朝の実行用に `TerminalStartPromptFormat` とは別の、書式引数が指示文のパス 1 つだけの文言を resx に足す
（AI 遂行の文言は成果物の出し先を伝える必要があるので、そのまま残す）。
`SessionLaunchRequest.OutputDirectoryName` と `JobFolderRequest.OutputDirectoryName` は
AI 遂行の `artifacts` 固定に戻り、朝の実行から渡さなくなる。

## 6. MCP ツール契約（4 本）

`MorningToolHost` に置く。名前は `BoardToolHost` と同じ流儀の snake_case で、
MCP 上の完全名は `mcp__motask__morning_*` になる。説明文は resx に置かずコードに直書きする
（`2026-09-05-motask-mcp-interface-design.md` §3 の決定に従う）。

すべてのツールが `runId`（整数）を必須にする。現在の未完了の実行と一致しなければ**ツールエラー**にし、
「この runId の朝の実行は動いていません」と返す。

### `morning_get_context(runId)`

対象日と盤面を返す。中身は現行の `board.json` と同じ `BoardSnapshot`（列・タスク・プロジェクト・期日・
`hasActiveAiJob`）に `date` を足したもの。`BoardSnapshot.Build` をそのまま使うので形は変わらない。

### `morning_add_candidate(runId, externalId, source, title, evidence, suggestedAction, …)`

候補を 1 件積む。引数は現行の JSON Lines 1 行と同じ項目
（`externalId` / `source` / `receivedAt` / `from` / `title` / `evidence` / `link` / `reasoning` /
`suggestedDueDate` / `suggestedProject` / `suggestedAction` / `mergeTargetTaskId`）。
必須は `externalId` / `source` / `title` / `evidence` / `suggestedAction`。

| 結果 | 返すもの |
| --- | --- |
| 受理 | `{"accepted":true,"candidateId":12,"total":3}` |
| 必須項目が欠けている | `{"accepted":false,"reason":"evidence が空です。元の文面から引用してください"}` |
| `suggestedAction` が 4 値以外 | `{"accepted":false,"reason":"register / merge / later / reject のどれかにしてください"}` |
| `merge` なのに `mergeTargetTaskId` が無い／盤面に無い | `{"accepted":false,"reason":"mergeTargetTaskId が盤面にありません"}` |
| 既知の `externalId`（過去に却下・登録済み） | `{"accepted":false,"reason":"この externalId は過去に処理済みです"}` |
| 同じ実行で 2 度目の `externalId` | `{"accepted":false,"reason":"今朝すでに積んだ externalId です"}` |

いずれも**ツールエラーではなく通常の結果**として返す（§3 の決定）。
受理のたびに `RunChanged` を上げるので、候補が画面に 1 件ずつ現れる。

既知の `externalId` の判定は現行の取り込みと同じく `GetKnownExternalIdsAsync` を使う。
現行は取り込み時に黙って捨てていたが、ここでは理由を返す。

### `morning_submit_plan(runId, plan)`

`plan` は現行の `plan.json` と同じ形の JSON。検証は `MorningPlanValidator` が行う
（`groups[].key` が `today` / `ifTime` / `aiReady` / `waiting` の 4 値・`items[]` の各要素が
`taskId` か `externalId` のどちらかを持つ・`firstThing` が同じ形）。

- 通れば `MorningRun.PlanJson` に保存し `{"accepted":true}`
- 通らなければ受理せず `{"accepted":false,"reason":"groups[2].key が 'later' です。today / ifTime / aiReady / waiting のどれかにしてください"}`

何度でも呼べて、最後に受理されたものが残る。
`PlanJson` に入る文字列の形は現行と同じなので、`MorningPlanResolver` は 1 行も変わらない。

### `morning_complete(runId)`

この朝の実行はこれで終わり、と宣言する。

- `PlanJson` が未提出なら受理せず `{"accepted":false,"reason":"先に morning_submit_plan を呼んでください"}`
- 受理したら `Ingested` にし、`RunChanged` を上げ、**端末を閉じる予約を入れる**（§7）
- 候補 0 件でも受理する。0 件は失敗ではない（親仕様 §8 の約束を引き継ぐ）

## 7. 状態遷移と後始末

```
Pending ──SessionStart──> Running ──morning_complete──> Ingested ──次の Stop──> 端末を閉じる
   │                         │
   │                         ├─ SessionEnd（complete 未着）───> Failed ＋ 端末を閉じる
   │                         └─ プロセスが先に終わった ────────> Failed
   └── 起動失敗 ─────────────────────────────────────────────> Failed
```

`MorningRunStatus` の値は変えない。

### 閉じる瞬間

`morning_complete` を受けた**その場では閉じない**。ツール結果を返した直後に殺すと、
Claude の最後の一言が切れるからである。

```
morning_complete 受信 → Ingested にして画面更新 → 「閉じる」を予約
                                                      ↓
                                  次に来る Stop フック（＝そのターンの終わり）
                                                      ↓
                                            CloseOwned(runId) → 窓が消える
```

予約から 60 秒経っても `Stop` が来なければ閉じる（保険）。
`Ingested` は終端状態なので現行の `OnHookLineAsync` は行を捨てるが、
**閉じる予約がある間だけは `Stop` を見る**必要がある。予約の有無で分岐する。

### 端末が先に死んだとき

`OwnedSessionExited` を購読し、実行がまだ終端でなければ `Failed`
（「端末が閉じられました」）にして追従を止める。

これは新しくできることである。現行は端末を × で閉じても MoTask は `events.jsonl` を延々ポーリングし続け、
人が「完了にする」を押すまで実行が宙に浮いたままだった。

### SessionEnd が complete 無しで来たとき

`Failed`（「Claude が完了を通知しないままセッションを終えました」）にし、`CloseOwned` を呼ぶ。
現行の `MorningResultUnreadable` の位置づけを引き継ぐ。

### 人の操作

現行の 2 つをそのまま残す。

- `CompleteAsync`（完了にする）— `Ingested` にし、**その場で端末を閉じる**
- `StopTrackingAsync`（追跡をやめる）— `Cancelled` にし、**端末は閉じない**
  （「端末は殺さない」という既存の約束をここでは守る）

`morning_complete` と `CompleteAsync` は同じ状態遷移を共有するが、**閉じ方だけが違う**。
`morning_complete` は Claude がまだ喋っている最中に届くので予約して次の `Stop` を待つ。
人の「完了にする」は端末が詰まっているか既に閉じられている場面で押されるもので、
待つべき `Stop` が来る保証が無いのでその場で閉じる。
`MorningService` の内部では「閉じるのを予約するか、その場で閉じるか」を引数で受け取る 1 本にまとめる。

### MoTask を閉じたとき・再起動したとき

`OnExit` ではハンドルを解放するだけで端末は殺さない。朝の実行は走り続け、
MCP 呼び出しはブリッジが MoTask を起動し直して届く（`EndpointResolver` が既にやっている）。

`run.json` は pid と開始時刻が決まってからでないと書けないので**起動に成功した後に書く**。
そのため起動に失敗した実行のジョブフォルダには `run.json` が無い（掛け直す相手も無いので困らない）。

再起動後の MoTask は掛けどころを失っているので、`run.json` に書いた `processId` と `processStartedAt` を
`RecoverOnStartupAsync` が読み、`TryReattach` で掛け直す。
`Process.GetProcessById` が引けて `StartTime` が一致すれば成功。
一致しなければ**閉じる能力だけ**を諦め、実行の追跡（`events.jsonl` の追従と MCP の受け口）は続ける。
`StartTime` を照合するのは、pid が再利用されて無関係のプロセスを殺さないためである。

## 8. instruction.md の契約

前半（人が AI 設定で書き換えられる収集方針、`MorningInstructionDefault`）は変えない。
MoTask が書く後半（`MorningInstructionContractFormat`）だけ差し替える。
書式引数は現行の「日付・board.json・candidates.jsonl・plan.json」から「日付・runId」の 2 つに減る。

```
## 出力の契約（この節は MoTask が書いています。変更しないでください）

対象日は {0} です。この朝の実行の runId は {1} です。
成果は MCP ツールで渡してください。ファイルは書かないでください。

1. mcp__motask__morning_get_context({{"runId":{1}}}) で今日の盤面を取る。
   統合先の推薦と今日のプランは、必ずこの結果に基づくこと。
2. 候補 1 件ごとに mcp__motask__morning_add_candidate({{"runId":{1}, ...}}) を呼ぶ。
   externalId は再実行しても同じ値になるようにすること（例: サービス名:元のID）。
   一度片づけた候補を二度出さないための鍵である。
   accepted:false が返ったら reason を読み、直せるなら直して呼び直す。
   直せないなら、その候補は諦めて次へ進んでよい。
3. mcp__motask__morning_submit_plan({{"runId":{1},"plan":{{...}}}}) でプランを出す。
   aiReady には、morning_get_context の hasActiveAiJob が false で AI に任せられるものを入れる。
4. mcp__motask__morning_complete({{"runId":{1}}}) を呼ぶ。呼ぶとこの端末は閉じる。

決めるのは人である。suggestedAction は推薦であって実行ではない。
候補が 0 件の朝もある。それは失敗ではない。その場合も 3 と 4 は必ず呼ぶこと。
```

各引数の形は MCP のスキーマが持つので、契約文に JSON の例を並べる必要がなくなる。
現行の契約文は 30 行ほどあるが、半分以下になる。

## 9. レイヤの分担

| 層 | 持つもの |
| --- | --- |
| `MoTask.Core/Morning` | `CandidateValidator`・`MorningPlanValidator`（純関数）、`MorningInstruction`、`BoardSnapshot`、`MorningPlanResolver`（無変更） |
| `MoTask.Core/Services` | `MorningService` — 実行のライフサイクル、候補とプランの保存、端末を閉じる判断 |
| `MoTask.Core/Ai` | `ISessionLauncher` の契約、`MorningRunDescriptor` |
| `MoTask.App/Ai/MorningTools` | `MorningToolHost` — ツール定義、引数の JSON ↔ ドメインの変換、結果の JSON 整形。HTTP も JSON-RPC も知らない |
| `MoTask.App/Ai` | `TerminalLauncher` — プロセスの起動・保持・終了。`Process` を触るのはここだけ |

`MorningToolHost` は `IMorningService` だけを呼ぶ。検証は Core の純関数が持ち、
`MorningToolHost` は「JSON を読んで渡し、結果を JSON にする」だけにする。
`BoardToolHost` と同じ分担である。

## 10. テスト方針

| 層 | 何を固定するか |
| --- | --- |
| Core・純関数 | `CandidateValidator` / `MorningPlanValidator`。現行 `MorningResultReader` のテストを移植する（JSON Lines 読みの分だけ落ちる） |
| Core・`MorningService` | plan 未提出の `complete` は受理しない／`complete` 後の `Stop` で `CloseOwned` が呼ばれる／60 秒の保険で閉じる／`SessionEnd` が complete 無しなら `Failed` ＋ `CloseOwned`／`OwnedSessionExited` で `Failed`／`StopTracking` は閉じない／`CompleteAsync` は閉じる |
| Core・重複 | 過去に却下・登録済みの `externalId` が弾かれ理由が返る／同じ実行で 2 度目が弾かれる |
| Core・再掛け直し | `TryReattach` が成功すれば閉じられる／失敗しても追従は続く |
| App・`MorningToolHost` | ツール 4 本を「引数 JSON → `McpToolResult`」で。`runId` 不一致がツールエラーになる。`BoardToolHost` のテストと同じ構え |
| App・`TerminalLauncher` | 朝用コマンドの組み立て（`cmd.exe /s /c` になる／`--mcp-config` が入る／出力先の案内が消える）／AI 遂行は `cmd.exe /s /k`／先頭が `wt.exe` の利用者テンプレートは朝の実行で既定に落ちる／`wt` 分岐が消えても既存のテンプレート置換テストが通る |

**組み立てた文字列を見るだけのテストでは足りない。** 実際に `cmd.exe` へ食わせて、狙った実行ファイルが
起動したことを確かめる 1 本を置く（無害な偽 claude を指し、目印の出力と終了コードで肯定形に見る）。
2026-09-15 に踏んだ引用符のバグは、文字列アサーションを全件緑にしたままアプリを完全に壊していた。
窓は開かず、何も kill せず、200ms 未満で終わるので、この 1 本だけは自動テストに置く。

### 既知の制約: シェルのメタ文字

ジョブフォルダ名は AI 遂行ではタスク名から作られる（`JobFolderPaths.Slug`）。`Slug` はファイル名に
使えない文字だけを潰すので、`%` や `$` は残る。そのパスはコマンドラインに載るので、`cmd` は `%…%` を、
PowerShell テンプレートを使う人は `$…` を展開してしまう。シェルを挟む限り付いて回る問題で、
今のところ手当てしていない。直すなら `Slug` 側でメタ文字も潰すのが素直である。

`FakeSessionLauncher` に `LaunchOwned` / `CloseOwned` / `TryReattach` の記録と
`OwnedSessionExited` の発火を足す。実プロセスの起動と終了そのものは §11 の手動確認に回す。

**`AiJob*` 一式とそのテストが 1 行も壊れないこと**を完了条件に入れる。

## 11. 手動確認が要る項目

README には足さない。実施したらここにチェックを入れてコミットする。
1 本目（起動の統一と端末の所有）で確認できるのは、端末が Windows Terminal の中に開くこと・× で閉じたときに `Failed` になること・MoTask を閉じても端末が残り開き直すと追跡が続くこと・「追跡をやめる」では閉じないこと・AI 遂行の端末が従来どおり開いたままであること、および `wt.exe` テンプレートの落とし込みである。
1 本目の時点では `morning_complete` がまだ無いので、`result/` が揃った実行では取り込みと同時に端末が閉じるのがこの時点の挙動として見られる。
残りは `morning_complete` と候補の逐次到着を要するので 2 本目（MCP への移行）で確認する。

- [ ] 朝の実行を起動すると端末が開き、Windows Terminal の中に出る（conhost の古い窓ではない）
- [ ] 候補が届くたびに、朝の画面の候補キューが 1 件ずつ増える
- [ ] Claude が `morning_complete` を呼ぶと、最後の一言を言い終えてから端末が閉じる
- [ ] 閉じた後、朝の画面にプランと候補が揃っている
- [ ] 候補 0 件の朝でも `complete` が通り、端末が閉じ、画面が「候補なし」になる
- [ ] `evidence` を空にした候補を Claude に投げさせると、理由が返って Claude が次へ進む
- [ ] 実行中に端末を × で閉じると、朝の画面が `Failed`（端末が閉じられました）になる
- [ ] 実行中に MoTask を閉じても端末は残り、MoTask を開き直すと追跡が続く
- [ ] その状態から `morning_complete` まで進めると、掛け直した MoTask が端末を閉じる
- [ ] 「追跡をやめる」では端末が閉じない
- [ ] AI 遂行の端末は従来どおり開いたままで、閉じない
- [ ] AI 設定で `TerminalCommandTemplate` に `wt.exe …` を入れると、朝の実行で注意が出て既定の起動になる
- [ ] 利用者の MCP 登録を外しても、朝の実行は `--mcp-config` のおかげで MoTask のツールを使える

## 12. 移行

実装をマージした時点で未完了の朝の実行が 1 件残っていたら、画面の「追跡をやめる」で片づける。
旧契約で走っている claude を新契約に繋ぎ直す自動処理は作らない。
既存の `result/` 由来の候補とプランは DB にあるので、そのまま読める。

過去のジョブフォルダに残っている `result/` と `board.json` は消さない。放っておく。

## 13. 実装計画の分割

2 本に分ける。1 本目だけでも「端末が自分で閉じる」という当初の目的は達成される。

**1 本目 — 起動の統一と端末の所有**（§5・§7・§11 の端末まわり）

- `TerminalLauncher` のテンプレート統一（`wt.exe` 分岐の削除）
- `ISessionLauncher` に `LaunchOwned` / `CloseOwned` / `TryReattach` / `OwnedSessionExited` を追加
- `MorningService` が朝の実行を所有し、`run.json` に `processId` / `processStartedAt` を書く
- 閉じる条件は**現行のファイル契約のまま**。`Stop` で `result/` が揃って `Ingested` になったら閉じる
- 端末が先に死んだときの `Failed`、`SessionEnd` の後始末、再起動後の掛け直し

この時点で `result/` の契約はまだ生きているので、MCP を触らずに端末が閉じるようになる。

**2 本目 — MCP への移行**（§6・§8・§9）

- `MorningToolHost` とツール 4 本
- `MorningResultReader` を `CandidateValidator` / `MorningPlanValidator` に解体
- `MorningService` に `GetContextAsync` / `AddCandidateAsync` / `SubmitPlanAsync` を追加し、
  `Stop` での `result/` 読みを撤去
- `instruction.md` の契約差し替え、`--mcp-config` の生成と受け渡し
- `result/` と `board.json` の生成をやめる
- 閉じる契機を「`Ingested` になったら」から「`morning_complete` の予約 ＋ 次の `Stop`」に移す

1 本目の完了条件に「`AiJob*` 一式とそのテストが 1 行も壊れないこと」を入れる。
2 本目の完了条件に「`MorningPlanResolver` とそのテストが 1 行も壊れないこと」を入れる。
