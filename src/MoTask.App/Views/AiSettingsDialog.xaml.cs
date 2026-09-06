using System.Windows;
using System.Windows.Input;
using MoTask.App.Themes;

namespace MoTask.App.Views;

/// <summary>AI 設定ダイアログ。保存は VM のコマンド、閉じるだけをここで持つ（管理ダイアログと同じ作法）。</summary>
public partial class AiSettingsDialog : Window
{
    public AiSettingsDialog()
    {
        InitializeComponent();
        DarkWindowChrome.Apply(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
