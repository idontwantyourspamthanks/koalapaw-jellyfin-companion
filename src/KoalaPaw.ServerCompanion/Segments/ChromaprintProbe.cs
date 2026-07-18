using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Audio-only chromaprint over a capped head or tail window.
/// Raw fingerprint is a sequence of 32-bit hashes (~0.1238s each).
/// </summary>
public sealed class ChromaprintProbe
{
    public const int HeadSeconds = 600;
    public const int TailSeconds = 300;

    /// <summary>Approximate seconds covered by one chromaprint raw item.</summary>
    public const double ItemSeconds = 0.1238;

    private readonly IMediaEncoder _encoder;
    private readonly ILogger _logger;

    public ChromaprintProbe(IMediaEncoder encoder, ILogger<ChromaprintProbe> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    public Task<uint[]?> FingerprintHeadAsync(string path, CancellationToken cancellationToken) =>
        FingerprintAsync(
            path,
            $"-hide_banner -nostats -vn -t {HeadSeconds.ToString(CultureInfo.InvariantCulture)} -i \"{path}\" " +
            "-ac 2 -f chromaprint -fp_format raw pipe:1",
            cancellationToken);

    /// <summary>Last <see cref="TailSeconds"/> of audio (sseof seek).</summary>
    public Task<uint[]?> FingerprintTailAsync(string path, CancellationToken cancellationToken) =>
        FingerprintAsync(
            path,
            $"-hide_banner -nostats -vn -sseof -{TailSeconds.ToString(CultureInfo.InvariantCulture)} -i \"{path}\" " +
            "-ac 2 -f chromaprint -fp_format raw pipe:1",
            cancellationToken);

    private async Task<uint[]?> FingerprintAsync(
        string path,
        string args,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            if (!process.Start())
            {
                return null;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // ignore
            }

            await using var stdout = process.StandardOutput.BaseStream;
            using var ms = new MemoryStream();
            await stdout.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var bytes = ms.ToArray();
            if (bytes.Length < 4 || bytes.Length % 4 != 0)
            {
                _logger.LogWarning("Chromaprint: unexpected byte length {Len} for {Path}", bytes.Length, path);
                return null;
            }

            var items = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, items, 0, bytes.Length);
            return items;
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
            _logger.LogError(ex, "Chromaprint failed for {Path}", path);
            return null;
        }
    }

    public static double IndexToSeconds(int index) => index * ItemSeconds;

    public static int SecondsToIndex(double seconds) =>
        (int)Math.Round(seconds / ItemSeconds);
}
