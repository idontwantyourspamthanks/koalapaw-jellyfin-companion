using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Dev proof that the companion loaded: logs once when the task runs.
/// Safe defaults — weekly, no library work. Replace with real analysers later.
/// </summary>
public class HeartbeatTask : IScheduledTask
{
    private readonly ILogger<HeartbeatTask> _logger;

    public HeartbeatTask(ILogger<HeartbeatTask> logger)
    {
        _logger = logger;
        _logger.LogInformation("KoalaPaw Server Companion heartbeat task registered");
    }

    public string Name => "KoalaPaw companion heartbeat";

    public string Key => "KoalaPawCompanionHeartbeat";

    public string Description =>
        "Logs that the KoalaPaw Server Companion is loaded. Skeleton only — no library analysis yet.";

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
            "KoalaPaw Server Companion is alive (version {Version})",
            typeof(Plugin).Assembly.GetName().Version);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
