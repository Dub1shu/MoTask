using System.Windows;
using System.Windows.Input;
using MoTask.App.Themes;

namespace MoTask.App.Views;

/// <summary>プロジェクトとラベルの管理ダイアログ。表示だけを持ち、操作は ViewModel 側にある。</summary>
public partial class ManageClassificationsDialog : Window
{
    public ManageClassificationsDialog()
    {
        InitializeComponent();
        DarkWindowChrome.Apply(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>他のインライン編集と同じく Esc で閉じる。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
