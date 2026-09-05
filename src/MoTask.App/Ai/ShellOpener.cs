using System.ComponentModel;
using System.Diagnostics;

namespace MoTask.App.Ai;

/// <summary>成果物を既定のアプリで、作業フォルダをエクスプローラで開く。</summary>
public static class ShellOpener
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            // 関連付けが無い・ファイルが消えている。UI 側でバナーにする値は返さない（成果物一覧は再読み込みで消える）。
        }
    }
}
