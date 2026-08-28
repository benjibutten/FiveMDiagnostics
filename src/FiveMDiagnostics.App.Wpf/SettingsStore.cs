using System.IO;
using System.Text.Json;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public SettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveMDiagnostics");
        Directory.CreateDirectory(root);
        SettingsPath = Path.Combine(root, "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<DiagnosticsSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = DiagnosticsSettings.CreateDefault();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        DiagnosticsSettings? settings;
        await using (var stream = File.OpenRead(SettingsPath))
        {
            settings = await JsonSerializer.DeserializeAsync<DiagnosticsSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (settings is null)
        {
            return DiagnosticsSettings.CreateDefault();
        }

        if (Migrate(settings))
        {
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    /// <summary>
    /// Brings settings persisted by earlier builds up to date. A stale argument template silently keeps
    /// frame telemetry broken, and a dropped WPR profile silently changes what deep capture records, so
    /// both are rewritten rather than ignored.
    /// </summary>
    private static bool Migrate(DiagnosticsSettings settings)
    {
        var changed = false;
        var template = settings.PresentMon.ArgumentsTemplate?.Trim();

        if (string.IsNullOrWhiteSpace(template)
            || PresentMonOptions.SupersededArgumentsTemplates.Any(superseded =>
                string.Equals(template, superseded, StringComparison.OrdinalIgnoreCase)))
        {
            settings.PresentMon.ArgumentsTemplate = PresentMonOptions.DefaultArgumentsTemplate;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(settings.DeepCapture.Profile))
        {
            var legacyProfile = settings.DeepCapture.Profile!.Trim();

            // Only a profile the user actually customised is worth carrying over; the old default is
            // already the first entry of the new stack.
            if (!settings.DeepCapture.Profiles.Contains(legacyProfile, StringComparer.OrdinalIgnoreCase))
            {
                settings.DeepCapture.Profiles.Insert(0, legacyProfile);
            }

            settings.DeepCapture.Profile = null;
            changed = true;
        }

        // A hand-edited settings file is the only way these values become degenerate, and the failure is
        // loud: a zero cooldown or a zero spike multiplier turns nearly every frame into an incident.
        // Normalize also migrates the pre-window MaxIncidentsPerSession ceiling, which is why it runs
        // before the file is compared for changes rather than only before the detector uses it.
        if (settings.AutoDetect.Normalize())
        {
            changed = true;
        }

        if (settings.DeepCapture.MigrateCaptureProfile())
        {
            changed = true;
        }

        if (settings.Gpu.MigrateProcessMemoryTopCount())
        {
            changed = true;
        }

        if (settings.Obs.Normalize())
        {
            changed = true;
        }

        settings.DeepCapture.Normalize();
        settings.Gpu.Normalize();

        if (settings.MaxRetainedIncidents is < 1 or > 1000)
        {
            settings.MaxRetainedIncidents = Math.Clamp(settings.MaxRetainedIncidents, 1, 1000);
            changed = true;
        }

        return changed;
    }

    public async Task SaveAsync(DiagnosticsSettings settings, CancellationToken cancellationToken = default)
    {
        // Saving is also the path an edit from the UI takes, so validation belongs here too rather than
        // only on the way in.
        settings.AutoDetect.Normalize();
        settings.DeepCapture.Normalize();
        settings.Gpu.Normalize();
        settings.Obs.Normalize();
        settings.MaxRetainedIncidents = Math.Clamp(settings.MaxRetainedIncidents, 1, 1000);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
