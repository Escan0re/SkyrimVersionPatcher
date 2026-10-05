using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SkyrimVersionPatcher.App;

public partial class InstallationWarningDialog : Window
{
    private static readonly TimeSpan AnswerDuration = TimeSpan.FromMilliseconds(220);
    private readonly bool warnStopUpdates;
    private readonly bool warnRenameCatalog;
    private int activeExitAnimations;
    private bool closed;

    public bool? StopUpdatesChoice { get; private set; }
    public bool? RenameCatalogChoice { get; private set; }

    public InstallationWarningDialog(bool warnStopUpdates, bool warnRenameCatalog)
    {
        if (!warnStopUpdates && !warnRenameCatalog)
            throw new ArgumentException("At least one installation warning must be requested.");

        this.warnStopUpdates = warnStopUpdates;
        this.warnRenameCatalog = warnRenameCatalog;
        InitializeComponent();
        Title = AppLocalization.Translate("Перед установкой", "Before installation");
        StopUpdatesPanel.Visibility = warnStopUpdates ? Visibility.Visible : Visibility.Collapsed;
        RenameCatalogPanel.Visibility = warnRenameCatalog ? Visibility.Visible : Visibility.Collapsed;

        PopulateStopUpdatesWarning();
        PopulateRenameCatalogWarning();
        ConfigureButtons(StopUpdatesYesButton, StopUpdatesNoButton,
            AppLocalization.Translate("остановить обновления Skyrim", "stop Skyrim updates"), StopUpdatesWarningText);
        ConfigureButtons(RenameCatalogYesButton, RenameCatalogNoButton,
            AppLocalization.Translate("переименовать ContentCatalog.txt", "rename ContentCatalog.txt"), RenameCatalogWarningText);

        WizardMotion.SetIsEnabled(this, SystemParameters.ClientAreaAnimation);
        Loaded += (_, _) =>
        {
            WizardMotion.SetIsEnabled(this, WizardMotion.GetIsEnabled(this) && SystemParameters.ClientAreaAnimation
                && (Owner is null || WizardMotion.GetIsEnabled(Owner)));
            FocusRemainingQuestion();
        };
        Closed += (_, _) => closed = true;
        SetDefaultButton();
    }

    private void PopulateStopUpdatesWarning()
    {
        StopUpdatesWarningText.Inlines.Add(new Run(AppLocalization.Translate("Похоже, файл ", "It looks like ")));
        StopUpdatesWarningText.Inlines.Add(new Bold(new Run("appmanifest_489830.acf")));
        StopUpdatesWarningText.Inlines.Add(new Run(AppLocalization.Translate(" не установлен в режим «только для чтения».", " is not set to read-only.")));
        StopUpdatesWarningText.Inlines.Add(new LineBreak());
        StopUpdatesWarningText.Inlines.Add(new Run(AppLocalization.Translate("Steam может изменить или перезаписать этот файл.", "Steam may modify or overwrite this file.")));
        StopUpdatesWarningText.Inlines.Add(new LineBreak());
        StopUpdatesWarningText.Inlines.Add(new Run(AppLocalization.Translate("Хотите сделать его доступным только для чтения? ", "Do you want to make it read-only? ")));
        StopUpdatesWarningText.Inlines.Add(new Bold(new Run(AppLocalization.Translate("(Рекомендуется)", "(recommended)"))));
    }

    private void PopulateRenameCatalogWarning()
    {
        RenameCatalogWarningText.Inlines.Add(new Run(AppLocalization.Translate("Настоятельно рекомендуем переименовать ", "We strongly recommend renaming ")));
        RenameCatalogWarningText.Inlines.Add(new Bold(new Run("ContentCatalog.txt")));
        RenameCatalogWarningText.Inlines.Add(new Run(AppLocalization.Translate(" в ", " to ")));
        RenameCatalogWarningText.Inlines.Add(new Bold(new Run("ContentCatalog.bak")));
        RenameCatalogWarningText.Inlines.Add(new Run(AppLocalization.Translate(
            ", чтобы избежать сбоев и вылетов при использовании более старой версии.",
            " to avoid crashes and other issues when using an older version.")));
        RenameCatalogWarningText.Inlines.Add(new LineBreak());
        RenameCatalogWarningText.Inlines.Add(new Run(AppLocalization.Translate("Переименовать? ", "Would you like to rename it? ")));
        RenameCatalogWarningText.Inlines.Add(new Bold(new Run(AppLocalization.Translate("(Рекомендуется)", "(Recommended)"))));
    }

