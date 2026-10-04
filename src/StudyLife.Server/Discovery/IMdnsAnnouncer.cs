namespace StudyLife.Server.Discovery;

/// <summary>
/// Announces the StudyLife service via mDNS / DNS-SD. The abstraction exists so the hosted service
/// can be tested without sockets. Implementations must never throw for "cannot announce" reasons
/// (no multicast, container without host networking): they log a warning and report false.
/// </summary>
public interface IMdnsAnnouncer
{
    /// <summary>Starts announcing and answering queries. Returns false when nothing could be bound.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);

    /// <summary>Withdraws the announcement (goodbye packet, TTL 0) and releases the sockets.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
