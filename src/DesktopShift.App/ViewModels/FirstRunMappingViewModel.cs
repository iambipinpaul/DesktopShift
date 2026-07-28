using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopShift.App.ViewModels;

public sealed class FirstRunMappingViewModel : INotifyPropertyChanged
{
    private string _desktopName;
    private string _executableNames;
    private bool _isEnabled;

    public FirstRunMappingViewModel(
        string semanticKey,
        string ruleId,
        string ruleName,
        string desktopName,
        string executableNames,
        bool isEnabled)
    {
        SemanticKey = semanticKey;
        RuleId = ruleId;
        RuleName = ruleName;
        _desktopName = desktopName;
        _executableNames = executableNames;
        _isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SemanticKey { get; }

    public string RuleId { get; }

    public string RuleName { get; }

    public string DesktopName
    {
        get => _desktopName;
        set => SetField(ref _desktopName, value);
    }

    public string ExecutableNames
    {
        get => _executableNames;
        set => SetField(ref _executableNames, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
