using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        DarkWindowChrome.Apply(this);
    }

    /// <summary>async void なので、例外が漏れるとプロセスごと落ちる。必ずバナーへ回す。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadAsync();
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
    }

    /// <summary>仕様 §6 キーボード: N=新規、Delete=論理削除、Esc=詳細を閉じる、Ctrl+F=検索。文字入力中は N/Delete を奪わない。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 朝の画面を表示している間は N（新規）と Delete（削除）をボードへ渡さない。
        // さもないと候補の仕分け中に Delete を押しただけでボードのタスクが消える。
        if (MorningHost.Visibility == Visibility.Visible && e.Key is Key.N or Key.Delete) return;

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
}
