using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.App;

/// <summary>
/// Модальное окно подбора комплектной насосной установки из каталога: подходящие варианты — первыми
/// (как автоподбор), текущий выбор проекта выделен, неподходящая установка требует явного подтверждения
/// в самом окне. Над таблицей — поиск по каталогу и фильтр «Только подходящие»; строки показывают габариты
/// установки, прибора управления и ось коллекторов для проверки помещения. В шапке — помещение насосной по
/// чертежу проекта (размеры, площадь, вводы/выпуски, патрубки ГМ) с бейджем состояния чертежа: данные видны
/// сразу, а сбой чтения остаётся в бейдже и не мешает подбору. Выбор сохраняется в
/// <c>design.pumpStation.setDesignation</c> через <see cref="ProjectJson"/> (правка ключа без потери
/// незнакомых полей проекта); пересчёт и обновление сводки выполняет главное окно после закрытия окна с
/// <see cref="Window.DialogResult"/> = <c>true</c>.
/// </summary>
public partial class PumpSetWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly string _projectPath;
    private readonly string _normsDir;
    private readonly double _requiredFlowM3h;
    private readonly double _requiredHeadMeters;
    private readonly string _currentDesignation;

    // Весь каталог в порядке автоподбора; фильтр лишь выбирает из него строки для таблицы.
    private List<PumpSetRow> _allRows = [];

    // Последняя осознанно выбранная установка: фильтр может временно убрать её из таблицы, но после сброса
    // фильтра выделение возвращается к ней, если она снова видна.
    private string? _selectedDesignation;

    /// <param name="projectPath">Файл проекта <c>*.waproj</c> — выбор сохраняется в него.</param>
    /// <param name="normsDir">Папка <c>data/norms</c> — из неё каталог установок (как в расчёте).</param>
    /// <param name="requiredFlowM3h">Требуемая подача установки из расчёта, м³/ч.</param>
    /// <param name="requiredHeadMeters">Требуемый напор установки из расчёта, м.</param>
    public PumpSetWindow(string projectPath, string normsDir, double requiredFlowM3h, double requiredHeadMeters)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentException.ThrowIfNullOrEmpty(normsDir);
        InitializeComponent();
        _projectPath = projectPath;
        _normsDir = normsDir;
        _requiredFlowM3h = requiredFlowM3h;
        _requiredHeadMeters = requiredHeadMeters;
        _currentDesignation = ReadCurrentDesignation();
        RequiredText.Text = string.Create(Ru, $"Q = {requiredFlowM3h:0.0} м³/ч · H = {requiredHeadMeters:0.0} м");
        CurrentSetText.Text = _currentDesignation.Length > 0
            ? $"Текущая установка: {_currentDesignation} (выбрана вручную)"
            : "Текущая установка: автоподбор по расчётным Q и H";
        LoadDrawingPumpRoom();
        LoadCandidates();
    }

    /// <summary>Обозначение выбранной установки; <c>null</c> — автоподбор (ключ <c>setDesignation</c> удалён из проекта).</summary>
    public string? SelectedDesignation { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is { } selected)
        {
            CandidatesGrid.ScrollIntoView(selected);
        }
    }

    // ---------- Каталог и фильтр ----------

    private void LoadCandidates()
    {
        IReadOnlyList<PumpSetCandidate> candidates;
        try
        {
            candidates = PumpAdvisor.Candidates(_normsDir, _requiredFlowM3h, _requiredHeadMeters);
        }
#pragma warning disable CA1031 // окно не должно закрываться из-за нечитаемого каталога: показываем причину и даём автоподбор
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError($"Каталог насосных установок прочитать не удалось: {ex.Message}");
            candidates = [];
        }

        _allRows = candidates
            .Select(c => new PumpSetRow(
                c.Designation,
                c.Pump,
                c.Maker,
                c.MainPumps,
                c.StandbyPumps,
                c.PowerKw,
                c.HeadAtDutyMeters,
                c.Fits,
                c.MarginMeters,
                c.CollectorDn,
                c.CollectorAxisMm,
                c.Hydraulic,
                c.Cabinet,
                _currentDesignation.Length > 0 && string.Equals(c.Designation, _currentDesignation, StringComparison.Ordinal)))
            .ToList();

        if (_allRows.Count == 0)
        {
            SearchBox.IsEnabled = false;
            FitsOnlyCheck.IsEnabled = false;
            EmptyPanel.Visibility = Visibility.Visible;
            NoResultsPanel.Visibility = Visibility.Collapsed;
            FilterCountText.Text = "Показано 0";
            ApplyButton.IsEnabled = false;
            SelectionHint.Text = "Каталог установок пуст: доступен только автоподбор по расчётным Q и H.";
            return;
        }

        if (_allRows.FirstOrDefault(r => r.IsCurrent) is { } current)
        {
            // Текущая установка проекта известна, её габариты — сразу в шапке: проверка помещения насосной.
            CurrentSetFootprintText.Text = string.Create(
                Ru,
                $"Габариты установки {current.FootprintText} мм, прибор управления {current.CabinetFootprintText} мм, ось коллекторов {current.CollectorAxisMm:0.#} мм от пола.");
            CurrentSetFootprintText.Visibility = Visibility.Visible;
        }
        else if (_currentDesignation.Length > 0)
        {
            CurrentSetText.Text = string.Create(
                Ru,
                $"Текущая установка: {_currentDesignation} — не найдена в каталоге, расчёт остановится с ошибкой pump.setNotFound.");
        }

        // Текущий выбор проекта, иначе первый подходящий (он же первый в порядке автоподбора).
        ApplyFilter(_currentDesignation.Length > 0 ? _currentDesignation : null);
    }

    // Пересобирает строки таблицы под запрос и флажок «Только подходящие»; порядок строк — порядок автоподбора.
    private void ApplyFilter(string? preferredDesignation = null)
    {
        if (SearchBox is null || FitsOnlyCheck is null || CandidatesGrid is null || _allRows.Count == 0)
        {
            return; // событие приходит ещё во время разбора XAML или каталог пуст — фильтровать нечего
        }

        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var query = SearchBox.Text.Trim();
        var fitsOnly = FitsOnlyCheck.IsChecked == true;
        var rows = _allRows
            .Where(row => (!fitsOnly || row.Fits) && Matches(row, query))
            .ToList();

        var previousDesignation = preferredDesignation
            ?? (CandidatesGrid.SelectedItem as PumpSetRow)?.Designation
            ?? _selectedDesignation;
        CandidatesGrid.ItemsSource = rows;
        FilterCountText.Text = string.Create(Ru, $"Показано {rows.Count} из {_allRows.Count}");

        if (rows.Count == 0)
        {
            NoResultsPanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = false;
            NoticeBanner.Visibility = Visibility.Collapsed;
            ConfirmInsufficient.Visibility = Visibility.Collapsed;
            SelectionHint.Text = "Ничего не найдено — измените запрос или снимите флажок «Только подходящие».";
            return;
        }

        NoResultsPanel.Visibility = Visibility.Collapsed;
        var selection = rows.FirstOrDefault(row => string.Equals(row.Designation, previousDesignation, StringComparison.Ordinal))
            ?? rows[0];
        CandidatesGrid.SelectedItem = selection;
        CandidatesGrid.ScrollIntoView(selection);
        UpdateSelectionState();
    }

    private static bool Matches(PumpSetRow row, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        const StringComparison CaseInsensitive = StringComparison.CurrentCultureIgnoreCase;
        return row.Designation.Contains(query, CaseInsensitive)
            || row.Pump.Contains(query, CaseInsensitive)
            || row.Maker.Contains(query, CaseInsensitive);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFitsOnlyChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void OnConfirmInsufficientChanged(object sender, RoutedEventArgs e)
    {
        if (CandidatesGrid?.SelectedItem is PumpSetRow { Fits: false })
        {
            ApplyButton.IsEnabled = ConfirmInsufficient.IsChecked == true;
        }
    }

    // Выбранная строка: понятная подсказка; неподходящая — предупреждение с обязательным подтверждением.
    private void UpdateSelectionState()
    {
        if (ApplyButton is null || CandidatesGrid is null)
        {
            return; // событие выбора приходит ещё во время разбора XAML
        }

        if (CandidatesGrid.SelectedItem is not PumpSetRow row)
        {
            ApplyButton.IsEnabled = false;
            NoticeBanner.Visibility = Visibility.Collapsed;
            ConfirmInsufficient.Visibility = Visibility.Collapsed;
            SelectionHint.Text = CandidatesGrid.Items.Count == 0 && _allRows.Count > 0
                ? "Ничего не найдено — измените запрос или снимите флажок «Только подходящие»."
                : "Выберите установку в списке.";
            return;
        }

        _selectedDesignation = row.Designation;

        if (row.Fits)
        {
            ApplyButton.IsEnabled = true;
            NoticeBanner.Visibility = Visibility.Collapsed;
            ConfirmInsufficient.Visibility = Visibility.Collapsed;
            SelectionHint.Text = string.Create(
                Ru,
                $"Выбрана {row.Designation}: напор по кривой {row.HeadAtDutyText} м при Q = {_requiredFlowM3h:0.0} м³/ч, требуется H = {_requiredHeadMeters:0.0} м, запас {row.MarginText} м.");
            return;
        }

        var headText = row.HeadAtDutyMeters is { } head
            ? string.Create(Ru, $"напор по кривой H = {head:0.0} м")
            : "подача Q вне диапазона кривой установки (напор не определён)";
        NoticeText.Text = string.Create(
            Ru,
            $"Установка {row.Designation} не обеспечивает требуемый напор: {headText} при Q = {_requiredFlowM3h:0.0} м³/ч, требуется H = {_requiredHeadMeters:0.0} м. При расчёте появится ошибка pump.setInsufficient и выпуск документов остановится. Выбор можно сохранить как решение проектировщика — подтвердите его.");
        NoticeBanner.Visibility = Visibility.Visible;
        ConfirmInsufficient.Visibility = Visibility.Visible;
        ConfirmInsufficient.IsChecked = false;
        ApplyButton.IsEnabled = false;
        SelectionHint.Text = "Неподходящая установка: подтвердите выбор или выберите вариант с запасом напора.";
    }

    // ---------- Действия ----------

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is not PumpSetRow row)
        {
            ShowError("Выберите установку в списке.");
            return;
        }

        if (!row.Fits && ConfirmInsufficient.IsChecked != true)
        {
            ShowError("Для неподходящей установки нужно подтвердить выбор: расчёт остановится с ошибкой.");
            return;
        }

        if (!SaveSelection(row.Designation))
        {
            return; // ошибка сохранения — окно остаётся открытым, файл проекта не изменён
        }

        SelectedDesignation = row.Designation;
        CloseWithResult(true);
    }

    private void OnAuto(object sender, RoutedEventArgs e)
    {
        if (!SaveSelection(null))
        {
            return;
        }

        SelectedDesignation = null;
        CloseWithResult(true);
    }

    /// <summary>
    /// Сохраняет ручной выбор в проект (или сбрасывает его) правкой одного ключа:
    /// <c>design.pumpStation.setDesignation</c>. Файл пересобирается только после проверки,
    /// что он читается моделью (<see cref="ProjectJson.Save"/>), поэтому при ошибке проект не меняется.
    /// </summary>
    private bool SaveSelection(string? designation)
    {
        try
        {
            var root = ProjectJson.LoadMerged(_projectPath);
            if (root["design"] is not JsonObject design)
            {
                ShowError("В файле проекта нет раздела design — выбор установки сохранить некуда.");
                return false;
            }

            if (design["pumpStation"] is not JsonObject station)
            {
                if (design["pumpStation"] is not null)
                {
                    ShowError("В проекте некорректный раздел design.pumpStation — выбор установки не сохранён.");
                    return false;
                }

                station = new JsonObject();
                design["pumpStation"] = station;
            }

            if (string.IsNullOrEmpty(designation))
            {
                station.Remove("setDesignation");
            }
            else
            {
                station["setDesignation"] = designation;
            }

            ProjectJson.Save(_projectPath, root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            ShowError($"Не удалось сохранить выбор установки в проект: {ex.Message}");
            return false;
        }
    }

    private string ReadCurrentDesignation()
    {
        try
        {
            var root = ProjectJson.LoadMerged(_projectPath);
            return root["design"]?["pumpStation"]?["setDesignation"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError($"Текущий выбор установки прочитать не удалось: {ex.Message}");
            return "";
        }
    }

    // ---------- Помещение насосной по чертежу ----------

    /// <summary>
    /// Габариты и обвязка помещения насосной из чертежа проекта — чтобы сравнить их с габаритами установки и
    /// прибора управления, не уходя из окна. Разбор синхронный: окно должно быть готово к снимку сразу после
    /// конструктора; любой сбой (нет файла, битый DWG/DXF, нечитаемый проект) остаётся в бейдже и не мешает
    /// подбору установки. Норм здесь нет — только геометрия плана и данные чертежа.
    /// </summary>
    private void LoadDrawingPumpRoom()
    {
        ProjectFile project;
        try
        {
            project = ProjectFile.Load(_projectPath);
        }
#pragma warning disable CA1031 // данные чертежа вспомогательные: сбой чтения проекта показываем в бейдже, окно подбора работает
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowDrawingRoomUnavailable("Файл проекта не прочитан: " + ex.Message);
            return;
        }

        DrawingPreviewResult preview;
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(_projectPath))!;
            var drawingPath = Path.GetFullPath(Path.Combine(projectDirectory, project.Drawing));
            preview = DrawingPreview.Analyze(project, drawingPath, _normsDir);
        }
