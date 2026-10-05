using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Compatibility;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Identity;
using SkyrimVersionPatcher.Core.Patching;
using SkyrimVersionPatcher.Core.Workflow;

namespace SkyrimVersionPatcher.App;

public partial class MainWindow : Window
{
    private enum WizardPage { Welcome, Location, Options, Review, Operation, Result }
    private sealed record ModeChoice(InstallationMode Mode, string Label);
    private readonly UserSettings settings;
    private readonly AutomaticPatchService workflow;
    private readonly ExecutableIdentityService identities = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private CancellationTokenSource? identityOperation;
    private Task identityTask = Task.CompletedTask;
    private ExecutableIdentity? currentIdentity;
    private string? detectedVersion;
    private string? gameDirectory;
    private readonly string storageDirectory;
    private string localDirectory = "";
    private string stage = "";
    private bool ready, busy, useSteamDirectory, operationRenameCatalog, operationStopUpdates;
    private int identityGeneration, catalogGeneration;
    private WizardPage page;
    private bool installationSucceeded;
    private string? resultDirectory;
    private TransitionStatistics? lastStatistics;
    private AutomaticPatchResult? successfulResult;
    private string? resultUpdateProtectionPath, resultUpdateProtectionError;

    private static string T(string ru, string en) => AppLocalization.Translate(ru, en);
    private static string M(string message) => AppLocalization.TranslateMessage(message);

