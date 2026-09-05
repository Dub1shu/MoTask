using System.Windows;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

/// <summary>承認ダイアログ。判断は ViewModel が持ち、ここは VM の Closed で閉じるだけ。× は「拒否・記憶しない」（VM の Decision が null のまま）。</summary>
public partial class PermissionDialog : Window
{
    public PermissionDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PermissionDialogViewModel old) old.Closed -= Close;
            if (e.NewValue is PermissionDialogViewModel vm) vm.Closed += Close;
        };
    }
}
