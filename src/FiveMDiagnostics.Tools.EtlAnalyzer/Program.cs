namespace FiveMDiagnostics.Tools.EtlAnalyzer;

using System.Runtime.InteropServices;

using Microsoft.Windows.EventTracing;

/// <summary>
/// Offline reader for the deep capture ETLs the app records.
/// </summary>
/// <remarks>
/// The app's own <c>EtlArtifactParser</c> answers "is this trace usable and did a driver hold the CPU",
/// which is what an automated verdict needs. This tool answers the questions that come after that, and
/// it exists because they were answered by hand three sessions running: which thread inside the game
/// was the bottleneck, what code that thread was executing, whether it was sharing a physical core, and
/// whether the file system traffic ever reached the disk. Windows Performance Analyzer can show all of
/// it, but not as a diffable column of numbers across four captures, which is what actually located the
/// cause.
/// <para>
/// It deliberately never loads symbols. Module attribution comes from image load events and is enough
/// to say "0.15 cores in citizen-scripting-lua.dll"; resolving function names needs symbol servers and
/// minutes per trace, and has not yet been what was missing.
/// </para>
/// </remarks>
internal static class Program
{
    private const string DefaultTargetProcess = "GTAProcess";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Trace not found: {path}");
            return 1;
        }

        var command = args.Length > 1 ? args[1] : "cpu";
        var target = ValueOf(args, "--process") ?? DefaultTargetProcess;

        try
        {
            // The wait report reads the file itself, with a different library — see WaitReports.
            if (command is "wait")
            {
                RunWait(path, target, args);
                return 0;
            }

            return Run(path, command, target, args);
        }
        catch (InvalidTraceDataException ex)
        {
            // Ring buffer traces routinely contain a stream the library refuses to parse — context
            // switches in particular. Saying which command hit it beats a stack trace, because the
            // other commands on the same file usually still work.
            Console.Error.WriteLine($"The trace contains a stream that could not be parsed: {ex.Message}");
            Console.Error.WriteLine("Other commands on the same file may still work.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // A path that exists but is not a trace lands here, and both libraries report it badly:
            // TraceEvent says "could not find file" about a file it is looking straight at, because it
            // only accepts .etl, and TraceProcessing surfaces a COM "catastrophic failure". Say what is
            // actually wrong instead of printing either.
            Console.Error.WriteLine($"{path} could not be read as an ETL trace: {ex.Message}");
            Console.Error.WriteLine("This tool reads .etl files recorded by the app's deep capture.");
            return 2;
        }
    }

    private static int Run(string path, string command, string target, string[] args)
    {
        // Lost events are the normal state of a ring buffer that wrapped, and refusing to open the file
        // over it would reject exactly the traces this tool is for.
        using var trace = TraceProcessor.Create(path, new TraceProcessorSettings { AllowLostEvents = true });

        var cpuSampling = trace.UseCpuSamplingData();
        var metadata = trace.UseMetadata();

        var wantsIo = command is "io" or "all";
        var hardFaults = wantsIo ? trace.UseHardFaults() : null;
        var diskIo = wantsIo ? trace.UseDiskIOData() : null;
        var fileIo = wantsIo ? trace.UseFileIOData() : null;

        trace.Process();

        var window = TraceWindow.From(cpuSampling.Result.Samples)
            .Slice(IntOf(args, "--from-ms"), IntOf(args, "--to-ms"));
        Console.WriteLine(window.Header(path));
        Console.WriteLine($"  trace {metadata.StartTime:HH:mm:ss}–{metadata.StopTime:HH:mm:ss} "
            + $"({(metadata.StopTime - metadata.StartTime).TotalSeconds:F0}s on disk), "
            + $"{metadata.ProcessorCount} logical processors, {metadata.LostEventCount} events lost");

        var requestedSlice = IntOf(args, "--from-ms") is not null || IntOf(args, "--to-ms") is not null;
        if (window.IsEmpty && (command is not "io" || requestedSlice))
        {
            Console.WriteLine();
            Console.WriteLine("No CPU samples were retained. For a ring buffer capture this means the buffer");
            Console.WriteLine("wrapped before the marker — raise DeepCapture.RingBufferMegabytes or narrow the profile.");
            return 3;
        }

        switch (command)
        {
            case "cpu":
                CpuReports.Cpu(window, target, IntOf(args, "--threads") ?? 12);
                break;

            case "thread":
                CpuReports.Thread(window, RequiredThreadId(args));
                break;

            case "smt":
                CpuReports.Smt(window, RequiredThreadId(args));
                break;

            case "timeline":
                CpuReports.Timeline(window, IntOf(args, "--bucket") ?? 200);
                break;

            case "vram":
                CpuReports.VideoMemory(window, target, IntOf(args, "--bucket") ?? 1000);
                break;

            case "io":
                IoReports.Report(window, hardFaults!.Result.Faults, diskIo!.Result.Activity, fileIo!.Result, target);
                break;

            case "all":
                RunWait(path, target, args);
                CpuReports.Cpu(window, target, IntOf(args, "--threads") ?? 12);
                CpuReports.VideoMemory(window, target, 1000);
                CpuReports.Timeline(window, IntOf(args, "--bucket") ?? 200);
                IoReports.Report(window, hardFaults!.Result.Faults, diskIo!.Result.Activity, fileIo!.Result, target);
                break;

            default:
                Console.Error.WriteLine($"Unknown command \"{command}\".");
                PrintUsage();
                return 1;
        }

        return 0;
    }

    private static void RunWait(string path, string target, string[] args)
    {
        WaitReports.Wait(
            path,
            target,
            IntOf(args, "--tid"),
            DoubleOf(args, "--min-ms") ?? 100,
            IntOf(args, "--top") ?? 15,
            IntOf(args, "--from-ms"),
            IntOf(args, "--to-ms"));
    }

    private static int RequiredThreadId(string[] args)
    {
        var threadId = IntOf(args, "--tid") ?? (args.Length > 2 && int.TryParse(args[2], out var positional) ? positional : null);
        return threadId ?? throw new ArgumentException("This command needs a thread id: pass --tid <id>, or run the cpu command first to find one.");
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? IntOf(string[] args, string name)
    {
        return int.TryParse(ValueOf(args, name), out var value) ? value : null;
    }

    private static double? DoubleOf(string[] args, string name)
    {
        return double.TryParse(ValueOf(args, name), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            etlanalyzer <trace.etl> [command] [options]

            Commands
              cpu                 cores per process, then threads and modules of the target process (default)
              thread --tid <id>   module breakdown for one thread, in cores
              smt    --tid <id>   how often that thread shared a physical core, and with what
              timeline            per-bucket CPU for the busiest processes
              vram                per-second dxgmms2.sys (video memory paging) next to the target
              io                  hard faults, disk operations and file system traffic
              wait                off-CPU intervals on the game thread, and the thread that released it
              all                 cpu + vram + timeline + wait + io

            Options
              --process <name>    substring of the process to focus on (default: GTAProcess)
              --threads <n>       threads to list in the cpu report (default: 12)
              --bucket <ms>       timeline bucket size (default: 200)
              --tid <id>          thread id for the thread, smt and wait commands
                                  (wait defaults to the busiest thread of the target process)
              --min-ms <ms>       shortest wait the wait command reports (default: 100)
              --top <n>           waits to detail in the wait report (default: 15)
              --from-ms <ms>      start offset from the first retained CPU sample
              --to-ms <ms>        end offset from the first retained CPU sample

            Rates are reported in cores: 1.00 cores is one logical processor held busy for the whole
            sampled window, so a thread at 0.89 cores spends 19.6 ms of CPU inside a 22 ms frame.
            """);
    }
}
