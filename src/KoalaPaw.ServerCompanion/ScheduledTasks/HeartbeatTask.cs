using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KoalaPaw.ServerCompanion.Bookkeeping;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Lightweight health check: logs the plugin version and the current analysis-failure count.
/// </summary>
public class HeartbeatTask : IScheduledTask
{
    private readonly ILogger<HeartbeatTask> _logger;
    private readonly AnalysisStore _analysisStore;

    public HeartbeatTask(ILogger<HeartbeatTask> logger, AnalysisStore analysisStore)
    {
        _logger = logger;
        _analysisStore = analysisStore;
    }

    public string Name => "KoalaPaw health check";

    public string Key => "KoalaPawCompanionHeartbeat";

    public string Description =>
        "Logs the KoalaPaw Server Companion version and analysis-failure count.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        ];
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "KoalaPaw Server Companion v{Version} — {Failures} tracked analysis failure(s)",
            typeof(Plugin).Assembly.GetName().Version,
            _analysisStore.GetFailureCount());
        progress.Report(100);
        return Task.CompletedTask;
    }
}