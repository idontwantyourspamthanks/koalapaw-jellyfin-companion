using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.Loudness;

/// <summary>
/// Measures integrated loudness (LUFS) with jellyfin-ffmpeg <c>loudnorm</c> and converts to the
/// same gain Jellyfin's DtoService uses: <c>NormalizationGain = -18 − LUFS</c> (ReplayGain 2.0).
/// </summary>
public sealed class LoudnessProbe
{
    /// <summary>Jellyfin / ReplayGain 2.0 reference (see DtoService).</summary>
    public const float DefaultTargetLufs = -18f;

    private static readonly Regex JsonBlock = new(
        @"\{[^{}]*""input_i""[^{}]*\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly float _targetLufs;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<LoudnessProbe> _logger;

    public LoudnessProbe(IMediaEncoder encoder, ILogger<LoudnessProbe> logger)
    {
        _encoder = encoder;
        _logger = logger;
        _targetLufs = Plugin.Instance?.Configuration.TargetLufs ?? DefaultTargetLufs;
    }

    /// <summary>
    /// Audio-only probe. Returns null on failure. Typical 15-min episode finishes in under a minute on a Pi.
    /// </summary>
    public async Task<float?> MeasureNormalizationGainAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var integrated = await MeasureIntegratedLufsAsync(path, cancellationToken).ConfigureAwait(false);
        if (integrated is null)
        {
            return null;
        }

        // Same formula as Emby.Server.Implementations.Dto.DtoService for item.LUFS.
        return _targetLufs - integrated.Value;
    }

    public async Task<float?> MeasureIntegratedLufsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            _logger.LogError("LoudnessProbe: IMediaEncoder.EncoderPath is empty");
            return null;
        }

        // Single-pass loudnorm print: we only need input_i (integrated loudness).
        var args =
            $"-hide_banner -nostats -vn -i \"{path}\" " +
            $"-af loudnorm=I={_targetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11:print_format=json " +
            "-f null -";

        var stderr = new StringBuilder();
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

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                _logger.LogError("LoudnessProbe: failed to start {Ffmpeg}", ffmpeg);
                return null;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LoudnessProbe: could not lower process priority");
            }

            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // WaitForExitAsync can return before the async ErrorDataReceived callbacks have drained;
            // the loudnorm JSON is the last thing on stderr, so flush pending output synchronously.
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "LoudnessProbe: ffmpeg exit {Code} for {Path}",
                    process.ExitCode,
                    path);
            }

            return ParseInputIntegrated(stderr.ToString());
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoudnessProbe: failed for {Path}", path);
            return null;
        }
    }

    public static float? ParseInputIntegrated(string stderr)
    {
        var match = JsonBlock.Match(stderr);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            if (!doc.RootElement.TryGetProperty("input_i", out var inputI))
            {
                return null;
            }

            var raw = inputI.GetString() ?? inputI.ToString();
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs))
            {
                return null;
            }

            return float.IsFinite(lufs) ? lufs : null;
        }
        catch
        {
            return null;
        }
    }
}
