using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Data;

namespace PatientIntakeApp.ViewModels;

public class TransferUserOption
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
}

public partial class TransferUserDialogViewModel : ObservableObject
{
    private readonly Action<Guid?> _close;

    [ObservableProperty]
    private string _title = "Transfer referral";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TransferUserOption> _users = new();

    [ObservableProperty]
    private ICollectionView? _usersView;

    [ObservableProperty]
    private TransferUserOption? _selectedUser;

    public TransferUserDialogViewModel(string title, IEnumerable<TransferUserOption> users, Action<Guid?> close)
    {
        _close = close;
        Title = title;
        Users = new ObservableCollection<TransferUserOption>(users ?? Array.Empty<TransferUserOption>());
        UsersView = CollectionViewSource.GetDefaultView(Users);
        UsersView.Filter = Filter;
    }

    private bool Filter(object obj)
    {
        if (obj is not TransferUserOption u) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return u.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               u.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        UsersView?.Refresh();
    }

    [RelayCommand]
    private void Cancel() => _close(null);

    [RelayCommand]
    private void Transfer()
    {
        _close(SelectedUser?.UserId);
    }

    [RelayCommand]
    private void SelectAndTransfer(TransferUserOption user)
    {
        SelectedUser = user;
        _close(SelectedUser?.UserId);
    }
}

