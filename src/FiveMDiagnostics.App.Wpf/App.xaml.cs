using System.Globalization;
using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.App.Wpf.Properties;
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
	private SingleInstanceManager? _singleInstanceManager;

	protected override async void OnStartup(System.Windows.StartupEventArgs e)
	{
		base.OnStartup(e);

		_singleInstanceManager = new SingleInstanceManager();
		if (!_singleInstanceManager.IsPrimaryInstance)
		{
			await SingleInstanceManager.SignalFirstInstanceAsync().ConfigureAwait(true);
			Shutdown();
			return;
		}

		var settingsStore = new SettingsStore();
		var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
		ApplyCulture(settings.Language);

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
		_singleInstanceManager.ActivationRequested += (_, _) => Dispatcher.Invoke(() =>
		{
			if (MainWindow is MainWindow shell)
			{
				shell.ActivateFromExternalRequest();
			}
		});
		_singleInstanceManager.StartListening();
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

		if (_singleInstanceManager is not null)
		{
			try
			{
				_singleInstanceManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
			}
			catch
			{
				// Ignore single-instance shutdown exceptions.
			}
		}

		base.OnExit(e);
	}

	private static void ApplyCulture(string? language)
	{
		var culture = new CultureInfo(string.IsNullOrWhiteSpace(language) ? "en" : language);
		Strings.Culture = culture;
		Thread.CurrentThread.CurrentUICulture = culture;
		Thread.CurrentThread.CurrentCulture = culture;
		CultureInfo.DefaultThreadCurrentUICulture = culture;
		CultureInfo.DefaultThreadCurrentCulture = culture;
	}
}

