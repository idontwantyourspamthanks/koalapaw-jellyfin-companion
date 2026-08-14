using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.Bookkeeping;

/// <summary>
/// Lightweight JSON file-backed store that remembers which items failed analysis so they
/// aren't retried every 12 h run forever.  Thread-safe via <see cref="ConcurrentDictionary"/>
/// with a periodic timer flush + flush-on-dispose.
/// </summary>
public sealed class AnalysisStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly string _reportFilePath;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(Guid ItemId, string Analyser), FailureRecord> _records = new();
    private SegmentScanReport? _segmentScanReport;
    private volatile bool _reportDirty;
    private readonly object _flushLock = new();
    private readonly Timer _flushTimer;
    private volatile bool _dirty;

    public AnalysisStore(string dataFolderPath, ILogger<AnalysisStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(dataFolderPath, "analysis-failures.json");
        _reportFilePath = Path.Combine(dataFolderPath, "segment-scan-report.json");
        Load();
        LoadReport();
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Record (or update) a failure for an item + analyser pair.</summary>
    public void RecordFailure(Guid itemId, string analyser, string reason)
    {
        _records[(itemId, analyser)] = new FailureRecord
        {
            ItemId = itemId,
            Analyser = analyser,
            Status = "failed",
            Reason = reason,
            Timestamp = DateTime.UtcNow,
        };
        _dirty = true;
    }

    /// <summary>True when the item has a recorded failure for the given analyser.</summary>
    public bool HasFailed(Guid itemId, string analyser) =>
        _records.ContainsKey((itemId, analyser));

    /// <summary>Clear all recorded failures and flush immediately.</summary>
    public void ClearFailures()
    {
        _records.Clear();
        _dirty = true;
        Flush();
    }

    /// <summary>Snapshot of all recorded failures, newest first.</summary>
    public IReadOnlyList<FailureRecord> GetFailures() =>
        _records.Values.OrderByDescending(r => r.Timestamp).ToList();

    /// <summary>Remove one failure record so the item is eligible again on the next scan run.</summary>
    public bool RemoveFailure(Guid itemId, string analyser)
    {
        if (!_records.TryRemove((itemId, analyser), out _))
        {
            return false;
        }

        _dirty = true;
        Flush();
        return true;
    }

    /// <summary>Number of distinct (item, analyser) failure records.</summary>
    public int GetFailureCount() => _records.Count;

    /// <summary>The last intro/credits scan's outcome, or null when no scan has completed yet.</summary>
    public SegmentScanReport? GetSegmentScanReport() => _segmentScanReport;

    /// <summary>Remember the last intro/credits scan's outcome so clients can read it anytime.</summary>
    public void SetSegmentScanReport(SegmentScanReport report)
    {
        _segmentScanReport = report;
        _reportDirty = true;
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }

    private void Flush()
    {
        if (!_dirty && !_reportDirty)
        {
            return;
        }

        lock (_flushLock)
        {
            if (_dirty)
            {
                _dirty = false;
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AnalysisStore: failed to flush to {Path}", _filePath);
                }
            }

            if (_reportDirty)
            {
                _reportDirty = false;
                try
                {
                    var dir = Path.GetDirectoryName(_reportFilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(_segmentScanReport, JsonOptions);
                    File.WriteAllText(_reportFilePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AnalysisStore: failed to flush report to {Path}", _reportFilePath);
                }
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var records = JsonSerializer.Deserialize<List<FailureRecord>>(json, JsonOptions);
            if (records is null)
            {
                return;
            }

            foreach (var r in records)
            {
                _records[(r.ItemId, r.Analyser)] = r;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnalysisStore: could not load {Path}, starting fresh", _filePath);
        }
    }

    private void LoadReport()
    {
        try
        {
            if (!File.Exists(_reportFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_reportFilePath);
            _segmentScanReport = JsonSerializer.Deserialize<SegmentScanReport>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnalysisStore: could not load report from {Path}, starting fresh", _reportFilePath);
        }
    }

    /// <summary>Outcome of one intro/credits scan run — what the TV's scan page reports.</summary>
    public sealed class SegmentScanReport
    {
        public int EpisodesScanned { get; set; }

        public int IntrosFound { get; set; }

        public int OutrosFound { get; set; }

        /// <summary>Mean correlation confidence (0–1) across seasons with an intro, or null when none found.</summary>
        public double? IntroConfidence { get; set; }

        /// <summary>Mean correlation confidence (0–1) across seasons with an outro, or null when none found.</summary>
        public double? OutroConfidence { get; set; }

        public DateTime FinishedUtc { get; set; }
    }

    public sealed class FailureRecord
    {
        public Guid ItemId { get; set; }

        public string Analyser { get; set; } = string.Empty;

        public string Status { get; set; } = "failed";

        public string Reason { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }
    }
}
