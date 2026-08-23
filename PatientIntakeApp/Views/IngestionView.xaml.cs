using System.Windows;
using System.Windows.Controls;
using PatientIntakeApp.ViewModels;

namespace PatientIntakeApp.Views;

public partial class IngestionView : UserControl
{
    public IngestionView()
    {
        InitializeComponent();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (DataContext is IngestionViewModel vm)
            {
                vm.FilesDroppedCommand.Execute(files);
            }
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (DataContext is IngestionViewModel vm)
        {
            vm.SetDragState(true);
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (DataContext is IngestionViewModel vm)
        {
            vm.SetDragState(false);
        }
    }
}


