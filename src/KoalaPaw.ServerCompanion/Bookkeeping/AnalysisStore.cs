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
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(Guid ItemId, string Analyser), FailureRecord> _records = new();
    private readonly object _flushLock = new();
    private readonly Timer _flushTimer;
    private volatile bool _dirty;

    public AnalysisStore(string dataFolderPath, ILogger<AnalysisStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(dataFolderPath, "analysis-failures.json");
        Load();
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

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }

    private void Flush()
    {
        if (!_dirty)
        {
            return;
        }

        lock (_flushLock)
        {
            if (!_dirty)
            {
                return;
            }

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

    public sealed class FailureRecord
    {
        public Guid ItemId { get; set; }

        public string Analyser { get; set; } = string.Empty;

        public string Status { get; set; } = "failed";

        public string Reason { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }
    }
}
