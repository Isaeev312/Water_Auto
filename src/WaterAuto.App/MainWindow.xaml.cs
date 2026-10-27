using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.App;

/// <summary>
/// Главное окно: проект → исходные данные → расчёт → файлы выпуска и замечания.
/// Расчёт — тот же <see cref="ProjectPipeline"/>, что и в консоли. Нормативных чисел в окне нет:
/// подписи и значения берутся из data/ через <see cref="InputCatalog"/> и сам конвейер.
/// </summary>
public partial class MainWindow : Window
{
    private const string TemplateFilter = "Чертёж DWG (*.dwg)|*.dwg|Чертёж DXF (*.dxf)|*.dxf|Шаблон DWT (*.dwt)|*.dwt";
    private const string ProjectFilter = "Проект Water_Auto (*.waproj)|*.waproj";
    private const string DrawingFilter = "Чертёж плана (*.dwg;*.dxf)|*.dwg;*.dxf|Все файлы (*.*)|*.*";

    // Порядок групп в списке файлов выпуска: важен для заголовков CollectionViewSource.
    private static readonly string[] FileGroupOrder =
    [
        "Планы DWG",
        "Планы DXF",
        "Схемы и листы DXF",
        "Спецификации DOCX/XLSX",
        "Записка и общие данные",
        "Замечания TXT/др.",
        "Прочие файлы",
    ];

    private string? _project;
    private string _outDir = "";
    private IReadOnlyList<IssueRow> _allIssues = [];

    // Требуемые Q и H последнего успешного расчёта: по ним окно подбора установки оценивает каталог,
    // даже если очередной расчёт остановился на ошибке выбранной вручную установки (pump.setInsufficient).
    private double? _requiredFlowM3h;
    private double? _requiredHeadMeters;
    private bool _calculating;

