using System.Globalization;
using System.IO;
using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.App.Wpf.Services;
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
	private string? _crashDirectory;
	private bool _exitReasonReported;

	protected override async void OnStartup(System.Windows.StartupEventArgs e)
	{
		base.OnStartup(e);

		var settingsStore = new SettingsStore();
		var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
		ApplyCulture(settings.Language);

		// Next to the session journals, which is where the evening is collected from.
		_crashDirectory = settings.WorkingDirectory;
		AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject);
		DispatcherUnhandledException += (_, args) => WriteCrashLog(args.Exception);

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

		// Started by Windows at sign-in: straight to the tray, where the session waits for FiveM.
		if (!WindowsAutostart.IsStartArgument(e.Args))
		{
			mainWindow.Show();
		}

		// Silent unless there is something to install, and at most one request per
		// 12 hours no matter how often the app is started.
		await UpdateCoordinator.CheckAsync(mainWindow, manual: false).ConfigureAwait(true);
	}

	/// <remarks>
	/// Handled here and not left to <see cref="OnExit"/>. WPF answers Windows by queueing the shutdown on
	/// the dispatcher, and Windows may end the process before the queue gets that far — which leaves a
	/// journal that stops mid-evening and cannot be told apart from a crash, the way 2026-09-13 03:36:25
	/// could not.
	/// </remarks>
	protected override void OnSessionEnding(System.Windows.SessionEndingCancelEventArgs e)
	{
		base.OnSessionEnding(e);

		if (_sessionManager is not { IsSessionActive: true } sessionManager)
		{
			return;
		}

		_exitReasonReported = true;

		var reason = e.ReasonSessionEnding == System.Windows.ReasonSessionEnding.Shutdown
			? "Windows stängs av eller startas om. Sammanfattningarna skrivs nu, och sessionen avslutas."
			: "Windows loggar ut. Sammanfattningarna skrivs nu, och sessionen avslutas.";
		// Guarded like OnExit: this runs inside Windows' shutdown message, and a throw here would end the
		// process through the unhandled-exception path instead of letting WPF shut down.
		try
		{
			sessionManager.Report(StatusLevel.Info, "App", reason);

			// Windows waits a few seconds for this message before it asks the user or ends the process.
			_ = Task.WhenAny(sessionManager.StopSessionAsync(), Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
		}
		catch
		{
		}
	}

	protected override void OnExit(System.Windows.ExitEventArgs e)
	{
		if (_sessionManager is not null)
		{
			if (_sessionManager.IsSessionActive && !_exitReasonReported)
			{
				_sessionManager.Report(StatusLevel.Info, "App", "FiveMDiagnostics avslutas medan sessionen körs. Sammanfattningarna skrivs nu.");
			}

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

	/// <summary>
	/// Leaves a file behind when the app dies of an exception, so a journal that stops without an exit line
	/// has an answer next to it.
	/// </summary>
	/// <remarks>
	/// Logged only; whether the process survives is unchanged. The journal gets the line too while a session
	/// is open, and every step is guarded because this runs while the process is already failing.
	/// </remarks>
	private void WriteCrashLog(object exception)
	{
		try
		{
			_sessionManager?.Report(StatusLevel.Error, "App.Crash", $"FiveMDiagnostics kraschade: {exception}");
		}
		catch
		{
		}

		try
		{
			var directory = _crashDirectory ?? AppContext.BaseDirectory;
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, $"app-crash_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.txt"),
				$"{DateTimeOffset.Now:O}{System.Environment.NewLine}{exception}");
		}
		catch
		{
		}
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

