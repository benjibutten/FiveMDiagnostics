using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

/// <summary>
/// Basic-mode network evidence. Note that Windows exposes no per-socket remote peer for UDP, and FiveM
/// carries gameplay over UDP, so the remote server is inferred from the TCP connection the client keeps
/// open to the same host rather than observed directly.
/// </summary>
public sealed class NetworkTelemetryCollector : ITelemetryCollector, IDisposable
{
    private const int FiveMDefaultPort = 30120;

    /// <summary>
    /// Port range FiveM servers are reachable on. The default is 30120 and community servers shift a
    /// few either way, so the whole 30000-series counts as plausibly a game server.
    /// </summary>
    /// <remarks>
    /// This range is the fix for a measured failure. Persistence alone accepted 23.36.77.184:443 — a
    /// CDN the NUI keeps a connection open to — as the game server 26 seconds into a five hour session,
    /// and every latency figure in that session's incident reports then described a content delivery
    /// network. Staying open for hours is what a CDN connection and a game connection have in common,
    /// so persistence cannot tell them apart. The port can.
    /// </remarks>
    private const int FiveMPortRangeStart = 30000;

    private const int FiveMPortRangeEnd = 30999;

    /// <summary>
    /// How many consecutive polls an endpoint in the FiveM range must persist before it is treated as
    /// the game server. A launcher fetch that happens to land in the range appears for a poll or two;
    /// the session connection stays for the whole session.
    /// </summary>
    private const int StableEndpointPolls = 3;

    private readonly Ping _ping = new();
    private string? _autoDetectedProbeHost;
    private bool _reportedAutoDetectedHost;
    private int? _autoDetectionProcessId;
    private string? _candidateEndpointKey;
    private int _candidateObservations;
    private int _latchedHostAbsentPolls;
    private bool _reportedNoPlausibleHost;

