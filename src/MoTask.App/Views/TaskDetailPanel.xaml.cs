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

    /// <summary>Enter で新規プロジェクトを作成。Esc は入力を空に戻して取り消す（他のインライン編集と同じ挙動）。</summary>
    private void NewProjectBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { Vm?.CreateProjectCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { box.Text = ""; Keyboard.ClearFocus(); e.Handled = true; }
    }

    /// <summary>Enter で新規ラベルを作成。Esc は入力を空に戻して取り消す（他のインライン編集と同じ挙動）。</summary>
    private void NewLabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { Vm?.CreateLabelCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { box.Text = ""; Keyboard.ClearFocus(); e.Handled = true; }
    }
}
