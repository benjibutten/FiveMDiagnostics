using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FiveMDiagnostics.Integrations.Obs;

using FiveMDiagnostics.Core;

public sealed class ObsTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _requestId;
    private DateTimeOffset _lastConnectAttemptUtc = DateTimeOffset.MinValue;

    public string Name => "OBS";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (context.ProcessResolver.TryGetTargetProcess() is not null)
            {
                var sample = await PollAsync(context.Settings.Obs, cancellationToken).ConfigureAwait(false);
                await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(context.Settings.Obs.PollingInterval, cancellationToken).ConfigureAwait(false);
        }

        await ResetSocketAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _socketLock.Dispose();
        _socket?.Dispose();
    }

    private async Task<ObsTelemetrySample> PollAsync(ObsOptions options, CancellationToken cancellationToken)
    {
        if (Process.GetProcessesByName("obs64").Length == 0 && Process.GetProcessesByName("obs32").Length == 0)
        {
            await ResetSocketAsync().ConfigureAwait(false);
            return CreateDisconnectedSample();
        }

        try
        {
            await EnsureConnectedAsync(options, cancellationToken).ConfigureAwait(false);
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                return CreateDisconnectedSample();
            }

            var stats = await SendRequestAsync("GetStats", null, cancellationToken).ConfigureAwait(false);
            var stream = await SendRequestAsync("GetStreamStatus", null, cancellationToken).ConfigureAwait(false);
            var record = await SendRequestAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);

            return new ObsTelemetrySample(
                DateTimeOffset.UtcNow,
                IsConnected: true,
                ActiveFps: TryGetDouble(stats, "activeFps"),
                AverageFrameRenderTimeMs: TryGetDouble(stats, "averageFrameRenderTime"),
                RenderSkippedFrames: TryGetLong(stats, "renderSkippedFrames"),
                OutputSkippedFrames: TryGetLong(stats, "outputSkippedFrames"),
                CpuUsagePercent: TryGetDouble(stats, "cpuUsage"),
                MemoryUsageMb: TryGetDouble(stats, "memoryUsage"),
                IsStreaming: TryGetBool(stream, "outputActive") || TryGetBool(stream, "outputReconnecting"),
                IsRecording: TryGetBool(record, "outputActive"));
        }
        catch
        {
            await ResetSocketAsync().ConfigureAwait(false);
            return CreateDisconnectedSample();
        }
    }

    private async Task EnsureConnectedAsync(ObsOptions options, CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastConnectAttemptUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastConnectAttemptUtc = DateTimeOffset.UtcNow;
        await ResetSocketAsync().ConfigureAwait(false);
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(options.Endpoint), cancellationToken).ConfigureAwait(false);

        using var hello = await ReceiveEnvelopeAsync(_socket, cancellationToken).ConfigureAwait(false);
        var authToken = BuildAuthenticationToken(hello.RootElement, options.Password);

        var identifyPayload = authToken is null
            ? JsonSerializer.Serialize(new { op = 1, d = new { rpcVersion = 1 } })
            : JsonSerializer.Serialize(new { op = 1, d = new { rpcVersion = 1, authentication = authToken } });

        await SendAsync(_socket, identifyPayload, cancellationToken).ConfigureAwait(false);
        using var identified = await ReceiveEnvelopeAsync(_socket, cancellationToken).ConfigureAwait(false);
        var opCode = identified.RootElement.GetProperty("op").GetInt32();
        if (opCode != 2)
        {
            throw new InvalidOperationException("OBS identify handshake misslyckades.");
        }
    }

    private async Task<JsonElement> SendRequestAsync(string requestType, object? requestData, CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("OBS socket är inte ansluten.");
        }

        await _socketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestId = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
            var payload = JsonSerializer.Serialize(new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId,
                    requestData,
                },
            });

            await SendAsync(_socket, payload, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var response = await ReceiveEnvelopeAsync(_socket, cancellationToken).ConfigureAwait(false);
                var root = response.RootElement;
                if (root.GetProperty("op").GetInt32() != 7)
                {
                    continue;
                }

                var responseData = root.GetProperty("d");
                if (!string.Equals(responseData.GetProperty("requestId").GetString(), requestId, StringComparison.Ordinal))
                {
                    continue;
                }

                var status = responseData.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    throw new InvalidOperationException(status.GetProperty("comment").GetString() ?? $"OBS request {requestType} misslyckades.");
                }

                return responseData.GetProperty("responseData").Clone();
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveEnvelopeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("OBS websocket stängdes oväntat.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? BuildAuthenticationToken(JsonElement helloEnvelope, string password)
    {
        if (!helloEnvelope.TryGetProperty("d", out var helloData))
        {
            return null;
        }

        if (!helloData.TryGetProperty("authentication", out var authData))
        {
            return null;
        }

        var salt = authData.GetProperty("salt").GetString();
        var challenge = authData.GetProperty("challenge").GetString();
        if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(challenge))
        {
            return null;
        }

        var secret = ComputeBase64Sha256(password + salt);
        return ComputeBase64Sha256(secret + challenge);
    }

    private static string ComputeBase64Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes);
    }

    private async Task ResetSocketAsync()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    private static ObsTelemetrySample CreateDisconnectedSample()
    {
        return new ObsTelemetrySample(DateTimeOffset.UtcNow, false, null, null, null, null, null, null, false, false);
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) ? value : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : null;
    }

    private static bool TryGetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True
            || (property.ValueKind == JsonValueKind.False ? false : false);
    }
}