using System.Windows.Controls;

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
}
