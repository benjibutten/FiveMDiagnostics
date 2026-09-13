using System.Security.Principal;
using System.Xml.Linq;

namespace FiveMDiagnostics.App.Wpf.Services;

/// <summary>
/// Starts the app in the tray when the user signs in to Windows, as a scheduled task.
/// </summary>
/// <remarks>
/// <para>
/// A task rather than the Run key, because the app is run as administrator for its ETW traces and the
/// Run key cannot start an elevated program: Windows skips it at sign-in without a word, and the evening
/// is measured without a single trace. A task registered from an elevated app runs with highest
/// privileges and no UAC prompt; one registered from an ordinary app runs as an ordinary app.
/// </para>
/// <para>
/// Registered through the Task Scheduler's COM API with the definition in memory. Handing
/// <c>schtasks.exe</c> an XML file would let anything running as the user rewrite that file between the
/// write and the read, and have its own command registered with highest privileges.
/// </para>
/// </remarks>
internal static class WindowsAutostart
{
    public const string Argument = "--autostart";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int FileNotFound = unchecked((int)0x80070002);
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Per user, so two accounts on one machine do not overwrite each other's task.</summary>
    private static string TaskName => $"FiveMDiagnostics ({Environment.UserName})";

    public static bool IsStartArgument(string[] args) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        try
        {
            _ = RootFolder().GetTask(TaskName);
            return true;
        }
        catch (Exception)
        {
            // Not found and not reachable both mean the app will not start with Windows.
            return false;
        }
    }

    /// <returns>Whether the task will start the app elevated.</returns>
    /// <exception cref="Exception">The Task Scheduler refused; the message says why.</exception>
    public static bool Enable()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var definition = BuildDefinition(Environment.ProcessPath!, identity.User!.Value, elevated);

        _ = RootFolder().RegisterTask(TaskName, definition, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
        return elevated;
    }

    /// <exception cref="Exception">The Task Scheduler refused; the message says why.</exception>
    public static void Disable()
    {
        try
        {
            RootFolder().DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex.HResult == FileNotFound)
        {
            // Already gone, which is what was asked for.
        }
    }

    internal static string BuildDefinition(string executablePath, string userSid, bool elevated)
    {
        XElement Element(string name, params object[] content) => new(TaskNamespace + name, content);

        var task = Element(
            "Task",
            new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", "Starts FiveMDiagnostics in the tray at sign-in.")),
            Element("Triggers", Element("LogonTrigger", Element("UserId", userSid))),
            Element(
                "Principals",
                Element(
                    "Principal",
                    new XAttribute("id", "Author"),
                    Element("UserId", userSid),
                    Element("LogonType", "InteractiveToken"),
                    Element("RunLevel", elevated ? "HighestAvailable" : "LeastPrivilege"))),
            Element(
                "Settings",
                Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"),

                // A task's default is to be killed after three days and to run below normal priority,
                // and this one measures an evening's frame times.
                Element("ExecutionTimeLimit", "PT0S"),
                Element("Priority", "4")),
            Element(
                "Actions",
                new XAttribute("Context", "Author"),
                Element("Exec", Element("Command", executablePath), Element("Arguments", Argument))));

        return task.ToString();
    }

    private static dynamic RootFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }
}
