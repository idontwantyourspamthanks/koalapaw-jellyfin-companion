using KoalaPaw.ServerCompanion.ThemeSongs;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class ThemeFromIntroLogicTests
{
    [Fact]
    public void FolderAlreadyHasTheme_detectsThemeFileAndThemeMusicFolder()
    {
        Assert.False(ThemeFromIntroLogic.FolderAlreadyHasTheme(["poster.jpg"], ["Season 1"]));
        Assert.True(ThemeFromIntroLogic.FolderAlreadyHasTheme(["theme.mp3"], []));
        Assert.True(ThemeFromIntroLogic.FolderAlreadyHasTheme(["Theme.MP4"], []));
        Assert.True(ThemeFromIntroLogic.FolderAlreadyHasTheme([], ["theme-music"]));
        Assert.False(ThemeFromIntroLogic.FolderAlreadyHasTheme(["not-a-theme.mp3"], ["trailers"]));
    }

    [Fact]
    public void ThemeFilePath_isThemeMp3InSeriesFolder()
    {
        Assert.Equal(
            Path.Combine("/shows/Bluey", "theme.mp3"),
            ThemeFromIntroLogic.ThemeFilePath("/shows/Bluey"));
    }

    [Fact]
    public void ClipWindow_capsLongIntroAtTwentySeconds()
    {
        var start = 0L;
        var end = TimeSpan.FromSeconds(45).Ticks;
        var window = ThemeFromIntroLogic.ClipWindow(start, end);

        Assert.NotNull(window);
        Assert.Equal(0, window!.Value.StartSeconds);
        Assert.Equal(ThemeFromIntroLogic.MaxThemeSeconds, window.Value.DurationSeconds);
    }

    [Fact]
    public void ClipWindow_keepsShortButValidIntro()
    {
        var window = ThemeFromIntroLogic.ClipWindow(0, TimeSpan.FromSeconds(12).Ticks);

        Assert.NotNull(window);
        Assert.Equal(12, window!.Value.DurationSeconds, precision: 3);
    }

    [Fact]
    public void ClipWindow_rejectsTinyOrEmptyWindows()
    {
        Assert.Null(ThemeFromIntroLogic.ClipWindow(0, TimeSpan.FromSeconds(3).Ticks));
        Assert.Null(ThemeFromIntroLogic.ClipWindow(TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(10).Ticks));
        Assert.Null(ThemeFromIntroLogic.ClipWindow(TimeSpan.FromSeconds(20).Ticks, TimeSpan.FromSeconds(10).Ticks));
    }

    [Fact]
    public void ClipWindow_honoursNonZeroStart()
    {
        var start = TimeSpan.FromSeconds(5).Ticks;
        var end = TimeSpan.FromSeconds(40).Ticks;
        var window = ThemeFromIntroLogic.ClipWindow(start, end);

        Assert.NotNull(window);
        Assert.Equal(5, window!.Value.StartSeconds, precision: 3);
        Assert.Equal(20, window.Value.DurationSeconds, precision: 3);
    }

    [Fact]
    public void BuildExtractArgs_seeksThenEncodesMp3()
    {
        var window = new ThemeClipWindow(0, 20);
        var args = ThemeFromIntroLogic.BuildExtractArgs("/shows/Bluey/S01E01.mkv", "/shows/Bluey/theme.mp3.part", window);

        Assert.Contains("-ss 0 -t 20", args);
        Assert.Contains("-i \"/shows/Bluey/S01E01.mkv\"", args);
        Assert.Contains("-c:a libmp3lame", args);
        Assert.Contains("\"/shows/Bluey/theme.mp3.part\"", args);
        Assert.DoesNotContain("http", args, StringComparison.OrdinalIgnoreCase);
    }
}
