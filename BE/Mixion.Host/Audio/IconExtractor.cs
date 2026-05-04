using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Mixion.Host.Audio;

/// <summary>
/// Reads the icon embedded in a Windows executable and serialises it as PNG.
/// Used by the <c>/api/process-icon</c> endpoint so the FE strip header can
/// show a real Chrome/Spotify/OBS icon next to a process-loopback channel
/// instead of a generic glyph.
///
/// <para>
/// Extraction goes through <see cref="Icon.ExtractAssociatedIcon"/> which
/// wraps the <c>ExtractAssociatedIcon</c> shell API. That returns whatever
/// 32×32 icon resource the file's first icon group exposes — good enough for
/// a 14–16px UI cell and consistent with how Explorer shows the same app.
/// </para>
///
/// <para>
/// Results are memoised in a process-wide concurrent cache keyed by the
/// canonical executable path. Icon resources are stable for the life of an
/// installed binary, so the same Chrome path can hand out the same PNG
/// bytes for the whole host session without re-reading the file.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class IconExtractor
{
    /// <summary>
    /// Maps absolute executable path → encoded PNG, or <c>null</c> if a prior
    /// attempt failed (so we don't retry a known-bad file every page-load).
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte[]?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Return PNG-encoded bytes for the icon associated with
    /// <paramref name="executablePath"/>, or <c>null</c> when extraction
    /// fails (file missing, locked, denied access, no icon resources, …).
    /// Failures cache as <c>null</c> too — we want one read attempt per
    /// host process, not one per browser tab refresh.
    /// </summary>
    public static byte[]? ExtractPng(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        return Cache.GetOrAdd(executablePath, ExtractCore);
    }

    private static byte[]? ExtractCore(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream(capacity: 4096);
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            // Protected processes (anti-cheat, system services), files
            // without icon resources, transient I/O errors — all collapse
            // to "no icon" without taking down the request.
            return null;
        }
    }
}
