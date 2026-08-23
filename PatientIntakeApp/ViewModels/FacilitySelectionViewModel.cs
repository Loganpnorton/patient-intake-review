using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;

namespace PatientIntakeApp.ViewModels;

public partial class FacilitySelectionViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IConfigurationService _configService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredFacilities))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private List<Facility> _facilities;

    public IEnumerable<Facility> FilteredFacilities => 
        string.IsNullOrWhiteSpace(SearchText) 
            ? Facilities 
            : Facilities.Where(f => f.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase)).ToList();

    [ObservableProperty]
    private Facility? _selectedFacility;

    public FacilitySelectionViewModel(MainViewModel mainViewModel, IConfigurationService configService)
    {
        _mainViewModel = mainViewModel;
        _configService = configService;
        _facilities = _configService.GetFacilities();

        var lastId = _configService.GetLastSelectedFacilityId();
        if (!string.IsNullOrEmpty(lastId))
        {
            SelectedFacility = _facilities.FirstOrDefault(f => f.Id == lastId);
        }
    }

    partial void OnSelectedFacilityChanged(Facility? value)
    {
        if (value != null)
        {
            // Optional: Clear search text on selection if desired
            // SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void Continue()
    {
        if (SelectedFacility != null)
        {
            _configService.SetLastSelectedFacilityId(SelectedFacility.Id);
            _mainViewModel.SelectedFacility = SelectedFacility;
            _mainViewModel.NavigateToIngestion();
        }
    }
}


