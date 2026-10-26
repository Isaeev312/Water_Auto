using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Простое окно сквозного расчёта: проект → «Рассчитать» → файлы выпуска и замечания.
/// Расчёт — тот же <see cref="ProjectPipeline"/>, что и в консоли; мастер из 8 шагов (макет «Экраны») — позже.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Проект Water_Auto (*.waproj)|*.waproj" };
        if (dialog.ShowDialog(this) == true)
        {
            SetProject(dialog.FileName);
        }
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Чертёж (*.dwg;*.dxf)|*.dwg;*.dxf", Title = "Чертёж плана по соглашению о слоях" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var drawing = dialog.FileName;
        var project = Path.ChangeExtension(drawing, ".waproj");
        if (!File.Exists(project))
        {
            File.WriteAllText(project, ProjectFile.ToJson(new ProjectFile { Name = Path.GetFileNameWithoutExtension(drawing), Drawing = Path.GetFileName(drawing) }));
        }

        SetProject(project);
        OpenInputData();
        Summary.Text = $"Создан проект {project}. Заполните исходные данные и нажмите «Рассчитать».";
    }

    private void OnEditData(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ProjectBox.Text))
        {
            Summary.Text = "Сначала выберите файл проекта.";
            return;
        }

        OpenInputData();
    }

    private void OpenInputData()
    {
        var dialog = new InputDataWindow(ProjectBox.Text) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            UpdateDesignSummary();
            Summary.Text = "Исходные данные сохранены.";
        }
    }

    private void OnEditProject(object sender, RoutedEventArgs e)
    {
        if (File.Exists(ProjectBox.Text))
        {
            Open(ProjectBox.Text);
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var project = ProjectBox.Text;
        if (!File.Exists(project))
        {
            Summary.Text = "Файл проекта не найден.";
            return;
        }

        var root = DataLocator.FindRoot(Path.GetDirectoryName(project)!, AppContext.BaseDirectory);
        if (root is null)
        {
            Summary.Text = "Не найдена папка data/norms с нормами.";
            return;
        }

        var output = OutBox.Text;
        RunButton.IsEnabled = false;
        Summary.Text = "Расчёт…";
        try
        {
            var result = await Task.Run(() => ProjectPipeline.Run(project, output, DataLocator.Norms(root), DataLocator.Registry(root)));
            Files.ItemsSource = result.Files;
            Issues.ItemsSource = result.Issues
                .OrderByDescending(i => i.Level)
                .Select(i => new { i.Level, i.Code, i.Message, i.Clause, Location = i.Location?.ToString() })
                .ToList();
            var errors = result.Issues.Count(i => i.Level == Core.IssueLevel.Error);
            Summary.Text = result.Design is { } d
                ? $"Готово. Расход насоса {d.PumpFlow.LitersPerSecond:0.00} л/с ({d.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч), напор {d.PumpHead.Meters:0.0} м, запас воды {d.WaterVolume:0.0} м³. Замечаний: {result.Issues.Count}."
                : $"Расчёт остановлен: ошибок {errors}. Исправьте чертёж или исходные данные — см. замечания.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Summary.Text = $"Ошибка: {ex.Message}";
        }
#pragma warning disable CA1031 // окно не должно закрываться из-за сбоя расчёта: показываем причину и пишем её в журнал
        catch (Exception ex)
        {
            var log = Path.Combine(Path.GetTempPath(), "WaterAuto-crash.log");
            File.WriteAllText(log, ex.ToString());
            Summary.Text = $"Внутренняя ошибка программы ({ex.GetType().Name}): {ex.Message} Подробности — {log}.";
        }
#pragma warning restore CA1031
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void OnOpenFile(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is string file && File.Exists(file))
        {
            Open(file);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(OutBox.Text))
        {
            Open(OutBox.Text);
        }
    }

    private void SetProject(string project)
    {
        ProjectBox.Text = project;
        OutBox.Text = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project) + "_out");
        UpdateDesignSummary();
    }

    // Краткая сводка исходных данных под строкой состояния: видно, что реально пойдёт в расчёт.
    private void UpdateDesignSummary()
    {
        try
        {
            var project = ProjectFile.Load(ProjectBox.Text);
            var d = project.Design;
            var room = d.PumpRoom;
            var temperature = d.MaxAmbientTemperature is { } t ? $"{t:0.#} °C" : "не задана";
            var spacing = d.MaxSpacing is { } m ? $"{m:0.##} м (строже табл. 6.1)" : "по табл. 6.1";
            DesignSummary.Text =
                $"K = {d.SprinklerK:0.###}; H над осью = {d.DictatingAboveAxis:0.00} м; подводящий DN{d.SupplyDn}, {d.SupplyLength:0.0} м; " +
                $"P_вх = {d.InletPressure:0.000} МПа; t среды = {temperature}; шаг = {spacing}; " +
                $"насосная: пол {room.FloorElevation:+0.000;−0.000;0.000} м, земля {room.GroundElevation:+0.000;−0.000;0.000} м, h = {room.Height:0.00} м";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            DesignSummary.Text = $"Исходные данные прочитать не удалось: {ex.Message}";
        }
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
