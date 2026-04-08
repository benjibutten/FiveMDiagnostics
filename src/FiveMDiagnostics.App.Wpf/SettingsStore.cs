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

        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<DiagnosticsSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return settings ?? DiagnosticsSettings.CreateDefault();
    }

    public async Task SaveAsync(DiagnosticsSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}