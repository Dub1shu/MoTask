using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MoTask.App.Resources;
using MoTask.App.Themes;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;

namespace MoTask.App.Views;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _vm;
    private readonly MorningPlanViewModel _morning;
    private readonly IAiSettingsStore _settings;

    public MainWindow(BoardViewModel vm, MorningPlanViewModel morning, IAiSettingsStore settings)
    {
        _vm = vm;
        _morning = morning;
        _settings = settings;
        DataContext = vm;
        InitializeComponent();
        MorningHost.DataContext = morning;
        morning.NavigateToTask += OnNavigateToTask;
        DarkWindowChrome.Apply(this);
        // 起動直後はボード表示。タブの見た目もそれに合わせておく(仕様に無い見た目調整なので、
        // 専用のスタイル/コンバーターは足さず、切り替えと同じコード経路で決める)。
        SetActiveTab(board: true);
    }

    /// <summary>async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadAsync();
            // タブのバッジを起動直後から出す（仕様 §6）。タブを押したときも LoadAsync で読み直す。
            await _morning.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    /// <summary>
    /// プロジェクトとラベルの管理ダイアログを開く。アーカイブに伴うフィルタと詳細パネルの
    /// 更新は BoardViewModel 側で完結しているので、ここは開いて閉じるだけ。
    /// </summary>
    private void OnManageClassificationsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageClassificationsDialog
        {
            Owner = this,
            DataContext = new ManageClassificationsViewModel(_vm),
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

    private void OnShowBoardClick(object sender, RoutedEventArgs e) => ShowBoard(true);

    /// <summary>朝のプランの「ボードで開く」／タスク行のクリック。ボードへ切り替えてそのタスクを選ぶ。</summary>
    private void OnNavigateToTask(object? sender, int taskId)
    {
        ShowBoard(true);
        _vm.SelectTask(taskId);
    }

    /// <summary>async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。</summary>
    private async void OnShowMorningClick(object sender, RoutedEventArgs e)
    {
        ShowBoard(false);
        try
        {
            await _morning.LoadAsync();
        }
        catch (Exception ex)
        {
            _vm.ShowBanner(string.Format(CultureInfo.CurrentCulture, Strings.StartupFailedFormat, ex.Message));
        }
    }

    private void ShowBoard(bool board)
    {
        BoardHost.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        MorningHost.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
        SetActiveTab(board);
    }

    /// <summary>
    /// どちらのタブが今の画面かを、下線と文字色で示す(この 2 ボタンだけの見た目調整なので、
    /// 新しいスタイルやコンバーターは足さず、既存の Brush.Accent / Brush.TextMuted を
    /// コードから直接当てる)。
    /// </summary>
    private void SetActiveTab(bool board)
    {
        SetActiveTab(BoardTabButton, board);
        SetActiveTab(MorningTabButton, !board);
    }

    private static void SetActiveTab(Button tab, bool active)
    {
        tab.BorderThickness = active ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        tab.BorderBrush = active ? (Brush)tab.FindResource("Brush.Accent") : Brushes.Transparent;
        tab.Foreground = (Brush)tab.FindResource(active ? "Brush.Accent" : "Brush.TextMuted");
    }

    /// <summary>
    /// 仕様 §6 キーボード: N=新規、Delete=論理削除、Esc=詳細を閉じる、Ctrl+F=検索。文字入力中は奪わない。
    /// 朝の画面では T/E/X/L（仕分け）だけを受け、ボードのキーは渡さない（候補の仕分け中に Delete を
    /// 押しただけでボードのタスクが消えないように）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBar.FocusSearch();
            e.Handled = true;
            return;
        }

        // 非編集の ComboBox も文字キーで項目を選ぶ。列追加の種別選択で Esc を奪わないよう、
        // IsEditable を問わず ComboBox は「入力中」として扱う。
        var typing = Keyboard.FocusedElement is TextBoxBase or ComboBox or DatePicker;
        if (typing) return; // インライン編集中の Enter/Esc は各入力欄が処理する

        if (MorningHost.Visibility == Visibility.Visible)
        {
            // OS のキーリピートで押しっぱなしのまま多重実行しないよう、リピートは無視する。
            if (!e.IsRepeat
                && _morning.LeftPanel is TriagePanelViewModel triage
                && MorningKeyMap.Resolve(e.Key, Keyboard.Modifiers) is { } action)
            {
                e.Handled = true;
                _ = RunTriageKeyAsync(triage, action);
            }
            return;
        }

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
