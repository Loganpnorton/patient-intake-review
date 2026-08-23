using System.Windows.Controls;
using System.Windows.Input;
using PatientIntakeApp.Models;
using PatientIntakeApp.ViewModels;

namespace PatientIntakeApp.Views;

public partial class ReviewView : UserControl
{
    public ReviewView()
    {
        InitializeComponent();
    }

    private void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is Finding finding)
        {
            if (DataContext is ReviewViewModel vm)
            {
                if (vm.JumpToFindingCommand.CanExecute(finding))
                {
                    vm.JumpToFindingCommand.Execute(finding);
                }
            }
        }
    }
}


