using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class BoardView : UserControl
{
    public BoardView()
    {
        InitializeComponent();
    }

    private void NewColumnEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BoardViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitAddColumnCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelAddColumnCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => FocusWhenVisible(sender, e);

    /// <summary>インライン入力欄が表示されたらフォーカスを移す。ColumnView からも使う。</summary>
    internal static void FocusWhenVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }
}
