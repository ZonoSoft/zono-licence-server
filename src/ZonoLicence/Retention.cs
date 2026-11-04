namespace ZonoLicence;

/// <summary>
/// Once a day, deletes what the privacy notice says isn't kept: activity records after 12 months, trial records of
/// devices not seen for 2 years, devices released from a licence over 2 years ago, and expired portal sign-ins.
/// (Daily backups are deleted after 30 days by the backup job.)
/// </summary>
public sealed class Retention(LicenceStore licences, CustomerStore customers, ILogger<Retention> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await RunAsync(stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }   // the server is stopping
    }

    private async Task RunAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var gone = licences.Prune(now) + customers.PruneMachines(now);
                if (gone > 0) log.LogInformation("Retention: deleted {Count} old records", gone);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                log.LogWarning(e, "Retention clean-up failed; will try again tomorrow");
            }
            await Task.Delay(TimeSpan.FromHours(24), stop);
        }
    }
}
