using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Настройки оформления и поведения: тема, акцент, автоматический расчёт после сохранения данных.
/// Все изменения применяются сразу и сохраняются в <see cref="AppSettings"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        _loading = true;
        var settings = ThemeManager.Settings;
        ThemeLight.IsChecked = settings.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = settings.Theme == ThemeMode.Dark;
        ThemeSystem.IsChecked = settings.Theme == ThemeMode.System;
        AccentBlue.IsChecked = settings.Accent == AccentColor.Blue;
        AccentTeal.IsChecked = settings.Accent == AccentColor.Teal;
        AccentIndigo.IsChecked = settings.Accent == AccentColor.Indigo;
        AutoCalcCheck.IsChecked = settings.AutoCalculateAfterSave;
        OpenOutDirCheck.IsChecked = settings.OpenOutDirAfterRun;
        FillTitleProfile(settings);
        FillAbout();
        _loading = false;
        ThemeManager.Changed += OnThemeChanged;
        Closing += (_, _) => SaveTitleProfile();
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void FillAbout()
    {
        VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "—";
        SettingsPathText.Text = AppSettings.FilePath;
        SettingsPathText.ToolTip = AppSettings.FilePath;

        var root = DataLocator.FindRoot(Environment.CurrentDirectory, AppContext.BaseDirectory);
        if (root is null)
        {
            DataRootText.Text = "папка data/norms не найдена";
            NormsText.Text = "—";
            return;
        }

        DataRootText.Text = root;
        DataRootText.ToolTip = root;
        NormsText.Text = InputCatalog.Norms(DataLocator.Norms(root)) is { } norms
            ? $"{norms.Document}, {norms.Edition}"
            : "—";
    }

    // ---------- Профиль титульного листа ----------

    private void FillTitleProfile(AppSettings settings)
    {
        TitleOrganizationBox.Text = settings.TitleOrganization;
        TitleAuthorBox.Text = settings.TitleAuthor;
        TitleSupervisorBox.Text = settings.TitleSupervisor;
        TitleCodeBox.Text = settings.TitleCode;
        TitleCityYearBox.Text = settings.TitleCityYear;
        TitleBuildingBox.Text = settings.TitleBuilding;
        SelectTitleStage(settings.TitleStage);
    }

    private void SelectTitleStage(string stage)
    {
        var value = (stage ?? "").Trim();
        foreach (var candidate in TitleStageCombo.Items)
        {
            if (candidate is ComboBoxItem { Tag: string tag }
                && string.Equals(tag, value, StringComparison.OrdinalIgnoreCase))
            {
                TitleStageCombo.SelectedItem = candidate;
                return;
            }
        }

        if (value.Length == 0)
        {
            TitleStageCombo.SelectedIndex = 0;
            return;
        }

        // В настройках могла оказаться стадия, отличная от «Р»/«П» (например, из старого проекта).
        // Показываем её как есть, чтобы не потерять при сохранении.
        var custom = new ComboBoxItem { Content = value, Tag = value };
        TitleStageCombo.Items.Add(custom);
        TitleStageCombo.SelectedItem = custom;
    }

    // Сохранение без отдельной кнопки: по Enter и по потере фокуса текстового поля, ComboBox — сразу.
    private void OnTitleTextBoxLostFocus(object sender, RoutedEventArgs e) => SaveTitleProfile();

    private void OnTitleTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SaveTitleProfile();
        Keyboard.ClearFocus();
    }

    private void OnTitleStageChanged(object sender, SelectionChangedEventArgs e) => SaveTitleProfile();

    private void OnClearTitleProfile(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            TitleOrganizationBox.Text = "";
            TitleAuthorBox.Text = "";
            TitleSupervisorBox.Text = "";
            TitleCodeBox.Text = "";
            TitleCityYearBox.Text = "";
            TitleBuildingBox.Text = "";
            SelectTitleStage("Р");

            // Очищаем именно сохранённые значения: пустой профиль — норма, в новые проекты ничего не подставляется.
            var settings = ThemeManager.Settings;
            settings.TitleOrganization = "";
            settings.TitleAuthor = "";
            settings.TitleSupervisor = "";
            settings.TitleCode = "";
            settings.TitleCityYear = "";
            settings.TitleBuilding = "";
            settings.TitleStage = "Р";
            settings.Save();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveTitleProfile()
    {
        if (_loading)
        {
            return;
        }

        var stage = TitleStageCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "Р";
        var settings = ThemeManager.Settings;
        var changed = false;
        changed |= SetTitleValue(TitleOrganizationBox.Text, settings.TitleOrganization, value => settings.TitleOrganization = value);
        changed |= SetTitleValue(TitleAuthorBox.Text, settings.TitleAuthor, value => settings.TitleAuthor = value);
        changed |= SetTitleValue(TitleSupervisorBox.Text, settings.TitleSupervisor, value => settings.TitleSupervisor = value);
        changed |= SetTitleValue(TitleCodeBox.Text, settings.TitleCode, value => settings.TitleCode = value);
        changed |= SetTitleValue(TitleCityYearBox.Text, settings.TitleCityYear, value => settings.TitleCityYear = value);
        changed |= SetTitleValue(TitleBuildingBox.Text, settings.TitleBuilding, value => settings.TitleBuilding = value);
        changed |= SetTitleValue(stage, settings.TitleStage, value => settings.TitleStage = value);
        if (changed)
        {
            settings.Save();
        }
    }

    private static bool SetTitleValue(string? raw, string current, Action<string> assign)
    {
        var value = (raw ?? "").Trim();
        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return false;
        }

        assign(value);
        return true;
    }

    // Изменения могли прийти извне (быстрая кнопка темы в главном окне) — синхронизируем переключатели.
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var settings = ThemeManager.Settings;
        ThemeLight.IsChecked = settings.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = settings.Theme == ThemeMode.Dark;
        ThemeSystem.IsChecked = settings.Theme == ThemeMode.System;
        AccentBlue.IsChecked = settings.Accent == AccentColor.Blue;
        AccentTeal.IsChecked = settings.Accent == AccentColor.Teal;
        AccentIndigo.IsChecked = settings.Accent == AccentColor.Indigo;
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<ThemeMode>(tag, out var mode))
        {
            ThemeManager.SetTheme(mode);
        }
    }

    private void OnAccentChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<AccentColor>(tag, out var accent))
        {
            ThemeManager.SetAccent(accent);
        }
    }

    private void OnAutoCalcChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ThemeManager.Settings.AutoCalculateAfterSave = AutoCalcCheck.IsChecked == true;
        ThemeManager.Settings.Save();
    }

    private void OnOpenOutDirChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ThemeManager.Settings.OpenOutDirAfterRun = OpenOutDirCheck.IsChecked == true;
        ThemeManager.Settings.Save();
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(AppSettings.FilePath);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _ = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Не удалось открыть папку настроек: {ex.Message}", "Оформление и поведение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
