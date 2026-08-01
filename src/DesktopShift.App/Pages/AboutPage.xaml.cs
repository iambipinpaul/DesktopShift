using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionSummary.Text =
            $"Version {version?.ToString(3) ?? "unknown"} • WinUI 3";
        RuntimeValue.Text = RuntimeInformation.FrameworkDescription;
        ArchitectureValue.Text = RuntimeInformation.ProcessArchitecture.ToString();
        WindowsBuildValue.Text = Environment.OSVersion.Version.Build.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
    }
}
