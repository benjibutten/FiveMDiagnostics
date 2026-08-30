namespace FiveMDiagnostics.Core;

/// <summary>
/// Says so when the attached displays are not running the same refresh rate.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most useful line the app can write at session start, and it is written because
/// its absence cost nine sessions. The machine ran a 120 Hz primary beside a 60 Hz secondary. Both
/// numbers are unremarkable alone, and the session log recorded only the first of them. Together they
/// forced DWM to resample the game's window every frame, and one frame in nine reached the screen a
/// refresh early or a refresh late — all evening, every evening, invisible in frame time because the
/// frames themselves were on time.
/// </para>
/// <para>
/// The condition needs the game's present mode to matter. A title that gets its own hardware plane is
/// not composed by DWM and is not resampled; the ones that are composed — <c>Composed: Copy with GPU
/// GDI</c>, which is what a D3D11 blt swapchain gets, and which this game used for 100% of frames
/// across nine sessions — are. The mode cannot be known when this is written: it is written at session
/// start, before a single frame has been presented, because the whole value of the line is that it is
/// acted on before playing rather than read afterwards. So the warning states the condition instead of
/// testing it, and <see cref="DescribeWithdrawal"/> withdraws it at the end of a session that turned
/// out to run on a hardware plane after all.
/// </para>
/// </remarks>
public static class RefreshRateMismatch
{
    /// <summary>
    /// How far apart two rates have to be, proportionally, before the pair is worth reporting.
    /// </summary>
    /// <remarks>
    /// A ratio rather than a difference in hertz, because what matters is whether the compositor can
    /// hold one cadence for both. The measurements this is set against are 120 beside 60, which is the
    /// broken case at a ratio of 2.0, and 59 beside 60 — the best the two panels on that machine could
    /// be synced to, which measured 0.41% off cadence and must not warn. Windows reports these as whole
    /// hertz from DEVMODE, so 59.94 arrives as 59 and the good case sits at 1.017.
    /// </remarks>
    private const double MaterialRatio = 1.1;

    /// <summary>
    /// Composed share below which the session never went through the compositor at all.
    /// </summary>
    /// <remarks>
    /// Deliberately far from the <c>0.9</c> the correlation engine calls a dominant mode. Anything in
    /// between is a session that spent part of its evening composed — alt-tabbing out of exclusive
    /// fullscreen does exactly that — and the mismatch cost it real frames while it did. Only a session
    /// that essentially never composed is one the warning did not apply to.
    /// </remarks>
    private const double NeverComposedShare = 0.1;

    /// <summary>
    /// Returns the warning when the displays disagree, and null when they do not.
    /// </summary>
    public static string? Describe(IReadOnlyList<AttachedDisplay>? displays)
    {
        if (displays is not { Count: > 1 })
        {
            return null;
        }

        var rates = displays.Where(display => display.RefreshRateHz > 0).ToArray();
        if (rates.Length < 2)
        {
            return null;
        }

        var slowest = rates.MinBy(display => display.RefreshRateHz)!;
        var fastest = rates.MaxBy(display => display.RefreshRateHz)!;
        if (slowest.RefreshRateHz <= 0 || fastest.RefreshRateHz / slowest.RefreshRateHz < MaterialRatio)
        {
            return null;
        }

        var listed = string.Join(
            ", ",
            rates.Select(display =>
                $"{display.DeviceName} {display.RefreshRateHz:F0} Hz{(display.IsPrimary ? " (primär)" : string.Empty)}"));

        return $"Skärmarna går i olika uppdateringsfrekvens: {listed}. Om spelet presenterar via "
            + "kompositorn — present-läge \"Composed: ...\", vilket en D3D11-titel i fönsterläge normalt "
            + "gör — måste DWM omsampla varje frame mot en enda takt, och frames når då skärmen en "
            + "uppdatering för tidigt eller för sent utan att det syns i frametime. Att sätta båda "
            + "skärmarna på samma frekvens tog det måttet från 11,3 % till 0,4 % på den maskin appen "
            + "utvecklades mot. Kontrollera raden \"nådde skärmen ur takt\" vid sessionens slut.";
    }

    /// <summary>
    /// Withdraws the start-of-session warning when the frames show the game never went through the
    /// compositor, and returns null when there is nothing to withdraw.
    /// </summary>
    /// <param name="composedShare">Share of the session's frames presented in a <c>Composed:</c> mode.</param>
    /// <remarks>
    /// The warning is written before the first frame exists and therefore states its condition rather
    /// than testing it. On a machine where the game takes its own hardware plane the condition is false
    /// and the mismatch costs nothing, and a warning left standing uncorrected is one the reader learns
    /// to skip — which is expensive here, because on the machine where it <em>is</em> true it is the
    /// single most useful line the app writes.
    /// </remarks>
    public static string? DescribeWithdrawal(IReadOnlyList<AttachedDisplay>? displays, double composedShare)
    {
        if (composedShare >= NeverComposedShare || Describe(displays) is null)
        {
            return null;
        }

        return $"Varningen om olika uppdateringsfrekvens gäller inte den här sessionen: {1 - composedShare:P0} "
            + "av frames presenterades utan kompositorn (hardware flip), så DWM har inte omsamplat dem mot "
            + "en gemensam takt. Skärmarnas frekvenser är fortfarande olika och varningen återkommer om "
            + "spelet körs i fönsterläge.";
    }
}