    // Номер версии проверки проекта: результат устаревшего фонового анализа не должен перебивать новый.
    private int _checkVersion;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Changed += (_, _) => UpdateThemeVisuals();
        Closing += OnClosing;
        ShowCheckIdle("Проект не выбран");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowPlacement();
        UpdateThemeVisuals();
        RefreshRecent();
        ShowRootStatus();
        OpenInitialDocument();
    }

    private void ShowRootStatus()
    {
        var root = FindDataRoot();
        if (root is not null)
        {
            RootStatus.Text = $"Данные: {root}";
            if (InputCatalog.Norms(DataLocator.Norms(root)) is { } norms)
            {
                NormsStatus.Text = $"Нормы: {norms.Document}, {norms.Edition}";
            }

            return;
        }

        RootStatus.Text = "Папка data/norms не найдена — расчёт недоступен.";
        NormsStatus.Text = "Нормы: —";
    }

    // Открытие файла, переданного в командной строке (или перетаскиванием на значок): проводник,
    // ярлык, ассоциация «Открыть с помощью». .waproj открывается, DWG/DXF — создаётся проект рядом.
    private void OpenInitialDocument()
    {
        string? path = null;
        try
        {
            path = Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(arg =>
                    !arg.StartsWith('-')
                    && (arg.EndsWith(".waproj", StringComparison.OrdinalIgnoreCase)
                        || arg.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                        || arg.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            if (path.EndsWith(".waproj", StringComparison.OrdinalIgnoreCase))
            {
                SetProject(path);
                Summary.Text = $"Открыт проект: {Path.GetFileName(path)}";
                return;
            }

            var project = Path.ChangeExtension(path, ".waproj");
            if (!File.Exists(project))
            {
                File.WriteAllText(
                    project,
                    ProjectFile.ToJson(new ProjectFile { Name = Path.GetFileNameWithoutExtension(path), Drawing = Path.GetFileName(path) }));
            }

            SetProject(project);
            Summary.Text = $"Создан проект по чертежу {Path.GetFileName(path)}. Заполните исходные данные.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Summary.Text = $"Не удалось открыть файл из командной строки: {ex.Message}";
        }
    }

    private string? FindDataRoot()
    {
        var starts = new List<string> { Environment.CurrentDirectory, AppContext.BaseDirectory };
        if (_project is not null)
        {
            starts.Insert(0, Path.GetDirectoryName(_project)!);
        }

        return DataLocator.FindRoot([.. starts]);
    }

    // ---------- Окно: положение и последняя папка ----------

    // Последняя папка проектов/чертежей для диалогов открытия и сохранения.
    private static string? StartDirectory()
    {
        var directory = ThemeManager.Settings.LastDirectory;
        return directory.Length > 0 && Directory.Exists(directory) ? directory : null;
    }

    private static void RememberDirectory(string path)
    {
        var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            ThemeManager.Settings.LastDirectory = directory;
            ThemeManager.Settings.Save();
        }
    }

    // Восстановление размеров и положения главного окна; окно вне видимой области не восстанавливается.
    private void RestoreWindowPlacement()
    {
        var settings = ThemeManager.Settings;
        if (settings.WindowWidth is not { } width || settings.WindowHeight is not { } height
            || width < MinWidth || height < MinHeight)
        {
            return;
        }

        var left = settings.WindowLeft ?? ((SystemParameters.PrimaryScreenWidth - width) / 2);
        var top = settings.WindowTop ?? ((SystemParameters.PrimaryScreenHeight - height) / 2);
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var visible = left + width > virtualLeft + 60
            && left < virtualLeft + SystemParameters.VirtualScreenWidth - 60
            && top + height > virtualTop + 60
            && top < virtualTop + SystemParameters.VirtualScreenHeight - 60;
        if (!visible)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var settings = ThemeManager.Settings;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.Save();
    }

    // ---------- Проект ----------

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ProjectFilter, Title = "Проект Water_Auto" };
        if (StartDirectory() is { } start)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetProject(dialog.FileName);
    }

    private async void OnNewProject(object sender, RoutedEventArgs e) => await CreateFromDrawing();

    // «Быстрый проект»: минимум вопросов, создание *.waproj с оценками по чертежу и сразу расчёт.
    private async void OnQuickProject(object sender, RoutedEventArgs e)
    {
        var root = FindDataRoot();
        if (root is null)
        {
            Summary.Text = "Не найдена папка data/norms: без норм и каталогов быстрый проект создать нельзя.";
            return;
        }

        var initialDirectory = _project is not null && File.Exists(_project) ? Path.GetDirectoryName(_project) : StartDirectory();
        var dialog = new QuickProjectWindow(initialDirectory) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is not { } request)
        {
            return;
        }

        try
        {
            var created = QuickProject.Create(request, root);
            SetProject(created.ProjectPath);
            if (created.AlreadyExisted)
            {
                Summary.Text = "Проект для этого чертежа уже существует — открыт. Выполняется расчёт…";
            }
            else
            {
                // Ответы быстрого проекта запоминаются: в следующий раз они подставлены по умолчанию.
                var settings = ThemeManager.Settings;
                settings.LastRoomGroup = request.RoomGroup;
                settings.LastSprinklerId = request.SprinklerId ?? "";
                settings.LastPumpSetDesignation = request.PumpSetDesignation ?? "";
                settings.QuickProjectApplySuggestions = request.ApplyDrawingSuggestions;
                settings.Save();

                var applied = created.AppliedSuggestions.Count > 0
                    ? " По чертежу подставлено: " + string.Join("; ", created.AppliedSuggestions) + "."
                    : " Оценки по чертежу не применялись.";
                var warnings = created.Issues.Count > 0 ? $" Замечаний проверки: {created.Issues.Count} — откройте мастер данных." : "";
                Summary.Text = $"Создан проект {Path.GetFileName(created.ProjectPath)}.{applied}{warnings}";
            }

            await RunCalculationAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Summary.Text = $"Не удалось создать быстрый проект: {ex.Message}";
        }
    }

    private async Task CreateFromDrawing()
    {
        var dialog = new OpenFileDialog { Filter = DrawingFilter, Title = "Чертёж плана по соглашению о слоях" };
        if (StartDirectory() is { } start)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var drawing = dialog.FileName;
        var project = Path.ChangeExtension(drawing, ".waproj");
        if (!File.Exists(project))
        {
            File.WriteAllText(
                project,
                ProjectFile.ToJson(new ProjectFile { Name = Path.GetFileNameWithoutExtension(drawing), Drawing = Path.GetFileName(drawing) }));
        }

        SetProject(project);
        Summary.Text = $"Создан проект {Path.GetFileName(project)}. Заполните исходные данные.";
        if (OpenInputData() && ThemeManager.Settings.AutoCalculateAfterSave)
        {
            await RunCalculationAsync();
        }
    }

    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = TemplateFilter,
            Title = "Шаблон чертежа: слои ПТ_ и памятка",
            FileName = "WaterAuto-шаблон.dwg",
            DefaultExt = ".dwg",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (StartDirectory() is { } start)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DrawingTemplate.Write(dialog.FileName);
            RememberDirectory(dialog.FileName);
            Summary.Text = $"Шаблон записан: {dialog.FileName}. Архитектурный план вставляется внешней ссылкой в 0,0, контуры — на слоях ПТ_.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary.Text = $"Не удалось записать шаблон: {ex.Message}";
        }
    }

    private void OnRecentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentBox.SelectedItem is string path && File.Exists(path))
        {
            SetProject(path);
            RecentBox.SelectedIndex = -1;
        }
    }

    private async void OnEditData(object sender, RoutedEventArgs e)
    {
        if (_project is null || !File.Exists(_project))
        {
            Summary.Text = "Сначала выберите проект или создайте его по чертежу.";
            return;
        }

        await OpenInputDataAndCalculateAsync();
    }

    /// <summary>Открыть мастер исходных данных; true — пользователь сохранил данные.</summary>
    private bool OpenInputData()
    {
        if (_project is null)
        {
            return false;
        }

        var dialog = new InputDataWindow(_project) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        UpdateDesignSummary();
        Summary.Text = "Исходные данные сохранены.";
        _ = RefreshProjectCheckAsync(); // данные изменились — готовность чертежа пересчитываем
        return true;
    }

    // Настройка «рассчитывать сразу после сохранения данных»: пользователь заполнил стандартные данные — считаем сами.
    private async Task<bool> OpenInputDataAndCalculateAsync()
    {
        if (!OpenInputData())
        {
            return false;
        }

        if (ThemeManager.Settings.AutoCalculateAfterSave)
        {
            await RunCalculationAsync();
        }

        return true;
    }

    private void OnEditProject(object sender, RoutedEventArgs e)
    {
        if (_project is not null && File.Exists(_project))
        {
            Open(_project);
        }
    }

    private void SetProject(string path)
    {
        _project = Path.GetFullPath(path);
        ProjectPathText.Text = _project;
        ProjectPathText.ToolTip = _project;
        RememberDirectory(_project);
        _outDir = Path.Combine(Path.GetDirectoryName(_project)!, Path.GetFileNameWithoutExtension(_project) + "_out");
        OutBox.Text = _outDir;
        AddRecent(_project);
        // Требуемые Q/H другого проекта не должны открывать окно подбора с чужими величинами.
        _requiredFlowM3h = null;
        _requiredHeadMeters = null;
        PumpSelectButton.Visibility = Visibility.Collapsed;
        CheckCard.Visibility = Visibility.Visible; // готовность нового проекта показываем до его расчёта
        UpdateDesignSummary();
        _ = RefreshProjectCheckAsync();
        Summary.Text = $"Проект: {_project}";
    }

    private void RefreshRecent()
    {
        var recent = ThemeManager.Settings.RecentProjects.Where(File.Exists).ToList();
        RecentBox.ItemsSource = recent;
        RecentBox.IsEnabled = recent.Count > 0;
        RecentBox.ToolTip = recent.Count == 0 ? "Список пуст — откройте проект или создайте его по чертежу." : _project ?? recent[0];
    }

    private void AddRecent(string path)
    {
        var recent = ThemeManager.Settings.RecentProjects;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        while (recent.Count > 10)
        {
            recent.RemoveAt(recent.Count - 1);
        }

        ThemeManager.Settings.Save();
        RefreshRecent();
    }

    private void OnBrowseOut(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка выпуска проекта" };
        if (StartDirectory() is { } start)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OutBox.Text = dialog.FolderName;
            _outDir = dialog.FolderName;
            RememberDirectory(dialog.FolderName);
        }
    }

    private void OnOpenOutFolder(object sender, RoutedEventArgs e)
    {
        var dir = OutBox.Text.Trim();
        if (Directory.Exists(dir))
        {
            Open(dir);
        }
        else
        {
            Summary.Text = "Папка выпуска ещё не создана — выполните расчёт.";
        }
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // ---------- Расчёт ----------

    private async void OnRun(object sender, RoutedEventArgs e) => await RunCalculationAsync();

    /// <summary>
    /// Общий путь расчёта и выпуска: кнопка «Рассчитать», «Применить и пересчитать» и «Автоподбор»
    /// в окне подбора установки. Фоновая работа — тот же <see cref="ProjectPipeline.Run"/>, что и в консоли;
    /// исключения наружу не выходят — причина показывается в строке состояния.
    /// </summary>
    private async Task RunCalculationAsync()
    {
        if (_calculating)
        {
            return;
        }

        if (_project is null || !File.Exists(_project))
        {
            Summary.Text = "Файл проекта не найден.";
            return;
        }

        var root = FindDataRoot();
        if (root is null)
        {
            Summary.Text = "Не найдена папка data/norms с нормами и каталогами.";
            return;
        }

        var output = string.IsNullOrWhiteSpace(OutBox.Text) ? _outDir : OutBox.Text.Trim();
        _calculating = true;
        RunButton.IsEnabled = false;
        PumpSelectButton.IsEnabled = false;
        RunButton.Content = "Расчёт…";
        RunProgress.Visibility = Visibility.Visible;
        Summary.Text = "Расчёт и выпуск документов…";
        try
        {
            var project = _project;
            var result = await Task.Run(() => ProjectPipeline.Run(project, output, DataLocator.Norms(root), DataLocator.Registry(root)));
            ShowResult(result, output);
            OpenOutputIfRequested(result, output);
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
            _calculating = false;
            RunButton.IsEnabled = true;
            PumpSelectButton.IsEnabled = true;
            RunButton.Content = "Рассчитать";
            RunProgress.Visibility = Visibility.Collapsed;
            UpdatePumpSelectVisibility();
        }
    }

    private void ShowResult(PipelineResult result, string output)
    {
        _outDir = output;
        var errors = result.Issues.Count(i => i.Level == IssueLevel.Error);
        ShowSummary(result);
        ShowFiles(result.Files, output);
        ShowIssues(result.Issues);

        if (!result.Success)
        {
            ResultsTabs.SelectedItem = IssuesTab;
            Summary.Text = $"Расчёт остановлен: ошибок {errors}. Исправьте чертёж или исходные данные — см. замечания.{PumpSelectionNote()}";
        }
        else if (result.Design is { } design)
        {
            if (errors > 0)
            {
                ResultsTabs.SelectedItem = IssuesTab;
                Summary.Text = string.Create(
                    CultureInfo.GetCultureInfo("ru-RU"),
                    $"Выпуск готов: файлов {result.Files.Count}, но есть ошибки — {errors}. Проверьте замечания: расход {design.PumpFlow.LitersPerSecond:0.00} л/с, напор {design.PumpHead.Meters:0.0} м, запас воды {design.WaterVolume:0.0} м³.{PumpSelectionNote()}");
            }
            else
            {
                ResultsTabs.SelectedItem = FilesTab;
                Summary.Text = string.Create(
                    CultureInfo.GetCultureInfo("ru-RU"),
                    $"Готово: файлов {result.Files.Count}. Расход насоса {design.PumpFlow.LitersPerSecond:0.00} л/с, напор {design.PumpHead.Meters:0.0} м, запас воды {design.WaterVolume:0.0} м³, замечаний {result.Issues.Count}.{PumpSelectionNote()}");
            }
        }
        else
        {
            Summary.Text = $"Расчёт не выполнен: замечаний {result.Issues.Count}, ошибок {errors}.{PumpSelectionNote()}";
            ResultsTabs.SelectedItem = IssuesTab;
        }

        ResultsEmpty.Visibility = Visibility.Collapsed;
        ResultsTabs.Visibility = Visibility.Visible;
        CheckCard.Visibility = Visibility.Collapsed; // место готовности занимает сводка и выпуск
        AnimateIn(ResultsTabs);
        AnimateIn(SummaryFilled);
    }

    // После успешного расчёта — открыть папку выпуска, если так задано в настройках.
    private static void OpenOutputIfRequested(PipelineResult result, string output)
    {
        if (!result.Success || !ThemeManager.Settings.OpenOutDirAfterRun || !Directory.Exists(output))
        {
            return;
        }

        try
        {
            Open(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Проводник не открылся — это не ошибка расчёта: сообщение уже есть в сводке.
        }
    }

    // Мягкое появление сводки/результатов после расчёта — визуальный акцент без «прыжков» вёрстки.
    private static void AnimateIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // Пометка в строке состояния: установка выбрана вручную в окне подбора или подобрана автоматически.
    private string PumpSelectionNote()
    {
        if (_project is null || !File.Exists(_project))
        {
            return "";
        }

        try
        {
            var designation = ProjectFile.Load(_project).Design.PumpStation.SetDesignation;
            return designation.Length > 0
                ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $" Установка выбрана вручную: {designation}.")
                : " Установка подобрана автоматически по Q и H.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void ShowSummary(PipelineResult result)
    {
        SummaryEmpty.Visibility = Visibility.Collapsed;
        SummaryFilled.Visibility = Visibility.Visible;
        IssuesValue.Text = result.Issues.Count.ToString(CultureInfo.InvariantCulture);
        if (result.Design is not { } design)
        {
            FlowValue.Text = HeadValue.Text = VolumeValue.Text = "—";
            PumpSetValue.Text = "Установка не подобрана — см. замечания";
            PumpCountsValue.Text = "";
            ShowPumpRoom(null);
            ShowPumpChart(null);
            return;
        }

        FlowValue.Text = string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.PumpFlow.LitersPerSecond:0.00}");
        HeadValue.Text = string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.PumpHead.Meters:0.0}");
        VolumeValue.Text = string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.WaterVolume:0.0}");
        if (result.Pump is { } pump)
        {
            PumpSetValue.Text = string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{pump.Designation} · {pump.Pump} · {pump.PowerKw:0.##} кВт");
            PumpCountsValue.Text = string.Create(
                CultureInfo.GetCultureInfo("ru-RU"),
                $"{pump.MainPumps} основных + {pump.StandbyPumps} резервных насосов; напор по кривой каталога при расчётной подаче — {pump.HeadAtDutyMeters:0.0} м.");

            // Требуемые Q и H помнятся после успешного расчёта: окно подбора остаётся доступным,
            // если следующий расчёт остановится на ошибке выбранной вручную установки.
            _requiredFlowM3h = pump.RequiredFlowM3h;
            _requiredHeadMeters = pump.RequiredHeadMeters;
        }
        else
        {
            PumpSetValue.Text = "Установка не подобрана — см. замечания";
            PumpCountsValue.Text = "";
        }

        ShowPumpRoom(result.PumpRoom);
        ShowPumpChart(result.Pump);
    }

    // Помещение насосной и габариты установки из расчёта (PipelineResult.PumpRoom): размеры контура в плане,
    // разметка вводов/выпусков/патрубков ГМ и размеры установки с прибором управления из каталога.
    // Нормативных чисел здесь нет — всё приходит из конвейера; нет данных — строка скрыта.
    private void ShowPumpRoom(PumpStationSummary? room)
    {
        if (room is null)
        {
            PumpRoomValue.Text = "";
            PumpRoomValue.Visibility = Visibility.Collapsed;
            return;
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var lines = new List<string>();
        if (room.RoomId is not null)
        {
            lines.Add(room is { RoomLengthM: { } length, RoomWidthM: { } width, RoomAreaM2: { } area }
                ? string.Create(ru, $"Помещение насосной: {length:0.0} × {width:0.0} м, {area:0.0} м²; вводов {room.Inlets}, выпусков {room.Outlets}, патрубков ГМ {room.TruckInlets}.")
                : string.Create(ru, $"Помещение насосной: вводов {room.Inlets}, выпусков {room.Outlets}, патрубков ГМ {room.TruckInlets}."));
        }
        else
        {
            lines.Add("Помещение насосной: в чертеже не размечено (слой ПТ_НС).");
        }

        if (room.SetDesignation.Length > 0)
        {
            lines.Add(string.Create(
                ru,
                $"Габариты установки: {room.SetHydraulic.LengthMm:0} × {room.SetHydraulic.WidthMm:0} × {room.SetHydraulic.HeightMm:0} мм; прибор управления: {room.SetCabinet.LengthMm:0} × {room.SetCabinet.WidthMm:0} × {room.SetCabinet.HeightMm:0} мм; коллектор DN{room.CollectorDn}, ось {room.CollectorAxisMm:0} мм."));
        }

        PumpRoomValue.Text = string.Join(Environment.NewLine, lines);
        PumpRoomValue.Visibility = Visibility.Visible;
    }

    // Мини-график Q–H в карточке насосной: кривая, рабочая точка и линия требуемого напора.
    private void ShowPumpChart(PumpSetSummary? pump)
    {
        if (pump is null || pump.Curve.Count == 0)
        {
            PumpChart.Clear(PumpChartCanvas);
            PumpChartCanvas.Visibility = Visibility.Collapsed;
            PumpChartHint.Visibility = Visibility.Collapsed;
            return;
        }

        PumpChart.Draw(PumpChartCanvas, pump);
        PumpChartCanvas.Visibility = Visibility.Visible;
        PumpChartHint.Visibility = Visibility.Visible;
    }

    // Кнопка «Подбор установки…» доступна, когда известны требуемые Q и H расчёта.
    private void UpdatePumpSelectVisibility()
    {
        var available = _requiredFlowM3h is not null && _requiredHeadMeters is not null;
        PumpSelectButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        PumpSelectButton.IsEnabled = available && !_calculating;
    }

    // Окно подбора: сохраняет выбор установки в проект, после применения — общий пересчёт.
    private async void OnSelectPumpSet(object sender, RoutedEventArgs e)
    {
        if (_project is null || !File.Exists(_project))
        {
            Summary.Text = "Сначала выберите проект или создайте его по чертежу.";
            return;
        }

        if (_requiredFlowM3h is not { } flow || _requiredHeadMeters is not { } head)
        {
            Summary.Text = "Подбор установки доступен после расчёта: нет расчётных Q и H.";
            return;
        }

        var root = FindDataRoot();
        if (root is null)
        {
            Summary.Text = "Не найдена папка data/norms с нормами и каталогом установок.";
            return;
        }

        var dialog = new PumpSetWindow(_project, DataLocator.Norms(root), flow, head) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return; // отмена — проект не менялся, пересчёт не нужен
        }

        UpdateDesignSummary();
        await RunCalculationAsync();
    }

    private void ShowFiles(IReadOnlyList<string> files, string output)
    {
        var items = new List<FileItem>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var info = new FileInfo(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var group = FileGroupFor(name, info.Extension.TrimStart('.'));
            items.Add(new FileItem(
                name,
                info.Extension.TrimStart('.').ToUpperInvariant(),
                info.Length < 1024 ? $"{info.Length} Б" : $"{info.Length / 1024.0:0.#} КБ",
                Path.GetRelativePath(output, file),
                file,
                group,
                Array.IndexOf(FileGroupOrder, group)));
        }

        // Группировка по типу выпуска: заголовки групп со счётчиками строит GroupStyle, поэтому сортируем
        // элементы по порядку групп, а внутри группы — по имени файла.
        var sorted = items
            .OrderBy(f => f.GroupOrder)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var view = new CollectionViewSource { Source = sorted };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileItem.Group)));
        FilesList.ItemsSource = view.View;
        FilesTabHeader.Text = $"Файлы выпуска ({items.Count})";
    }

    // Группа файла выпуска по имени и расширению: состав выпуска задаёт ProjectPipeline, здесь — только раскладка.
    private static string FileGroupFor(string name, string extension)
    {
        return extension.ToUpperInvariant() switch
        {
            "DWG" => "Планы DWG",
            "DXF" => name.StartsWith("plan", StringComparison.OrdinalIgnoreCase) ? "Планы DXF" : "Схемы и листы DXF",
            "DOCX" or "XLSX" => name.StartsWith("specification", StringComparison.OrdinalIgnoreCase)
                ? "Спецификации DOCX/XLSX"
                : "Записка и общие данные",
            "TXT" => "Замечания TXT/др.",
            _ => "Прочие файлы",
        };
    }

    private void ShowIssues(IReadOnlyList<Issue> issues)
    {
        _allIssues =
        [
            .. issues
                .OrderByDescending(i => i.Level)
                .ThenBy(i => i.Code, StringComparer.Ordinal)
                .Select(i => new IssueRow(
                    i.Level switch
                    {
                        IssueLevel.Error => "Ошибка",
                        IssueLevel.Warning => "Предупреждение",
                        _ => "Информация",
                    },
                    i.Level.ToString(),
                    (int)i.Level,
                    i.Code,
                    i.Message,
                    i.Clause ?? "",
                    i.Location?.ToString() ?? "")),
        ];
        IssuesTabHeader.Text = $"Замечания ({_allIssues.Count})";
        ApplyIssueFilter();
    }

    private void OnIssueFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyIssueFilter();

    private void ApplyIssueFilter()
    {
        if (Issues is null)
        {
            return; // событие выбора фильтра приходит ещё во время разбора XAML
        }

        if (_allIssues.Count == 0)
        {
            Issues.ItemsSource = _allIssues;
            return;
        }

        var filter = (IssueFilter.SelectedItem as ComboBoxItem)?.Content as string;
        Issues.ItemsSource = filter switch
        {
            "Ошибки" => _allIssues.Where(i => i.LevelKey == nameof(IssueLevel.Error)).ToList(),
            "Предупреждения" => _allIssues.Where(i => i.LevelKey == nameof(IssueLevel.Warning)).ToList(),
            "Информация" => _allIssues.Where(i => i.LevelKey == nameof(IssueLevel.Info)).ToList(),
            _ => _allIssues,
        };
    }

    private void OnOpenFile(object sender, MouseButtonEventArgs e)
    {
        // Открываем именно файл под курсором: двойной щелчок по заголовку группы ничего не открывает.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(FilesList, source) is ListBoxItem { DataContext: FileItem file }
            && File.Exists(file.FullPath))
        {
            e.Handled = true;
            Open(file.FullPath);
        }
    }

    // ---------- Исходные данные: краткая сводка ----------

    private void UpdateDesignSummary()
    {
        if (_project is null)
        {
            DesignSummary.Text = "Проект не выбран.";
            return;
        }

        try
        {
            var project = ProjectFile.Load(_project);
            var d = project.Design;
            var room = d.PumpRoom;
            var temperature = d.MaxAmbientTemperature is { } t ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{t:0.#} °C") : "не задана";
            var spacing = d.MaxSpacing is { } m ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{m:0.##} м (строже табл. 6.1)") : "по табл. 6.1";
            var pump = d.PumpStation.SetDesignation.Length > 0 ? $"установка {d.PumpStation.SetDesignation}" : "установка — автоподбор по Q и H";
            DesignSummary.Text = string.Create(
                CultureInfo.GetCultureInfo("ru-RU"),
                $"Объект: {project.Name}. Группа {d.RoomGroup}; K = {d.SprinklerK:0.###}; H над осью = {d.DictatingAboveAxis:0.00} м; подводящий DN{d.SupplyDn}, {d.SupplyLength:0.0} м; P_вх = {d.InletPressure:0.000} МПа; t среды = {temperature}; шаг = {spacing}. Насосная: {pump}; пол {room.FloorElevation:+0.000;−0.000;0.000} м, земля {room.GroundElevation:+0.000;−0.000;0.000} м, h = {room.Height:0.00} м.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            DesignSummary.Text = $"Исходные данные прочитать не удалось: {ex.Message}";
        }
    }

    // ---------- Проверка проекта: готовность до расчёта ----------

    private void OnCheckProject(object sender, RoutedEventArgs e) => _ = RefreshProjectCheckAsync();

    /// <summary>
    /// Асинхронная проверка чертежа и исходных данных до расчёта: <see cref="DrawingPreview.Analyze"/> выполняется
    /// в фоне, результат показывается в карточке. Проверка информационная — кнопку «Рассчитать» не блокирует;
    /// номер версии отсекает устаревший результат, если проект сменили или проверку запустили повторно.
    /// </summary>
    private async Task RefreshProjectCheckAsync()
    {
        if (_project is null)
        {
            ShowCheckIdle("Проект не выбран");
            return;
        }

        if (!File.Exists(_project))
        {
            ShowCheckIdle("Файл проекта не найден");
            return;
        }

        var version = ++_checkVersion;
        var project = _project;
        var normsDir = FindDataRoot() is { } root ? DataLocator.Norms(root) : null;
        CheckButton.IsEnabled = false;
        CheckProgress.Visibility = Visibility.Visible;
        CheckDetails.Visibility = Visibility.Collapsed;
        SetCheckPill("Pill.Info", "Проверка…");
        try
        {
            var preview = await Task.Run(() => DrawingPreview.Analyze(project, normsDir));
            if (version == _checkVersion)
            {
                ShowCheck(preview);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (version == _checkVersion)
            {
                ShowCheckError($"Не удалось проверить проект: {ex.Message}");
            }
        }
#pragma warning disable CA1031 // проверка информационная: любой сбой показываем в карточке, окно не роняем
        catch (Exception ex)
        {
            if (version == _checkVersion)
            {
                ShowCheckError($"Проверка не выполнена ({ex.GetType().Name}): {ex.Message}");
            }
        }
#pragma warning restore CA1031
        finally
        {
            if (version == _checkVersion)
            {
                CheckButton.IsEnabled = true;
                CheckProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    // Статус-пилюля по худшему уровню и короткая сводка распознанного в чертеже.
    private void ShowCheck(DrawingPreviewResult preview)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var errors = preview.Issues.Count(i => i.Level == IssueLevel.Error);
        var warnings = preview.Issues.Count(i => i.Level == IssueLevel.Warning);
        if (!preview.DrawingFound)
        {
            SetCheckPill("Pill.Warning", "Чертёж не найден");
        }
        else if (errors > 0)
        {
            SetCheckPill("Pill.Error", string.Create(ru, $"Есть ошибки ({errors})"));
        }
        else if (warnings > 0)
        {
            SetCheckPill("Pill.Warning", string.Create(ru, $"Есть замечания ({warnings})"));
        }
        else
        {
            SetCheckPill("Pill.Success", "Готов к расчёту");
        }

        var lines = new List<string>();
        if (!preview.DrawingFound)
        {
            lines.Add(Truncate(
                preview.Issues.Count > 0 ? preview.Issues[0].Message : $"Чертёж не найден: {preview.DrawingPath}.",
                200));
        }
        else
        {
            lines.Add(preview.Rooms.Count > 0
                ? string.Create(ru, $"Помещений: {preview.Rooms.Count}, площадь {preview.TotalProtectedAreaM2:0.0} м².")
                : "Помещения на плане не распознаны (слой ПТ_КОНТУР).");
            if (preview.PumpRoomId is { } roomId)
            {
                lines.Add(preview is { PumpRoomLengthM: { } length, PumpRoomWidthM: { } width }
                    ? string.Create(ru, $"Насосная: {length:0.0} × {width:0.0} м; вводов {preview.PumpRoomInlets}, выпусков {preview.PumpRoomOutlets}, патрубков ГМ {preview.TruckInlets}.")
                    : string.Create(ru, $"Насосная {roomId}: вводов {preview.PumpRoomInlets}, выпусков {preview.PumpRoomOutlets}, патрубков ГМ {preview.TruckInlets}."));
            }
            else
            {
                lines.Add("Помещение насосной не размечено (слой ПТ_НС).");
            }

            if (preview.SlabBottomM is { } slab)
            {
                lines.Add(string.Create(ru, $"Низ перекрытия: отм. {slab:+0.00;−0.00;0.00} м."));
            }

            if (preview.Suggestions.Count > 0)
            {
                lines.Add(string.Create(ru, $"Подсказок по чертежу: {preview.Suggestions.Count} — применяются в мастере исходных данных."));
            }

            var problem = preview.Issues.FirstOrDefault(i => i.Level == IssueLevel.Error)
                ?? preview.Issues.FirstOrDefault(i => i.Level == IssueLevel.Warning);
            if (problem is not null)
            {
                lines.Add("Проверить: " + Truncate(problem.Message, 200));
            }
        }

        CheckDetails.Text = string.Join(Environment.NewLine, lines);
        CheckDetails.ToolTip = preview.Issues.Count == 0
            ? null
            : string.Join(Environment.NewLine, preview.Issues.Select(i => $"{i.Level}: {i.Message}"));
        CheckDetails.Visibility = Visibility.Visible;
    }

    // Нет проекта/файла или проверка ещё не выполнялась: карточка остаётся с нейтральной подсказкой.
    private void ShowCheckIdle(string status)
    {
        _checkVersion++;
        SetCheckPill("Pill.Neutral", status);
        CheckButton.IsEnabled = _project is not null && File.Exists(_project);
        CheckProgress.Visibility = Visibility.Collapsed;
        CheckDetails.Text = "";
        CheckDetails.ToolTip = null;
        CheckDetails.Visibility = Visibility.Collapsed;
    }

    private void ShowCheckError(string message)
    {
        SetCheckPill("Pill.Warning", "Проверка не выполнена");
        CheckDetails.Text = Truncate(message, 240);
        CheckDetails.ToolTip = message;
        CheckDetails.Visibility = Visibility.Visible;
    }

    // Стили пилюли — именованные из дизайн-системы (Pill.Neutral/Success/Warning/Error/Info).
    private void SetCheckPill(string styleKey, string text)
    {
        CheckStatusPill.Style = (Style)FindResource(styleKey);
        CheckStatusText.Text = text;
    }

    private static string Truncate(string text, int maxLength)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
    }

    // ---------- Тема и настройки ----------

    private void OnToggleTheme(object sender, RoutedEventArgs e) => ThemeManager.ToggleTheme();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        UpdateThemeVisuals();
    }

    private void UpdateThemeVisuals()
    {
        var dark = ThemeManager.IsDark;
        ThemeStatus.Text = dark ? "Тёмная тема" : "Светлая тема";
        ThemeLabel.Text = dark ? "Светлая" : "Тёмная";
        ThemeButton.ToolTip = dark ? "Включить светлую тему" : "Включить тёмную тему";
        SunGroup.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
        MoonIcon.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Горячие клавиши ----------

    // F5 — расчёт, Ctrl+O — открыть проект, Ctrl+E — мастер исходных данных, Ctrl+, — настройки.
    // В полях ввода Ctrl+E не перехватываем: в редакторах это команда перемещения курсора.
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var textEditing = Keyboard.FocusedElement is TextBoxBase;

        if (e.Key == Key.F5)
        {
            if (!RunButton.IsEnabled)
            {
                return;
            }

            e.Handled = true;
            await RunCalculationAsync();
        }
        else if (ctrl && e.Key == Key.O)
        {
            e.Handled = true;
            OnOpenProject(this, new RoutedEventArgs());
        }
        else if (ctrl && e.Key == Key.E && !textEditing)
        {
            e.Handled = true;
            OnEditData(this, new RoutedEventArgs());
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            e.Handled = true;
            OnOpenSettings(this, new RoutedEventArgs());
        }
    }

    // ---------- Перетаскивание ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return;
        }

        var file = files[0];
        if (file.EndsWith(".waproj", StringComparison.OrdinalIgnoreCase))
        {
            SetProject(file);
            return;
        }

        if (file.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            var project = Path.ChangeExtension(file, ".waproj");
            if (!File.Exists(project))
            {
                File.WriteAllText(project, ProjectFile.ToJson(new ProjectFile { Name = Path.GetFileNameWithoutExtension(file), Drawing = Path.GetFileName(file) }));
            }

            SetProject(project);
            await OpenInputDataAndCalculateAsync();
        }
    }

    private sealed record FileItem(string Name, string Kind, string Size, string Relative, string FullPath, string Group, int GroupOrder);

    private sealed record IssueRow(string LevelText, string LevelKey, int LevelOrder, string Code, string Message, string Clause, string Location);
}
