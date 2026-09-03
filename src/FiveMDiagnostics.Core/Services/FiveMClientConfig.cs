namespace FiveMDiagnostics.Core;

/// <summary>
/// One reading of FiveM's own client configuration, which is where the setting that decides how much
/// video memory the game will take actually lives.
/// </summary>
/// <param name="Path">The <c>fivem.cfg</c> that was read.</param>
/// <param name="LastWriteTimeUtc">When the client last wrote it.</param>
/// <param name="BudgetScale">
/// The <c>vid_budgetScale</c> convar, which is the <em>Extended Texture Budget</em> slider in the
/// client's own graphics menu. Null when the file has no such line, which means the slider is at its
/// default.
/// </param>
/// <remarks>
/// Lives in Core rather than beside its reader because the VRAM budget is stated against it, and the
/// budget monitor cannot see the collectors. The reader is
/// <c>FiveMDiagnostics.Collectors.FiveMClientConfigReader</c>.
/// </remarks>
public sealed record FiveMClientConfig(string Path, DateTimeOffset LastWriteTimeUtc, int? BudgetScale)
{
    /// <summary>
    /// The base streaming budget FiveM scales, taken from the client's own source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetGamePhysicalBudget(3 * GB)</c> in <c>PatchExtendedBudgeting.cpp</c>. It is a constant
    /// there, not a fraction of the card, which is why this record can state a figure in gigabytes from
    /// a config file alone.
    /// </para>
    /// <para>
    /// The client's own <c>GB</c> is <c>1000 * 1024 * 1024</c>, so its three of them are 2.93 GiB and
    /// not the 3.0 this constant used to hold. Seventy-five megabytes is not much on its own; it is
    /// written out exactly because the budget is compared byte for byte against a counter that reports
    /// real gibibytes, and a base that is silently a different unit than the measurement is the kind of
    /// error that survives for years.
    /// </para>
    /// </remarks>
    public const ulong BaseBudgetBytes = 3UL * 1000 * 1024 * 1024;

    /// <summary>
    /// The divisor in the client's multiplier: <c>multiplier = value / 12.0f + 1.0f</c>.
    /// </summary>
    /// <remarks>
    /// It was 8 in the commit that introduced the slider (citizenfx/fivem c42ebdb) and this class was
    /// written against that. CitizenFX raised it to 12 on 12 November 2022 (76f4a54) precisely because
    /// people were running out of real VRAM with the settings the old divisor had given them, and
    /// master still reads <c>(GetBudgetVar().GetValue() / 12.0f) + 1.0f</c>. Reading a 2026 client with
    /// the 2021 divisor overstated the budget by half a gigabyte at the value this machine runs, which
    /// turned a setting that fits into a headline finding.
    /// </remarks>
    public const double ScaleDivisor = 12d;

    /// <summary>
    /// The convar value at the far right of the slider, which is where its percentage comes from.
    /// </summary>
    /// <remarks>
    /// Not in the client's source: the convar is declared without bounds. It is anchored on a
    /// CitizenFX developer's own worked example of 40 % being <c>vid_budgetScale 8</c>, which puts the
    /// full slider at 20. Everything stated in percent is therefore said as "ungefär", because the
    /// number somebody actually sees in the pause menu is a percentage and the number in the file is
    /// not.
    /// </remarks>
    public const int MaxScale = 20;

    /// <summary>
    /// The convar value at the far left of the slider, and the lowest one the arithmetic may use.
    /// </summary>
    /// <remarks>
    /// The convar is declared without bounds, so a hand-edited file can hold anything an integer can.
    /// Below zero the client's own multiplier goes to zero and then negative — <c>-12</c> is a budget of
    /// nothing and <c>-24</c> is a negative one, which this record used to cast straight to
    /// <see cref="ulong"/> and report as sixteen exabytes of streaming budget. A negative is not a state
    /// the slider can be left in; it is a broken line, and it is read as the default with the file's own
    /// value named in the log so whoever wrote it can see what happened.
    /// </remarks>
    public const int MinScale = 0;

    /// <summary>
    /// The value the arithmetic below uses: the file's, with a negative read as the default.
    /// </summary>
    /// <remarks>
    /// Not clamped at the top. A value above <see cref="MaxScale"/> is outside the slider but inside
    /// what the client will honour — the convar has no upper bound and the budget really is that large —
    /// so capping it here would understate a budget somebody has actually given the game. Only the
    /// percentage is clamped, because a percentage above 100 describes a slider position that does not
    /// exist.
    /// </remarks>
    public int EffectiveScale => Math.Max(BudgetScale ?? 0, MinScale);

