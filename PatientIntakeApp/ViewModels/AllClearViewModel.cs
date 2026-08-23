using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PatientIntakeApp.ViewModels;

public partial class AllClearViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;

    public AllClearViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    [RelayCommand]
    private void NextPatient()
    {
        _mainViewModel.NavigateToIngestion();
    }
}


