using System.Globalization;

namespace FiveMDiagnostics.Integrations.Etw;

using FiveMDiagnostics.Core;

/// <summary>
/// Writes the .wprp that deep capture records through, replacing WPR's built-in profile stack.
/// </summary>
/// <remarks>
/// <para>
/// The built-in <c>GeneralProfile</c> enables syscall enter/exit tracing. On a 54 second capture that
/// was 88 of 132 million events and about 5 GB of a 6.9 GB trace, and the events could not even be
/// attributed to a thread — so the cost bought nothing. Everything the analysis actually reads is in
/// the keyword set below, which is why the same window fits in well under a gigabyte.
/// </para>
/// <para>
/// The second reason for a profile of our own is the ring buffer. Memory logging mode sizes its buffer
/// from the profile, not the command line, so how many seconds of history a marker can save is decided
/// here and nowhere else.
/// </para>
/// <para>
/// Both a Memory and a File variant are declared because WPR resolves the two from the same profile
/// name: the background session starts the Memory one, and the one-shot fallback that runs when the
/// ring buffer never came up needs the File one to exist.
/// </para>
/// <para>
/// The system collector is named <c>NT Kernel Logger</c> because that is one of the two names the
/// profile format documents for it, the other being <c>Circular Kernel Context Logger</c>. WPR
/// 10.0.26100 ignores the value and names the session <c>WPR_initiated_WprApp_WPR System Collector</c>
/// either way, so this is about not depending on that leniency rather than about a rejected profile.
/// </para>
/// </remarks>
public static class WprProfileWriter
{
    /// <summary>Profile name passed to WPR as <c>path!ProfileName</c>.</summary>
    public const string ProfileName = "FiveMStall";

    public const string FileName = "FiveMDiagnostics.wprp";

    /// <summary>
    /// Buffer size in kilobytes. WPR multiplies this by the buffer count to reach the ring's total, so
    /// the pair is chosen for the total rather than for either number on its own.
    /// </summary>
    private const int BufferSizeKilobytes = 1024;

    /// <summary>
    /// Event collector share of the ring, as a fraction of the system collector's. The DXGI/DWM
    /// providers produce a small fraction of the system provider's volume, so they need a small
    /// fraction of the memory.
    /// </summary>
    private const int EventBufferDivisor = 8;

