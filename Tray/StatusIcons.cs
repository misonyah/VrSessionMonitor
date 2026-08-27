using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Tray;

/// <summary>How a status row is doing, which decides its dot colour.</summary>
public enum StatusLevel { Unknown, Good, Warning, Bad }

/// <summary>
/// Small images for the status view: a coloured state dot, a Bluetooth glyph, and the real icon of
/// an application taken from its own executable.
///
/// Everything is cached. These are rebuilt on a 5s tick, and both GDI+ bitmap creation and
/// ExtractAssociatedIcon (which hits the disk) are far too expensive to repeat that often — an
/// uncached extract would read every managed app's exe every five seconds, forever.
/// </summary>
public static class StatusIcons
{
    private const int Size = 12;

    private static readonly ConcurrentDictionary<StatusLevel, Image> Dots = new();
    private static readonly ConcurrentDictionary<string, Image?> AppIcons = new(StringComparer.OrdinalIgnoreCase);
    private static Image? _bluetooth;

    public static Image Dot(StatusLevel level) => Dots.GetOrAdd(level, BuildDot);

    private static Image BuildDot(StatusLevel level)
    {
        var colour = level switch
        {
            StatusLevel.Good => Color.FromArgb(76, 175, 80),
            StatusLevel.Warning => Color.FromArgb(255, 167, 38),
            StatusLevel.Bad => Color.FromArgb(229, 57, 53),
            _ => Color.FromArgb(158, 158, 158),
        };

        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(colour);
        g.FillEllipse(brush, 1, 1, Size - 3, Size - 3);
        // A dark outline keeps the dot legible against both light and dark row backgrounds —
        // the same reason the Home Assistant colour swatches have one.
        using var pen = new Pen(Color.FromArgb(120, 0, 0, 0));
        g.DrawEllipse(pen, 1, 1, Size - 3, Size - 3);
        return bmp;
    }

    /// <summary>The Bluetooth rune, drawn rather than shipped as an asset so there is no icon file
    /// to lose. Falls back to a plain dot if the glyph is unavailable in the installed fonts.</summary>
    public static Image Bluetooth()
    {
        if (_bluetooth is not null) return _bluetooth;

        try
        {
            var bmp = new Bitmap(Size, Size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI Symbol", 9f, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.FromArgb(33, 150, 243));
            g.DrawString("ᛦ", font, brush, -2, -2); // ᛦ — the Bluetooth bind rune
            _bluetooth = bmp;
        }
        catch (Exception ex)
        {
            Log.Debug("StatusIcons", $"Could not render the Bluetooth glyph, using a plain dot: {ex.Message}");
            _bluetooth = Dot(StatusLevel.Unknown);
        }

        return _bluetooth;
    }

    /// <summary>
    /// The application's own icon, read from its executable, so a triggered app is recognisable
    /// the way it is in the taskbar rather than by name alone.
    ///
    /// Returns null when there is nothing to show — a blank path, a Steam app id rather than a
    /// path, an exe that has been moved, or a file the shell declines to extract from. Null is
    /// cached too, so a missing exe is not re-probed every five seconds.
    /// </summary>
    public static Image? ForExecutable(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        return AppIcons.GetOrAdd(exePath, path =>
        {
            try
            {
                // A Steam app id is a perfectly valid launch target but is not a file.
                if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;

                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;

                // Drawn into a fixed square so rows keep an even height whatever size the source
                // icon happens to be.
                var bmp = new Bitmap(16, 16);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawIcon(icon, new Rectangle(0, 0, 16, 16));
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Debug("StatusIcons", $"No icon for {path}: {ex.Message}");
                return null;
            }
        });
    }
}
