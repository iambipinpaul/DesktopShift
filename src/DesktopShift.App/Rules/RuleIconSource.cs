using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using DesktopShift.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DesktopShift.App.Rules;

/// <summary>
/// Turns the pixels an <see cref="ApplicationIcon"/> carries into something XAML
/// can draw.
/// </summary>
/// <remarks>
/// This is the only place the application layer knows an icon is BGRA. Keeping
/// the conversion here is what lets the rule projection stay in the domain layer
/// with no XAML types in it, and lets it be tested without a renderer.
/// </remarks>
public static class RuleIconSource
{
    /// <summary>
    /// Creates a bitmap from an icon.
    /// </summary>
    /// <param name="icon">The icon, which may be absent.</param>
    /// <returns>
    /// The bitmap, or <see langword="null"/> when there is no icon or its pixels
    /// do not match its dimensions.
    /// </returns>
    public static ImageSource? Create(ApplicationIcon? icon)
    {
        if (icon is not { IsComplete: true })
        {
            return null;
        }

        WriteableBitmap bitmap = new(icon.Width, icon.Height);
        using Stream pixels = bitmap.PixelBuffer.AsStream();
        pixels.Write(icon.Pixels.AsSpan());
        return bitmap;
    }

    /// <summary>
    /// Whether the drawn icon should be shown.
    /// </summary>
    /// <param name="icon">The icon, which may be absent.</param>
    /// <returns>The icon's visibility.</returns>
    public static Visibility IconVisibility(ApplicationIcon? icon) =>
        icon is { IsComplete: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Whether the fallback glyph should be shown, so a rule without a readable
    /// icon still has something in its icon slot.
    /// </summary>
    /// <param name="icon">The icon, which may be absent.</param>
    /// <returns>The glyph's visibility.</returns>
    public static Visibility GlyphVisibility(ApplicationIcon? icon) =>
        icon is { IsComplete: true }
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>
    /// Shows an element only when a condition holds.
    /// </summary>
    /// <param name="isVisible">Whether the element applies.</param>
    /// <returns>The element's visibility.</returns>
    public static Visibility WhenTrue(bool isVisible) =>
        isVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows an element only when there is something to say.
    /// </summary>
    /// <param name="text">The text the element would show.</param>
    /// <returns>The element's visibility.</returns>
    public static Visibility TextVisibility(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
}
