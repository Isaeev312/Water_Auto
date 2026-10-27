using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Настройки оформления и поведения: тема, акцент, автоматический расчёт после сохранения данных.
/// Все изменения применяются сразу и сохраняются в <see cref="AppSettings"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly bool _loading;

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
        FillAbout();
        _loading = false;
        ThemeManager.Changed += OnThemeChanged;
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
