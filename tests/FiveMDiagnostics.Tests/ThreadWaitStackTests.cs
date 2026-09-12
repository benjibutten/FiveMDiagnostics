namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// What the second pass over a trace adds to the release chain: the waker as a recorded fact, and what
/// the thread at the end of the chain did inside the wait rather than across the window.
/// </summary>
/// <remarks>
/// Eight sessions ended in the same sentence about the blocking thread — "46 % FiveM, 15 % ntdll,
/// 12 % ntoskrnl, 11 % d3d11" — because the module mix was counted over the whole retained window and
/// the waker was guessed from which thread the switch stream had on the processor. The stacks that turn
/// both into measurements were in the file the whole time.
/// </remarks>
public sealed class ThreadWaitStackTests
{
    private static readonly DateTime Start = new(2026, 9, 12, 1, 53, 34, DateTimeKind.Utc);

    private const string Waiting = "Wait";

    /// <summary>
    /// The switch stream says the render thread was on CPU 3 when the wake fired; the ReadyThread
    /// stack says a different thread did the readying. The stack wins, and the link says it was read.
    /// </summary>
    [Fact]
    public void TheReadyThreadStackNamesTheWakerOverTheProcessorInference()
    {
        var attribution = new ThreadWaitAttribution();

        attribution.RecordSwitch(3, Start, newThreadId: 22600, oldThreadId: -1, oldProcessId: -1, Waiting, "Executive");
        attribution.RecordSwitch(2, Start, newThreadId: 998, oldThreadId: 13572, oldProcessId: 24400, Waiting, "UserRequest");

        attribution.RecordReady(13572, processorNumber: 3, fromDeferredProcedureCall: false, qpc: 777);
        attribution.RecordSwitch(2, Start.AddMilliseconds(300), newThreadId: 13572, oldThreadId: 998, oldProcessId: 24400, Waiting, "Executive");

        var inferred = Assert.Single(attribution.ChainFor(13572, Names));
        Assert.Equal(22600, inferred.ThreadId);
        Assert.False(inferred.WakerRecorded);

        var recorded = Assert.Single(attribution.ChainFor(13572, Names, new Dictionary<(int, long), int> { [(3, 777)] = 26108 }));
        Assert.Equal(26108, recorded.ThreadId);
        Assert.True(recorded.WakerRecorded);
    }

