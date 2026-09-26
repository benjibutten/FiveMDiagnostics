namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// A thread that is asleep is waiting for something, and the useful sentence names what.
/// </summary>
/// <remarks>
/// The app has been writing "aktiv GTA-tråd tid 13572 låg sammanhängande av CPU:n upp till 355,7 ms" for
/// three sessions and stopping there, while the same question was answered by hand with the offline
/// analyser three times an evening. The first link is rarely the answer either: across those sessions the
/// thread that released the game's main thread was a near-idle synchronisation thread inside
/// <c>gta-core-five.dll</c>, itself waiting on the render thread for the same interval. What separates
/// "the game blocked on itself" from "something outside took the processor" is the far end of the chain.
/// </remarks>
public sealed class ThreadWaitReleaseChainTests
{
    private static readonly DateTime Start = new(2026, 9, 5, 0, 24, 0, DateTimeKind.Utc);

    private const string Waiting = "Wait";

    /// <summary>
    /// The shape the captures show: the main thread waits, the thread that releases it was waiting too,
    /// and the one behind that was on the processor the whole time.
    /// </summary>
    [Fact]
    public void TheChainRunsToTheThreadThatWasActuallyRunning()
    {
        var attribution = new ThreadWaitAttribution();

        // The render thread — 22600 — runs on CPU 3 throughout and never waits.
        attribution.RecordSwitch(3, Start, newThreadId: 22600, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");

        // The synchronisation thread and the main thread both go off the processor at the start.
        attribution.RecordSwitch(1, Start, newThreadId: 999, oldThreadId: 30920, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");

        // The render thread releases the synchronisation thread, which releases the main thread.
        attribution.RecordReady(30920, processorNumber: 3, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(1, Start.AddMilliseconds(350), newThreadId: 30920, oldThreadId: 999, oldProcessId: 24400, Waiting, "Executive");

        attribution.RecordReady(13572, processorNumber: 1, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(2, Start.AddMilliseconds(356), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        var chain = attribution.ChainFor(13572, Names);

        Assert.Equal(2, chain.Count);

        Assert.Equal(30920, chain[0].ThreadId);
        Assert.False(chain[0].EndsChain);
        Assert.Equal(350, chain[0].WaitMs, 0);

        Assert.Equal(22600, chain[1].ThreadId);
        Assert.True(chain[1].EndsChain);
        Assert.Equal("FiveM_b3407_GTAProcess", chain[1].ProcessName);
    }

    /// <summary>
    /// A wake from inside a DPC names no thread: the one on that processor is merely the one the
    /// interrupt suspended. Claiming it would attribute the wake to a thread that had nothing to do with
    /// it, and the walk would then continue from there.
    /// </summary>
    [Fact]
    public void AWakeFromADeferredProcedureCallEndsTheChainWithoutNamingAThread()
    {
        var attribution = new ThreadWaitAttribution();

        attribution.RecordSwitch(3, Start, newThreadId: 22600, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");
        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");

        attribution.RecordReady(13572, processorNumber: 3, fromDeferredProcedureCall: true);
        attribution.RecordSwitch(2, Start.AddMilliseconds(220), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        var link = Assert.Single(attribution.ChainFor(13572, Names));

        Assert.True(link.FromDpc);
        Assert.True(link.EndsChain);
        Assert.Equal(-1, link.ThreadId);
    }

    /// <summary>
    /// Nothing readied the thread — its own timer expired, or the keyword was off. There is no far end to
    /// name and the walk says so by producing nothing.
    /// </summary>
    [Fact]
    public void AWaitNothingReleasedHasNoChain()
    {
        var attribution = new ThreadWaitAttribution();

        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordSwitch(2, Start.AddMilliseconds(400), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        Assert.Empty(attribution.ChainFor(13572, Names));
    }

    /// <summary>
    /// A thread that parked near the wait but was awake for most of it is not a link. Overlap is what
    /// keeps an unrelated thread out of a chain built from timestamps.
    /// </summary>
    [Fact]
    public void AThreadThatOnlyOverlappedBrieflyIsNotALink()
    {
        var attribution = new ThreadWaitAttribution();

        attribution.RecordSwitch(3, Start, newThreadId: 22600, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");

        // The waker parks for 110 ms in the middle of a 400 ms wait — long enough to be recorded, far too
        // short to explain it.
        attribution.RecordSwitch(1, Start.AddMilliseconds(100), newThreadId: 997, oldThreadId: 30920, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordSwitch(1, Start.AddMilliseconds(215), newThreadId: 30920, oldThreadId: 997, oldProcessId: 24400, Waiting, "Executive");

        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordReady(13572, processorNumber: 1, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(2, Start.AddMilliseconds(400), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        var link = Assert.Single(attribution.ChainFor(13572, Names));

        // Named, and named as the end of the chain rather than as another waiter.
        Assert.Equal(30920, link.ThreadId);
        Assert.True(link.EndsChain);
    }

    /// <summary>
    /// A wake the processor attributes to the waiting thread itself is not a link. The waker is inferred
    /// from which thread the switch stream had on that processor, and a stale entry can name the sleeper
    /// — following it would walk the chain round in a circle.
    /// </summary>
    [Fact]
    public void AWakeAttributedToTheThreadItselfEndsTheWalk()
    {
        var attribution = new ThreadWaitAttribution();

        // The switch stream last saw the main thread running on CPU 5.
        attribution.RecordSwitch(5, Start, newThreadId: 13572, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");
        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");

        attribution.RecordReady(13572, processorNumber: 5, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(2, Start.AddMilliseconds(300), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        Assert.Empty(attribution.ChainFor(13572, Names));
    }

    /// <summary>
    /// The chain describes the wait behind the frame the capture was taken for, not the longest wait
    /// the trace happens to hold.
    /// </summary>
    /// <remarks>
    /// 25 September 23:27: the trace for a 138 ms frame held a 132 ms wait 26 seconds earlier, released
    /// by 24668, while the frame's own 125 ms wait ran through 8544 to the render thread 8132.
    /// </remarks>
    [Fact]
    public void TheChainIsWalkedFromTheWaitBehindTheFrame()
    {
        var attribution = new ThreadWaitAttribution();

        // 24668 on CPU 3 and the render thread 8132 on CPU 4, both running throughout.
        attribution.RecordSwitch(3, Start, newThreadId: 24668, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");
        attribution.RecordSwitch(4, Start, newThreadId: 8132, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");

        // The longest wait, early in the trace: released by 24668.
        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 16320, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordReady(16320, processorNumber: 3, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(2, Start.AddMilliseconds(132), newThreadId: 16320, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        // The frame's wait, 26 seconds later: 8544 waits on the render thread and releases the main thread.
        var frameWait = Start.AddSeconds(26);
        attribution.RecordSwitch(1, frameWait, newThreadId: 997, oldThreadId: 8544, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordSwitch(2, frameWait, newThreadId: 998, oldThreadId: 16320, oldProcessId: 24400, Waiting, "UserRequest");
        attribution.RecordReady(8544, processorNumber: 4, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(1, frameWait.AddMilliseconds(124), newThreadId: 8544, oldThreadId: 997, oldProcessId: 24400, Waiting, "Executive");
        attribution.RecordReady(16320, processorNumber: 1, fromDeferredProcedureCall: false);
        attribution.RecordSwitch(2, frameWait.AddMilliseconds(125), newThreadId: 16320, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        // Presented about half a second after the wait ended, which is how far PresentMon's clock and the
        // trace's disagree.
        var presentedAt = new DateTimeOffset(frameWait.AddMilliseconds(700));

        var atFrame = attribution.ChainFor(16320, Names, presentedAt);
        Assert.Equal([8544, 8132], atFrame.Select(link => link.ThreadId));

        // Without a frame, or with one nowhere near any wait, it is the longest wait.
        Assert.Equal(24668, Assert.Single(attribution.ChainFor(16320, Names)).ThreadId);
        Assert.Equal(24668, Assert.Single(attribution.ChainFor(16320, Names, presentedAt.AddSeconds(10))).ThreadId);
    }

    private static string Names(int threadId) => "FiveM_b3407_GTAProcess";
}
