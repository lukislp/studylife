using Microsoft.Extensions.Hosting;

namespace StudyLife.Server.Discovery;

/// <summary>
/// Runs the <see cref="IMdnsAnnouncer"/> for the lifetime of the host and withdraws the
/// announcement on shutdown. Registered only when Discovery:Mdns:Enabled (or Only) is set.
/// Discovery is a nicety: whatever the announcer does wrong, the host keeps running.
/// </summary>
public sealed class MdnsHostedService(
    IMdnsAnnouncer announcer,
    ILogger<MdnsHostedService> logger,
    IMdnsIdSource? idSource = null,
    TimeSpan? idRetryInterval = null) : BackgroundService
{
    /// <summary>Matches the announcer's own re-announcement interval.</summary>
    public static readonly TimeSpan DefaultIdRetryInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _retryInterval = idRetryInterval ?? DefaultIdRetryInterval;
    private bool _announcedWithoutId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? id = null;
        try
        {
            // Ask first so the very first announcement already carries the id; when the source
            // cannot answer, announce without it right away and keep retrying below.
            id = await FetchIdAsync(stoppingToken);
            if (id is not null) announcer.SetId(id);
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
            // Without an id: try again on every re-announcement tick until it is known, then
            // announce again with it (SetId). With an id (or no source) just wait for shutdown.
            while (id is null && idSource is not null)
            {
                await Task.Delay(_retryInterval, stoppingToken);
                id = await FetchIdAsync(stoppingToken);
                if (id is not null) announcer.SetId(id);
            }
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Never throws (except for cancellation); logs once per state change.</summary>
    private async Task<string?> FetchIdAsync(CancellationToken cancellationToken)
    {
        if (idSource is null) return null;
        string? id;
        try
        {
            id = await idSource.TryGetIdAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            id = null;
            logger.LogDebug(ex, "mDNS: Instanz-ID konnte nicht ermittelt werden.");
        }

        if (id is null && !_announcedWithoutId)
        {
            _announcedWithoutId = true;
            logger.LogWarning("mDNS: Instanz-ID derzeit nicht verfügbar; Ankündigung ohne ID, erneuter Versuch bei der nächsten Ankündigung.");
        }
        else if (id is not null)
        {
            logger.LogInformation(_announcedWithoutId
                ? "mDNS: Instanz-ID jetzt verfügbar; kündige erneut mit ID an."
                : "mDNS: Instanz-ID ermittelt.");
            _announcedWithoutId = false;
        }
        return id;
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