    /// <summary>
    /// "Not read out of its stack" was true of every chain the app had written, and it must stop being
    /// said the moment it stops being true.
    /// </summary>
    [Fact]
    public void TheSentenceSaysWhetherTheChainWasReadOrInferred()
    {
        Assert.Contains("inte avläst ur dess stack", Summary(Link(recorded: false)).Describe(), StringComparison.Ordinal);

        var read = Summary(Link(recorded: true)).Describe();
        Assert.Contains("avläst ur ReadyThread-stacken", read, StringComparison.Ordinal);
        Assert.DoesNotContain("inte avläst", read, StringComparison.Ordinal);
        Assert.DoesNotContain("härledd", read, StringComparison.Ordinal);

        var mixed = Summary(new ThreadWaitChainLink(30920, "FiveM", 350, 3, EndsChain: false, FromDpc: false, WakerRecorded: true), Link(recorded: false)).Describe();
        Assert.Contains("tid 26108 (FiveM, härledd)", mixed, StringComparison.Ordinal);
        Assert.DoesNotContain("tid 30920 (FiveM, härledd)", mixed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A thread the chain ends on had no single wait of 100 ms or more; that is all the chain knows.
    /// The samples say how much of the wait it was actually running, and the sentence has to carry that
    /// instead of asserting it was on the processor throughout.
    /// </summary>
    [Fact]
    public void TheSentenceSaysHowMuchOfTheWaitTheBlockerWasRunning()
    {
        var running = Summary(Link(recorded: true), coresDuringWait: 0.95).Describe();
        Assert.Contains("nästan hela väntan", running, StringComparison.Ordinal);

        var stepping = Summary(Link(recorded: true), coresDuringWait: 0.01).Describe();
        Assert.Contains("bara 1 % av väntan", stepping, StringComparison.Ordinal);
        Assert.DoesNotContain("hela tiden", stepping, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mix inside the wait is the mix inside the wait. A thread sampled for one second of a twenty
    /// second window reads as 0.05 cores across the window and one full core inside that second.
    /// </summary>
    [Fact]
    public void ModulesForThreadCountsOnlyTheSamplesInsideTheInterval()
    {
        var cpu = new CpuSampleAttribution();

        cpu.RecordSample(threadId: 2, processId: 100, instructionPointer: 0x1000, Start);
        for (var ms = 0; ms < 1000; ms++)
        {
            cpu.RecordSample(threadId: 1, processId: 100, instructionPointer: 0x1000, Start.AddSeconds(10).AddMilliseconds(ms));
        }

        cpu.RecordSample(threadId: 2, processId: 100, instructionPointer: 0x1000, Start.AddSeconds(20));

        var window = Assert.Single(cpu.ModulesForThread(1));
        Assert.Equal(0.05, window.Cores, 3);

        var inside = Assert.Single(cpu.ModulesForThread(1, take: 3, Start.AddSeconds(10), Start.AddSeconds(11)));
        Assert.Equal(1.0, inside.Cores, 2);

        Assert.Empty(cpu.ModulesForThread(1, take: 3, Start.AddSeconds(2), Start.AddSeconds(3)));
    }

    /// <summary>
    /// The chain is read caller to callee, user side then kernel side, so that a lock and a driver call
    /// look different at a glance.
    /// </summary>
    [Fact]
    public void AStackCollapsesToItsInnermostUserAndKernelModules()
    {
        var chain = StackSecondPass.Chain(
        [
            ("ntoskrnl.exe", true),
            ("nvlddmkm.sys", true),
            ("dxgkrnl.sys", true),
            ("ntoskrnl.exe", true),
            ("ntdll.dll", false),
            ("nvwgf2umx.dll", false),
            ("d3d11.dll", false),
            ("FiveM_b3407_GTAProcess.exe", false),
        ]);

        Assert.Equal("d3d11.dll → nvwgf2umx.dll → ntdll.dll → dxgkrnl.sys → nvlddmkm.sys → ntoskrnl.exe", chain);
    }

    /// <summary>
    /// A thread that sits in the kernel for a second has its deferred user-mode walks dropped, and the
    /// chain must say that the caller is missing rather than look like a thread with no caller.
    /// </summary>
    [Fact]
    public void AKernelOnlyStackSaysSo()
    {
        var chain = StackSecondPass.Chain([("ntoskrnl.exe", true), ("win32kbase.sys", true)]);

        Assert.Equal("win32kbase.sys → ntoskrnl.exe (inga användarramar)", chain);
    }

    private static ThreadWaitChainLink Link(bool recorded)
    {
        return new ThreadWaitChainLink(26108, "FiveM", 0, 3, EndsChain: true, FromDpc: false, WakerRecorded: recorded);
    }

    private static ThreadWaitSummary Summary(ThreadWaitChainLink link, double coresDuringWait = 0.5)
    {
        return Summary([link], coresDuringWait);
    }

    private static ThreadWaitSummary Summary(params ThreadWaitChainLink[] links)
    {
        return Summary(links, coresDuringWait: 0.5);
    }

    private static ThreadWaitSummary Summary(IReadOnlyList<ThreadWaitChainLink> links, double coresDuringWait)
    {
        ModuleShare[] modules = [new ModuleShare("d3d11.dll", 0.9, coresDuringWait)];
        return new ThreadWaitSummary(
            13572,
            [new ThreadWaitInterval(0, 1237, 1237, IsUserRequest: true)],
            UserRequestWaitCount: 1,
            CpuSampleCount: 5000,
            Reasons: [],
            links,
            modules,
            modules,
            coresDuringWait,
            BlockerStacksDuringWait: []);
    }

    private static string Names(int threadId) => "FiveM_b3407_GTAProcess";
}
