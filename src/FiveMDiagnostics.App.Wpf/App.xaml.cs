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
		_sessionManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		base.OnExit(e);
	}
}

