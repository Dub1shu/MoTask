using System.Windows.Controls;
using System.Windows.Input;

namespace MoTask.App.Views;

public partial class FilterBar : UserControl
{
    public FilterBar()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ポップアップは外側の押下でしか閉じないので、Esc はここで拾う。
    // フォーカスはボタンにあるときとポップアップ内のチップにあるときがあり、両方から呼ばれる。
    private void LabelPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || LabelToggle.IsChecked != true) return;
        LabelToggle.IsChecked = false;
        LabelToggle.Focus();
        e.Handled = true;
    }
}
