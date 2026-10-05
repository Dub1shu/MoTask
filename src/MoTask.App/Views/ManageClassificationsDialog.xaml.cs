using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MoTask.App.ViewModels;

namespace MoTask.App.Views;

/// <summary>プロジェクト設定／ラベル設定のダイアログ（どちらを出すかは ViewModel の種別で決まる）。キー操作とフォーカスの配線だけを持ち、操作は ViewModel 側にある。</summary>
public partial class ManageClassificationsDialog : Window
{
    public ManageClassificationsDialog()
    {
        InitializeComponent();
    }

    private ManageClassificationsViewModel Vm => (ManageClassificationsViewModel)DataContext;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 他のインライン編集と同じく Esc で閉じる。ただし入力欄にいるときの Esc は、その編集の取り消しなので閉じない
    /// （入力欄の KeyDown が取り消しを受け持つ）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Keyboard.FocusedElement is TextBox) return;
        e.Handled = true;
        Close();
    }

    private void OnNameClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClassificationRow row }) Vm.BeginRenameCommand.Execute(row);
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClassificationRow row }) return;
        if (e.Key == Key.Enter) { Vm.CommitRenameCommand.Execute(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelRenameCommand.Execute(row); e.Handled = true; }
    }

    /// <summary>フォーカスが外れたら確定する（Esc で取り消した後は IsEditing が false なので何もしない）。</summary>
    private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClassificationRow row }) Vm.CommitRenameCommand.Execute(row);
    }

    private void OnNewBoxKeyDown(object sender, KeyEventArgs e)
    {
        var isProject = (string)((FrameworkElement)sender).Tag == "Project";
        if (e.Key == Key.Enter)
        {
            if (isProject) Vm.CreateProjectCommand.Execute(null); else Vm.CreateLabelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (isProject) Vm.CancelAddProjectCommand.Execute(null); else Vm.CancelAddLabelCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>何も書かずにフォーカスが外れたら閉じる（詳細パネルの「＋」と同じ）。</summary>
    private void OnNewBoxLostFocus(object sender, RoutedEventArgs e)
    {
        var isProject = (string)((FrameworkElement)sender).Tag == "Project";
        if (isProject && Vm is { IsAddingProject: true } && string.IsNullOrWhiteSpace(Vm.NewProjectName))
            Vm.CancelAddProjectCommand.Execute(null);
        if (!isProject && Vm is { IsAddingLabel: true } && string.IsNullOrWhiteSpace(Vm.NewLabelName))
            Vm.CancelAddLabelCommand.Execute(null);
    }

    private void OnEditBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => BoardView.FocusWhenVisible(sender, e);

    /// <summary>OS のフォルダ選択を開き、選ばれたら保存する。今のフォルダがあればそこから開く。</summary>
    private void OnChooseWorkingDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClassificationRow row }) return;
        var dialog = new OpenFolderDialog { Title = row.Name };
        if (row.WorkingDirectory is { } current && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) Vm.SetWorkingDirectory(row, dialog.FolderName);
    }

    /// <summary>見本を選んだら色を変えてポップアップを閉じる。</summary>
    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaletteSwatch swatch }) return;
        Vm.SetColorCommand.Execute(swatch);
        // Popup の中身のビジュアルツリーの根（PopupRoot）の論理上の親が Popup。IsOpen は既定で TwoWay なので色丸の IsChecked も戻る
        DependencyObject d = (DependencyObject)sender;
        while (VisualTreeHelper.GetParent(d) is { } parent) d = parent;
        if (LogicalTreeHelper.GetParent(d) is Popup popup) popup.IsOpen = false;
    }
}
