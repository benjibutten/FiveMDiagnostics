using System.Globalization;
using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.App.Wpf.Updates;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Export;
using FiveMDiagnostics.Integrations.Etw;
using FiveMDiagnostics.Integrations.Nvml;
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

		var settingsStore = new SettingsStore();
		var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
		ApplyCulture(settings.Language);

		// Both update modes run in a copy of the exe in a temp folder, and both must
		// return before the single-instance mutex below is touched: the app they are
		// waiting for still owns it.
		if (UpdateInstaller.IsCleanupMode(e.Args))
		{
			await UpdateInstaller.RunCleanupAsync(e.Args).ConfigureAwait(true);
			Shutdown();
			return;
		}

		if (UpdateInstaller.IsUpdateMode(e.Args))
		{
			await UpdateInstaller.RunAsync(e.Args).ConfigureAwait(true);
			Shutdown();
			return;
		}

		// Set when this process is the freshly installed build: the updater's temp
		// folder is still on disk and nothing else will remove it.
		UpdateInstaller.ScheduleCleanup(e.Args);

		_singleInstanceManager = new SingleInstanceManager();

		if (!_singleInstanceManager.IsPrimaryInstance)
		{
			if (!await SingleInstanceManager.SignalFirstInstanceAsync().ConfigureAwait(true))
			{
				new UserDialogService().ShowInfo(Strings.AppTitle, Strings.AlreadyRunningMessage);
			}

			Shutdown();
			return;
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
				new WindowFocusCollector(),
				new NetworkTelemetryCollector(),
				new PresentMonTelemetryCollector(),
				new NvmlGpuTelemetryCollector(),
				new GpuProcessMemoryCollector(),
				new ObsTelemetryCollector(),
			],
			artifactParsers:
			[
				new NetStatsCsvArtifactParser(),
				new ProfilerJsonArtifactParser(),
				new ResmonArtifactParser(),

				// Ahead of LogArtifactParser, which also claims .txt: the first parser that says it can
				// handle a path wins, and an OBS log reduced to a keyword grep loses the render and
				// encoding lag totals that are the only OBS telemetry four sessions have produced.
				new ObsLogArtifactParser(),
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

		// Silent unless there is something to install, and at most one request per
		// 12 hours no matter how often the app is started.
		await UpdateCoordinator.CheckAsync(mainWindow, manual: false).ConfigureAwait(true);
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