    public MainWindow()
    {
        settings = DesktopServices.LoadSettings();
        AppLocalization.SetLanguage(settings.InterfaceLanguage);
        AppLocalization.LoadResources(Application.Current?.Resources ?? Resources);
        InitializeComponent();
        AppLocalization.LoadResources(Resources);
        useSteamDirectory = settings.UseSteamDirectory;
        storageDirectory = Path.Combine(DesktopServices.AppDataDirectory, "Storage");
        workflow = new(new PatchEngine(), async (request, progress, token) =>
        {
            await using var bridge = new SteamAutomationBridge(message => progress?.Report(new(message)));
            var downloader = new SteamClientDownloadService(bridge);
            var result = await downloader.DownloadAsync(request with
            {
                ExistingFilesDirectory = request.LocalFilesOnly ? localDirectory : null
            }, progress, token);
            var executableDepot = request.Depots.FirstOrDefault(depot => depot.DepotId == 489833);
            if (executableDepot is not null)
            {
                try
                {
                    await identities.LearnFromSteamCacheAsync(request.CacheDirectory, request.VersionId, executableDepot,
                        SteamClientLocator.FindSteamDirectories(), token);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    progress?.Report(new("Дополнительная проверка хеша EXE недоступна: " + error.Message));
                }
            }
            return result;
        }, DesktopServices.GetExecutableVersion,
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition"),
            (directory, token) => identities.IdentifyAsync(Path.Combine(directory, "SkyrimSE.exe"), token),
            getLocalizationProfileDirectory: () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Skyrim Special Edition"));
        LocalizationBox.ItemsSource = GameLocalizationCatalog.All;
        var savedLanguage = GameLocalizationCatalog.IsSupported(settings.GameLanguage)
            ? settings.GameLanguage! : GameLocalizationCatalog.ResolveSystemLanguage(AppLocalization.SystemCulture);
        LocalizationBox.SelectedItem = GameLocalizationCatalog.Get(savedLanguage);
        DesktopServices.RestoreCompatibilityOptions(settings, RenameCatalogCheckBox, StopUpdatesCheckBox);
        LocalDirectoryBox.Text = localDirectory = settings.LocalDirectory;
        LoadModes(settings.InstallationMode == nameof(InstallationMode.LocalDepots) ? InstallationMode.LocalDepots : InstallationMode.SteamDepots);
        LoadCatalog(settings.VersionId);
        ready = true;
        UpdateLanguageButtons();
        UpdateMode();
        _ = UpdateCatalogExplanationAsync();
        ShowPage(WizardPage.Welcome);
        Loaded += async (_, _) =>
        {
            AnimatePage(1);
            FindInstallation();
            await UpdateInstalledVersionAsync(learnCache: true);
        };
        Closed += (_, _) => lifetime.Cancel();
    }

    private GameVersionDefinition SelectedVersion => VersionBox.SelectedItem as GameVersionDefinition
        ?? throw new InvalidOperationException(T("Выберите желаемую версию Skyrim.", "Select the Skyrim version you want to install."));
    private InstallationMode SelectedMode => (ModeBox.SelectedItem as ModeChoice)?.Mode ?? InstallationMode.SteamDepots;
    private GameLocalizationDefinition SelectedLocalization => LocalizationBox.SelectedItem as GameLocalizationDefinition
        ?? GameLocalizationCatalog.Get(GameLocalizationCatalog.ResolveSystemLanguage(AppLocalization.SystemCulture));

    private void LoadModes(InstallationMode selected)
    {
        ModeBox.ItemsSource = new[]
        {
            new ModeChoice(InstallationMode.SteamDepots, T("Чистая установка", "Clean installation")),
            new ModeChoice(InstallationMode.LocalDepots, T("У меня уже есть depot", "I already have depot files"))
        };
        ModeBox.SelectedIndex = selected == InstallationMode.LocalDepots ? 1 : 0;
    }

    private void UpdateLanguageButtons()
    {
        RussianLanguageButton.IsChecked = AppLocalization.CurrentLanguage == "ru";
        EnglishLanguageButton.IsChecked = AppLocalization.CurrentLanguage == "en";
    }

    private void InterfaceLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string language }) ApplyInterfaceLanguage(language);
    }

    private void ApplyInterfaceLanguage(string language, bool persist = true)
    {
        var selectedMode = SelectedMode;
        var hint = NavigationHintText.Text;
        var scrollOffset = PageScrollViewer.VerticalOffset;
        AppLocalization.SetLanguage(language);
        ready = false;
        LoadModes(selectedMode);
        ready = true;
        UpdateLanguageButtons();
        UpdateMode();
        TextBlock[] dynamicMessages = [StatusText, DetailText, NavigationHintText, StatisticsText];
        foreach (var text in dynamicMessages) text.Text = M(text.Text);
        SetCurrentVersion(detectedVersion);
        if (lastStatistics is not null) UpdateStatistics(lastStatistics);
        if (successfulResult is not null && page == WizardPage.Result) ShowSuccessfulResult(successfulResult);
        ShowPage(page);
        NavigationHintText.Text = M(hint);
        PageScrollViewer.ScrollToVerticalOffset(scrollOffset);
        UpdateStageIndicators(stage);
        _ = UpdateCatalogExplanationAsync();
        if (persist)
        {
            try { SaveSettings(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { NavigationHintText.Text = M(error.Message); }
        }
    }

    private void LocalizationBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ConfigurationChanged();

    private void LoadCatalog(string selected)
    {
        var versions = VersionCatalogService.LoadBuiltIn();
        VersionBox.ItemsSource = versions;
        VersionBox.SelectedItem = versions.FirstOrDefault(version => version.Id == selected) ?? versions[0];
    }

    private void FindInstallation(bool resolveSteamPath = true)
    {
        try
        {
            var selected = !resolveSteamPath ? GameDirectoryBox.Text : !useSteamDirectory && !string.IsNullOrWhiteSpace(GameDirectoryBox.Text)
                ? GameDirectoryBox.Text : !useSteamDirectory ? settings.GameDirectory : DesktopServices.FindGame();
            if (selected is null && !string.IsNullOrWhiteSpace(settings.GameDirectory)) selected = settings.GameDirectory;
            gameDirectory = DesktopServices.CanonicalDirectory(selected ?? "", T("Папка Skyrim", "Skyrim folder"));
            if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException(T("Выберите существующую папку Skyrim или отдельной копии игры.", "Select an existing Skyrim folder or a separate copy of the game."));
            if (DesktopServices.Overlap(storageDirectory, gameDirectory))
                throw new InvalidOperationException(T("Рабочая папка патчера пересекается с папкой игры.", "The patcher working folder overlaps the game folder."));
            GameDirectoryBox.Text = gameDirectory;
            var version = DesktopServices.GetExecutableVersion(gameDirectory);
            SetCurrentVersion(version);
            StatusText.Text = T("Готов к установке", "Ready to install");
            DetailText.Text = useSteamDirectory ? T("Выбрана папка Skyrim в Steam.", "The Skyrim folder in Steam is selected.") : T("Выбрана отдельная папка. Установки в другие каталоги не будут изменены.", "A separate folder is selected. Installations in other folders will stay unchanged.");
            NavigationHintText.Text = "";
        }
        catch (Exception error)
        {
            gameDirectory = null;
            currentIdentity = null;
            StatusText.Text = T("Выберите папку Skyrim", "Select the Skyrim folder");
            DetailText.Text = M(error.Message);
            NavigationHintText.Text = M(error.Message);
            SetCurrentVersion(null);
        }
        UpdateNavigation();
    }

    private Task UpdateInstalledVersionAsync(bool learnCache = false)
    {
        if (gameDirectory is null || busy) return Task.CompletedTask;
        identityOperation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        identityOperation = source;
        var generation = ++identityGeneration;
        var selectedDirectory = gameDirectory;
        return identityTask = ReadInstalledVersionAfterAsync(identityTask, selectedDirectory, generation, learnCache, source);
    }

    private async Task ReadInstalledVersionAfterAsync(Task previous, string selectedDirectory, int generation,
        bool learnCache, CancellationTokenSource source)
    {
        // Wait for the previous read to close its handles before starting another or changing game files.
        await previous;
        await ReadInstalledVersionAsync(selectedDirectory, generation, learnCache, source);
    }

    private async Task ReadInstalledVersionAsync(string selectedDirectory, int generation, bool learnCache, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (generation != identityGeneration || gameDirectory != selectedDirectory || busy) return;
            if (learnCache)
            {
                foreach (var version in ((IEnumerable<GameVersionDefinition>)VersionBox.ItemsSource).ToArray())
                {
                    var depot = version.Depots.FirstOrDefault(item => item.DepotId == 489833);
                    if (depot is null) continue;
                    try { await identities.LearnFromSteamCacheAsync(storageDirectory, version.Id, depot, SteamClientLocator.FindSteamDirectories(), token); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
                }
            }
            if (generation != identityGeneration || gameDirectory != selectedDirectory || busy) return;
            var identity = await identities.IdentifyAsync(Path.Combine(selectedDirectory, "SkyrimSE.exe"), token);
            if (generation != identityGeneration || gameDirectory != selectedDirectory) return;
            currentIdentity = identity;
            UpdateCurrentIdentity();
            if (page == WizardPage.Review) UpdateReview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != identityGeneration) return;
            currentIdentity = null;
            NavigationHintText.Text = M("Хеш EXE не проверен: " + error.Message + "\nПрерванную установку можно повторить.");
        }
        finally
        {
            if (ReferenceEquals(identityOperation, source)) identityOperation = null;
            source.Dispose();
        }
    }

    private void UpdateCurrentIdentity()
    {
        if (currentIdentity is not { } identity) return;
        SetCurrentVersion(identity.Version);
    }

    private void SetCurrentVersion(string? version)
    {
        detectedVersion = version;
        CurrentVersionValue.Text = version ?? T("не определена", "unknown");
    }

    private void UpdateMode()
    {
        if (!ready) return;
        var local = SelectedMode == InstallationMode.LocalDepots;
        LocalDepotPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ModeDescriptionText.Text = local
            ? T("Установка из ваших depot без загрузки через Steam.", "Install from your depot files without downloading through Steam.")
            : T("Все файлы выбранной версии скачиваются заново в папку Steam. После проверки старые файлы с такими же именами удаляются из всей папки Skyrim, затем устанавливаются новые.",
                "All files for the selected version are downloaded again to the Steam folder. After verification, files with matching names are removed throughout the Skyrim folder, then the new files are installed.");
    }

    private async Task UpdateCatalogExplanationAsync()
    {
        if (VersionBox.SelectedItem is not GameVersionDefinition version) return;
        var generation = ++catalogGeneration;
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition");
            var catalog = await Task.Run(() => ContentCatalogCompatibility.Prepare(profile, version.Id));
            if (generation != catalogGeneration || lifetime.IsCancellationRequested) return;
            CatalogHelpText.Inlines.Clear();
            if (catalog is not null)
            {
                CatalogHelpText.Inlines.Add(new Run(T("Обнаружен файл ", "A ")));
                CatalogHelpText.Inlines.Add(new Bold(new Run("ContentCatalog.txt")));
                CatalogHelpText.Inlines.Add(new Run(T(", созданный в более новой версии (", " file created in a newer version (")));
                CatalogHelpText.Inlines.Add(new Bold(new Run(catalog.SourceVersion)));
                CatalogHelpText.Inlines.Add(new Run(T(").", ") has been detected.")));
                CatalogHelpText.Inlines.Add(new LineBreak());
                CatalogHelpText.Inlines.Add(new Run(T("В старых версиях он может работать некорректно или вызывать ошибки.",
                    "It may not work correctly or may cause errors in older versions.")));
            }
            else
            {
                CatalogHelpText.Text = T("Несовместимый ContentCatalog.txt для выбранной версии не обнаружен. При включённой настройке обнаруженный каталог нового формата будет переименован в ContentCatalog.bak.",
                    "No incompatible ContentCatalog.txt was detected for the selected version. With this option enabled, a detected new-format catalog will be renamed to ContentCatalog.bak.");
            }
            AutomationProperties.SetHelpText(CatalogHelpButton, new TextRange(CatalogHelpText.ContentStart, CatalogHelpText.ContentEnd).Text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (generation != catalogGeneration || lifetime.IsCancellationRequested) return;
            CatalogHelpText.Text = T("Не удалось проверить ContentCatalog.txt: ", "Could not check ContentCatalog.txt: ") + M(error.Message);
            AutomationProperties.SetHelpText(CatalogHelpButton, CatalogHelpText.Text);
        }
    }

    private async void CatalogHelp_Click(object sender, RoutedEventArgs e)
    {
        await UpdateCatalogExplanationAsync();
        if (!lifetime.IsCancellationRequested && page == WizardPage.Options && CatalogHelpButton.IsVisible && CatalogHelpButton.IsEnabled)
            CatalogTooltip.IsOpen = true;
    }

    private async void CatalogHelp_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await UpdateCatalogExplanationAsync();
        if (!lifetime.IsCancellationRequested && CatalogHelpButton.IsKeyboardFocused) CatalogTooltip.IsOpen = true;
    }

    private void CatalogHelp_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CatalogTooltip.IsOpen = false;

    private async void CatalogTooltip_Opened(object sender, RoutedEventArgs e) => await UpdateCatalogExplanationAsync();

    private void CatalogHelp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !CatalogTooltip.IsOpen) return;
        CatalogTooltip.IsOpen = false;
        e.Handled = true;
    }

    private void Configuration_Changed(object sender, RoutedEventArgs e) => ConfigurationChanged();
    private void LocalDirectory_Changed(object sender, TextChangedEventArgs e) => ConfigurationChanged();
    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ConfigurationChanged();
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateMode(); ConfigurationChanged(); }
    private void ConfigurationChanged()
    {
        if (!ready || busy) return;
        lastStatistics = null;
        NavigationHintText.Text = "";
        StatusText.Text = T("Готов к установке", "Ready to install");
        DetailText.Text = T("Выбранные файлы будут проверены перед изменением игры.", "The selected files will be verified before changing the game.");
        StatisticsText.Text = T("Объёмы появятся после проверки выбранных файлов.", "Data sizes will appear after the selected files are verified.");
        OperationProgress.Value = 0;
        _ = UpdateCatalogExplanationAsync();
        if (page == WizardPage.Review) UpdateReview();
        UpdateNavigation();
    }

    private void ShowPage(WizardPage next)
    {
        var previous = page;
        if (next != WizardPage.Options) CatalogTooltip.IsOpen = false;
        page = next;
        WelcomePage.Visibility = next == WizardPage.Welcome ? Visibility.Visible : Visibility.Collapsed;
        LocationPage.Visibility = next == WizardPage.Location ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = next == WizardPage.Options ? Visibility.Visible : Visibility.Collapsed;
        ReviewPage.Visibility = next == WizardPage.Review ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationPanel.Visibility = next is WizardPage.Operation or WizardPage.Result ? Visibility.Collapsed : Visibility.Visible;
        OperationPage.Visibility = next is WizardPage.Operation or WizardPage.Result ? Visibility.Visible : Visibility.Collapsed;
        OperationProgress.Visibility = next == WizardPage.Operation ? Visibility.Visible : Visibility.Collapsed;
        ProgressStagesPanel.Visibility = next == WizardPage.Operation ? Visibility.Visible : Visibility.Collapsed;
        ResultIconBorder.Visibility = next == WizardPage.Result ? Visibility.Visible : Visibility.Collapsed;
        ResultSymbolText.Text = installationSucceeded ? "✓" : "!";
        ResultSymbolText.SetResourceReference(TextBlock.ForegroundProperty, installationSucceeded ? "WizardSuccess" : "WizardError");
        ContentControl[] steps = [StepWelcomeItem, StepLocationItem, StepOptionsItem, StepReviewItem, StepOperationItem, StepResultItem];
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index].Tag = index < (int)next ? "Complete"
                : index > (int)next ? "Upcoming"
                : next == WizardPage.Result && !installationSucceeded ? "Failed" : "Current";
            var state = steps[index].Tag switch
            {
                "Complete" => T("пройден", "completed"),
                "Current" => T("текущий шаг", "current step"),
                "Failed" => T("установка не завершена", "installation incomplete"),
                _ => T("предстоит", "upcoming")
            };
            AutomationProperties.SetName(steps[index], $"{index + 1}. {steps[index].Content}, {state}");
        }
        ResultActionsPanel.Visibility = next == WizardPage.Result ? Visibility.Visible : Visibility.Collapsed;
        (PageTitleText.Text, PageDescriptionText.Text, StepText.Text) = next switch
        {
            WizardPage.Welcome => (T("Добро пожаловать", "Welcome"), "Skyrim Version Patcher", T("Шаг 1 из 6", "Step 1 of 6")),
            WizardPage.Location => (T("Папка Skyrim", "Skyrim folder"), T("Выберите установленную игру или её отдельную копию.", "Choose the installed game or a separate copy."), T("Шаг 2 из 6", "Step 2 of 6")),
            WizardPage.Options => (T("Параметры установки", "Installation options"), T("Выберите версию Skyrim, которую хотите установить.", "Choose the Skyrim version you want to install."), T("Шаг 3 из 6", "Step 3 of 6")),
            WizardPage.Review => (T("Всё готово к началу", "Ready to begin"), T("Проверьте параметры и нажмите «Установить».", "Review your options and select Install."), T("Шаг 4 из 6", "Step 4 of 6")),
            WizardPage.Operation => (T("Установка версии", "Installing the version"), T("Дождитесь завершения загрузки и установки файлов.", "Wait for the files to finish downloading and installing."), T("Шаг 5 из 6", "Step 5 of 6")),
            _ => (installationSucceeded ? T("Установка завершена", "Installation complete") : T("Установка не завершена", "Installation incomplete"),
                installationSucceeded ? T("Версия Skyrim установлена в выбранную папку.", "The Skyrim version has been installed in the selected folder.") : T("Ниже указаны результат операции и дальнейшие действия.", "The operation result and next steps are shown below."), T("Шаг 6 из 6", "Step 6 of 6"))
        };
        if (next == WizardPage.Result && (StatusText.Text == T("Готов к установке", "Ready to install")
            || StatusText.Text == T("Подготовка к установке…", "Preparing to install…")))
        {
            StatusText.Text = PageTitleText.Text;
            if (DetailText.Text == T("Выбранные файлы будут проверены перед изменением игры.", "The selected files will be verified before changing the game.")
                || DetailText.Text == T("Используется ваш открытый клиент Steam.", "Using your open Steam client."))
                DetailText.Text = PageDescriptionText.Text;
        }
        AutomationProperties.SetName(StepText, StepText.Text);
        NavigationHintText.Text = "";
        if (next == WizardPage.Location && gameDirectory is null)
            NavigationHintText.Text = T("Выберите существующую папку Skyrim, чтобы продолжить.", "Select an existing Skyrim folder to continue.");
        if (next == WizardPage.Review) UpdateReview();
        if (next is WizardPage.Options or WizardPage.Review) _ = UpdateCatalogExplanationAsync();
        PageScrollViewer.ScrollToTop();
        UpdateNavigation();
        if (previous != next) AnimatePage(next > previous ? 1 : -1);
        if (InstallButton.IsEnabled) InstallButton.Focus();
    }

    private void AnimatePage(int direction)
    {
        WizardMotion.Enter(PageContent, horizontalOffset: 18 * direction);
        WizardMotion.Enter(PageHeader, verticalOffset: 6);
        if (page == WizardPage.Result) WizardMotion.RevealResult(ResultIconBorder);
    }

    private void UpdateNavigation()
    {
        if (!ready) return;
        BackButton.IsEnabled = !busy && (page is WizardPage.Location or WizardPage.Options or WizardPage.Review);
        InstallButton.Content = page switch
        {
            WizardPage.Review => T("Установить", "Install"),
            WizardPage.Operation => T("Установка…", "Installing…"),
            WizardPage.Result => installationSucceeded ? T("Готово", "Done") : T("Изменить параметры", "Change options"),
            _ => T("Далее", "Next")
        };
        AutomationProperties.SetName(InstallButton, InstallButton.Content.ToString());
        InstallButton.IsEnabled = !busy && (page switch
        {
            WizardPage.Welcome or WizardPage.Result => true,
            WizardPage.Location => gameDirectory is not null,
            WizardPage.Options or WizardPage.Review => gameDirectory is not null && VersionBox.SelectedItem is GameVersionDefinition,
            _ => false
        });
        CancelButton.Content = busy ? T("Отменить", "Cancel") : T("Закрыть", "Close");
        AutomationProperties.SetName(CancelButton, CancelButton.Content.ToString());
        CancelButton.IsEnabled = !busy || operation?.IsCancellationRequested != true;
    }

    private void UpdateReview()
    {
        ReviewDirectoryText.Text = gameDirectory ?? T("Папка не выбрана", "No folder selected");
        var installed = currentIdentity?.Version ?? (gameDirectory is null ? null : DesktopServices.GetExecutableVersion(gameDirectory));
        ReviewVersionText.Text = (installed is null ? T("Версия не определена", "Version unknown") : installed) + "  →  " + SelectedVersion.Id;
        ReviewLanguageText.Text = SelectedLocalization.NativeName;
        ReviewModeText.Text = (ModeBox.SelectedItem as ModeChoice)?.Label ?? T("Чистая установка", "Clean installation");
        ReviewLocalDirectoryText.Visibility = SelectedMode == InstallationMode.LocalDepots ? Visibility.Visible : Visibility.Collapsed;
        ReviewLocalDirectoryLabel.Visibility = ReviewLocalDirectoryText.Visibility;
        ReviewLocalDirectoryText.Text = LocalDirectoryBox.Text.Trim().Trim('"');
        ReviewCatalogText.Text = RenameCatalogCheckBox.IsChecked == true
            ? T("Переименовать ContentCatalog.txt", "Rename ContentCatalog.txt")
            : T("Оставить ContentCatalog.txt", "Keep ContentCatalog.txt");
        ReviewUpdatesText.Text = StopUpdatesCheckBox.IsChecked == true
            ? T("Остановить обновления Skyrim", "Stop Skyrim updates")
            : T("Не останавливать обновления", "Allow updates");
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (page is WizardPage.Location or WizardPage.Options or WizardPage.Review)
            ShowPage((WizardPage)((int)page - 1));
    }

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Выберите папку Skyrim или независимой копии", "Select the Skyrim folder or an independent copy"), InitialDirectory = gameDirectory ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        useSteamDirectory = false;
        GameDirectoryBox.Text = dialog.FolderName;
        currentIdentity = null;
        FindInstallation();
        await UpdateInstalledVersionAsync();
    }
    private async void FindSteam_Click(object sender, RoutedEventArgs e)
    {
        useSteamDirectory = true;
        currentIdentity = null;
        FindInstallation();
        await UpdateInstalledVersionAsync();
    }
    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Выберите скачанные depot или steamapps/content", "Select downloaded depot files or steamapps/content") };
        if (dialog.ShowDialog(this) == true) LocalDirectoryBox.Text = dialog.FolderName;
    }
    private void ClearLocal_Click(object sender, RoutedEventArgs e) => LocalDirectoryBox.Clear();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (page == WizardPage.Result)
        {
            if (installationSucceeded) Close();
            else ShowPage(WizardPage.Options);
            return;
        }
        if (page == WizardPage.Welcome) { ShowPage(WizardPage.Location); return; }
        if (page == WizardPage.Location)
        {
            FindInstallation(resolveSteamPath: false);
            if (gameDirectory is not null) ShowPage(WizardPage.Options);
            return;
        }
        if (page == WizardPage.Options)
        {
            if (VersionBox.SelectedItem is not GameVersionDefinition) return;
            if (!ValidateLocalDirectory()) return;
            ShowPage(WizardPage.Review);
            return;
        }
        if (page != WizardPage.Review) return;
        await InstallSelectedVersionAsync();
    }

    private bool ValidateLocalDirectory()
    {
        if (SelectedMode != InstallationMode.LocalDepots) return true;
        try
        {
            var selected = DesktopServices.CanonicalDirectory(LocalDirectoryBox.Text.Trim().Trim('"'), T("Папка с depot", "Depot folder"));
            if (!Directory.Exists(selected)) throw new DirectoryNotFoundException(T("Папка с depot не найдена. Вставьте полный путь к существующей папке.", "The depot folder was not found. Enter the full path to an existing folder."));
            if (gameDirectory is not null && DesktopServices.Overlap(selected, gameDirectory))
                throw new InvalidOperationException(T("Папка с depot должна быть отдельно от папки устанавливаемой игры.", "The depot folder must be separate from the game installation folder."));
            LocalDirectoryBox.Text = selected;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            NavigationHintText.Text = M(error.Message);
            return false;
        }
    }

    private async Task InstallSelectedVersionAsync()
    {
        FindInstallation(resolveSteamPath: false);
        if (gameDirectory is null) { ShowPage(WizardPage.Location); return; }
        if (!ValidateLocalDirectory())
        {
            var diagnostic = NavigationHintText.Text;
            ShowPage(WizardPage.Options);
            NavigationHintText.Text = diagnostic;
            return;
        }
        var stopUpdates = StopUpdatesCheckBox.IsChecked == true;
        var renameCatalog = RenameCatalogCheckBox.IsChecked == true;
        if (!stopUpdates || !renameCatalog)
        {
            var warning = new InstallationWarningDialog(!stopUpdates, !renameCatalog) { Owner = this };
            if (warning.ShowDialog() != true) return;
            stopUpdates = warning.StopUpdatesChoice ?? stopUpdates;
            renameCatalog = warning.RenameCatalogChoice ?? renameCatalog;
            StopUpdatesCheckBox.IsChecked = stopUpdates;
            RenameCatalogCheckBox.IsChecked = renameCatalog;
        }
        busy = true;
        installationSucceeded = false;
        successfulResult = null;
        lastStatistics = null;
        operationRenameCatalog = renameCatalog;
        operationStopUpdates = stopUpdates;
        resultUpdateProtectionPath = resultUpdateProtectionError = null;
        resultDirectory = gameDirectory;
        ResultDirectoryText.Text = gameDirectory;
        StatisticsText.Text = T("Объёмы появятся после проверки выбранных файлов.", "Data sizes will appear after the selected files are verified.");
        stage = "Starting";
        operation = new CancellationTokenSource();
        ConfigurationPanel.IsEnabled = false;
        StatusText.Text = T("Подготовка установки…", "Preparing installation…");
        DetailText.Text = T("Проверяем выбранные параметры и папку игры.", "Checking the selected options and game folder.");
        ShowPage(WizardPage.Operation);
        UpdateStageIndicators(stage);
        OperationProgress.IsIndeterminate = true;
        OperationProgress.Value = 0;
        try
        {
            identityOperation?.Cancel();
            ++identityGeneration;
            await identityTask;
            operation.Token.ThrowIfCancellationRequested();
            var version = SelectedVersion;
            var mode = SelectedMode;
            var localization = SelectedLocalization;
            DesktopServices.EnsureGameStopped();
            SaveSettings();
            var result = await workflow.RunAsync(new(gameDirectory, storageDirectory, version, localization.Id == "russian",
                mode, RenameContentCatalog: operationRenameCatalog, GameLanguage: localization.Id), new Progress<AutomaticPatchProgress>(OperationUpdated), operation.Token);
            installationSucceeded = true;
            successfulResult = result;
            ApplyUpdateProtection();
            ShowSuccessfulResult(result);
            if (result.Statistics is { } statistics) UpdateStatistics(statistics);
            OperationProgress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("Установка отменена", "Installation cancelled");
            DetailText.Text = stage == "Downloading" && SelectedMode == InstallationMode.SteamDepots
                ? T("Установка не началась. Загрузка в Steam может продолжаться.", "Installation did not start. The Steam download may continue.")
                : T("Установка остановлена до изменения файлов игры.", "Installation stopped before changing any game files.");
        }
        catch (PatchIncompleteInstallationException error)
        {
            StatusText.Text = error.WasCancelled ? T("Установка отменена", "Installation cancelled") : T("Установка не завершена", "Installation incomplete");
            DetailText.Text = M(error.Message);
        }
        catch (Exception error)
        {
            var failedStage = StatusText.Text;
            StatusText.Text = T("Установка не завершена", "Installation incomplete");
            DetailText.Text = error is UnauthorizedAccessException
                ? M($"Windows отказала в доступе на этапе «{failedStage}».\n{error.Message}\nПапка игры: {gameDirectory}\nРабочая папка: {storageDirectory}") : M(error.Message);
        }
        finally
        {
            operation.Dispose(); operation = null;
            busy = false;
            ConfigurationPanel.IsEnabled = true;
            OperationProgress.IsIndeterminate = false;
            ShowPage(WizardPage.Result);
        }
        await UpdateInstalledVersionAsync();
    }

    private void OperationUpdated(AutomaticPatchProgress progress)
    {
        if (!busy || operation?.IsCancellationRequested == true) return;
        stage = progress.Stage;
        UpdateStageIndicators(stage);
        StatusText.Text = M(progress.Message);
        DetailText.Text = progress.RelativePath ?? progress.Stage switch
        {
            "Downloading" => SelectedMode == InstallationMode.LocalDepots
                ? T("Проверяются файлы из указанной папки с depot.", "Checking files from the selected depot folder.")
                : T("Файлы выбранной версии подготавливаются через Steam.", "Preparing files for the selected version through Steam."),
            "Verifying" or "Validating" => T("Проверяются версия, состав и SHA256 файлов. Это может занять несколько минут.", "Checking the file version, contents and SHA256 hashes. This may take a few minutes."),
            "Removing" => T("Удаляются файлы с именами из скачанного набора во всех вложенных папках Skyrim.",
                "Removing files with names from the downloaded set throughout the Skyrim folder."),
            _ => ""
        };
        if (progress.Statistics is { } statistics) UpdateStatistics(statistics);
        OperationProgress.IsIndeterminate = progress.Percentage is null;
        if (progress.Percentage is { } percentage) OperationProgress.Value = Math.Clamp(percentage, 0, 100);
    }

    private void ShowSuccessfulResult(AutomaticPatchResult result)
    {
        StatusText.Text = T($"Установлена версия {result.TargetVersion}", $"Version {result.TargetVersion} installed");
        var ru = SelectedMode == InstallationMode.SteamDepots
            ? $"Установлено файлов: {result.Installation.FilesCopied:N0}. Все выбранные файлы скачаны заново и заменены."
            : result.Plan.ChangedFileCount == 0
            ? "Все выбранные файлы уже совпадают. Повторное копирование не потребовалось."
            : $"Обновлено файлов: {result.Installation.FilesCopied:N0}. Пропущено одинаковых: {result.Plan.SkippedFileCount:N0}.";
        var en = SelectedMode == InstallationMode.SteamDepots
            ? $"Files installed: {result.Installation.FilesCopied:N0}. All selected files were downloaded again and replaced."
            : result.Plan.ChangedFileCount == 0
            ? "All selected files already match. No files needed to be copied again."
            : $"Files updated: {result.Installation.FilesCopied:N0}. Identical files skipped: {result.Plan.SkippedFileCount:N0}.";
        if (result.ContentCatalogRenamedPath is { } renamedCatalog)
        {
            ru += "\nContentCatalog.txt переименован в ContentCatalog.bak:\n" + renamedCatalog;
            en += "\nContentCatalog.txt was renamed to ContentCatalog.bak:\n" + renamedCatalog;
        }
        if (resultUpdateProtectionPath is { } manifest)
        {
            ru += "\nФайл appmanifest_489830.acf установлен в режим «только для чтения»:\n" + manifest;
            en += "\nappmanifest_489830.acf has been set to read-only:\n" + manifest;
        }
        else if (resultUpdateProtectionError is { } protectionError)
        {
            ru += "\nПредупреждение: не удалось установить appmanifest_489830.acf в режим «только для чтения».\n" + M(protectionError);
            en += "\nWarning: appmanifest_489830.acf could not be set to read-only.\n" + M(protectionError);
        }
        DetailText.Text = T(ru, en);
    }

    private void ApplyUpdateProtection()
    {
        if (!operationStopUpdates) return;
        try
        {
            var manifest = SkyrimUpdateProtection.FindManifest(resultDirectory!);
            if (manifest is null)
                throw new FileNotFoundException(T("Файл appmanifest_489830.acf не найден в библиотеках Steam.",
                    "appmanifest_489830.acf was not found in the Steam libraries."));
            SkyrimUpdateProtection.SetReadOnly(manifest);
            resultUpdateProtectionPath = manifest;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            resultUpdateProtectionError = error.Message;
        }
    }

    private void UpdateStageIndicators(string currentStage)
    {
        var active = currentStage switch
        {
            "Verifying" or "Prepared" or "Validating" => 1,
            "Removing" or "Copying" or "Applying" or "Compatibility" or "Completed" => 2,
            _ => 0
        };
        TextBlock[] labels = [PreparationStepText, VerificationStepText, InstallationStepText];
        string[] captions = [T("Подготовка файлов", "Preparing files"), T("Проверка файлов", "Verifying files"), T("Установка", "Installation")];
        for (var index = 0; index < labels.Length; index++)
        {
            labels[index].Text = (index < active ? "✓  " : index == active ? "›  " : "    ") + captions[index];
            labels[index].FontWeight = index == active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateStatistics(TransitionStatistics value)
    {
        lastStatistics = value;
        var lines = new List<string>
        {
            T($"Подготовлено данных: {DesktopServices.FormatBytes(value.TargetBytes)}", $"Data prepared: {DesktopServices.FormatBytes(value.TargetBytes)}"),
            value.Mode == InstallationMode.LocalDepots
                ? T($"Из своих depot: {DesktopServices.FormatBytes(value.ImportedLocalBytes)}", $"From your depot files: {DesktopServices.FormatBytes(value.ImportedLocalBytes)}")
                : T($"Запрошено в Steam: {DesktopServices.FormatBytes(value.SteamRequestedBytes)}", $"Requested from Steam: {DesktopServices.FormatBytes(value.SteamRequestedBytes)}"),
            T($"Записано в игру: {DesktopServices.FormatBytes(value.WrittenBytes)}", $"Written to the game: {DesktopServices.FormatBytes(value.WrittenBytes)}")
        };
        if (value.Mode == InstallationMode.LocalDepots)
        {
            lines.Insert(1, T($"Из кэша: {DesktopServices.FormatBytes(value.ReusedCacheBytes)}", $"From cache: {DesktopServices.FormatBytes(value.ReusedCacheBytes)}"));
            lines.Add(T($"Пропущено одинаковых: {DesktopServices.FormatBytes(value.SkippedBytes)}", $"Identical data skipped: {DesktopServices.FormatBytes(value.SkippedBytes)}"));
        }
        StatisticsText.Text = string.Join("\n", lines);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!busy) { Close(); return; }
        operation?.Cancel(); CancelButton.IsEnabled = false;
        StatusText.Text = T("Отмена операции…", "Cancelling operation…");
        DetailText.Text = T("Дождитесь остановки записи файлов.", "Wait for file writing to stop.");
    }
    private void OpenGameFolder_Click(object sender, RoutedEventArgs e) => OpenResultFolder(resultDirectory);
    private void OpenResultFolder(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            NavigationHintText.Text = T("Папка недоступна.", "The folder is unavailable.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            NavigationHintText.Text = M("Не удалось открыть папку: " + error.Message);
        }
    }
    private void SaveSettings() => DesktopServices.SaveSettings(settings with
    {
        GameDirectory = gameDirectory ?? GameDirectoryBox.Text,
        LocalDirectory = localDirectory = LocalDirectoryBox.Text.Trim().Trim('"'), VersionId = SelectedVersion.Id,
        GameLanguage = SelectedLocalization.Id,
        InterfaceLanguage = AppLocalization.CurrentLanguage, RenameContentCatalog = RenameCatalogCheckBox.IsChecked == true,
        StopSkyrimUpdates = StopUpdatesCheckBox.IsChecked == true,
        UseSteamDirectory = useSteamDirectory, InstallationMode = SelectedMode.ToString()
    });
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            StatusText.Text = T("Дождитесь завершения операции", "Wait for the operation to finish");
            DetailText.Text = T("Для остановки нажмите «Отменить» и дождитесь завершения операции.", "To stop, select Cancel and wait for the operation to finish.");
            return;
        }
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
