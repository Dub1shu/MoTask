using System.Text;
using System.Text.RegularExpressions;

// Claude Code のフック。stdin に来る 1 件の JSON を 1 行に畳んで、引数のファイルへ追記する。
// 中身は解釈しない（CLI がキーを足しても壊れない）。標準出力には何も書かない
// （フックの stdout は CLI に解釈されうる）。

if (args.Length < 1) return 1;
var target = args[0];

// Console.In は OEM コードページで開かれることがある。日本語を壊さないよう UTF-8 を明示する。
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var payload = input.ReadToEnd();

// 改行を単一の半角空白に畳んで 1 行にする（改行直後のインデントも一緒に畳む）。
// ペイロード内の改行なしの連続空白（JSON 文字列の空白など）は保持したままにする。
// （events.jsonl は「1 行 = 1 イベント」が唯一の約束）。
var line = Regex.Replace(payload, @"[\r\n]+[ \t]*", " ").Trim();
if (line.Length == 0) return 0;

var bytes = new UTF8Encoding(false).GetBytes(line + "\n");

// 4 種のフックが同時に走りうる。追記は排他で取り、取れなければ少し待って諦めずに再試行する。
// ここで書き損ねるとイベントが 1 件消えるだけで、CLI 側の作業は続く（終了コードは常に 0）。
for (var attempt = 0; attempt < 50; attempt++)
{
    try
    {
        using var stream = new FileStream(
            target, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        stream.Write(bytes);
        return 0;
    }
    catch (IOException)
    {
        Thread.Sleep(20);
    }
    catch (UnauthorizedAccessException)
    {
        Thread.Sleep(20);
    }
}
return 0;