    public string Name => "NetworkTelemetry";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var target = context.ProcessResolver.TryGetTargetProcess();
            if (target is not null)
            {
                var timestamp = context.UtcNow();
                var endpointHint = context.Settings.ServerProfile.EndpointHint;
                var remoteEndpoints = ReadTcpEndpoints(target.ProcessId, endpointHint);
                var udpLocalPorts = ReadUdpPorts(target.ProcessId);

                await context.Writer.WriteAsync(
                    new NetworkEndpointSample(timestamp, target.ProcessId, remoteEndpoints, udpLocalPorts),
                    cancellationToken).ConfigureAwait(false);

                var probeHost = ResolveProbeHost(context, target.ProcessId, remoteEndpoints);
                if (!string.IsNullOrWhiteSpace(probeHost))
                {
                    var probeSample = await ProbeAsync(probeHost, timestamp).ConfigureAwait(false);
                    await context.Writer.WriteAsync(probeSample, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(context.Settings.NetworkPollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prefers an explicitly configured host, otherwise latches onto the FiveM server the client is
    /// actually talking to. Without this the probe silently never runs when ProbeHost is unset, which
    /// leaves the network hypothesis with nothing to weigh.
    /// </summary>
    /// <remarks>
    /// Probing the wrong host is worse than not probing: RTT to some CDN would be presented as evidence
    /// about the game server, and it looks entirely healthy while the game connection is not. So a
    /// candidate has to be on a port a FiveM server actually listens on — the default outright, the rest
    /// of the 30000-series after it has persisted — and anything else is refused rather than guessed at.
    /// The latch is dropped whenever the target process changes so a stale address cannot leak into the
    /// next session.
    /// </remarks>
    private string? ResolveProbeHost(CollectorContext context, int processId, IReadOnlyList<RemoteEndpointInfo> remoteEndpoints)
    {
        var configured = context.Settings.ServerProfile.ProbeHost;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (_autoDetectionProcessId != processId)
        {
            ResetAutoDetection(processId);
        }

        if (_autoDetectedProbeHost is not null)
        {
            // Drop the latch once the connection it was based on has been gone for a while: the player
            // may have switched servers without restarting the game. An empty endpoint list counts as
            // absence too — treating it as "no information" was how a stale address survived — but a
            // single poll is not enough, since the table read can fail transiently.
            if (remoteEndpoints.Any(item => item.RemoteAddress == _autoDetectedProbeHost))
            {
                _latchedHostAbsentPolls = 0;
                return _autoDetectedProbeHost;
            }

            if (++_latchedHostAbsentPolls < StableEndpointPolls)
            {
                return _autoDetectedProbeHost;
            }

            ResetAutoDetection(processId);
        }

        var candidate = SelectCandidate(remoteEndpoints);
        if (candidate is null)
        {
            _candidateEndpointKey = null;
            _candidateObservations = 0;

            // Said once rather than every poll. Silence here used to be indistinguishable from "probing
            // fine", and the reports then carried no network evidence at all without explaining why.
            if (!_reportedNoPlausibleHost && remoteEndpoints.Count > 0)
            {
                _reportedNoPlausibleHost = true;
                context.StatusSink.Report(
                    StatusLevel.Info,
                    Name,
                    $"Ingen av FiveM:s {remoteEndpoints.Count} anslutningar ligger på en spelserverport "
                    + $"({FiveMPortRangeStart}-{FiveMPortRangeEnd}), så probe-hosten kan inte härledas. Nätproberna är "
                    + "avstängda tills ProbeHost sätts manuellt — hellre ingen mätning än RTT mot fel maskin.");
            }

            return null;
        }

        if (candidate.RemotePort != FiveMDefaultPort)
        {
            var key = $"{candidate.RemoteAddress}:{candidate.RemotePort}";
            if (!string.Equals(_candidateEndpointKey, key, StringComparison.Ordinal))
            {
                _candidateEndpointKey = key;
                _candidateObservations = 1;
                return null;
            }

            if (++_candidateObservations < StableEndpointPolls)
            {
                return null;
            }
        }

        _autoDetectedProbeHost = candidate.RemoteAddress;
        if (!_reportedAutoDetectedHost)
        {
            _reportedAutoDetectedHost = true;
            var basis = candidate.RemotePort == FiveMDefaultPort
                ? "FiveM:s standardport"
                : $"en spelserverport som varit stabil i {StableEndpointPolls} mätningar";
            context.StatusSink.Report(
                StatusLevel.Info,
                Name,
                $"Probe-host härleddes automatiskt till {candidate.RemoteAddress}:{candidate.RemotePort} via {basis}. Sätt ProbeHost manuellt om det är fel server.");
        }

        return _autoDetectedProbeHost;
    }

    private void ResetAutoDetection(int processId)
    {
        _autoDetectionProcessId = processId;
        _autoDetectedProbeHost = null;
        _reportedAutoDetectedHost = false;
        _candidateEndpointKey = null;
        _candidateObservations = 0;
        _latchedHostAbsentPolls = 0;
        _reportedNoPlausibleHost = false;
    }

    /// <summary>
    /// Picks the endpoint most likely to be the game server, or none at all.
    /// </summary>
    /// <remarks>
    /// Returning "the first routable connection" as a last resort was the bug: FiveM's embedded browser
    /// holds connections to CDNs, telemetry endpoints and the CitizenFX backend for the whole session,
    /// and any of them could win that fallback. There is no last resort any more — an endpoint is either
    /// on a plausible game server port or it is not a candidate.
    /// </remarks>
    internal static RemoteEndpointInfo? SelectCandidate(IReadOnlyList<RemoteEndpointInfo> remoteEndpoints)
    {
        var plausible = remoteEndpoints
            .Where(item => IsRoutableRemote(item.RemoteAddress) && IsGameServerPort(item.RemotePort))
            .ToArray();

        return plausible.FirstOrDefault(item => item.RemotePort == FiveMDefaultPort) ?? plausible.FirstOrDefault();
    }

    internal static bool IsGameServerPort(int port)
    {
        return port is >= FiveMPortRangeStart and <= FiveMPortRangeEnd;
    }

    private static bool IsRoutableRemote(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed))
        {
            return false;
        }

        if (IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any))
        {
            return false;
        }

        var octets = parsed.GetAddressBytes();
        if (octets.Length != 4)
        {
            return true;
        }

        // Link-local (169.254/16) and multicast are never a game server.
        return !(octets[0] == 169 && octets[1] == 254) && octets[0] < 224;
    }

    public void Dispose()
    {
        _ping.Dispose();
    }

    private async Task<NetworkProbeSample> ProbeAsync(string host, DateTimeOffset timestamp)
    {
        try
        {
            var reply = await _ping.SendPingAsync(host, 300).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new NetworkProbeSample(timestamp, host, reply.RoundtripTime, Success: true)
                : new NetworkProbeSample(timestamp, host, null, Success: false, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return new NetworkProbeSample(timestamp, host, null, Success: false, ex.Message);
        }
    }

    private static IReadOnlyList<RemoteEndpointInfo> ReadTcpEndpoints(int processId, string? endpointHint)
    {
        var size = 0;
        _ = WindowsInterop.GetExtendedTcpTable(IntPtr.Zero, ref size, sort: true, WindowsInterop.AfInet, TcpTableClass.OwnerPidAll);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            var result = WindowsInterop.GetExtendedTcpTable(buffer, ref size, sort: true, WindowsInterop.AfInet, TcpTableClass.OwnerPidAll);
            if (result != 0)
            {
                return [];
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var tableStart = IntPtr.Add(buffer, sizeof(int));
            var endpoints = new List<RemoteEndpointInfo>();

            for (var index = 0; index < rowCount; index++)
            {
                var rowPointer = IntPtr.Add(tableStart, index * rowSize);
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                if (row.OwningPid != processId)
                {
                    continue;
                }

                var remotePort = ConvertPort(row.RemotePort);
                if (remotePort == 0 || row.RemoteAddress == 0)
                {
                    continue;
                }

                endpoints.Add(new RemoteEndpointInfo(
                    "TCP",
                    new IPAddress(BitConverter.GetBytes(row.RemoteAddress)).ToString(),
                    remotePort,
                    endpointHint));
            }

            return endpoints
                .GroupBy(item => $"{item.Protocol}:{item.RemoteAddress}:{item.RemotePort}")
                .Select(group => group.First())
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<int> ReadUdpPorts(int processId)
    {
        var size = 0;
        _ = WindowsInterop.GetExtendedUdpTable(IntPtr.Zero, ref size, sort: true, WindowsInterop.AfInet, UdpTableClass.OwnerPid);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            var result = WindowsInterop.GetExtendedUdpTable(buffer, ref size, sort: true, WindowsInterop.AfInet, UdpTableClass.OwnerPid);
            if (result != 0)
            {
                return [];
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var tableStart = IntPtr.Add(buffer, sizeof(int));
            var ports = new List<int>();

            for (var index = 0; index < rowCount; index++)
            {
                var rowPointer = IntPtr.Add(tableStart, index * rowSize);
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPointer);
                if (row.OwningPid == processId)
                {
                    ports.Add(ConvertPort(row.LocalPort));
                }
            }

            return ports.Distinct().Order().ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ConvertPort(byte[] bytes)
    {
        return bytes.Length < 2 ? 0 : (bytes[0] << 8) + bytes[1];
    }
}