using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

public partial class TaskDetailPanel : UserControl
{
    public TaskDetailPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            LabelChips.Collection = Vm?.Labels;
            AddLabelSlot.DataContext = Vm;
        };
    }

    private TaskDetailViewModel? Vm => DataContext as TaskDetailViewModel;

    /// <summary>Esc: 編集を取り消して VM の値に戻す。Enter（単一行）: フォーカスを移して確定。</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    /// <summary>Enter で新規プロジェクトを作成して閉じる。Esc は入力を捨てて閉じる。</summary>
    private void NewProjectBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CreateProjectCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelAddProjectCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>空のまま離れたら閉じる。入力途中なら残す（列の「＋ 追加」と同じ）。</summary>
    private void NewProjectBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Vm is { IsAddingProject: true } vm && string.IsNullOrWhiteSpace(vm.NewProjectName)) vm.CancelAddProjectCommand.Execute(null);
    }

    /// <summary>Enter で新規ラベルを作成して閉じる。Esc は入力を捨てて閉じる。</summary>
    private void NewLabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter) { Vm.CreateLabelCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelAddLabelCommand.Execute(null); e.Handled = true; }
    }

    private void NewLabelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Vm is { IsAddingLabel: true } vm && string.IsNullOrWhiteSpace(vm.NewLabelName)) vm.CancelAddLabelCommand.Execute(null);
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => BoardView.FocusWhenVisible(sender, e);
}
