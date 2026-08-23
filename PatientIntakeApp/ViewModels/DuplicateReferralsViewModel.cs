using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Data.Entities;
using System.Collections.ObjectModel;

namespace PatientIntakeApp.ViewModels;

public partial class DuplicateReferralsViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly Action _close;

    [ObservableProperty]
    private string _title = "Possible duplicates";

    [ObservableProperty]
    private ObservableCollection<ReferralEntity> _duplicates = new();

    public DuplicateReferralsViewModel(MainViewModel mainViewModel, IEnumerable<ReferralEntity> duplicates, Action close)
    {
        _mainViewModel = mainViewModel;
        _close = close;
        Duplicates = new ObservableCollection<ReferralEntity>(
            (duplicates ?? Array.Empty<ReferralEntity>())
                .OrderByDescending(d => d.IngestedAt)
                .ToList());
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private async Task Open(ReferralEntity referral)
    {
        if (referral == null) return;
        _close();
        await _mainViewModel.OpenReferralInNewWindowAsync(referral.Id);
    }
}

