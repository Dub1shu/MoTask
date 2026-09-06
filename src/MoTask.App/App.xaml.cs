using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoTask.App.Ai;
using MoTask.App.Resources;
using MoTask.App.Views;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Services;
using MoTask.Data;

namespace MoTask.App;

public partial class App : Application
{
    private IHost? _host;
    /// <summary>多重起動を防ぐ Mutex。取れたインスタンスだけが保持し、終了時に手放す（MCP 仕様 §5.3）。</summary>
    private Mutex? _instanceLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2 つ目のインスタンスは何も出さずに終わる（MCP 仕様 §5.3）。ブリッジからの自動起動が
        // 起動済みのアプリを二重に立ち上げても、SQLite の書き手は 1 つのままになる。
        _instanceLock = SingleInstance.TryAcquire(SingleInstance.MutexName);
        if (_instanceLock is null)
        {
            Shutdown();
            return;
        }

        // async void なので例外が漏れるとプロセスごと落ちる。ここで受け止めて必ず通常終了させる。
        try
        {
            var dbPath = DbPaths.DefaultDatabase;
            Directory.CreateDirectory(DbPaths.DefaultDirectory);

            _host = BuildHost(dbPath);
            if (!await TryInitializeDatabaseAsync(dbPath))
            {
                Shutdown();
                return;
            }

            // MCP サーバはアプリに 1 つだけ。外部セッションが繋ぎに来る前に立っていればよい。
            var mcp = _host.Services.GetRequiredService<MoTaskMcpServer>();
            mcp.Start();
            // 外部セッション（MoTask.Mcp ブリッジ）が繋ぎに来る先を置く（MCP 仕様 §5）。
            // Start の後でなければ McpUrl / BoardToken は取れない。
            McpEndpointFile.Write(AppPaths.EndpointFile,
                new McpEndpoint(mcp.McpUrl!.ToString(), mcp.BoardToken, Environment.ProcessId));

            // 前回閉じたあとも端末は走り続けている。未完了ジョブの events.jsonl に追いつく（ターミナル AI 仕様 §8）
            await _host.Services.GetRequiredService<IAiJobService>().RecoverOnStartupAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            // 例外メッセージはフレームワーク由来で英語のことが多い。日本語の前置きを付けて出す。
            MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message),
                Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// BoardService と AiJobService が同じ OperationGate を受け取ることがこのアプリの並行性設計の前提。
    /// その配線をテストから確かめられるように internal で公開する（テストは MoTask.App.Tests のみ）。
    /// </summary>
    internal static IHost BuildHost(string dbPath)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMoTaskData(dbPath); // OperationGate / リポジトリ / 設定ストアもここで登録される
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IBoardService, BoardService>();
        // 端末で claude を起こす／ジョブフォルダを作る／events.jsonl を追う
        builder.Services.AddSingleton<ISessionLauncher, TerminalLauncher>();
        builder.Services.AddSingleton<IJobFolder, JobFolder>();
        builder.Services.AddSingleton<JobEventWatcher>();
        builder.Services.AddSingleton<IJobEventSource>(sp => sp.GetRequiredService<JobEventWatcher>());
        // MCP の board ツール。BoardToolHost 自身が外部変更の通知元なので、同じインスタンスを両方に配る。
        builder.Services.AddSingleton<Ai.BoardTools.BoardToolHost>();
        builder.Services.AddSingleton<IBoardChangeSource>(sp => sp.GetRequiredService<Ai.BoardTools.BoardToolHost>());
        builder.Services.AddSingleton<MoTaskMcpServer>();
        builder.Services.AddSingleton<IAiJobService, AiJobService>();
        builder.Services.AddSingleton<ViewModels.BoardViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    /// <summary>カンバン v1 仕様 §8: 開けない／壊れている DB はバックアップしてから再作成するか、終了するかを尋ねる。</summary>
    private async Task<bool> TryInitializeDatabaseAsync(string dbPath)
    {
        try
        {
            await _host!.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            var answer = MessageBox.Show(
                string.Format(Strings.DbOpenFailedFormat, ex.Message), Strings.AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer != MessageBoxResult.Yes) return false;
        }

        // DatabaseRecovery は起動時専用。壊れた DbContext を先に破棄してから呼ぶ。
        _host!.Dispose();
        try
        {
            var backup = DatabaseRecovery.BackupAndReset(dbPath, DateTime.Now);
            _host = BuildHost(dbPath);
            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            MessageBox.Show(string.Format(Strings.DbRecreatedFormat, backup), Strings.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.DbRecreateFailedFormat, ex.Message), Strings.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Mutex を取れずに終わった 2 つ目のインスタンスは、1 つ目の endpoint.json を消してはいけない。
        if (_instanceLock is not null) McpEndpointFile.Delete(AppPaths.EndpointFile);
        _host?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
