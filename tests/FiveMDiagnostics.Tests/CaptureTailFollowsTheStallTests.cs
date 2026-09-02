namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// The capture that stopped two seconds before the evening's worst frame.
/// </summary>
/// <remarks>
/// <para>
/// 1 September, 00:11. A freeze ran from 00:11:45 to 00:11:54. The capture triggered at 00:11:47,
/// waited its fixed two-second tail and wrote a trace covering 00:11:23–00:11:51 — so the 790, 677 and
/// 291 ms frames, the three largest of the whole evening, fell outside the file attached to the
/// incident named after them. Four further captures were refused in the same seconds because the first
/// was still writing, which is a real constraint: WPR cannot write two files at once.
/// </para>
/// <para>
/// Extending the capture that is already running is therefore the only way the file can cover the
/// event, and the tail is bounded because the ring buffer is: a tail that ran for a minute would
/// overwrite the run-up it was recorded to keep.
/// </para>
/// </remarks>
public sealed class CaptureTailFollowsTheStallTests
{
    /// <summary>The ordinary hitch. Nothing to follow, so nothing changes.</summary>
    [Fact]
    public async Task AStallThatIsAlreadyOverKeepsTheFixedTail()
    {
        var recorded = new List<TimeSpan>();
        var options = Options();

        var tail = await WprDeepCaptureService.WaitForRecoveryAsync(
            options,
            stallInProgress: () => false,
            delay: Record(recorded),
            CancellationToken.None);

        Assert.Equal(options.PostMarkerTail, tail);
        Assert.Equal([options.PostMarkerTail], recorded);
    }

    /// <summary>
    /// The 1 September case: frames still arriving late when the fixed tail runs out, so the capture
    /// keeps recording until they stop.
    /// </summary>
    [Fact]
    public async Task ATailIsExtendedWhileFramesAreStillArrivingLate()
    {
        var options = Options();
        var remaining = 6;

        var tail = await WprDeepCaptureService.WaitForRecoveryAsync(
            options,
            stallInProgress: () => remaining-- > 0,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        // Two fixed seconds plus six half-second polls: long enough to have covered the whole freeze.
        Assert.Equal(TimeSpan.FromSeconds(5), tail);
    }

    /// <summary>
    /// A stall that never ends must not be allowed to overwrite the run-up the ring buffer is holding,
    /// which is the half of the trace that explains the hitch.
    /// </summary>
    [Fact]
    public async Task TheTailStopsAtTheConfiguredCeiling()
    {
        var options = Options();

        var tail = await WprDeepCaptureService.WaitForRecoveryAsync(
            options,
            stallInProgress: () => true,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(options.MaxPostMarkerTail, tail);
    }

    /// <summary>
    /// Without a probe the behaviour is exactly what it was, which is what makes the capability optional
    /// rather than a change every backend has to absorb.
    /// </summary>
    [Fact]
    public async Task WithoutAProbeNothingIsExtended()
    {
        var options = Options();

        var tail = await WprDeepCaptureService.WaitForRecoveryAsync(
            options,
            stallInProgress: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(options.PostMarkerTail, tail);
    }

    /// <summary>
    /// A ceiling below the fixed tail would silently shorten the ordinary capture, so normalisation
    /// raises it instead.
    /// </summary>
    [Fact]
    public void TheCeilingCanNeverShortenTheFixedTail()
    {
        var options = new DeepCaptureOptions
        {
            PostMarkerTail = TimeSpan.FromSeconds(4),
            MaxPostMarkerTail = TimeSpan.FromSeconds(1),
        };

        options.Normalize();

        Assert.Equal(TimeSpan.FromSeconds(4), options.MaxPostMarkerTail);
    }

    private static DeepCaptureOptions Options()
    {
        var options = new DeepCaptureOptions
        {
            PostMarkerTail = TimeSpan.FromSeconds(2),
            MaxPostMarkerTail = TimeSpan.FromSeconds(12),
        };

        options.Normalize();
        return options;
    }

    private static Func<TimeSpan, CancellationToken, Task> Record(List<TimeSpan> recorded)
    {
        return (span, _) =>
        {
            recorded.Add(span);
            return Task.CompletedTask;
        };
    }
}
