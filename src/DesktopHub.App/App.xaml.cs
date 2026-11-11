using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DesktopHub.App.Platform;
using DesktopHub.App.Shell;
using DesktopHub.App.Theme;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Finance;
using DesktopHub.Infrastructure.Finance;
using DesktopHub.Core.Events;
using DesktopHub.Infrastructure.Agenda;
using DesktopHub.Core.News;
using DesktopHub.Core.Weather;
using DesktopHub.Infrastructure.Net;
using DesktopHub.Infrastructure.News;
using DesktopHub.Infrastructure.Weather;
using DesktopHub.Core.Activity;
using DesktopHub.Infrastructure.Activity;
using DesktopHub.Core.Media;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Security;
using DesktopHub.Infrastructure.Media;
using DesktopHub.Infrastructure.Notifications;
using DesktopHub.Infrastructure.Productivity;
using DesktopHub.Core.Settings;
using DesktopHub.Infrastructure.Logging;
using DesktopHub.Infrastructure.Persistence;
using DesktopHub.Infrastructure.Security;
using DesktopHub.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesktopHub.App;

/// <summary>Raiz de composição: monta os serviços, inicializa o banco e entrega o controle ao <see cref="AppShell"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Recursos descartados em OnExit, que é o fim do ciclo de vida do Application.")]
public partial class App : Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;
    private JsonFileLoggerProvider? _logProvider;
    private ILogger<App>? _log;
    private string? _crashDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = StartupOptions.Parse(e.Args);

        _instance = SingleInstance.TryAcquire(options.InstanceId, requestQuit: options.Quit, options.Page);
        if (_instance is null || options.Quit)
        {
            // Outra instância já roda e foi sinalizada (abrir janela ou encerrar).
            Shutdown();
            return;
        }

        var migration = MigrationOutcome.NotNeeded;
        AppPaths paths;
        if (options.DataDir is { } dir)
        {
            paths = new AppPaths(dir);
        }
        else
        {
            // Só depois da instância única: nenhuma outra cópia do app está usando a pasta.
            migration = AppPaths.MigrateLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), out paths);
        }
        paths.EnsureCreated();
        _crashDirectory = paths.Logs;
        _logProvider = new JsonFileLoggerProvider(paths.Logs);

        _services = ConfigureServices(paths, options, _logProvider).BuildServiceProvider();
        _log = _services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();
        if (migration != MigrationOutcome.NotNeeded)
        {
            LogFolderMigration(_log, migration, paths.Root);
        }

        var startup = _services.GetRequiredService<StartupState>();
        try
        {
            startup.Database = _services.GetRequiredService<SqliteDatabase>().Initialize();
        }
        catch (DatabaseNewerThanAppException ex)
        {
            LogDatabaseTooNew(_log, ex);
            MessageBox.Show(ex.Message, "Diary Ghost", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        StartFinance();

        var settings = _services.GetRequiredService<SettingsService>();
        settings.Load();
        DesktopHub.App.Finance.FinanceUi.Attach(settings, _services.GetRequiredService<IEventBus>());
        ThemeManager.ApplyTheme(settings.General.Theme);
        ThemeManager.ApplyAccent(settings.General.AccentColor);
        _services.GetRequiredService<IEventBus>().Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.General)
            {
                ThemeManager.ApplyTheme(settings.General.Theme);
            }
        }, SynchronizationContext.Current);

        var shell = _services.GetRequiredService<AppShell>();
        shell.Start();
        _instance.Listen(
            onActivate: page => Dispatcher.BeginInvoke(() => shell.OpenMain(page)),
            onQuit: () => Dispatcher.BeginInvoke(shell.Exit));

        using var process = Process.GetCurrentProcess();
        LogStarted(_log, (int)(DateTime.Now - process.StartTime).TotalMilliseconds, options.Minimized, options.NoWidget);
    }

    /// <summary>
    /// Banco próprio das finanças (ADR-028). Se ele falhar, só o módulo fica indisponível, com o motivo na
    /// página; o resto do app abre normalmente.
    /// </summary>
    private void StartFinance()
    {
        var services = _services!;
        var finance = services.GetRequiredService<FinanceService>();
        try
        {
            services.GetRequiredService<FinanceDatabase>().Initialize();
            finance.Start();
        }
        catch (DatabaseNewerThanAppException ex)
        {
            LogFinanceUnavailable(_log!, ex);
            finance.MarkUnavailable(ex.Message);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            LogFinanceUnavailable(_log!, ex);
            finance.MarkUnavailable("Não foi possível abrir o banco de finanças. Os detalhes estão no log do app; seus dados não foram alterados.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Módulo de finanças indisponível")]
    private static partial void LogFinanceUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inicialização concluída em {ElapsedMs} ms (minimizado: {Minimized}, sem widget: {NoWidget})")]
    private static partial void LogStarted(ILogger logger, int elapsedMs, bool minimized, bool noWidget);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pasta de dados da versão Desktop Hub: {Outcome} (em uso: {Root})")]
    private static partial void LogFolderMigration(ILogger logger, MigrationOutcome outcome, string root);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Banco de dados mais novo que o aplicativo")]
    private static partial void LogDatabaseTooNew(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Exceção não tratada na thread de UI")]
    private static partial void LogUiException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Exceção fatal não tratada (encerrando: {Terminating})")]
    private static partial void LogFatal(ILogger logger, Exception? ex, bool terminating);

    [LoggerMessage(Level = LogLevel.Error, Message = "Exceção não observada em tarefa")]
    private static partial void LogUnobserved(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processo encerrado (código {ExitCode})")]
    private static partial void LogExited(ILogger logger, int exitCode);

    private static ServiceCollection ConfigureServices(AppPaths paths, StartupOptions options, JsonFileLoggerProvider logProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new StartupState { Options = options });
        services.AddSingleton<IEventBus, EventBus>();

        // Infraestrutura
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();

        services.AddSingleton<IMediaSessionSource, GsmtcMediaSource>();
        services.AddSingleton<IMasterVolume, CoreAudioMasterVolume>();
        services.AddSingleton<IProductivityStore, SqliteProductivityStore>();
        services.AddSingleton<INotificationHistory, SqliteNotificationHistory>();
        services.AddSingleton<WindowsToastSink>();
        services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<WindowsToastSink>());
        services.AddSingleton<IAgendaStore, SqliteAgendaStore>();
        services.AddSingleton<IReminderLog, SqliteReminderLog>();
        services.AddSingleton<FinanceDatabase>();
        services.AddSingleton<IFinanceStore, SqliteFinanceStore>();
        services.AddSingleton(_ => HttpClients.Create());
        services.AddSingleton<IFeedFetcher, HttpFeedFetcher>();
        services.AddSingleton<INewsStore, SqliteNewsStore>();
        services.AddSingleton<IWeatherProvider, OpenMeteoProvider>();
        services.AddSingleton<IWeatherCache, SqliteWeatherCache>();
        services.AddSingleton<IActivityStore, SqliteActivityStore>();
        services.AddSingleton<IActivityChannel>(sp => new ActivityPipeServer(ActivityPipe.Name(options.DataDir), sp.GetRequiredService<ILogger<ActivityPipeServer>>()));
        services.AddSingleton<IClaudeHooksInstaller>(_ => new ClaudeHooksInstaller(ClaudeHooksInstaller.DefaultPath));

        // Aplicação
        services.AddSingleton<SettingsService>();
        services.AddSingleton<MediaService>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<ProductivityService>();
        services.AddSingleton<AgendaService>();
        services.AddSingleton<IReminderSource, FinanceReminders>();
        services.AddSingleton<ICalendarOverlaySource, FinanceCalendarOverlay>();
        services.AddSingleton<ReminderScheduler>();
        services.AddSingleton<NewsService>();
        services.AddSingleton<WeatherService>();
        services.AddSingleton<ActivityService>();
        services.AddSingleton<CalendarSubscriptionService>();
        services.AddSingleton<FinanceService>();

        // Plataforma e apresentação
        services.AddSingleton<FullscreenMonitor>();
        services.AddSingleton<GlobalHotkeys>();
        services.AddSingleton<WidgetController>();
        services.AddSingleton<AppShell>();
        return services;
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // Registra e mantém o app vivo: um erro de UI não deve derrubar lembretes e temporizadores.
            if (_log is not null)
            {
                LogUiException(_log, args.Exception);
            }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (_log is not null)
            {
                LogFatal(_log, args.ExceptionObject as Exception, args.IsTerminating);
            }
            // O processo vai morrer e o logger assíncrono pode não gravar (ou já ter sido descartado
            // no encerramento): grava de forma síncrona, sem depender de nenhum serviço.
            WriteCrashFile(args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (_log is not null)
            {
                LogUnobserved(_log, args.Exception);
            }
            args.SetObserved();
        };
    }

    private void WriteCrashFile(object exception)
    {
        if (_crashDirectory is null)
        {
            return;
        }
        try
        {
            File.AppendAllText(Path.Combine(_crashDirectory, "crash.log"),
                $"{DateTimeOffset.Now:O} [{Environment.CurrentManagedThreadId}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Último recurso: não há mais onde registrar.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_log is not null)
        {
            LogExited(_log, e.ApplicationExitCode);
        }
        _services?.Dispose();   // descarta shell, widget, hooks e a fábrica de logs
        _logProvider?.Dispose(); // garante a gravação das últimas linhas
        _instance?.Dispose();
        base.OnExit(e);
    }
}
