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

            // 前回閉じたあとも端末は走り続けている。未完了ジョブの events.jsonl に追いつく（仕様 §8）
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
