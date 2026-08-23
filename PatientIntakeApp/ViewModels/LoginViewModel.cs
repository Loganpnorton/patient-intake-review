using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;

namespace PatientIntakeApp.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IAuthService _authService;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public LoginViewModel(MainViewModel mainViewModel, IAuthService authService)
    {
        _mainViewModel = mainViewModel;
        _authService = authService;
    }

    [RelayCommand]
    private async Task Login()
    {
        ErrorMessage = string.Empty;

        try
        {
            var user = await _authService.LoginAsync(Username, Password);
            if (user == null)
            {
                ErrorMessage = "Invalid username or password.";
                return;
            }

            _mainViewModel.CurrentUser = user;
            _mainViewModel.NavigateToDashboard();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login failed: {ex.Message}";
        }
    }
}


