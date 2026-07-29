using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopShift.App.ViewModels;

/// <param name="destinationSummary">
/// Where the rule sends what it claims, in words. It is the only thing that
/// distinguishes a rule bound for a Managed Desktop from one that is exempt from
/// placement, because an Anywhere rule has no destination to show in the editable
/// desktop field.
/// </param>
/// <param name="identitySummary">
/// Every identity the rule declares, not only its process names. Three of the
/// shipped rules are identified by a package family name or a full executable
/// path and carry no process name at all, so a row showing process names alone
/// reads as though the rule were empty.
/// </param>
/// <param name="allowsAnywhere">
/// Whether the rule exempts its windows from placement. The desktop name field is
/// meaningless on such a rule — nothing writes it back — so it is not offered.
/// </param>
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
        bool isEnabled,
        string destinationSummary = "",
        string identitySummary = "",
        bool allowsAnywhere = false)
    {
        SemanticKey = semanticKey;
        RuleId = ruleId;
        RuleName = ruleName;
        _desktopName = desktopName;
        _executableNames = executableNames;
        _isEnabled = isEnabled;
        DestinationSummary = destinationSummary;
        IdentitySummary = identitySummary;
        AllowsAnywhere = allowsAnywhere;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SemanticKey { get; }

    public string RuleId { get; }

    public string RuleName { get; }

    public string DestinationSummary { get; }

    public string IdentitySummary { get; }

    public bool AllowsAnywhere { get; }

    /// <summary>
    /// Whether the editable Managed Desktop name is worth offering.
    /// </summary>
    public bool IsDesktopNameEditable => !AllowsAnywhere;

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
