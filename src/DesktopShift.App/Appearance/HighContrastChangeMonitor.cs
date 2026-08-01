using System.Runtime.InteropServices;

namespace DesktopShift.App.Appearance;

/// <summary>
/// Reads Windows High Contrast state and treats live change notifications as
/// optional when the current Windows shell cannot provide the event source.
/// </summary>
public sealed class HighContrastChangeMonitor : IDisposable
{
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);

    private readonly Func<bool> _readState;
    private readonly Action _unsubscribe;
    private int _disposed;

    public HighContrastChangeMonitor(
        Func<bool> readState,
        Action subscribe,
        Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        _readState = readState;
        _unsubscribe = unsubscribe;

        try
        {
            subscribe();
            IsChangeNotificationAvailable = true;
        }
        catch (COMException exception)
            when (exception.HResult == ElementNotFoundHResult)
        {
            // Some supported desktop sessions expose AccessibilitySettings but
            // no backing HighContrastChanged event source. Theme resources still
            // respond to Windows; only the optional fixed accent loses live
            // notification until the next launch.
        }
    }

    public bool IsChangeNotificationAvailable { get; }

    /// <summary>
    /// Returns true when Windows reports High Contrast, or when the state cannot
    /// be read safely. Suppressing a product accent is the accessible fallback.
    /// </summary>
    public bool IsHighContrast
    {
        get
        {
            try
            {
                return _readState();
            }
            catch (COMException exception)
                when (exception.HResult == ElementNotFoundHResult)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 ||
            !IsChangeNotificationAvailable)
        {
            return;
        }

        try
        {
            _unsubscribe();
        }
        catch (COMException exception)
            when (exception.HResult == ElementNotFoundHResult)
        {
            // The event source can disappear during shell teardown. There is
            // nothing left registered for this process to release in that case.
        }
    }
}
