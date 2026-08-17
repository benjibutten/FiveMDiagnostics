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
    /// How many consecutive polls an endpoint must persist before it is treated as the game server. A
    /// CDN fetch, an overlay's telemetry call or a launcher update appears for a poll or two; the
    /// session connection stays for the whole session.
    /// </summary>
    private const int StableEndpointPolls = 3;

    private readonly Ping _ping = new();
    private string? _autoDetectedProbeHost;
    private bool _reportedAutoDetectedHost;
    private int? _autoDetectionProcessId;
    private string? _candidateEndpointKey;
    private int _candidateObservations;
    private int _latchedHostAbsentPolls;

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
    /// about the game server. So a candidate is accepted only on FiveM's default port, or after it has
    /// persisted across several polls, and the latch is dropped whenever the target process changes so a
    /// stale address cannot leak into the next session.
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
                : $"en anslutning som varit stabil i {StableEndpointPolls} mätningar";
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
    }

    private static RemoteEndpointInfo? SelectCandidate(IReadOnlyList<RemoteEndpointInfo> remoteEndpoints)
    {
        var routable = remoteEndpoints.Where(item => IsRoutableRemote(item.RemoteAddress)).ToArray();
        return routable.FirstOrDefault(item => item.RemotePort == FiveMDefaultPort) ?? routable.FirstOrDefault();
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