    private static void ConfigureButtons(Button yes, Button no, string action, TextBlock question)
    {
        yes.Content = AppLocalization.Translate("Да", "Yes");
        no.Content = AppLocalization.Translate("Нет", "No");
        AutomationProperties.SetName(yes, $"{yes.Content}: {action}");
        AutomationProperties.SetName(no, $"{no.Content}: {action}");
        AutomationProperties.SetLabeledBy(yes, question);
        AutomationProperties.SetLabeledBy(no, question);
    }

    private void StopUpdatesYes_Click(object sender, RoutedEventArgs e) => AnswerStopUpdates(true);
    private void StopUpdatesNo_Click(object sender, RoutedEventArgs e) => AnswerStopUpdates(false);
    private void RenameCatalogYes_Click(object sender, RoutedEventArgs e) => AnswerRenameCatalog(true);
    private void RenameCatalogNo_Click(object sender, RoutedEventArgs e) => AnswerRenameCatalog(false);

    private void AnswerStopUpdates(bool choice)
    {
        if (closed || !warnStopUpdates || StopUpdatesChoice.HasValue) return;
        StopUpdatesChoice = choice;
        StopUpdatesYesButton.IsEnabled = StopUpdatesNoButton.IsEnabled = false;
        AnimateAnswer(StopUpdatesPanel, StopUpdatesCard);
    }

    private void AnswerRenameCatalog(bool choice)
    {
        if (closed || !warnRenameCatalog || RenameCatalogChoice.HasValue) return;
        RenameCatalogChoice = choice;
        RenameCatalogYesButton.IsEnabled = RenameCatalogNoButton.IsEnabled = false;
        AnimateAnswer(RenameCatalogPanel, RenameCatalogCard);
    }

    private void AnimateAnswer(FrameworkElement panel, FrameworkElement card)
    {
        FocusRemainingQuestion();
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation || !WizardMotion.GetIsEnabled(this))
        {
            panel.Visibility = Visibility.Collapsed;
            CompleteIfAnswered();
            return;
        }

        activeExitAnimations++;
        panel.IsHitTestVisible = false;
        var height = panel.ActualHeight;
        var translate = (TranslateTransform)card.RenderTransform;
        card.Opacity = 0;
        translate.Y = -8;
        panel.Height = 0;
        card.BeginAnimation(OpacityProperty, CreateAnimation(1, 0));
        translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, -8));
        var collapse = CreateAnimation(height, 0);
        collapse.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            panel.BeginAnimation(HeightProperty, null);
            card.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            activeExitAnimations--;
            CompleteIfAnswered();
        };
        // Shrinking the answered panel moves the remaining question continuously.
        panel.BeginAnimation(HeightProperty, collapse);
    }

    private static DoubleAnimation CreateAnimation(double from, double to) => new(from, to, AnswerDuration)
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    private void CompleteIfAnswered()
    {
        if (!closed && activeExitAnimations == 0
            && (!warnStopUpdates || StopUpdatesChoice.HasValue)
            && (!warnRenameCatalog || RenameCatalogChoice.HasValue))
            DialogResult = true;
    }

    private void SetDefaultButton()
    {
        StopUpdatesYesButton.IsDefault = warnStopUpdates && !StopUpdatesChoice.HasValue;
        RenameCatalogYesButton.IsDefault = warnRenameCatalog && !RenameCatalogChoice.HasValue
            && !StopUpdatesYesButton.IsDefault;
    }

    private void FocusRemainingQuestion()
    {
        SetDefaultButton();
        if (StopUpdatesYesButton.IsDefault) StopUpdatesYesButton.Focus();
        else if (RenameCatalogYesButton.IsDefault) RenameCatalogYesButton.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
