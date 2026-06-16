using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Launcher.App;

public partial class App : System.Windows.Application
{
    private const string ActivateEventName = @"Global\BLModernTFGM.Launcher.Activate";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEventHandle;
    private RegisteredWaitHandle? _activateWaitHandle;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            StartupCore(e);
        }
        catch (Exception exception)
        {
            ReportFatal("OnStartup", exception);
            Shutdown(1);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Global\BLModernTFGM.Launcher.App", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (createdNew)
        {
            _activateEventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                _activateEventHandle,
                (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.RestoreFromExternalActivation();
                        }
                    });
                },
                null,
                Timeout.Infinite,
                false);

            MainWindow = new MainWindow();
            MainWindow.Show();
            return;
        }

        TrySignalRunningInstance();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWaitHandle?.Unregister(null);
        _activateWaitHandle = null;
        _activateEventHandle?.Dispose();
        _activateEventHandle = null;

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        base.OnExit(e);
    }

    private static void ReportFatal(string source, Exception? exception)
    {
        var message = exception?.ToString() ?? "Неизвестная ошибка (нет деталей).";
        var logText =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Источник: {source}{Environment.NewLine}{message}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";

        var logPath = TryWriteCrashLog(logText);

        try
        {
            var hint = logPath is null
                ? string.Empty
                : $"{Environment.NewLine}{Environment.NewLine}Подробности сохранены в:{Environment.NewLine}{logPath}";

            System.Windows.MessageBox.Show(
                $"Лаунчер не смог запуститься.{Environment.NewLine}{Environment.NewLine}{exception?.Message ?? message}{hint}",
                "BL-modern TFGM — ошибка запуска",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            // В среде без GUI окно показать нельзя — лог уже записан.
        }
    }

    private static string? TryWriteCrashLog(string logText)
    {
        // Пишем рядом с exe, а если туда нельзя — в %AppData%\ForgeLauncher\.launcher.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "launcher-crash.log"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ForgeLauncher",
                ".launcher",
                "launcher-crash.log")
        };

        foreach (var path in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, logText);
                return path;
            }
            catch
            {
                // пробуем следующий путь
            }
        }

        return null;
    }

    private static void TrySignalRunningInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch
        {
        }
    }
}
