using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class EnvironmentMetadataProvider : IEnvironmentMetadataProvider
{
    public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(settings.WorkingDirectory);
            Directory.CreateDirectory(settings.ExportDirectory);
            Directory.CreateDirectory(settings.ArtifactDirectory);

            var gpu = QuerySingle("SELECT Name, DriverVersion FROM Win32_VideoController", managementObject => (
                Name: managementObject["Name"]?.ToString() ?? "Unknown GPU",
                DriverVersion: managementObject["DriverVersion"]?.ToString()));

            var cpu = QuerySingle("SELECT Name FROM Win32_Processor", managementObject => managementObject["Name"]?.ToString() ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU");
            var totalMemoryBytes = QuerySingle("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", managementObject =>
            {
                var raw = managementObject["TotalPhysicalMemory"]?.ToString();
                return ulong.TryParse(raw, out var value) ? value : 0UL;
            });

            return new EnvironmentMetadata(
                RuntimeInformation.OSDescription,
                cpu,
                totalMemoryBytes,
                gpu.Name,
                gpu.DriverVersion,
                TryGetDisplayRefreshRate(),
                TryGetHagsState(),
                Process.GetProcessesByName("obs64").Length > 0,
                settings.ServerProfile.Name,
                DateTimeOffset.UtcNow,
                SessionEndedAt: null,
                TryGetAttachedDisplays());
        }, cancellationToken);
    }

    private static TResult QuerySingle<TResult>(string query, Func<ManagementObject, TResult> selector)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var collection = searcher.Get();
            var item = collection.Cast<ManagementObject>().FirstOrDefault();
            return item is null ? default! : selector(item);
        }
        catch
        {
            return default!;
        }
    }

    private static double? TryGetDisplayRefreshRate()
    {
        try
        {
            var devMode = new DevMode();
            devMode.Size = (short)Marshal.SizeOf<DevMode>();
            return WindowsInterop.EnumDisplaySettings(null, WindowsInterop.EnumCurrentSettings, ref devMode)
                ? devMode.DisplayFrequency
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every display attached to the desktop, with the mode it is actually running.
    /// </summary>
    /// <remarks>
    /// <see cref="TryGetDisplayRefreshRate"/> passes null and so reads the primary display only, which
    /// is the figure the session has always recorded. It was not enough: the defect that took nine
    /// sessions to find was a 120 Hz primary sitting beside a 60 Hz secondary, and neither number is
    /// wrong on its own — it is the pair that forces DWM to resample. Failures are swallowed per device
    /// so one adapter that will not report a mode does not cost the rest of the list.
    /// <para>
    /// The outer loop walks adapter <em>outputs</em>, not cards: two monitors on one card arrive as
    /// <c>\\.\DISPLAY1</c> and <c>\\.\DISPLAY2</c>, each attached to the desktop and each carrying its
    /// own mode, so the pair of rates this exists to find is present here. What is not present is the
    /// panel's name — <see cref="DisplayDevice.DeviceString"/> at this level is the card, which prints
    /// the same word twice beside two different rates — and <see cref="DescribeDisplay"/> fetches it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<AttachedDisplay>? TryGetAttachedDisplays()
    {
        try
        {
            var displays = new List<AttachedDisplay>();

            for (var index = 0u; index < 64; index++)
            {
                var device = new DisplayDevice();
                device.Size = Marshal.SizeOf<DisplayDevice>();

                if (!WindowsInterop.EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                if ((device.StateFlags & DisplayDevice.AttachedToDesktop) == 0
                    || (device.StateFlags & DisplayDevice.MirroringDriver) != 0)
                {
                    continue;
                }

                var devMode = new DevMode();
                devMode.Size = (short)Marshal.SizeOf<DevMode>();
                if (!WindowsInterop.EnumDisplaySettings(device.DeviceName, WindowsInterop.EnumCurrentSettings, ref devMode))
                {
                    continue;
                }

                displays.Add(new AttachedDisplay(
                    DescribeDisplay(device),
                    devMode.DisplayFrequency,
                    devMode.PelsWidth,
                    devMode.PelsHeight,
                    (device.StateFlags & DisplayDevice.PrimaryDevice) != 0));
            }

            return displays.Count > 0 ? displays : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Names the panel on an adapter output rather than the card driving it.
    /// </summary>
    /// <remarks>
    /// Passing an output's own name back into <c>EnumDisplayDevices</c> enumerates the monitors attached
    /// to it, and their <c>DeviceString</c> is the panel — which is what the mismatch warning has to
    /// print, because "NVIDIA GeForce RTX 3080 120 Hz, NVIDIA GeForce RTX 3080 60 Hz" names neither
    /// screen the reader has to go and change. Falls back to the card and then to the output, since a
    /// monitor entry that will not answer must not cost the rate it belongs to.
    /// </remarks>
    private static string DescribeDisplay(DisplayDevice adapterOutput)
    {
        try
        {
            var monitor = new DisplayDevice();
            monitor.Size = Marshal.SizeOf<DisplayDevice>();
            if (WindowsInterop.EnumDisplayDevices(adapterOutput.DeviceName, 0, ref monitor, 0)
                && !string.IsNullOrWhiteSpace(monitor.DeviceString))
            {
                return monitor.DeviceString;
            }
        }
        catch
        {
            // Falls through to the adapter's own name.
        }

        return string.IsNullOrWhiteSpace(adapterOutput.DeviceString)
            ? adapterOutput.DeviceName
            : adapterOutput.DeviceString;
    }

    private static string? TryGetHagsState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers");
            var value = key?.GetValue("HwSchMode");
            return value switch
            {
                1 => "Disabled",
                2 => "Enabled",
                0 => "System default",
                _ => "Unknown",
            };
        }
        catch
        {
            return null;
        }
    }
}