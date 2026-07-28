namespace DesktopShift.Core;

/// <summary>
/// Stable product identifiers shared by every DesktopShift process.
/// </summary>
public static class ProductInfo
{
    public const string ApplicationName = "DesktopShift";

    public const string ApplicationId = "BipinPaul.DesktopShift";

    /// <summary>
    /// Identifies the one application instance that owns activation handling.
    /// </summary>
    public const string SingleInstanceKey = "DesktopShift.PrimaryInstance";
}
