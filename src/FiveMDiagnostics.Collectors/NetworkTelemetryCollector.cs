using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class NetworkTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly Ping _ping = new();

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

                if (!string.IsNullOrWhiteSpace(context.Settings.ServerProfile.ProbeHost))
                {
                    var probeSample = await ProbeAsync(context.Settings.ServerProfile.ProbeHost!, timestamp).ConfigureAwait(false);
                    await context.Writer.WriteAsync(probeSample, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(context.Settings.NetworkPollingInterval, cancellationToken).ConfigureAwait(false);
        }
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