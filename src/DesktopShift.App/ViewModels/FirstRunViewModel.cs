using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopShift.App.ViewModels;

public sealed class FirstRunViewModel : INotifyPropertyChanged
{
    private bool _startWithWindows;
    private string _validationSummary = string.Empty;

    public FirstRunViewModel(
        IEnumerable<FirstRunMappingViewModel> mappings,
        bool startWithWindows,
        CompatibilityPresentation compatibility)
    {
        Mappings = new ObservableCollection<FirstRunMappingViewModel>(mappings);
        _startWithWindows = startWithWindows;
        Compatibility = compatibility;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FirstRunMappingViewModel> Mappings { get; }

    public CompatibilityPresentation Compatibility { get; }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetField(ref _startWithWindows, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        set
        {
            if (SetField(ref _validationSummary, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasValidationIssues)));
            }
        }
    }

    public bool HasValidationIssues => !string.IsNullOrWhiteSpace(ValidationSummary);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
