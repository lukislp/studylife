using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StudyLife.Server.Discovery;

/// <summary>
/// Minimal mDNS responder for exactly one DNS-SD service (no third-party library, see the commit
/// message: the only candidate, Makaretu.Dns.Multicast.New, last shipped 2024-11 and pulls in the
/// abandoned Common.Logging 3.4.1). It
/// <list type="bullet">
/// <item>joins 224.0.0.251 / ff02::fb on port 5353 on every usable network interface,</item>
/// <item>announces the service on start (twice, 1 s apart) and re-announces every few minutes,</item>
/// <item>answers matching PTR/SRV/TXT/A/AAAA queries, on the interface the query arrived on, with that
/// interface's own addresses,</item>
/// <item>sends a goodbye (TTL 0) on stop.</item>
/// </list>
/// Binding is best effort: no multicast, a container without host networking or a port held by
/// another process only logs a warning (<see cref="StartAsync"/> returns false) - discovery is a
/// nicety, never a reason to fail. Probing/conflict resolution (RFC 6762 section 8) is deliberately
/// not implemented; the instance name is operator-configured and a second announcer under the same
/// name is a misconfiguration.
/// </summary>
public sealed class MdnsAnnouncer(MdnsServiceDescription initialService, ILogger<MdnsAnnouncer> logger) : IMdnsAnnouncer, IDisposable
{
    // Replaced (never mutated) when the instance id becomes known after start - see SetId.
    private volatile MdnsServiceDescription service = initialService;

    private const int Port = 5353;
    private static readonly IPAddress GroupV4 = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress GroupV6 = IPAddress.Parse("ff02::fb");
    private static readonly TimeSpan ReannounceInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResponseThrottle = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<Socket> _sockets = [];
    private CancellationTokenSource? _cts;
    private Task[] _loops = [];
    private DateTime _lastResponseUtc = DateTime.MinValue;