    /// <summary>Whether the file holds a value the slider itself could not have produced.</summary>
    public bool IsScaleOutOfRange => BudgetScale is { } scale && (scale < MinScale || scale > MaxScale);

    /// <summary>What the slider multiplies the base budget by. 1.0 when it is at its default.</summary>
    public double Multiplier => EffectiveScale / ScaleDivisor + 1d;

    /// <summary>
    /// How much the game may stream into. Not how much video memory the game will use — see
    /// <see cref="DescribeAgainstCard"/>, whose whole subject is the difference.
    /// </summary>
    public ulong TextureBudgetBytes => (ulong)(BaseBudgetBytes * Multiplier);

    /// <summary>Roughly where the slider stands in the pause menu, which is where it gets changed.</summary>
    public double SliderPercent => PercentOf(EffectiveScale);

    /// <summary>
    /// Roughly where a given convar value puts the slider.
    /// </summary>
    /// <remarks>
    /// Clamped to the slider's own ends. The percentage exists so somebody can be told where to drag a
    /// control that runs from left to right, and "dra reglaget till 215 %" is not an instruction anybody
    /// can follow. The value itself is reported unclamped next to it — see <see cref="Describe"/> — so
    /// nothing is hidden by saying the slider is at its right end.
    /// </remarks>
    public static double PercentOf(int scale) => Math.Clamp(scale, MinScale, MaxScale) * 100d / MaxScale;

    /// <summary>Whether two readings describe the same file in the same state.</summary>
    public bool Matches(FiveMClientConfig other) =>
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
        && LastWriteTimeUtc == other.LastWriteTimeUtc
        && BudgetScale == other.BudgetScale;

    /// <summary>
    /// The line written to the session log.
    /// </summary>
    /// <remarks>
    /// Says the arithmetic out loud rather than only its result. The value is an integer with no unit
    /// on a slider that shows percent, and the sentence has to survive being read by somebody who has
    /// never heard of the convar — including whoever is asked to go and change it, who will be looking
    /// at a percentage and nothing else. It also has to say what the budget is <em>not</em>: the number
    /// only governs streaming, and the game's video memory is this figure plus everything the budget
    /// does not reach.
    /// </remarks>
    public string Describe()
    {
        var written = $"senast skriven {LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        if (BudgetScale is not { } scale)
        {
            return $"FiveM:s klientkonfiguration ({Path}, {written}) har ingen vid_budgetScale-rad, "
                + "vilket betyder att Extended Texture Budget står på sitt standardvärde: spelet får "
                + $"{Gigabytes(BaseBudgetBytes)} streamingbudget.";
        }

        // Said before the arithmetic rather than after it: a value the slider cannot produce was typed
        // into the file by hand, and the reader has to know that before being told what it works out to.
        var range = scale < MinScale
            ? $" VARNING: {scale} är ett ogiltigt värde — reglaget går inte under {MinScale}, och "
                + "raden läses därför som standardvärdet. Rätta eller ta bort den."
            : scale > MaxScale
                ? $" Värdet ligger ovanför reglagets högsta läge ({MaxScale}); klienten godtar det, men "
                    + "det går inte att ställa in från pausmenyn och procenten ovan är reglagets ände."
                : string.Empty;

        return $"FiveM:s Extended Texture Budget står på {scale}, ungefär {SliderPercent:F0} % på "
            + $"reglaget ({Path}, {written}).{range} Klienten räknar {EffectiveScale}/{ScaleDivisor:F0} + 1 = "
            + $"{Multiplier:F3}× på en basbudget om {Gigabytes(BaseBudgetBytes)}, alltså "
            + $"{Gigabytes(TextureBudgetBytes)} streamingbudget åt spelet. Inställningen finns i "
            + "spelets pausmeny under Settings → Graphics → Extended Texture Budget, och den höjer "
            + "taket oavsett vad TextureQuality står på. Budgeten styr bara strömmade texturer och "
            + "modeller; NUI, render targets och bildbuffertar ligger ovanpå den och följer med "
            + "upplösning, MSAA och webbläsarlager i stället.";
    }

