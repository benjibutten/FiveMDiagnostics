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
                SessionEndedAt: null);
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