    private sealed record Iface(string Name, int IndexV4, int IndexV6, IReadOnlyList<IPAddress> V4, IReadOnlyList<IPAddress> V6);

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) return true;
        try
        {
            var ifaces = DiscoverInterfaces();
            if (ifaces.Count == 0)
            {
                logger.LogWarning("mDNS-Ankündigung nicht gestartet: keine nutzbare Netzwerkschnittstelle mit Multicast gefunden "
                    + "(Container ohne Host-Netzwerk?). Der Server läuft ohne Discovery weiter.");
                return false;
            }

            var v4 = TryOpen(AddressFamily.InterNetwork, ifaces);
            var v6 = TryOpen(AddressFamily.InterNetworkV6, ifaces);
            if (v4 is null && v6 is null)
            {
                logger.LogWarning("mDNS-Ankündigung nicht gestartet: UDP-Port {Port} konnte nicht für Multicast gebunden werden "
                    + "(Container ohne Host-Netzwerk, Multicast gesperrt oder Port belegt?). Der Server läuft ohne Discovery weiter.", Port);
                return false;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            var loops = new List<Task>();
            foreach (var socket in new[] { v4, v6 }.OfType<Socket>())
                loops.Add(Task.Run(() => ReceiveLoopAsync(socket, ifaces, token), CancellationToken.None));
            loops.Add(Task.Run(() => AnnounceLoopAsync(token), CancellationToken.None));
            _loops = [.. loops];

            logger.LogInformation(
                "mDNS-Ankündigung aktiv: {Instance}.{Type} Port {PortAnnounced} ({Txt}) auf {Count} Schnittstelle(n).",
                service.InstanceName, MdnsServiceDescription.ServiceType, service.Port,
                string.Join(", ", service.TxtRecords), ifaces.Count);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "mDNS-Ankündigung nicht gestartet. Der Server läuft ohne Discovery weiter.");
            CloseSockets();
            return false;
        }
    }

    public void SetId(string id)
    {
        var updated = service.WithId(id);
        if (ReferenceEquals(updated, service) || updated.Id == service.Id) return;
        service = updated;
        logger.LogInformation("mDNS-Ankündigung enthält jetzt die Instanz-ID; sende erneut.");
        if (_cts is null) return;
        try { SendAll(ttlZero: false); }
        catch (Exception ex) { logger.LogDebug(ex, "mDNS: erneute Ankündigung mit ID fehlgeschlagen."); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;
        try
        {
            // Goodbye first, while the sockets are still open.
            SendAll(ttlZero: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mDNS-Goodbye konnte nicht gesendet werden.");
        }
        await cts.CancelAsync();
        CloseSockets();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception)
        {
            // Loops end with the closed sockets; a slow one must not delay shutdown.
        }
        cts.Dispose();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        CloseSockets();
    }

    private void CloseSockets()
    {
        lock (_gate)
        {
            foreach (var socket in _sockets)
            {
                try { socket.Dispose(); } catch (Exception) { /* closing */ }
            }
            _sockets.Clear();
        }
    }

    private static List<Iface> DiscoverInterfaces()
    {
        var result = new List<Iface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Container/CNI plumbing on a hostNetwork node: not a LAN the announcement is meant for.
            if (VirtualPrefixes.Any(p => ni.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses.Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                    && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)).ToList();
            var v6 = props.UnicastAddresses.Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal
                    && !a.IsIPv6Multicast && !IPAddress.IsLoopback(a)).ToList();
            if (v4.Count == 0 && v6.Count == 0) continue;

            var i4 = -1;
            var i6 = -1;
            try { i4 = props.GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { /* no IPv4 */ }
            try { i6 = props.GetIPv6Properties()?.Index ?? -1; } catch (NetworkInformationException) { /* no IPv6 */ }
            result.Add(new Iface(ni.Name, i4, i6, v4, v6));
        }
        return result;
    }

    private static readonly string[] VirtualPrefixes = ["docker", "veth", "br-", "cni", "flannel", "cali", "cilium", "lxc", "kube", "virbr"];

    private Socket? TryOpen(AddressFamily family, List<Iface> ifaces)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (family == AddressFamily.InterNetwork)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
            }

            var joined = 0;
            foreach (var iface in ifaces)
            {
                if (JoinGroup(socket, family, iface)) joined++;
            }
            if (joined == 0)
            {
                socket.Dispose();
                return null;
            }
            lock (_gate) _sockets.Add(socket);
            return socket;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mDNS: {Family}-Socket konnte nicht geöffnet werden.", family);
            socket?.Dispose();
            return null;
        }
    }

    private bool JoinGroup(Socket socket, AddressFamily family, Iface iface)
    {
        try
        {
            if (family == AddressFamily.InterNetwork && iface.V4.Count > 0 && iface.IndexV4 >= 0)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupV4, iface.IndexV4));
            else if (family == AddressFamily.InterNetworkV6 && iface.V6.Count > 0 && iface.IndexV6 >= 0)
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(GroupV6, iface.IndexV6));
            else
                return false;
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true; // already a member (periodic refresh)
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mDNS: Multicast-Gruppe auf {Interface} nicht beitretbar.", iface.Name);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, List<Iface> ifaces, CancellationToken token)
    {
        var v4 = socket.AddressFamily == AddressFamily.InterNetwork;
        var buffer = new byte[9000];
        EndPoint remote = v4 ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, token);
                if (!MdnsPacket.TryReadQuestions(buffer.AsSpan(0, result.ReceivedBytes), out var questions)
                    || !MdnsPacket.Matches(service, questions))
                    continue;

                var now = DateTime.UtcNow;
                lock (_gate)
                {
                    if (now - _lastResponseUtc < ResponseThrottle) continue;
                    _lastResponseUtc = now;
                }
                var index = result.PacketInformation.Interface;
                var iface = ifaces.FirstOrDefault(i => (v4 ? i.IndexV4 : i.IndexV6) == index);
                if (iface is not null) SendOn(socket, iface, ttlZero: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "mDNS: Empfangsfehler.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        try
        {
            // RFC 6762 section 8.3: announce at least twice, one second apart.
            SendAll(ttlZero: false);
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            SendAll(ttlZero: false);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ReannounceInterval, token);
                // Interfaces come and go (Wi-Fi, VPN): rejoin and announce again.
                var ifaces = DiscoverInterfaces();
                lock (_gate)
                {
                    foreach (var socket in _sockets)
                        foreach (var iface in ifaces) JoinGroup(socket, socket.AddressFamily, iface);
                }
                SendAll(ttlZero: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mDNS: periodische Ankündigung abgebrochen.");
        }
    }

    private void SendAll(bool ttlZero)
    {
        var ifaces = DiscoverInterfaces();
        Socket[] sockets;
        lock (_gate) sockets = [.. _sockets];
        foreach (var socket in sockets)
            foreach (var iface in ifaces)
                SendOn(socket, iface, ttlZero);
    }

    private void SendOn(Socket socket, Iface iface, bool ttlZero)
    {
        var v4 = socket.AddressFamily == AddressFamily.InterNetwork;
        var addresses = v4 ? iface.V4 : iface.V6;
        var index = v4 ? iface.IndexV4 : iface.IndexV6;
        if (addresses.Count == 0 || index < 0) return;
        try
        {
            var packet = MdnsPacket.BuildResponse(service, iface.V4.Concat(iface.V6), ttlZero);
            lock (socket)
            {
                if (v4)
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(index));
                else
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                socket.SendTo(packet, new IPEndPoint(v4 ? GroupV4 : GroupV6, Port));
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "mDNS: Senden auf {Interface} fehlgeschlagen.", iface.Name);
        }
    }
}