    /// <summary>
    /// What the budget means against a card of a given size and a measured desktop cost, or null when
    /// there is nothing alarming to say.
    /// </summary>
    /// <param name="cardTotalBytes">The adapter's capacity.</param>
    /// <param name="reservedBytes">
    /// What everything other than the game holds, measured rather than assumed — the budget only
    /// becomes a problem in combination with it.
    /// </param>
    /// <param name="bandPercent">The occupancy above which the session's own measurements say it hitches.</param>
    /// <param name="overheadBytes">
    /// What the game holds beyond its streaming budget, measured as the largest excess seen this
    /// session — NUI, render targets and the frame buffers that come with the resolution and the MSAA
    /// setting. Zero before the game has ever exceeded its budget, which is the honest value: it has
    /// not been observed yet.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the sentence that could have been written at session start on any of eleven evenings
    /// without a single frame of telemetry: the budget is in a config file, the card's size is known at
    /// startup, and the desktop's cost is the first process sample. It says nothing when the budget
    /// fits, because a budget that fits is not news.
    /// </para>
    /// <para>
    /// The budget alone is the wrong thing to compare against the card, and comparing it was the second
    /// half of the same mistake as the divisor. On 2 September the game held 6.63 GB median against a
    /// streaming budget of 5.6: a full gigabyte of it was never governed by the slider at all. A verdict
    /// that ignores that overhead calls a budget "fits" when the game is already over the band, and —
    /// worse — <see cref="SuggestScale"/> would happily name a <em>higher</em> value as safe.
    /// </para>
    /// </remarks>
    public string? DescribeAgainstCard(
        ulong cardTotalBytes,
        ulong reservedBytes,
        double bandPercent,
        ulong overheadBytes)
    {
        if (cardTotalBytes == 0)
        {
            return null;
        }

        var roomForGame = RoomForGame(cardTotalBytes, reservedBytes, bandPercent);
        var needBytes = TextureBudgetBytes + overheadBytes;
        if (needBytes <= roomForGame)
        {
            return null;
        }

        var overhead = overheadBytes > 0
            ? $" plus {Gigabytes(overheadBytes)} som mätts ovanpå budgeten"
            : string.Empty;

        return $"Texturbudgeten är större än vad kortet klarar: spelet behöver "
            + $"{Gigabytes(needBytes)} ({Gigabytes(TextureBudgetBytes)} streamingbudget{overhead}), "
            + $"men med {Gigabytes(reservedBytes)} upptaget av skrivbord och streamstack ryms bara "
            + $"{Gigabytes(roomForGame)} under {bandPercent:F0} %. Det är "
            + $"{Gigabytes(needBytes - roomForGame)} för mycket, och spelet kommer att ta dem. "
            + $"{SuggestScale(roomForGame, overheadBytes)}";
    }

    /// <summary>
    /// The largest slider value that still fits once the overhead is paid for, phrased as the change to
    /// make.
    /// </summary>
    /// <param name="roomForGame">What the game may hold in total before the card enters the band.</param>
    /// <param name="overheadBytes">
    /// What it holds outside the budget. Subtracted first, because lowering the slider does not touch
    /// it: a frame buffer at 1440p with 2× MSAA costs what it costs whatever the streaming budget says.
    /// </param>
    public string SuggestScale(ulong roomForGame, ulong overheadBytes)
    {
        var roomForBudget = roomForGame > overheadBytes ? roomForGame - overheadBytes : 0;

        if (roomForBudget < BaseBudgetBytes)
        {
            return "Även standardvärdet 0 är för stort för det som är ledigt — här räcker det inte "
                + "att sänka reglaget, något annat måste bort från kortet eller ur bilden "
                + "(upplösning, MSAA, NUI-lager).";
        }

        // Clamped to the slider, because this sentence is an instruction to go and move one. The card
        // may well have room for a value the menu cannot reach — a 24 GB card leaves room for 60 — and
        // naming it would send somebody to a pause menu to look for a position that is not there.
        var fits = Math.Clamp(
            (int)Math.Floor((roomForBudget / (double)BaseBudgetBytes - 1d) * ScaleDivisor),
            MinScale,
            MaxScale);

        var budget = (ulong)(BaseBudgetBytes * (fits / ScaleDivisor + 1d));

        return $"Extended Texture Budget {fits}, ungefär {PercentOf(fits):F0} % på reglaget, ger "
            + $"{Gigabytes(budget)} och ryms. Sänk ett steg i taget och besök serverns tyngsta "
            + "områden mellan stegen: för lågt värde syns som byggnader, vägar eller texturer som "
            + "inte laddar, och då är det bara att gå upp igen.";
    }

    /// <summary>How much the game may hold before the card enters the pressure band.</summary>
    public static ulong RoomForGame(ulong cardTotalBytes, ulong reservedBytes, double bandPercent)
    {
        var bandBytes = (ulong)(cardTotalBytes * bandPercent / 100);
        return bandBytes > reservedBytes ? bandBytes - reservedBytes : 0;
    }

    internal static string Gigabytes(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GB";
}
