using System.IO;
using System.Windows;

namespace SkyrimVersionPatcher.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, @"Local\SkyrimVersionPatcher.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            AppLocalization.SetLanguage(DesktopServices.LoadSettings().InterfaceLanguage);
            MessageBox.Show(AppLocalization.Translate("Патчер уже открыт. Завершите текущую операцию в его окне.",
                "The patcher is already open. Finish the current operation in its window."), "Skyrim Version Patcher");
            Shutdown();
            return;
        }
        try
        {
            var legacyDirectory = e.Args.Length == 2 && e.Args[0] == "--legacy-data-directory"
                ? e.Args[1] : AppContext.BaseDirectory;
            DesktopServices.MigrateLegacyData(legacyDirectory, DesktopServices.AppDataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(error.Message, "Skyrim Version Patcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        AppLocalization.SetLanguage(DesktopServices.LoadSettings().InterfaceLanguage);
        AppLocalization.LoadResources(Resources);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(AppLocalization.TranslateMessage(args.Exception.Message),
                AppLocalization.Translate("Ошибка приложения", "Application error"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