    /// <summary>
    /// Writes the profile for these settings and returns its path, or null with a reason when it could
    /// not be written. A missing profile is not fatal — the caller falls back to the built-in ones.
    /// </summary>
    public static string? TryWrite(DiagnosticsSettings settings, out string? error)
    {
        error = null;

        if (settings.DeepCapture.CustomProfilePath is { Length: > 0 } custom)
        {
            if (File.Exists(custom))
            {
                return custom;
            }

            error = $"Den angivna WPR-profilen finns inte: {custom}.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(settings.WorkingDirectory);
            var path = Path.Combine(settings.WorkingDirectory, FileName);
            var content = Build(settings.DeepCapture.RingBufferMegabytes);

            // Rewritten every session rather than only when missing: the buffer size is a setting, and a
            // profile left over from a session that used a different one would silently win.
            File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Builds the profile XML for a ring buffer of the given size.
    /// </summary>
    /// <remarks>
    /// The keyword set is deliberately narrow, and every entry answers a question the app asks:
    /// <list type="bullet">
    /// <item><description><c>CSwitch</c>/<c>ReadyThread</c> — what a blocked thread was waiting for, which is the whole
    /// point of tracing a stall rather than sampling it.</description></item>
    /// <item><description><c>SampledProfile</c> — where CPU time went when a thread was running rather than waiting.</description></item>
    /// <item><description><c>DPC</c>/<c>Interrupt</c> — a driver holding the CPU at raised IRQL, which stalls every
    /// thread at once and is what <see cref="EtlArtifactParser"/> measures.</description></item>
    /// <item><description><c>DiskIO</c>/<c>DiskIOInit</c>/<c>FileIO</c>/<c>FileIOInit</c>/<c>HardFaults</c> — whether a
    /// stall was storage, which is exactly the question the performance counters cannot answer when
    /// they fail to open.</description></item>
    /// <item><description><c>MemoryInfo</c>/<c>MemoryInfoWS</c> — resident set behaviour, for a stall caused by paging
    /// rather than by the disk being slow.</description></item>
    /// </list>
    /// Stacks are enabled only for the events whose stacks get read. Stack walking is most of what an
    /// event costs, so enabling it broadly is how a trace reaches several gigabytes.
    /// </remarks>
    private static string Build(int ringBufferMegabytes)
    {
        var systemBuffers = Math.Max(1, ringBufferMegabytes * 1024 / BufferSizeKilobytes);
        var eventBuffers = Math.Max(1, systemBuffers / EventBufferDivisor);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!--
              Generated by FiveMDiagnostics. Edited copies are ignored: this file is rewritten every
              session so the buffer size follows DeepCapture.RingBufferMegabytes. Point
              DeepCapture.CustomProfilePath at your own file to keep changes.
            -->
            <WindowsPerformanceRecorder Version="1.0" Author="FiveMDiagnostics" Comments="FiveM stall analysis" Company="">
              <Profiles>
                <SystemCollector Id="SystemCollector_{ProfileName}" Name="NT Kernel Logger">
                  <BufferSize Value="{BufferSizeKilobytes.ToString(CultureInfo.InvariantCulture)}" />
                  <Buffers Value="{systemBuffers.ToString(CultureInfo.InvariantCulture)}" />
                </SystemCollector>

                <EventCollector Id="EventCollector_{ProfileName}" Name="FiveMDiagnostics Event Collector">
                  <BufferSize Value="{BufferSizeKilobytes.ToString(CultureInfo.InvariantCulture)}" />
                  <Buffers Value="{eventBuffers.ToString(CultureInfo.InvariantCulture)}" />
                </EventCollector>

                <SystemProvider Id="SystemProvider_{ProfileName}">
                  <Keywords>
                    <Keyword Value="ProcessThread" />
                    <Keyword Value="Loader" />
                    <Keyword Value="CSwitch" />
                    <Keyword Value="ReadyThread" />
                    <Keyword Value="SampledProfile" />
                    <Keyword Value="DPC" />
                    <Keyword Value="Interrupt" />
                    <Keyword Value="DiskIO" />
                    <Keyword Value="DiskIOInit" />
                    <Keyword Value="FileIO" />
                    <Keyword Value="FileIOInit" />
                    <Keyword Value="HardFaults" />
                    <Keyword Value="MemoryInfo" />
                    <Keyword Value="MemoryInfoWS" />
                  </Keywords>
                  <Stacks>
                    <Stack Value="CSwitch" />
                    <Stack Value="ReadyThread" />
                    <Stack Value="SampledProfile" />
                    <Stack Value="DiskReadInit" />
                    <Stack Value="DiskWriteInit" />
                    <Stack Value="DiskFlushInit" />
                  </Stacks>
                </SystemProvider>

                <!-- Present path. DxgKrnl explains a frame that the GPU finished but the compositor did
                     not put on screen, which is what a "Composed:" present mode looks like from below. -->
                <EventProvider Id="EventProvider_DxgKrnl" Name="802ec45a-1e99-4b83-9920-87c98277ba9d" Level="4" />
                <EventProvider Id="EventProvider_DwmCore" Name="9e9bba3c-2e38-40cb-99f4-9e8281425164" Level="4" />

                <Profile Id="{ProfileName}.Verbose.Memory"
                         Name="{ProfileName}"
                         Description="FiveM stall analysis, ring buffer"
                         DetailLevel="Verbose"
                         LoggingMode="Memory">
                  <Collectors>
                    <SystemCollectorId Value="SystemCollector_{ProfileName}">
                      <SystemProviderId Value="SystemProvider_{ProfileName}" />
                    </SystemCollectorId>
                    <EventCollectorId Value="EventCollector_{ProfileName}">
                      <EventProviders>
                        <EventProviderId Value="EventProvider_DxgKrnl" />
                        <EventProviderId Value="EventProvider_DwmCore" />
                      </EventProviders>
                    </EventCollectorId>
                  </Collectors>
                </Profile>

                <Profile Id="{ProfileName}.Verbose.File"
                         Name="{ProfileName}"
                         Description="FiveM stall analysis, file mode"
                         DetailLevel="Verbose"
                         LoggingMode="File"
                         Base="{ProfileName}.Verbose.Memory" />

                <Profile Id="{ProfileName}.Light.Memory"
                         Name="{ProfileName}"
                         Description="FiveM stall analysis, ring buffer"
                         DetailLevel="Light"
                         LoggingMode="Memory"
                         Base="{ProfileName}.Verbose.Memory" />

                <Profile Id="{ProfileName}.Light.File"
                         Name="{ProfileName}"
                         Description="FiveM stall analysis, file mode"
                         DetailLevel="Light"
                         LoggingMode="File"
                         Base="{ProfileName}.Verbose.Memory" />
              </Profiles>
            </WindowsPerformanceRecorder>

            """;
    }
}
