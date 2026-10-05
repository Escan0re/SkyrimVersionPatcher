using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SkyrimVersionPatcher.App;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Compatibility;

internal static class Program
{
    private static readonly List<string> log = [];
    private static readonly Type Localization = typeof(InstallationWarningDialog).Assembly.GetType("SkyrimVersionPatcher.App.AppLocalization")!;
    private static int checks;
    private static string output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        output = args.Length > 0 ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../analysis/catalog-checkbox-ui"));
        Directory.CreateDirectory(output);
        // Use a plain Application so App startup, MainWindow settings, Steam and installation never run.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalLanguage = (string)Localization.GetProperty("CurrentLanguage")!.GetValue(null)!;
        var exitCode = 0;
        try
        {
            VerifySettingsMigration();
            VerifyLegacyDataMigration();
            VerifyDirectoryPresentation();
            foreach (var language in new[] { "ru", "en" })
            {
                SetLanguage(language);
                VerifyWizardMarkupPreview(language);
                VerifyTextAndLayout(language);
                foreach (var stop in new[] { false, true })
                foreach (var rename in new[] { false, true })
                foreach (var renameFirst in new[] { false, true })
                    VerifyAnswers(stop, rename, renameFirst, animate: false);
                VerifyAnswers(stop: false, rename: true, renameFirst: false, animate: true, capture: language);
                VerifyAnswers(stop: true, rename: false, renameFirst: true, animate: true, capture: language);
                VerifyConcurrentAnswers();
                foreach (var stopQuestion in new[] { false, true })
                foreach (var yes in new[] { false, true })
                    VerifySingleQuestion(stopQuestion, yes);
                foreach (var escape in new[] { false, true })
                foreach (var answerFirst in new[] { false, true })
                    VerifyCancelled(escape, answerFirst);
                VerifyReducedMotionOwner();
            }
            log.Add($"PASS {checks} WPF dialog checks. No game/Steam actions or user settings writes.");
        }
        catch (Exception error)
        {
            exitCode = 1;
            log.Add("FAIL " + error);
        }
        finally
        {
            SetLanguage(originalLanguage);
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
        }
        File.WriteAllLines(Path.Combine(output, "dialog-checks.log"), log);
        Console.WriteLine(log[^1]);
        return exitCode;
    }

    private static void VerifySettingsMigration()
    {
        var settingsType = typeof(InstallationWarningDialog).Assembly.GetType("SkyrimVersionPatcher.App.UserSettings")!;
        var constructor = settingsType.GetConstructors().Single(c => c.GetParameters().All(p => p.HasDefaultValue));
        var fresh = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
        var rename = settingsType.GetProperty("RenameContentCatalog")!;
        Check("fresh settings enable ContentCatalog rename", (bool)rename.GetValue(fresh)!);
        Check("fresh settings enable Skyrim update protection", (bool)settingsType.GetProperty("StopSkyrimUpdates")!.GetValue(fresh)!);
        foreach (var legacyJson in new[] { "{\"CreateBackup\":false}", "{\"CreateBackup\":true}" })
        {
            var legacy = JsonSerializer.Deserialize(legacyJson, settingsType)!;
            Check("legacy backup preference defaults new rename to true", (bool)rename.GetValue(legacy)!);
        }
        var disabled = JsonSerializer.Deserialize("{\"RenameContentCatalog\":false}", settingsType)!;
        Check("explicit false rename choice is honored", !(bool)rename.GetValue(disabled)!);
        Check("serialization retains explicit false rename choice", JsonSerializer.Serialize(disabled, settingsType).Contains("\"RenameContentCatalog\":false", StringComparison.Ordinal));
        Check("settings no longer expose backup preference", settingsType.GetProperty("CreateBackup") is null);
    }

    private static void VerifyRestoredCompatibilityOptions(CheckBox rename, CheckBox updates)
    {
        var assembly = typeof(MainWindow).Assembly;
        var settingsType = assembly.GetType("SkyrimVersionPatcher.App.UserSettings")!;
        var restore = assembly.GetType("SkyrimVersionPatcher.App.DesktopServices")!.GetMethod("RestoreCompatibilityOptions")!;
        foreach (var savedRename in new[] { false, true })
        foreach (var savedUpdates in new[] { false, true })
        {
            var saved = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
            {
                RenameContentCatalog = savedRename,
                StopSkyrimUpdates = savedUpdates
            }), settingsType)!;
            restore.Invoke(null, [saved, rename, updates]);
            Check("saved compatibility preferences are applied to the wizard controls",
                rename.IsChecked == savedRename && updates.IsChecked == savedUpdates);
        }
        restore.Invoke(null, [JsonSerializer.Deserialize("{}", settingsType)!, rename, updates]);
        Check("fresh compatibility preferences restore both recommended defaults", rename.IsChecked == true && updates.IsChecked == true);
    }

    private static void VerifyLegacyDataMigration()
    {
        var desktop = typeof(InstallationWarningDialog).Assembly.GetType("SkyrimVersionPatcher.App.DesktopServices")!;
        var migrate = desktop.GetMethod("MigrateLegacyData")!;
        var root = Path.Combine(output, "migration-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "portable");
        var data = Path.Combine(root, "local-app-data");
        var legacySettings = Path.Combine(legacy, "settings.json");
        var migratedSettings = Path.Combine(data, "settings.json");
        var journal = Path.Combine("Storage", "Transactions", "transaction-fixture", "journal.json");
        var cachedFile = Path.Combine("Storage", "Temp", "clean-fixture", "cached.bin");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(legacy, journal))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(legacy, cachedFile))!);
            File.WriteAllText(legacySettings, "{\"InterfaceLanguage\":\"en\"}");
            File.WriteAllText(Path.Combine(legacy, journal), "interrupted installation journal");
            File.WriteAllText(Path.Combine(legacy, cachedFile), "downloaded content");
            migrate.Invoke(null, [legacy, data]);
            Check("legacy settings move to the explicit data directory",
                File.ReadAllText(migratedSettings) == "{\"InterfaceLanguage\":\"en\"}" && !File.Exists(legacySettings));
            Check("migration retains interrupted installation journals",
                File.ReadAllText(Path.Combine(data, journal)) == "interrupted installation journal" && !File.Exists(Path.Combine(legacy, journal)));
            Check("migration moves nested working files and removes empty legacy Storage",
                File.ReadAllText(Path.Combine(data, cachedFile)) == "downloaded content" && !Directory.Exists(Path.Combine(legacy, "Storage")));
            migrate.Invoke(null, [legacy, data]);
            Check("repeated migration preserves previously migrated settings and journals",
                File.ReadAllText(migratedSettings) == "{\"InterfaceLanguage\":\"en\"}" &&
                File.ReadAllText(Path.Combine(data, journal)) == "interrupted installation journal");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(legacy, journal))!);
            File.WriteAllText(legacySettings, "{\"InterfaceLanguage\":\"ru\"}");
            File.WriteAllText(Path.Combine(legacy, journal), "conflicting legacy journal");
            File.WriteAllText(Path.Combine(legacy, "Storage", "additional.bin"), "additional content");
            migrate.Invoke(null, [legacy, data]);
            Check("existing data settings win without deleting conflicting legacy settings",
                File.ReadAllText(migratedSettings) == "{\"InterfaceLanguage\":\"en\"}" &&
                File.ReadAllText(legacySettings) == "{\"InterfaceLanguage\":\"ru\"}");
            Check("conflicting journals remain intact in both locations",
                File.ReadAllText(Path.Combine(data, journal)) == "interrupted installation journal" &&
                File.ReadAllText(Path.Combine(legacy, journal)) == "conflicting legacy journal");
            Check("migration merges nonconflicting files into existing Storage",
                File.ReadAllText(Path.Combine(data, "Storage", "additional.bin")) == "additional content" &&
                !File.Exists(Path.Combine(legacy, "Storage", "additional.bin")));
            migrate.Invoke(null, [data, data]);
            Check("same-directory migration leaves settings and journals intact",
                File.ReadAllText(migratedSettings) == "{\"InterfaceLanguage\":\"en\"}" &&
                File.ReadAllText(Path.Combine(data, journal)) == "interrupted installation journal");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyDirectoryPresentation()
    {
        var desktop = typeof(InstallationWarningDialog).Assembly.GetType("SkyrimVersionPatcher.App.DesktopServices")!;
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = output;
            var supportDirectory = (string)desktop.GetProperty("AppDataDirectory")!.GetValue(null)!;
            Check("support files use LocalAppData when launched from another working directory",
                Path.GetFullPath(supportDirectory) == Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimVersionPatcher")));
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
        var expected = @"D:\steam\steamapps\common\Skyrim Special Edition";
        Check("game path uppercases drive while preserving folder casing",
            CanonicalDirectory(@"d:\steam\steamapps\common\Skyrim Special Edition") == expected);
        Check("uppercase drive remains unchanged", CanonicalDirectory(expected) == expected);
        var rejectsNetworkPath = false;
        try { CanonicalDirectory(@"\\server\share\Skyrim Special Edition"); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
        { rejectsNetworkPath = true; }
        Check("network paths remain rejected", rejectsNetworkPath);
    }

    private static string CanonicalDirectory(string path)
    {
        var desktop = typeof(InstallationWarningDialog).Assembly.GetType("SkyrimVersionPatcher.App.DesktopServices")!;
        return (string)desktop.GetMethod("CanonicalDirectory")!.Invoke(null, [path, "Skyrim folder"])!;
    }

    private static void VerifyWizardMarkupPreview(string language)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SkyrimVersionPatcher.App/MainWindow.xaml"));
        var document = XDocument.Load(sourcePath);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace originalLocal = "clr-namespace:SkyrimVersionPatcher.App";
        XNamespace compiledLocal = "clr-namespace:SkyrimVersionPatcher.App;assembly=SkyrimVersionPatcher";
        document.Root!.Attribute(xaml + "Class")!.Remove();
        document.Root.SetAttributeValue(XNamespace.Xmlns + "local", compiledLocal.NamespaceName);
        foreach (var attached in document.Descendants().Attributes().Where(attribute => attribute.Name.Namespace == originalLocal).ToArray())
        {
            attached.Parent!.SetAttributeValue(compiledLocal + attached.Name.LocalName, attached.Value);
            attached.Remove();
        }
        var eventNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Closing", "Loaded", "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged",
            "GotKeyboardFocus", "LostKeyboardFocus", "PreviewKeyDown", "Opened"
        };
        foreach (var attribute in document.Descendants().Attributes().ToArray())
        {
            if (eventNames.Contains(attribute.Name.LocalName)) attribute.Remove();
            else if (attribute.Name.LocalName is "Source" or "Icon" && !attribute.Value.StartsWith("{", StringComparison.Ordinal))
                attribute.Value = "pack://application:,,,/SkyrimVersionPatcher;component/" + attribute.Value;
        }
        Check("source markup contains no backup controls", !document.Descendants().Attributes(xaml + "Name")
            .Any(attribute => attribute.Value.Contains("Backup", StringComparison.OrdinalIgnoreCase)));
        var preview = (Window)XamlReader.Parse(document.ToString(SaveOptions.DisableFormatting));
        Check("preview uses plain Window without application code-behind", preview.GetType() == typeof(Window));
        preview.Left = preview.Top = -30000;
        preview.WindowStartupLocation = WindowStartupLocation.Manual;
        preview.ShowInTaskbar = false;
        WizardMotion.SetIsEnabled(preview, false);
        Localization.GetMethod("LoadResources")!.Invoke(null, [preview.Resources]);
        var rename = Find<CheckBox>(preview, "RenameCatalogCheckBox");
        var updates = Find<CheckBox>(preview, "StopUpdatesCheckBox");
        var help = Find<Button>(preview, "CatalogHelpButton");
        var recommended = language == "ru" ? "(Рекомендуется)" : "(Recommended)";
        var renameLabel = rename.Content as TextBlock;
        var updatesLabel = updates.Content as TextBlock;
        log.Add("Checkbox label: " + JsonSerializer.Serialize(renameLabel is null ? rename.Content?.ToString() : Flatten(renameLabel.Inlines)));
        log.Add("Checkbox bold spans: " + JsonSerializer.Serialize(renameLabel?.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines)).ToArray()));
        Check("rename checkbox localized with bold recommendation: " + language,
            renameLabel is not null
            && Flatten(renameLabel.Inlines) == (language == "ru" ? "Переименовать ContentCatalog.txt " : "Rename ContentCatalog.txt ") + recommended
            && renameLabel.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines)).SequenceEqual(new[] { recommended }));
        Check("update checkbox localized with bold recommendation: " + language,
            updatesLabel is not null
            && Flatten(updatesLabel.Inlines) == (language == "ru" ? "Остановить обновления Skyrim " : "Stop Skyrim updates ") + recommended
            && updatesLabel.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines)).SequenceEqual(new[] { recommended }));
        Check("rename checkbox defaults checked in markup", rename.IsChecked == true && !rename.IsThreeState);
        VerifyRestoredCompatibilityOptions(rename, updates);
        Check("rename checkbox accessible label", AutomationProperties.GetName(rename) == Flatten(renameLabel!.Inlines));
        Check("update checkbox accessible label", AutomationProperties.GetName(updates) == Flatten(updatesLabel!.Inlines));
        Check("help button keyboard reachable", help.Focusable && KeyboardNavigation.GetIsTabStop(help));
        Check("help button has accessible name", !string.IsNullOrWhiteSpace(AutomationProperties.GetName(help)));
        Check("question mark in circle", help.Content is Border { Width: 18, Height: 18, CornerRadius.TopLeft: 9, Child: TextBlock { Text: "?" } });
        Check("old backup option and result action absent", preview.FindName("BackupCheckBox") is null && preview.FindName("OpenBackupButton") is null);
        var welcome = Find<StackPanel>(preview, "WelcomePage");
        Check("welcome introduces supported versions", welcome.Children[0] is TextBlock first
            && first.Text.Contains("1.5.97") && first.Text.Contains("1.6.1170"));
        Check("welcome contains no logo", !LogicalDescendants(welcome).OfType<Image>().Any());
        var versionText = Find<TextBlock>(preview, "CurrentVersionText");
        var versionValue = Find<Run>(preview, "CurrentVersionValue");
        var versionLabel = language == "ru" ? "Версия Skyrim: " : "Skyrim version: ";
        Check("version card contains only the localized version line", Flatten(versionText.Inlines)
            == versionLabel + (language == "ru" ? "не определена" : "unknown"));
        Check("version value is bold and label is regular", versionValue.FontWeight == FontWeights.Bold
            && versionText.Inlines.OfType<Run>().First().FontWeight == FontWeights.Normal);
        Check("version card has no separate heading", versionText.Parent is Border);
        Check("version accessible name describes only the version", AutomationProperties.GetName(versionText)
            == (language == "ru" ? "Версия Skyrim" : "Skyrim version"));

        var versions = VersionCatalogService.LoadBuiltIn();
        Find<ComboBox>(preview, "VersionBox").ItemsSource = versions;
        Find<ComboBox>(preview, "VersionBox").SelectedItem = versions.First(version => version.Id == "1.5.97");
        Find<ComboBox>(preview, "LocalizationBox").ItemsSource = GameLocalizationCatalog.All;
        Find<ComboBox>(preview, "LocalizationBox").SelectedItem = GameLocalizationCatalog.Get(language == "ru" ? "russian" : "english");
        var mode = Find<ComboBox>(preview, "ModeBox");
        mode.DisplayMemberPath = "";
        mode.Items.Add(language == "ru" ? "Чистая установка" : "Clean installation");
        mode.Items.Add(language == "ru" ? "У меня уже есть depot" : "I already have depot files");
        mode.SelectedIndex = 0;
        Find<TextBox>(preview, "GameDirectoryBox").Text = CanonicalDirectory(@"d:\steam\steamapps\common\Skyrim Special Edition");
        versionValue.Text = "1.7.99";
        Check("detected version line keeps the label and bold value", Flatten(versionText.Inlines) == versionLabel + "1.7.99"
            && versionValue.FontWeight == FontWeights.Bold);
        Find<TextBox>(preview, "LocalDirectoryBox").Text = @"D:\Skyrim depots";
        Find<TextBlock>(preview, "ReviewDirectoryText").Text = @"D:\SteamLibrary\steamapps\common\Skyrim Special Edition";
        Find<TextBlock>(preview, "ReviewVersionText").Text = "1.7.99  →  1.5.97";
        Find<TextBlock>(preview, "ReviewLanguageText").Text = language == "ru" ? "Русский" : "English";
        Find<TextBlock>(preview, "ReviewModeText").Text = mode.Items[0].ToString()!;
        Find<TextBlock>(preview, "ReviewCatalogText").Text = language == "ru" ? "Переименовать ContentCatalog.txt" : "Rename ContentCatalog.txt";
        Find<TextBlock>(preview, "ReviewUpdatesText").Text = language == "ru" ? "Остановить обновления Skyrim" : "Stop Skyrim updates";
        Find<ToggleButton>(preview, "RussianLanguageButton").IsChecked = language == "ru";
        Find<ToggleButton>(preview, "EnglishLanguageButton").IsChecked = language == "en";
        var tooltip = (ToolTip)help.ToolTip;
        var tooltipText = (TextBlock)tooltip.Content;
        tooltipText.Inlines.Add(new Run(language == "ru" ? "Обнаружен файл " : "A "));
        tooltipText.Inlines.Add(new Bold(new Run("ContentCatalog.txt")));
        tooltipText.Inlines.Add(new Run(language == "ru" ? ", созданный в более новой версии (" : " file created in a newer version ("));
        tooltipText.Inlines.Add(new Bold(new Run(ContentCatalogCompatibility.ModernFormatVersion)));
        tooltipText.Inlines.Add(new Run(language == "ru" ? ")." : ") has been detected."));
        tooltipText.Inlines.Add(new LineBreak());
        tooltipText.Inlines.Add(new Run(language == "ru" ? "В старых версиях он может работать некорректно или вызывать ошибки." : "It may not work correctly or may cause errors in older versions."));
        AutomationProperties.SetHelpText(help, Flatten(tooltipText.Inlines));
        Check("fixture help bold filename and detected format version", tooltipText.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines))
            .SequenceEqual(new[] { "ContentCatalog.txt", ContentCatalogCompatibility.ModernFormatVersion }));
        // The detached popup needs the owner's implicit styles when rendered without opening it.
        tooltip.Style = (Style)preview.FindResource(typeof(ToolTip));
        tooltipText.Style = (Style)preview.FindResource(typeof(TextBlock));
        RenderElement(tooltip, preview.Background, language + "-catalog-help.png", 410);

        preview.Show();
        foreach (var page in new[] { "Welcome", "Location", "Options", "Review" })
        foreach (var size in new[] { (Width: 940, Height: 720), (Width: 820, Height: 650) })
        {
            ConfigurePreviewPage(preview, page, language);
            preview.Width = size.Width;
            preview.Height = size.Height;
            preview.UpdateLayout();
            var content = (FrameworkElement)preview.Content;
            RenderElement(content, preview.Background, $"{language}-{page.ToLowerInvariant()}-{size.Width}x{size.Height}.png", (int)content.ActualWidth, (int)content.ActualHeight);
            var scroll = Find<ScrollViewer>(preview, "PageScrollViewer");
            Check("primary action fits preview viewport", FitsInside(Find<Button>(preview, "InstallButton"), content));
            if (page == "Welcome")
            {
                foreach (var instruction in welcome.Children.OfType<FrameworkElement>())
                    Check("welcome instructions fit actual window viewport", FitsInside(instruction, scroll));
            }
            else if (page == "Location")
            {
                Check("game path fits actual window viewport", FitsInside(Find<TextBox>(preview, "GameDirectoryBox"), scroll));
                Check("detected version fits actual window viewport", FitsInside(versionText, scroll));
                var metrics = new FormattedText(Flatten(versionText.Inlines), CultureInfo.CurrentUICulture, versionText.FlowDirection,
                    new Typeface(versionText.FontFamily, versionText.FontStyle, versionText.FontWeight, versionText.FontStretch),
                    versionText.FontSize, versionText.Foreground, VisualTreeHelper.GetDpi(versionText).PixelsPerDip);
                Check("detected version fits card on one line", metrics.Width <= versionText.ActualWidth + 0.1);
            }
            else if (page == "Options")
            {
                Check("rename checkbox fits actual window viewport", FitsInside(rename, scroll));
                Check("update checkbox fits actual window viewport", FitsInside(updates, scroll));
                Check("rename label including recommendation fits checkbox", FitsInside(renameLabel!, rename));
                Check("update label including recommendation fits checkbox", FitsInside(updatesLabel!, updates));
                Check("question button fits actual window viewport", FitsInside(help, scroll));
                var renameOrigin = rename.TranslatePoint(new Point(), content);
                var helpOrigin = help.TranslatePoint(new Point(), content);
                Check("question button next to rename checkbox", helpOrigin.X >= renameOrigin.X + rename.ActualWidth
                    && helpOrigin.Y < renameOrigin.Y + rename.ActualHeight
                    && helpOrigin.Y + help.ActualHeight > renameOrigin.Y);
                Find<StackPanel>(preview, "LocalDepotPanel").Visibility = Visibility.Visible;
                mode.SelectedIndex = 1;
                Find<TextBlock>(preview, "ModeDescriptionText").Text = language == "ru" ? "Установка из ваших depot без загрузки через Steam." : "Install from your depot files without downloading through Steam.";
                preview.UpdateLayout();
                RenderElement(content, preview.Background, $"{language}-options-local-{size.Width}x{size.Height}.png", (int)content.ActualWidth, (int)content.ActualHeight);
                Check("rename checkbox fits local depot preview", FitsInside(rename, scroll));
                Check("update checkbox fits local depot preview", FitsInside(updates, scroll));
                Check("question button fits local depot preview", FitsInside(help, scroll));
                Find<StackPanel>(preview, "LocalDepotPanel").Visibility = Visibility.Collapsed;
                mode.SelectedIndex = 0;
            }
            else
            {
                var catalogReview = Find<TextBlock>(preview, "ReviewCatalogText");
                Check("catalog review choice fits actual window viewport", FitsInside(catalogReview, scroll));
                var label = ((Grid)catalogReview.Parent).Children.OfType<TextBlock>()
                    .Single(text => Grid.GetRow(text) == 5 && Grid.GetColumn(text) == 0);
                var metrics = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                    label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                Check("review catalog filename label fits on one line", metrics.Width <= label.ActualWidth + 0.1);
            }
        }
        preview.Close();
        log.Add("Source-XAML preview removes every code-behind event; detected version, dropdown values, review fields and detected help text are fixtures. Path capitalization invokes the production canonicalizer without reading folders.");
    }

    private static void ConfigurePreviewPage(Window preview, string page, string language)
    {
        foreach (var pageName in new[] { "Welcome", "Location", "Options", "Review", "Operation" })
            Find<StackPanel>(preview, pageName + "Page").Visibility = pageName == page ? Visibility.Visible : Visibility.Collapsed;
        var steps = new[] { "Welcome", "Location", "Options", "Review", "Operation", "Result" };
        var active = Array.IndexOf(steps, page);
        for (var index = 0; index < steps.Length; index++)
            Find<ContentControl>(preview, "Step" + steps[index] + "Item").Tag = index < active ? "Complete" : index == active ? "Current" : "Upcoming";
        Find<TextBlock>(preview, "PageTitleText").Text = page switch
        {
            "Welcome" => language == "ru" ? "Добро пожаловать" : "Welcome",
            "Location" => language == "ru" ? "Папка Skyrim" : "Skyrim folder",
            "Options" => language == "ru" ? "Параметры установки" : "Installation options",
            _ => language == "ru" ? "Всё готово к началу" : "Ready to begin"
        };
        Find<TextBlock>(preview, "PageDescriptionText").Text = page switch
        {
            "Welcome" => "Skyrim Version Patcher",
            "Location" => language == "ru" ? "Выберите установленную игру или её отдельную копию." : "Choose the installed game or a separate copy.",
            "Options" => language == "ru" ? "Выберите версию Skyrim, которую хотите установить." : "Choose the Skyrim version you want to install.",
            _ => language == "ru" ? "Проверьте параметры и нажмите «Установить»." : "Review your options and select Install."
        };
        Find<TextBlock>(preview, "StepText").Text = language == "ru" ? $"Шаг {active + 1} из 6" : $"Step {active + 1} of 6";
        Find<TextBlock>(preview, "ModeDescriptionText").Text = language == "ru" ? "Все файлы выбранной версии устанавливаются из Steam." : "All files for the selected version will be installed from Steam.";
        Find<Button>(preview, "InstallButton").Content = page == "Review"
            ? language == "ru" ? "Установить" : "Install"
            : language == "ru" ? "Далее" : "Next";
        Find<Button>(preview, "BackButton").IsEnabled = page != "Welcome";
        Find<Button>(preview, "CancelButton").Content = language == "ru" ? "Закрыть" : "Close";
    }

    private static void VerifyTextAndLayout(string language)
    {
        var dialog = CreateDialog(true, true);
        var expectedStop = language == "ru"
            ? "Похоже, файл appmanifest_489830.acf не установлен в режим «только для чтения».\nSteam может изменить или перезаписать этот файл.\nХотите сделать его доступным только для чтения? (Рекомендуется)"
            : "It looks like appmanifest_489830.acf is not set to read-only.\nSteam may modify or overwrite this file.\nDo you want to make it read-only? (recommended)";
        var expectedRename = language == "ru"
            ? "Настоятельно рекомендуем переименовать ContentCatalog.txt в ContentCatalog.bak, чтобы избежать сбоев и вылетов при использовании более старой версии.\nПереименовать? (Рекомендуется)"
            : "We strongly recommend renaming ContentCatalog.txt to ContentCatalog.bak to avoid crashes and other issues when using an older version.\nWould you like to rename it? (Recommended)";
        var stopText = Find<TextBlock>(dialog, "StopUpdatesWarningText");
        var renameText = Find<TextBlock>(dialog, "RenameCatalogWarningText");
        Check("original appmanifest wording preserved: " + language, Flatten(stopText.Inlines) == expectedStop);
        Check("requested ContentCatalog wording: " + language, Flatten(renameText.Inlines) == expectedRename);
        Check("appmanifest filename and recommendation bold: " + language,
            stopText.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines)).SequenceEqual(new[] { "appmanifest_489830.acf", language == "ru" ? "(Рекомендуется)" : "(recommended)" }));
        Check("catalog filenames and recommendation bold: " + language,
            renameText.Inlines.OfType<Bold>().Select(b => Flatten(b.Inlines)).SequenceEqual(new[] { "ContentCatalog.txt", "ContentCatalog.bak", language == "ru" ? "(Рекомендуется)" : "(Recommended)" }));
        foreach (var buttonName in new[] { "StopUpdatesYesButton", "StopUpdatesNoButton", "RenameCatalogYesButton", "RenameCatalogNoButton" })
        {
            var button = Find<Button>(dialog, buttonName);
            Check("localized answer: " + buttonName,
                button.Content?.ToString() == (buttonName.Contains("Yes", StringComparison.Ordinal) ? language == "ru" ? "Да" : "Yes" : language == "ru" ? "Нет" : "No"));
            Check("accessible action name: " + buttonName, AutomationProperties.GetName(button).Length > button.Content!.ToString()!.Length);
            Check("answer accessible question label: " + buttonName, AutomationProperties.GetLabeledBy(button) is TextBlock);
        }
        foreach (var flags in new[] { (Stop: true, Rename: true), (Stop: true, Rename: false), (Stop: false, Rename: true) })
        foreach (var width in new[] { 572, 430 })
        {
            var renderDialog = CreateDialog(flags.Stop, flags.Rename);
            Render(renderDialog, $"{language}-{flags.Stop}-{flags.Rename}-{width}.png", width);
            foreach (var panelName in new[] { "StopUpdatesPanel", "RenameCatalogPanel" })
            {
                var panel = Find<FrameworkElement>(renderDialog, panelName);
                if (panel.Visibility != Visibility.Visible) continue;
                var cardName = panelName.Replace("Panel", "Card", StringComparison.Ordinal);
                var buttonPrefix = panelName.Replace("Panel", "", StringComparison.Ordinal);
                var content = (FrameworkElement)renderDialog.Content;
                Check("question fits rendered content", FitsInside(panel, content));
                Check("question card fits panel", FitsInside(Find<FrameworkElement>(renderDialog, cardName), panel));
                Check("Yes fits question card", FitsInside(Find<Button>(renderDialog, buttonPrefix + "YesButton"), panel));
                Check("No fits question card", FitsInside(Find<Button>(renderDialog, buttonPrefix + "NoButton"), panel));
            }
        }
    }

    private static void VerifyAnswers(bool stop, bool rename, bool renameFirst, bool animate, string? capture = null)
    {
        var dialog = CreateDialog(true, true);
        WizardMotion.SetIsEnabled(dialog, animate);
        var first = renameFirst ? "RenameCatalog" : "StopUpdates";
        var second = renameFirst ? "StopUpdates" : "RenameCatalog";
        var result = ShowWithScript(dialog, () =>
        {
            var secondPanel = Find<FrameworkElement>(dialog, second + "Panel");
            var secondStartY = secondPanel.TranslatePoint(new Point(), (FrameworkElement)dialog.Content).Y;
            Answer(dialog, first, renameFirst ? rename : stop);
            Check("first answer leaves dialog open", dialog.IsVisible);
            Check("unanswered choice stays null", renameFirst ? dialog.StopUpdatesChoice is null : dialog.RenameCatalogChoice is null);
            Answer(dialog, first, !(renameFirst ? rename : stop));
            Check("repeated answer cannot overwrite decision", renameFirst ? dialog.RenameCatalogChoice == rename : dialog.StopUpdatesChoice == stop);
            Check("remaining Yes becomes default", Find<Button>(dialog, second + "YesButton").IsDefault);
            Check("remaining question remains answerable", Find<Button>(dialog, second + "NoButton").IsEnabled);
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                Check("answered panel remains during exit animation", Find<FrameworkElement>(dialog, first + "Panel").Visibility == Visibility.Visible);
                PumpUntil(() => Find<FrameworkElement>(dialog, first + "Panel").Visibility == Visibility.Collapsed);
                dialog.UpdateLayout();
                if (!renameFirst)
                    Check("remaining lower question moves up", secondPanel.TranslatePoint(new Point(), (FrameworkElement)dialog.Content).Y < secondStartY);
            }
            else Check("reduced motion collapses answered panel immediately", Find<FrameworkElement>(dialog, first + "Panel").Visibility == Visibility.Collapsed);
            if (capture is not null) Render(dialog, $"{capture}-after-{first}.png", 572);
            Answer(dialog, second, renameFirst ? stop : rename);
            PumpUntil(() => !dialog.IsVisible);
        });
        Check("all requested decisions allow installation", result == true && dialog.StopUpdatesChoice == stop && dialog.RenameCatalogChoice == rename);
        Check("answered panels collapse", Find<FrameworkElement>(dialog, first + "Panel").Visibility == Visibility.Collapsed
            && Find<FrameworkElement>(dialog, second + "Panel").Visibility == Visibility.Collapsed);
    }

    private static void VerifyConcurrentAnswers()
    {
        var dialog = CreateDialog(true, true);
        WizardMotion.SetIsEnabled(dialog, true);
        var result = ShowWithScript(dialog, () =>
        {
            Answer(dialog, "StopUpdates", true);
            Check("second question interactive while first exits", Find<Button>(dialog, "RenameCatalogNoButton").IsEnabled);
            Answer(dialog, "RenameCatalog", false);
            PumpUntil(() => !dialog.IsVisible);
        });
        Check("concurrent exits preserve both decisions", result == true && dialog.StopUpdatesChoice == true && dialog.RenameCatalogChoice == false);
    }

    private static void VerifySingleQuestion(bool stopQuestion, bool yes)
    {
        var dialog = CreateDialog(stopQuestion, !stopQuestion);
        WizardMotion.SetIsEnabled(dialog, false);
        var result = ShowWithScript(dialog, () =>
        {
            Check("unrequested panel hidden", Find<FrameworkElement>(dialog, (stopQuestion ? "RenameCatalog" : "StopUpdates") + "Panel").Visibility == Visibility.Collapsed);
            Answer(dialog, stopQuestion ? "StopUpdates" : "RenameCatalog", yes);
        });
        Check("single question completes", result == true && (stopQuestion ? dialog.StopUpdatesChoice : dialog.RenameCatalogChoice) == yes);
        Check("unrequested choice stays null", stopQuestion ? dialog.RenameCatalogChoice is null : dialog.StopUpdatesChoice is null);
    }

    private static void VerifyCancelled(bool escape, bool answerFirst)
    {
        var dialog = CreateDialog(true, true);
        WizardMotion.SetIsEnabled(dialog, true);
        var result = ShowWithScript(dialog, () =>
        {
            if (answerFirst) Answer(dialog, "StopUpdates", false);
            if (escape)
                dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else dialog.Close();
        });
        Check((escape ? "Escape" : "window close") + " prevents incomplete installation", result != true && dialog.RenameCatalogChoice is null
            && dialog.StopUpdatesChoice == (answerFirst ? false : null));
        // A cancelled exit animation must not complete the dialog afterward.
        PumpFor(TimeSpan.FromMilliseconds(260));
        Check("cancelled animation never completes installation", dialog.DialogResult != true);
    }

    private static void VerifyReducedMotionOwner()
    {
        var owner = new Window { Width = 1, Height = 1, Left = -30000, Top = -30000, Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        WizardMotion.SetIsEnabled(owner, false);
        owner.Show();
        var dialog = CreateDialog(true, true);
        dialog.Owner = owner;
        var result = ShowWithScript(dialog, () =>
        {
            Check("dialog honors owner's reduced motion preference", !WizardMotion.GetIsEnabled(dialog));
            Answer(dialog, "StopUpdates", true);
            Check("owner reduced motion removes panel immediately", Find<FrameworkElement>(dialog, "StopUpdatesPanel").Visibility == Visibility.Collapsed);
            Answer(dialog, "RenameCatalog", false);
        });
        owner.Close();
        Check("owner reduced motion completes both decisions", result == true && dialog.StopUpdatesChoice == true && dialog.RenameCatalogChoice == false);
    }

    private static InstallationWarningDialog CreateDialog(bool stop, bool rename) => new(stop, rename)
    {
        Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false
    };

    private static bool? ShowWithScript(InstallationWarningDialog dialog, Action script)
    {
        Exception? error = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { script(); }
            catch (Exception failure)
            {
                error = failure;
                if (dialog.IsVisible) dialog.Close();
            }
        }));
        var result = dialog.ShowDialog();
        if (error is not null) throw new InvalidOperationException("Modal dialog script failed.", error);
        return result;
    }

    private static void PumpUntil(Func<bool> complete)
    {
        if (complete()) return;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!complete()) throw new TimeoutException("Dialog animation did not complete.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SetLanguage(string language) => Localization.GetMethod("SetLanguage")!.Invoke(null, [language]);
    private static void Answer(Window dialog, string action, bool yes) => Find<Button>(dialog, action + (yes ? "YesButton" : "NoButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Find<T>(Window dialog, string name) => (T)dialog.FindName(name);
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }
    private static string Flatten(InlineCollection inlines) => string.Concat(inlines.Cast<Inline>().Select(i => i switch { Run run => run.Text, LineBreak => "\n", Span span => Flatten(span.Inlines), _ => "" }));
    private static bool FitsInside(FrameworkElement element, FrameworkElement parent)
    {
        var origin = element.TranslatePoint(new Point(), parent);
        return origin.X >= -0.1 && origin.Y >= -0.1 && origin.X + element.ActualWidth <= parent.ActualWidth + 0.1 && origin.Y + element.ActualHeight <= parent.ActualHeight + 0.1;
    }
    private static void Check(string label, bool passed)
    {
        if (!passed) throw new InvalidOperationException(label);
        checks++;
        log.Add("PASS " + label);
    }

    private static void Render(Window dialog, string filename, int width)
    {
        RenderElement((FrameworkElement)dialog.Content, dialog.Background, filename, width);
    }

    private static void RenderElement(FrameworkElement content, Brush background, string filename, int width, int? fixedHeight = null)
    {
        content.Measure(new Size(width, double.PositiveInfinity));
        var height = fixedHeight ?? (int)Math.Ceiling(content.DesiredSize.Height);
        if (fixedHeight.HasValue) content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
            drawing.DrawImage(bitmap, new Rect(0, 0, width, height));
        }
        bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, filename));
        encoder.Save(stream);
    }
}
