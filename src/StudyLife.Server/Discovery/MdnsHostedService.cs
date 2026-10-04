using Microsoft.Extensions.Hosting;

namespace StudyLife.Server.Discovery;

/// <summary>
/// Runs the <see cref="IMdnsAnnouncer"/> for the lifetime of the host and withdraws the
/// announcement on shutdown. Registered only when Discovery:Mdns:Enabled (or Only) is set.
/// Discovery is a nicety: whatever the announcer does wrong, the host keeps running.
/// </summary>
public sealed class MdnsHostedService(IMdnsAnnouncer announcer, ILogger<MdnsHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await announcer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // An unhandled exception here would stop the whole host (default BackgroundService behaviour).
            logger.LogWarning(ex, "mDNS-Ankündigung konnte nicht gestartet werden; der Server läuft ohne Discovery weiter.");
            return;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await announcer.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mDNS-Abmeldung (Goodbye) fehlgeschlagen.");
        }
    }
}
