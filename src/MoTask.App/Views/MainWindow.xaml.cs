using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;

namespace MoTask.App.Views;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _vm;
    private readonly PlanViewModel _plan;
    private readonly ArchiveViewModel _archive;
    private readonly IAiSettingsStore _settings;

    /// <summary>どの画面を出しているか。タブの下線と中身の表示を、この 1 つから決める。</summary>
    private enum View { Board, Plan, Archive }

    public MainWindow(BoardViewModel vm, PlanViewModel plan, ArchiveViewModel archive, IAiSettingsStore settings)
    {
        _vm = vm;
        _plan = plan;
        _archive = archive;
        _settings = settings;
        DataContext = vm;
        InitializeComponent();
        PlanHost.DataContext = plan;
        ArchiveHost.DataContext = archive;
        plan.NavigateToTask += OnNavigateToTask;
        // 起動直後はボード表示。タブの選択状態もそれに合わせておく(切り替えと同じコード経路で決める)。
        ShowView(View.Board);
    }

    /// <summary>async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadAsync();
            // タブのバッジを起動直後から出す（仕様 §6）。タブを押したときも LoadAsync で読み直す。
            await _plan.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    private void OnManageProjectsClick(object sender, RoutedEventArgs e) => ShowManageDialog(ClassificationKind.Project);

    private void OnManageLabelsClick(object sender, RoutedEventArgs e) => ShowManageDialog(ClassificationKind.Label);

    /// <summary>
    /// プロジェクト設定／ラベル設定のダイアログを開く。アーカイブに伴うフィルタと詳細パネルの
    /// 更新は BoardViewModel 側で完結しているので、ここは開いて閉じるだけ。
    /// </summary>
    private void ShowManageDialog(ClassificationKind kind)
    {
        var dialog = new ManageClassificationsDialog
        {
            Owner = this,
            DataContext = new ManageClassificationsViewModel(_vm, kind, _settings.Load().DefaultWorkingDirectory),
        };
        dialog.ShowDialog();
    }

    /// <summary>AI 設定ダイアログ。保存は VM 内で完結するので、ここは開いて閉じるだけ。</summary>
    private void OnAiSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsDialog
        {
            Owner = this,
            DataContext = new AiSettingsViewModel(_settings),
        };
        dialog.ShowDialog();
    }

    private void OnShowBoardClick(object sender, RoutedEventArgs e) => ShowView(View.Board);

    /// <summary>
    /// 計画の「ボードで開く」／タスク行のクリック。ボードへ切り替えてそのタスクを選ぶ。
    /// async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。
    /// </summary>
    private async void OnNavigateToTask(object? sender, int taskId)
    {
        ShowView(View.Board);
        try
        {
            await _vm.SelectTaskAsync(taskId);
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    /// <summary>async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。</summary>
    private async void OnShowPlanClick(object sender, RoutedEventArgs e)
    {
        ShowView(View.Plan);
        try
        {
            await _plan.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    /// <summary>
    /// 開くたびに読み直す（ボードで完了にした分や、週が替わった分を拾う）。
    /// async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。
    /// </summary>
    private async void OnShowArchiveClick(object sender, RoutedEventArgs e)
    {
        ShowView(View.Archive);
        try
        {
            await _archive.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    private void ShowView(View view)
    {
        var board = view == View.Board;
        BoardHost.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        PlanHost.Visibility = view == View.Plan ? Visibility.Visible : Visibility.Collapsed;
        ArchiveHost.Visibility = view == View.Archive ? Visibility.Visible : Visibility.Collapsed;
        SetActiveTab(view);
    }

    /// <summary>
    /// どのタブが今の画面かを示す。下線と文字色は Btn.Tab スタイルが持ち、ここでは
    /// 選択中かどうかだけを Tag で渡す(色を局所値で当てるとホバーのトリガが効かない)。
    /// </summary>
    private void SetActiveTab(View view)
    {
        SetActiveTab(BoardTabButton, view == View.Board);
        SetActiveTab(PlanTabButton, view == View.Plan);
        SetActiveTab(ArchiveTabButton, view == View.Archive);
    }

    private static void SetActiveTab(Button tab, bool active)
    {
        tab.Tag = active ? "Active" : null;
    }

    /// <summary>
    /// 仕様 §6 キーボード: N=新規、Delete=論理削除、Esc=詳細を閉じる、Ctrl+F=検索。文字入力中は奪わない。
    /// 計画の画面では T/E/X/L（仕分け）だけを受け、ボードのキーは渡さない（候補の仕分け中に Delete を
    /// 押しただけでボードのタスクが消えないように）。アーカイブの画面ではボードのキーを受けない。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBar.FocusSearch();
            e.Handled = true;
            return;
        }

        // 非編集の ComboBox も文字キーで項目を選ぶ。選択中に Esc を奪わないよう、
        // IsEditable を問わず ComboBox は「入力中」として扱う。
        var typing = Keyboard.FocusedElement is TextBoxBase or ComboBox or DatePicker;
        if (typing) return; // インライン編集中の Enter/Esc は各入力欄が処理する

        if (PlanHost.Visibility == Visibility.Visible)
        {
            // OS のキーリピートで押しっぱなしのまま多重実行しないよう、リピートは無視する。
            if (!e.IsRepeat
                && _plan.LeftPanel is TriagePanelViewModel triage
                && TriageKeyMap.Resolve(e.Key, Keyboard.Modifiers) is { } action)
            {
                e.Handled = true;
                _ = RunTriageKeyAsync(triage, action);
            }
            return;
        }

        // アーカイブは見るだけ。見えていないボードで選ばれているカードが Delete で消えたり、
        // N で新しいタスクが作られたりしないよう、ボードのキーは渡さない。
        if (ArchiveHost.Visibility == Visibility.Visible) return;

        switch (e.Key)
        {
            case Key.N when Keyboard.Modifiers == ModifierKeys.None:
                _vm.NewTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                _vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.CloseDetailCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>誰も待たない Task なので、例外はここで捕まえてバナーへ回す（OnLoaded と同じ考え方）。</summary>
    private async Task RunTriageKeyAsync(TriagePanelViewModel triage, TriageKeyAction action)
    {
        try
        {
            await triage.RunAsync(action);
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }
}
