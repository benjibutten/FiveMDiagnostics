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
    private bool? _lastProcessRunning;
    private DateTimeOffset? _disconnectedSince;
    private bool _reportedConnectionWarning;

    public string Name => "OBS";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        var sessionStart = context.UtcNow();
        var everConnected = false;

        // The manager reuses one collector across sessions, so a state remembered from the previous
        // evening would swallow this evening's first transition.
        _lastProcessRunning = null;
        _disconnectedSince = null;
        _reportedConnectionWarning = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (context.ProcessResolver.TryGetTargetProcess() is not null)
                {
                    var sample = await PollAsync(context.Settings.Obs, cancellationToken).ConfigureAwait(false);
                    everConnected |= sample.IsConnected;
                    ReportProcessTransition(context, sample);
                    ReportConnectionHealth(context, sample);
                    await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(context.Settings.Obs.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs on the way out, cancellation included, because that is when the session is ending and
            // the pointer is worth having. Reported rather than sampled: OBS writes its lag totals when
            // an output stops, which is usually a minute or two after the diagnostics session, so the
            // numbers are genuinely not available yet and the honest thing is to say where they will be.
            if (!everConnected)
            {
                ReportLogFallback(context, sessionStart);
            }

            await ResetSocketAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a status line whenever OBS starts or stops during a session.
    /// </summary>
    /// <remarks>
    /// OBS costs several hundred megabytes of VRAM and hooks itself into the game's present path, so a
    /// session where it starts or stops is two experiments rather than one — and the only reason we know
    /// that is a session where it happened to shut down mid-evening and the incidents in the log carried
    /// its state by accident. The comparison it made possible was the most informative measurement of
    /// that whole investigation. It should not depend on luck: a transition is a session event, and this
    /// is the line that makes it one. Reported rather than sampled, because the journal is where the
    /// timeline is reconstructed from afterwards.
    /// </remarks>
    private void ReportProcessTransition(CollectorContext context, ObsTelemetrySample sample)
    {
        var running = sample.IsProcessRunning;
        if (_lastProcessRunning == running)
        {
            return;
        }

        var previous = _lastProcessRunning;
        _lastProcessRunning = running;

        context.StatusSink.Report(
            StatusLevel.Info,
            Name,
            previous is null
                ? running
                    ? "OBS körde när sessionen startade."
                    : "OBS körde inte när sessionen startade."
                : running
                    ? "OBS startade. Allt efter den här punkten mäts med OBS igång — VRAM-fotavtryck och "
                        + "game capture-hooken i present-vägen tillkommer."
                    : "OBS avslutades. Allt efter den här punkten mäts utan OBS, vilket gör perioderna "
                        + "före och efter jämförbara som ett A/B-test.");
    }

    /// <summary>
    /// Says out loud, while there is still time to fix it, that OBS is running and not answering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state was already recorded — every incident of the session of 27 August carried "process
    /// körs, WebSocket frånkopplad" and four empty fields after it — and being recorded is not the same
    /// as being noticed. That session ran 5 h 47 min without a single OBS measurement, on the evening
    /// the stream was failing to start, and the gap was found the next day by reading incident reports.
    /// Render lag and skipped frames are the only measurements this app has of the stream's own health,
    /// and they were missing on the one night they were the question.
    /// </para>
    /// <para>
    /// A warning while the session is running is worth a great deal more than the same fact in a report
    /// afterwards: the fix is two clicks in OBS and the rest of the evening is then measured. Reported
    /// once, with the recovery reported once as well, so it cannot become the same background noise it
    /// replaces.
    /// </para>
    /// </remarks>
    internal void ReportConnectionHealth(CollectorContext context, ObsTelemetrySample sample)
    {
        if (sample.IsConnected)
        {
            if (_reportedConnectionWarning)
            {
                context.StatusSink.Report(
                    StatusLevel.Info,
                    Name,
                    "OBS WebSocket är ansluten. Render lag och skippade frames mäts från och med nu.");
            }

            _disconnectedSince = null;
            _reportedConnectionWarning = false;
            return;
        }

        if (!sample.IsProcessRunning)
        {
            // Nothing to warn about: OBS is not running, and the reports say so rather than leaving
            // fields blank. The warning is armed again, because the next launch is a new chance to get
            // the socket right and a second silent evening would otherwise pass unremarked — OBS being
            // restarted mid-session is exactly when somebody has just changed something in it.
            _disconnectedSince = null;
            _reportedConnectionWarning = false;
            return;
        }

        var now = context.UtcNow();
        _disconnectedSince ??= now;

        if (_reportedConnectionWarning || now - _disconnectedSince.Value < context.Settings.Obs.ConnectionWarningDelay)
        {
            return;
        }

        _reportedConnectionWarning = true;
        context.StatusSink.Report(
            StatusLevel.Warning,
            Name,
            $"OBS körs men WebSocket svarar inte på {context.Settings.Obs.Endpoint}. Aktivera Verktyg → "
            + "WebSocket Server Settings i OBS (och fyll i samma lösenord här) — annars saknas render lag, "
            + "skippade renderframes och skippade outputframes för hela sessionen, vilket är den enda mätning "
            + "appen har av streamens egen hälsa.");
    }

    /// <summary>
    /// Names the OBS log that covers this session, so the render and encoding lag figures can be
    /// recovered by importing it even though the WebSocket never connected.
    /// </summary>
    /// <remarks>
    /// Four sessions running, the WebSocket was not connected for a single incident, and the render and
    /// encoding lag were simply absent from every report. OBS had been writing them to a text file the
    /// whole time. This does not make the file into telemetry — one figure for a whole session cannot be
    /// lined up against an incident — but it turns "no OBS data" into "here is where the OBS data is".
    /// </remarks>
    private void ReportLogFallback(CollectorContext context, DateTimeOffset sessionStart)
    {
        try
        {
            var summary = ObsSessionLogReader.TryReadLatest(sessionStart, context.UtcNow());
            if (summary is null)
            {
                return;
            }

            context.StatusSink.Report(
                StatusLevel.Info,
                Name,
                $"OBS WebSocket var aldrig ansluten, så render- och encoding lag saknas per incident. OBS egen logg "
                + $"({summary.LogPath}) täcker sessionen — importera den som artefakt när streamen avslutats. "
                + $"Nuvarande innehåll: {summary.Describe()}");
        }
        catch (Exception ex)
        {
            // A missing or unreadable log is not a session failure; it just means the fallback has
            // nothing to offer.
            context.StatusSink.Report(StatusLevel.Info, Name, $"OBS-loggen kunde inte läsas: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _socketLock.Dispose();
        _socket?.Dispose();
    }

    private async Task<ObsTelemetrySample> PollAsync(ObsOptions options, CancellationToken cancellationToken)
    {
        var processRunning = IsObsProcessRunning();
        if (!processRunning)
        {
            await ResetSocketAsync().ConfigureAwait(false);
            return CreateDisconnectedSample(processRunning: false);
        }

        try
        {
            await EnsureConnectedAsync(options, cancellationToken).ConfigureAwait(false);
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                return CreateDisconnectedSample(processRunning: true);
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
                IsRecording: TryGetBool(record, "outputActive"),
                IsProcessRunning: true);
        }
        catch
        {
            await ResetSocketAsync().ConfigureAwait(false);
            return CreateDisconnectedSample(processRunning: true);
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

    private static ObsTelemetrySample CreateDisconnectedSample(bool processRunning)
    {
        return new ObsTelemetrySample(DateTimeOffset.UtcNow, false, null, null, null, null, null, null, false, false, processRunning);
    }

    private static bool IsObsProcessRunning()
    {
        var processes = Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")).ToArray();
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
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
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True;
    }
}