#pragma warning disable CA1031 // битый DWG/DXF или каталог не должны ронять окно: причина остаётся в бейдже
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowDrawingRoomUnavailable("Чертёж не прочитан: " + ex.Message);
            return;
        }

        if (!preview.DrawingFound)
        {
            ShowDrawingRoomUnavailable(preview.Issues.Count > 0
                ? preview.Issues[0].Message
                : "Файл чертежа не найден.");
            return;
        }

        if (preview.PumpRoomId is null)
        {
            // Ошибки импорта (файл не разобран, не те единицы) — это «чертёж не прочитан», а не «насосная не размечена».
            if (FirstImportError(preview) is { } readError)
            {
                ShowDrawingRoomUnavailable(readError, preview.HasErrors ? preview.Issues.Count : 0);
                return;
            }

            SetDrawingRoomPill("Pill.Warning", "в чертеже не размечено");
            DrawingRoomBlock.ToolTip = null;
            DrawingRoomText.Text = "Контур помещения насосной на плане не найден (слой ПТ_НС).";
            DrawingRoomHintText.Text = WithIssuesHint("Листы насосной и ПТ2.СО не выпустятся (слой ПТ_НС).", preview);
            return;
        }

        SetDrawingRoomPill("Pill.Success", "по чертежу");
        DrawingRoomText.Text = FormatPumpRoom(preview);
        DrawingRoomHintText.Text = WithIssuesHint("Сравните с габаритами установки и прибора управления в таблице.", preview);
        DrawingRoomBlock.ToolTip = string.Create(
            Ru,
            $"Помещение {preview.PumpRoomId}: дверь {(preview.PumpRoomHasDoor ? "есть" : "не найдена")}, помещений на плане: {preview.Rooms.Count}.");
    }

    // «Помещение насосной: 12 × 8 м, 96 м²; вводов 2, выпусков 1, патрубков ГМ 2.» — только данные чертежа.
    private static string FormatPumpRoom(DrawingPreviewResult preview)
    {
        var size = preview.PumpRoomLengthM is { } length && preview.PumpRoomWidthM is { } width
            ? string.Create(Ru, $"{length:0.##} × {width:0.##} м")
            : "размер не определён";
        var area = preview.PumpRoomAreaM2 is { } areaM2 ? string.Create(Ru, $", {areaM2:0.##} м²") : "";
        return string.Create(
            Ru,
            $"Помещение насосной: {size}{area}; вводов {preview.PumpRoomInlets}, выпусков {preview.PumpRoomOutlets}, патрубков ГМ {preview.TruckInlets}.");
    }

    // Число замечаний предпроверки — рядом с данными: видно, насколько чертёж готов к выпуску листов насосной.
    private static string WithIssuesHint(string hint, DrawingPreviewResult preview) => preview.Issues.Count == 0
        ? hint
        : string.Create(Ru, $"{hint} Замечаний предпроверки: {preview.Issues.Count}.");

    // Ошибка именно чтения файла: план не разобран, поэтому «насосная не размечена» здесь была бы неправдой.
    private static string? FirstImportError(DrawingPreviewResult preview) => preview.Issues
        .FirstOrDefault(issue => issue.Level == IssueLevel.Error && issue.Code.StartsWith("import.", StringComparison.Ordinal))
        ?.Message;

    // Чертёж не найден/не читается: в бейдже — статус и краткая причина, без MessageBox и без падения окна.
    private void ShowDrawingRoomUnavailable(string reason, int issueCount = 0)
    {
        SetDrawingRoomPill("Pill.Warning", "чертёж не прочитан");
        DrawingRoomBlock.ToolTip = null;
        DrawingRoomText.Text = Shorten(reason);
        DrawingRoomHintText.Text = issueCount > 0
            ? string.Create(Ru, $"Подбор установки работает и без чертежа: проверьте файл проекта и поле drawing. Замечаний предпроверки: {issueCount}.")
            : "Подбор установки работает и без чертежа: проверьте файл проекта и поле drawing.";
    }

    // Длинные сообщения импорта оставляем в бейдже одной-двумя строками.
    private static string Shorten(string message) => message.Length switch
    {
        0 => "Чертёж не прочитан.",
        <= 240 => message,
        _ => message[..239] + "…",
    };

    // Бейдж состояния данных чертежа; стили и кисти — только из дизайн-системы (светлая и тёмная тема).
    private void SetDrawingRoomPill(string styleKey, string text)
    {
        DrawingRoomPill.Style = (Style)FindResource(styleKey);
        DrawingRoomPillText.Text = text;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    // В обычном режиме окно модальное; при оффскрин-проверке (Show без ShowDialog) DialogResult недоступен.
    private void CloseWithResult(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    // Строка каталога для DataGrid: подписи готовятся заранее, чтобы XAML не содержал вычислений и форматирования.
    private sealed record PumpSetRow(
        string Designation,
        string Pump,
        string Maker,
        int MainPumps,
        int StandbyPumps,
        double PowerKw,
        double? HeadAtDutyMeters,
        bool Fits,
        double MarginMeters,
        int CollectorDn,
        double CollectorAxisMm,
        Footprint Hydraulic,
        Footprint Cabinet,
        bool IsCurrent)
    {
        public string PumpsText => $"{MainPumps} + {StandbyPumps}";

        public string PowerText => PowerKw.ToString("0.##", Ru);

        public string HeadAtDutyText => HeadAtDutyMeters is { } head ? head.ToString("0.0", Ru) : "—";

        public string MarginText => MarginMeters.ToString("+0.0;−0.0;0.0", Ru);

        public string FitsText => Fits ? "Да" : "Нет";

        public string CollectorText => $"DN{CollectorDn}";

        // Габариты гидравлической части (длина×ширина×высота) — по ним проверяют помещение насосной.
        public string FootprintText => FootprintString(Hydraulic);

        // Прибор управления — отдельная позиция комплекта, поэтому отдельной строкой под габаритами установки.
        public string CabinetFootprintText => FootprintString(Cabinet);

        public string CabinetText => $"прибор {CabinetFootprintText}";

        // Высота оси коллекторов от пола, мм.
        public string CollectorAxisText => string.Create(Ru, $"ось {CollectorAxisMm:0.#} мм");

        public string DetailText => $"{Maker} · {Pump}";

        private static string FootprintString(Footprint footprint) => string.Create(
            Ru,
            $"{footprint.LengthMm:0.#}×{footprint.WidthMm:0.#}×{footprint.HeightMm:0.#}");
    }
}
