using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KoalaPaw.ServerCompanion.ThemeSongs;

/// <summary>
/// Pure helpers for turning a detected intro window into a short <c>theme.mp3</c> in the
/// series folder. No network — the audio comes from an episode the library already has.
/// </summary>
internal static class ThemeFromIntroLogic
{
    /// <summary>Jellyfin extra folder name for theme audio.</summary>
    public const string ThemeMusicFolderName = "theme-music";

    /// <summary>Stem Jellyfin recognises as a theme-song extra in the series folder.</summary>
    public const string ThemeFileStem = "theme";

    /// <summary>Looping a full 90s title sequence on the detail page is too much; cap the sting.</summary>
    public const double MaxThemeSeconds = 20;

    /// <summary>Shorter than this is a false intro, not a theme.</summary>
    public const double MinThemeSeconds = 8;

    public static string ThemeFilePath(string seriesPath) =>
        Path.Combine(seriesPath, ThemeFileStem + ".mp3");

    public static bool FolderAlreadyHasTheme(string seriesPath)
    {
        if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
        {
            return false;
        }

        var fileNames = Directory.EnumerateFiles(seriesPath).Select(Path.GetFileName);
        var directoryNames = Directory.EnumerateDirectories(seriesPath).Select(Path.GetFileName);
        return FolderAlreadyHasTheme(fileNames!, directoryNames!);
    }

    internal static bool FolderAlreadyHasTheme(
        IEnumerable<string> fileNames,
        IEnumerable<string> directoryNames)
    {
        if (directoryNames.Any(name =>
                string.Equals(name, ThemeMusicFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return fileNames.Any(name =>
            string.Equals(Path.GetFileNameWithoutExtension(name), ThemeFileStem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Slice of the intro to encode as the theme. Null when the window is missing or too short.
    /// </summary>
    public static ThemeClipWindow? ClipWindow(long startTicks, long endTicks)
    {
        if (endTicks <= startTicks)
        {
            return null;
        }

        var start = Math.Max(0, startTicks / (double)TimeSpan.TicksPerSecond);
        var duration = (endTicks - startTicks) / (double)TimeSpan.TicksPerSecond;
        if (duration < MinThemeSeconds)
        {
            return null;
        }

        if (duration > MaxThemeSeconds)
        {
            duration = MaxThemeSeconds;
        }

        return new ThemeClipWindow(start, duration);
    }

    public static string BuildExtractArgs(string sourcePath, string destPath, ThemeClipWindow window)
    {
        var start = window.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = window.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return
            $"-hide_banner -nostats -y -ss {start} -t {duration} -i \"{sourcePath}\" " +
            $"-vn -ac 2 -ar 44100 -c:a libmp3lame -q:a 4 \"{destPath}\"";
    }
}

internal readonly record struct ThemeClipWindow(double StartSeconds, double DurationSeconds);
