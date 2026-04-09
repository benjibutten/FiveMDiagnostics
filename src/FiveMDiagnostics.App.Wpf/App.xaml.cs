using System.Globalization;
using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Export;
using FiveMDiagnostics.Integrations.Etw;
using FiveMDiagnostics.Integrations.Obs;
using FiveMDiagnostics.Integrations.PresentMon;

namespace FiveMDiagnostics.App.Wpf;

public partial class App : System.Windows.Application
{
	private DiagnosticsSessionManager? _sessionManager;

	protected override async void OnStartup(System.Windows.StartupEventArgs e)
	{
		base.OnStartup(e);

		var settingsStore = new SettingsStore();
		var settings = await settingsStore.LoadAsync().ConfigureAwait(true);

		if (!string.IsNullOrEmpty(settings.Language))
		{
			var culture = new CultureInfo(settings.Language);
			Thread.CurrentThread.CurrentUICulture = culture;
			CultureInfo.DefaultThreadCurrentUICulture = culture;
		}

		var sessionManager = new DiagnosticsSessionManager(
			settings,
			new EnvironmentMetadataProvider(),
			new FiveMCorrelationEngine(),
			new IncidentBundleExporter(),
			new WprDeepCaptureService(),
			collectors:
			[
				new SystemTelemetryCollector(),
				new FiveMProcessTelemetryCollector(),
				new NetworkTelemetryCollector(),
				new PresentMonTelemetryCollector(),
				new ObsTelemetryCollector(),
			],
			artifactParsers:
			[
				new NetStatsCsvArtifactParser(),
				new ProfilerJsonArtifactParser(),
				new ResmonArtifactParser(),
				new LogArtifactParser(),
				new EtlArtifactParser(),
			]);

		_sessionManager = sessionManager;
		var viewModel = new MainWindowViewModel(sessionManager, settingsStore, settings, new UserDialogService());
		var mainWindow = new MainWindow(viewModel);
		MainWindow = mainWindow;
		mainWindow.Show();
	}

	protected override void OnExit(System.Windows.ExitEventArgs e)
	{
		if (_sessionManager is not null)
		{
			try
			{
				var shutdownTask = _sessionManager.DisposeAsync().AsTask();
				_ = Task.WhenAny(shutdownTask, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
			}
			catch
			{
				// Ignore shutdown exceptions to avoid blocking process exit.
			}
		}

		base.OnExit(e);
	}
}

