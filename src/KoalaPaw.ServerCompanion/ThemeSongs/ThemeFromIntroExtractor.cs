using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ThemeSongs;

/// <summary>
/// Encodes a short MP3 from an episode's intro window into the series folder as
/// <c>theme.mp3</c>, then tells Jellyfin to pick it up as a theme extra.
/// </summary>
public sealed class ThemeFromIntroExtractor
{
    internal const string AnalyserName = "themes";

    private readonly IMediaEncoder _encoder;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<ThemeFromIntroExtractor> _logger;

    public ThemeFromIntroExtractor(
        IMediaEncoder encoder,
        ILibraryMonitor libraryMonitor,
        ILogger<ThemeFromIntroExtractor> logger)
    {
        _encoder = encoder;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    internal async Task<ThemeExtractResult> ExtractAsync(
        string seriesPath,
        string episodePath,
        long introStartTicks,
        long introEndTicks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesPath) || string.IsNullOrWhiteSpace(episodePath))
        {
            return new ThemeExtractResult(ThemeExtractStatus.Failed, "missing series or episode path");
        }

        if (ThemeFromIntroLogic.FolderAlreadyHasTheme(seriesPath))
        {
            return new ThemeExtractResult(ThemeExtractStatus.AlreadyPresent, null);
        }

        var window = ThemeFromIntroLogic.ClipWindow(introStartTicks, introEndTicks);
        if (window is null)
        {
            return new ThemeExtractResult(ThemeExtractStatus.NoClip, "intro window too short for a theme");
        }

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return new ThemeExtractResult(ThemeExtractStatus.Failed, "IMediaEncoder.EncoderPath is empty");
        }

        Directory.CreateDirectory(seriesPath);
        var themePath = ThemeFromIntroLogic.ThemeFilePath(seriesPath);
        var partPath = themePath + ".part";
        var args = ThemeFromIntroLogic.BuildExtractArgs(episodePath, partPath, window.Value);

        _libraryMonitor.ReportFileSystemChangeBeginning(themePath);
        try
        {
            var ok = await RunFfmpegAsync(ffmpeg, args, cancellationToken).ConfigureAwait(false);
            if (!ok || !File.Exists(partPath) || new FileInfo(partPath).Length < 1024)
            {
                TryDelete(partPath);
                return new ThemeExtractResult(ThemeExtractStatus.Failed, "ffmpeg did not produce a theme MP3");
            }

            File.Move(partPath, themePath, overwrite: true);
            _logger.LogInformation(
                "ThemeFromIntro: wrote {Theme} from {Episode} ({Start:0.#}s+{Duration:0.#}s)",
                themePath,
                episodePath,
                window.Value.StartSeconds,
                window.Value.DurationSeconds);
            return new ThemeExtractResult(ThemeExtractStatus.Written, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            _logger.LogError(ex, "ThemeFromIntro: failed writing {Theme}", themePath);
            return new ThemeExtractResult(ThemeExtractStatus.Failed, ex.Message);
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(themePath, refreshPath: true);
        }
    }

    private async Task<bool> RunFfmpegAsync(string ffmpeg, string args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            if (!process.Start())
            {
                _logger.LogError("ThemeFromIntro: failed to start {Ffmpeg}", ffmpeg);
                return false;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ThemeFromIntro: could not lower process priority");
            }

            // Drain pipes so a chatty ffmpeg cannot stall on a full buffer.
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ThemeFromIntro: ffmpeg exit {Code}", process.ExitCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}

internal enum ThemeExtractStatus
{
    Written,
    AlreadyPresent,
    NoClip,
    Failed,
}

internal readonly record struct ThemeExtractResult(ThemeExtractStatus Status, string? Detail);
