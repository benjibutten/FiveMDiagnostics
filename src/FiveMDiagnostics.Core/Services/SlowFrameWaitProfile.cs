namespace FiveMDiagnostics.Core;

/// <summary>
/// Counts how many of a session's large frames still had CPU slack left, which is the one number that
/// separates a pipeline out of headroom from a thread that stopped.
/// </summary>
/// <remarks>
/// <para>
/// The sharpest single observation of the 30 August review — "none of the 35 frames over 100 ms waited"
/// — was worked out by hand from the exported CSV. It is free to compute: <c>MsCPUWait</c> travels with
/// every frame PresentMon v2 produces, and a frame that lost 300 ms while waiting 0.2 ms of it cannot
/// have been waiting on anything. That one line separates the class of evening where a thread is
/// blocked from the class where the machine is simply working flat out, and the app has never printed
/// it.
/// </para>
/// <para>
/// A hundred milliseconds rather than a multiple of the cadence. This measures the frames somebody will
/// go looking for an explanation of, and that population is the same on every machine — six missed
/// frames at 60 Hz, twelve at 120 — which is what makes the figure comparable between evenings.
/// <see cref="FramePacingMonitor"/> already answers the relative question minute by minute.
/// </para>
/// </remarks>
public sealed class SlowFrameWaitProfile
{
    /// <summary>Frame time above which a frame is one somebody will ask about.</summary>
    public const double SlowFrameMs = 100;

    /// <summary>
    /// Wait below which a frame is counted as not having waited at all.
    /// </summary>
    /// <remarks>
    /// The same millisecond <see cref="FramePacingOptions.SaturatedCpuWaitMs"/> uses, and the same one
    /// the correlation engine refuses to rank a thread-wait hypothesis under. A frame with less than a
    /// millisecond of slack inside a 100 ms frame spent 99% of itself doing something other than
    /// waiting.
    /// </remarks>
    public const double WaitedMs = 1.0;

    /// <summary>
    /// Slow frames retained. Tens per evening is the ordinary case and a very bad one produced 120, so
    /// the bound is only there to keep a broken capture from growing the list without limit.
    /// </summary>
    private const int Capacity = 8192;

    private readonly object _sync = new();
    private readonly List<double> _waits = [];

    private int _slowFrames;
    private int _withoutColumn;
    private int _waited;

    /// <summary>Folds one frame in. Frames below the bar cost a single comparison.</summary>
    public void Observe(FrameTelemetrySample sample)
    {
        if (sample.FrameTimeMs < SlowFrameMs)
        {
            return;
        }

        lock (_sync)
        {
            _slowFrames++;

            if (sample.CpuWaitMs is not { } wait)
            {
                // PresentMon v1, or a capture that lost the column. Counted separately rather than as a
                // frame that did not wait, which is a claim the data cannot support.
                _withoutColumn++;
                return;
            }

            if (wait >= WaitedMs)
            {
                _waited++;
            }

            if (_waits.Count < Capacity)
            {
                _waits.Add(wait);
            }
        }
    }

    /// <summary>The distribution, or null when the session produced no frame large enough to ask about.</summary>
    /// <remarks>
    /// A session whose capture carried no <c>MsCPUWait</c> at all still has something to report — how
    /// many large frames it had, and that nothing measured them. Returning null there dropped the count
    /// along with the distribution, so an evening of 35 unmeasured 300 ms frames read exactly like an
    /// evening that had none.
    /// </remarks>
    public SlowFrameWaitReport? Summary()
    {
        lock (_sync)
        {
            if (_slowFrames == 0)
            {
                return null;
            }

            var sorted = _waits.Order().ToArray();
            return new SlowFrameWaitReport(
                _slowFrames,
                _withoutColumn,
                _waited,
                sorted.Length > 0 ? sorted[sorted.Length / 2] : null,
                sorted.Length > 0 ? sorted[^1] : null);
        }
    }
}

/// <summary>How much CPU slack the session's largest frames still had.</summary>
/// <param name="WithoutColumn">Slow frames whose capture carried no <c>MsCPUWait</c> at all.</param>
/// <param name="Waited">Slow frames that still had at least <see cref="SlowFrameWaitProfile.WaitedMs"/> of slack.</param>
/// <param name="MedianWaitMs">Null when not one large frame carried the column, which is not the same as no wait.</param>
/// <param name="MaxWaitMs">Null on the same condition as <paramref name="MedianWaitMs"/>.</param>
public sealed record SlowFrameWaitReport(
    int SlowFrames,
    int WithoutColumn,
    int Waited,
    double? MedianWaitMs,
    double? MaxWaitMs)
{
    /// <summary>Slow frames the column was actually present for, which is what the counts are of.</summary>
    public int Measured => SlowFrames - WithoutColumn;

    /// <summary>
    /// True when effectively none of the large frames waited, which rules out a blocked thread as the
    /// explanation for them however tempting the trace looks.
    /// </summary>
    public bool NoneWaited => Measured > 0 && Waited == 0;

    public string Message
    {
        get
        {
            if (MedianWaitMs is not { } median || MaxWaitMs is not { } max)
            {
                // Every large frame came from a capture without the column. The count is still worth
                // printing, and the silence about whether they waited is the whole sentence.
                return $"MsCPUWait: ingen av sessionens {SlowFrames} frames över {SlowFrameWaitProfile.SlowFrameMs:F0} ms "
                    + "bar kolumnen, så det går inte att säga om de väntade. Det är PresentMon v1 eller en capture "
                    + "som tappade kolumnen — inte ett belägg för att de inte väntade.";
            }

            var missing = WithoutColumn > 0
                ? $" {WithoutColumn} frames till saknade kolumnen och räknas inte."
                : string.Empty;

            var verdict = NoneWaited
                ? " Ingen av dem väntade, så en blockerad tråd förklarar dem inte: tiden gick till "
                    + "exekvering eller GPU."
                : string.Empty;

            return $"MsCPUWait: {Waited} av {Measured} frames över {SlowFrameWaitProfile.SlowFrameMs:F0} ms "
                + $"hade CPU-marginal kvar (≥{SlowFrameWaitProfile.WaitedMs:F1} ms); median {median:F1} ms, "
                + $"störst {max:F1} ms.{missing}{verdict}";
        }
    }
}
