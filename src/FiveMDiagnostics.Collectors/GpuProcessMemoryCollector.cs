using System.ComponentModel;
using System.Diagnostics;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Samples which processes hold the adapter's memory.
/// </summary>
/// <remarks>
/// The adapter-wide VRAM figure told four sessions of investigation that stalls cluster above a
/// threshold, and nothing about what to do: the answer differs entirely depending on whether the game,
/// the capture software or a browser owns the last gigabyte. Every incident report carried the caveat
/// "VRAM is measured per card, not per process" — this collector is what removes it.
/// </remarks>
public sealed class GpuProcessMemoryCollector : ITelemetryCollector
{
    private readonly Func<(IGpuProcessMemoryProbe? Probe, string? Error)> _openProbe;

    private readonly HashSet<int> _reportedImplausible = [];

    private bool _reportedLogFailure;
    private string? _lastReadError;

    public GpuProcessMemoryCollector()
        : this(() =>
        {
            var probe = GpuProcessMemoryProbe.TryOpen(out var error);
            return (probe, error);
        })
    {
    }

    internal GpuProcessMemoryCollector(Func<(IGpuProcessMemoryProbe? Probe, string? Error)> openProbe)
    {
        _openProbe = openProbe;
    }

    public string Name => "GpuProcessMemory";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.Gpu.Enabled || !context.Settings.Gpu.ProcessMemoryEnabled)
        {
            return;
        }

        // The session manager holds one instance of each collector and runs it again for every session,
        // so anything remembered here describes the *previous* evening until it is cleared. An earlier
        // failure that survived into a working session would disable the feature with no way to notice.
        _reportedLogFailure = false;
        _lastReadError = null;
        _reportedImplausible.Clear();

        // Held as locals rather than fields: an early return then cannot leave a live counter query or
        // an open file behind, which is exactly what a "this session is unavailable" flag used to do.
        using var probe = OpenProbe(context);
        if (probe is null)
        {
            return;
        }

        using var csvLog = OpenCsvLog(context);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (context.ProcessResolver.TryGetTargetProcess() is { } target)
            {
                var sample = Sample(context, probe, target.ProcessId);

                csvLog?.Append(sample);
                ReportLogFailureIfAny(context, csvLog);
                ReportImplausibleReadingsIfAny(context, sample);

                await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Interval(context), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Floored at a second. The setting exists to make the sample rarer than the adapter poll, not to
    /// turn a whole-system counter query into a per-frame one.
    /// </summary>
    private static TimeSpan Interval(CollectorContext context)
    {
        var configured = context.Settings.Gpu.ProcessMemoryInterval;
        return configured < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : configured;
    }

    private IGpuProcessMemoryProbe? OpenProbe(CollectorContext context)
    {
        var (probe, error) = _openProbe();
        context.StatusSink.Report(
            probe is null ? StatusLevel.Warning : StatusLevel.Info,
            Name,
            probe is null
                ? $"VRAM per process kunde inte mätas: {error} Adapterns totala VRAM mäts fortfarande."
                : "VRAM per process mäts via Windows GPU-räknare.");

        return probe;
    }

    private GpuProcessMemoryCsvLog? OpenCsvLog(CollectorContext context)
    {
        var csvLog = GpuProcessMemoryCsvLog.TryOpen(context.Settings.WorkingDirectory, context.UtcNow(), out var error);

        context.StatusSink.Report(
            csvLog is null ? StatusLevel.Warning : StatusLevel.Info,
            Name,
            csvLog is null
                ? $"VRAM-per-process-loggen kunde inte skapas: {error}. Data finns då bara i incidentfönstren."
                : $"VRAM per process loggas kontinuerligt till {csvLog.Path}.");

        return csvLog;
    }

    /// <summary>Hands over the log's first failure once, so a full disk produces one warning rather than one per sample.</summary>
    private void ReportLogFailureIfAny(CollectorContext context, GpuProcessMemoryCsvLog? csvLog)
    {
        if (_reportedLogFailure || csvLog?.Failure is not { } failure)
        {
            return;
        }

        _reportedLogFailure = true;
        context.StatusSink.Report(StatusLevel.Warning, Name, failure);
    }

    /// <summary>
    /// Names a process whose reading is impossible for the adapter, once per process per session.
    /// </summary>
    /// <remarks>
    /// The reading that prompted this stood in 145 incident reports as the largest VRAM holder before
    /// anyone noticed, because nothing in the app was surprised by 213 GB on a 10 GB card. One warning
    /// naming the process and its instance count is what turns the next occurrence into a five minute
    /// diagnosis instead of a session of arithmetic against the adapter figure.
    /// </remarks>
    private void ReportImplausibleReadingsIfAny(CollectorContext context, GpuProcessMemorySample sample)
    {
        foreach (var process in sample.ImplausibleProcesses)
        {
            if (!_reportedImplausible.Add(process.ProcessId))
            {
                continue;
            }

            context.StatusSink.Report(
                StatusLevel.Warning,
                Name,
                $"VRAM-avläsningen för {process.ProcessName} är omöjlig: {process.DedicatedGigabytes:F0} GB "
                + $"summerat över {process.InstanceCount} räknarinstanser. Raden loggas men utesluts ur "
                + $"rapporternas topplista. Övriga processer påverkas inte.{DescribeInstances(process)}");
        }
    }

    /// <summary>
    /// Lists the counter instances behind an impossible reading, with the adapter each belongs to.
    /// </summary>
    /// <remarks>
    /// The count alone was the previous answer and it turned out to be the wrong question: obs64
    /// reported 209 GB with an instance count of one, all session. What a single instance leaves open is
    /// which adapter it is on and whether shared memory is being counted as dedicated, and both are in
    /// the instance name and its own pair of figures. Printed once per process per session, so the cost
    /// is a line in the journal for a case that should never happen.
    /// </remarks>
    private static string DescribeInstances(GpuProcessMemoryUsage process)
    {
        if (process.Instances is not { Count: > 0 } instances)
        {
            return string.Empty;
        }

        var listed = instances
            .Take(6)
            .Select(instance =>
                $"{instance.InstanceName} (adapter {instance.Adapter ?? "okänd"}, "
                + $"dedikerat {instance.DedicatedBytes / 1024d / 1024:F0} MB, "
                + $"delat {instance.SharedBytes / 1024d / 1024:F0} MB)");

        var remaining = instances.Count > 6 ? $" och {instances.Count - 6} till" : string.Empty;
        return $" Instanser: {string.Join("; ", listed)}{remaining}.";
    }

    private GpuProcessMemorySample Sample(CollectorContext context, IGpuProcessMemoryProbe probe, int anchorProcessId)
    {
        var timestamp = context.UtcNow();

        if (!probe.TryRead(out var dedicated, out var shared, out var error))
        {
            // Reported once per distinct message: a counter that starts failing usually keeps failing,
            // and the status list is also where the user reads that a session is healthy.
            if (error is not null && error != _lastReadError)
            {
                _lastReadError = error;
                context.StatusSink.Report(StatusLevel.Warning, Name, $"VRAM per process kunde inte läsas: {error}");
            }

            return new GpuProcessMemorySample(timestamp, IsAvailable: false, [], error);
        }

        _lastReadError = null;
        var processes = GpuProcessMemoryAggregator.Aggregate(
            dedicated,
            shared,
            ProcessNames(),
            context.Settings.Gpu.ProcessMemoryTopCount,
            anchorProcessId);

        // Computed from the same readings rather than from the table above, which is cut to the largest
        // holders: the reconciliation against the adapter needs everything that is accounted for, not
        // everything that fitted in the report.
        var accounted = GpuProcessMemoryAggregator.TotalDedicatedBytes(dedicated, shared, anchorProcessId);

        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            processes,
            UnavailableReason: null,
            AllProcessesDedicatedBytes: accounted);
    }

    /// <summary>
    /// Process ids to names, from one system-wide enumeration.
    /// </summary>
    /// <remarks>
    /// One <see cref="Process.GetProcesses"/> call rather than a lookup per counter instance: resolving
    /// a single id queries the whole process table anyway, so doing it for each of forty instances
    /// repeats that work forty times. Built fresh each sample rather than cached, because Windows
    /// reuses process ids and a cached name would quietly attribute one program's memory to another.
    /// <para>
    /// Nothing in here is allowed to escape. A protected process — an anti-cheat driver's service, a
    /// DRM helper — throws <see cref="Win32Exception"/> on a property as ordinary as its name, and this
    /// runs inside a game session on a machine that has both. Losing one name costs a row that says
    /// "pid 4242"; losing the collector costs the whole evening's VRAM history.
    /// </para>
    /// </remarks>
    private static Dictionary<int, string> ProcessNames()
    {
        var names = new Dictionary<int, string>();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or UnauthorizedAccessException)
        {
            // No names this sample; the byte counts are still worth reporting.
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                names[process.Id] = process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or UnauthorizedAccessException)
            {
                // Exited between enumeration and read, or refused to be named. The aggregator falls
                // back to the id.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }
}
