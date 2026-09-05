using System.Text;

namespace MoTask.App.Ai;

/// <summary>
/// Windows のコマンドライン 1 本を組み立て／分解する。起動テンプレートが人の書いた 1 行なので、
/// ProcessStartInfo.ArgumentList には載せられず、自分で引用符を付ける必要がある。
/// </summary>
public static class CommandLine
{
    /// <summary>CommandLineToArgvW の規則に沿って 1 要素を引用する。</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 8).Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                // " の直前のバックスラッシュは 2 倍にしてから \" を置く
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        // 閉じ " の直前のバックスラッシュも 2 倍にする
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>1 行のコマンドラインを（実行ファイル, 残りの引数）に割る。引用符付きの実行ファイルも扱う。</summary>
    public static (string FileName, string Arguments) SplitFirstToken(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0) return ("", "");

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return (text[1..], "");
            return (text[1..close], text[(close + 1)..].TrimStart());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].TrimStart());
    }
}
