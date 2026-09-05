using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoTask.App.Ai;
using MoTask.App.Resources;
using MoTask.App.Views;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Services;
using MoTask.Data;

namespace MoTask.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

            // 承認 MCP サーバは 1 つだけ。ジョブ開始前に立っていればよい。
            _host.Services.GetRequiredService<ApprovalMcpServer>().Start();
            // 前回クラッシュして Running のまま残ったジョブは、プロセスが無いので Suspended に戻す
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

    private static IHost BuildHost(string dbPath)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMoTaskData(dbPath); // OperationGate / リポジトリ / 設定ストアもここで登録される
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IBoardService, BoardService>();
        builder.Services.AddSingleton<IPermissionPolicy, PermissionPolicy>();
        builder.Services.AddSingleton<IPermissionPrompt, WpfPermissionPrompt>();
        builder.Services.AddSingleton<ApprovalMcpServer>();
        builder.Services.AddSingleton<IAgentRunner, ClaudeCodeRunner>();
        builder.Services.AddSingleton<IAiJobService, AiJobService>();
        builder.Services.AddSingleton<ViewModels.BoardViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    /// <summary>仕様 §8: 開けない／壊れている DB はバックアップしてから再作成するか、終了するかを尋ねる。</summary>
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
        _host?.Dispose();
        base.OnExit(e);
    }
}
