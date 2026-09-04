using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoTask.App.DragDrop;
using MoTask.App.ViewModels;
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;

namespace MoTask.App.Views;

public partial class ColumnView : UserControl
{
    public ColumnView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachDragDrop();
        Loaded += (_, _) => AttachDragDrop();
    }

    private ColumnViewModel? Vm => DataContext as ColumnViewModel;

    /// <summary>
    /// D&amp;D のハンドラはここで挿す。ViewModel に持たせると ViewModels が
    /// gong-wpf-dragdrop に依存してしまう（依存は DragDrop → ViewModels の一方通行）。
    /// </summary>
    private void AttachDragDrop()
    {
        if (Vm is not { } vm) return;
        GongDragDrop.SetDropHandler(CardList, new CardDropHandler(vm.Board));
        GongDragDrop.SetDragHandler(Header, new ColumnDragHandler());
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitAddTaskCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelAddTaskCommand.Execute(null); e.Handled = true; }
    }

    private void NewTaskBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 空のまま離れたら閉じる。入力途中なら残す（Enter で確定、Esc で取り消し）
        if (Vm is { IsAddingTask: true } vm && string.IsNullOrWhiteSpace(vm.NewTaskTitle)) vm.CancelAddTaskCommand.Execute(null);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelRenameCommand.Execute(null); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => Vm?.CommitRenameCommand.Execute(null);

    private void WipBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CommitWipCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelEditWipCommand.Execute(null); e.Handled = true; }
    }

    private void WipBox_LostFocus(object sender, RoutedEventArgs e) => Vm?.CommitWipCommand.Execute(null);

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => BoardView.FocusWhenVisible(sender, e);
}
