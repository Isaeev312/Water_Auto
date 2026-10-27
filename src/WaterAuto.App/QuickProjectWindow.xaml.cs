using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WaterAuto.App.InputData;
using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.App;

/// <summary>
/// Диалог «Быстрый проект по чертежу»: чертёж плана и минимум стандартных данных (группа помещений,
/// при желании — ороситель и комплектная установка). Списки значений и подписи приходят из data/ через
/// <see cref="InputCatalog"/>; оценки по геометрии показывает фоновая предпроверка <see cref="DrawingPreview"/>
/// (не блокирует окно, сбой разбора не роняет диалог). Сам проект создаёт <see cref="QuickProject.Create"/>
/// по запросу <see cref="Request"/> после закрытия окна с <see cref="Window.DialogResult"/> = <c>true</c>:
/// диалог не пишет файлы и не запускает расчёт.
/// </summary>
public partial class QuickProjectWindow : Window
{
    private const string DrawingFilter = "Чертёж плана (*.dwg;*.dxf)|*.dwg;*.dxf|Все файлы (*.*)|*.*";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string DefaultRoomGroup = new ProjectDesign().RoomGroup;
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Состояние поля «Файл чертежа»: пусто, корректный существующий DWG/DXF или ошибка с сообщением.</summary>
    private enum DrawingPathKind
    {
        Empty,
        Valid,
        Invalid,
    }

    /// <summary>Строка выпадающего списка: Id — значение для проекта, Label — подпись; Id = null — автоподбор.</summary>
    private sealed record Choice(string? Id, string Label);

    private readonly string? _initialDirectory;
    private readonly DispatcherTimer _previewTimer;
    private IReadOnlyList<InputCatalog.RoomGroupOption> _roomGroups = [];
    private IReadOnlyList<InputCatalog.SprinklerOption> _sprinklers = [];
    private IReadOnlyList<InputCatalog.PumpSetOption> _pumpSets = [];
    private string? _root;
    private string? _normsDir;
    private int _previewVersion;
    private bool _loadingCatalogs;
    private bool _closed;

    /// <param name="initialDirectory">Папка, с которой начинается выбор чертежа и поиск data/norms; null — текущая.</param>
    public QuickProjectWindow(string? initialDirectory = null)
    {
        InitializeComponent();
        _initialDirectory = ExistingDirectory(initialDirectory);
        _previewTimer = new DispatcherTimer { Interval = PreviewDelay };
        _previewTimer.Tick += OnPreviewTimerTick;
        LoadCatalogs();
        ApplyCatalogs(DefaultRoomGroup, null, null);
        SetPreview("Pill.Neutral", "Ожидает чертёж", "Выберите файл чертежа — программа покажет, что распознано на плане, и предложит оценки исходных данных.");
        UpdateCreateState();
    }

    /// <summary>Вариант для оффскрин-проверки вида: сразу заполненный чертёж (папка выбора — <paramref name="initialDirectory"/>).</summary>
    public QuickProjectWindow(string? initialDirectory, string? initialDrawingPath)
        : this(initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDrawingPath))
        {
            DrawingBox.Text = initialDrawingPath;
        }
    }

    /// <summary>Заполняется кнопкой «Создать проект» (окно закрыто с DialogResult = true); иначе null.</summary>
    public QuickProjectRequest? Request { get; private set; }

    // ---------- Данные из data/ ----------

    /// <summary>Корень установки ищется от папки чертежа (если он уже задан), затем от начальной папки и программы.</summary>
    private void LoadCatalogs(string? drawingPath = null)
    {
        var drawingDirectory = drawingPath is null ? null : DrawingDirectory(drawingPath);
        _root = DataLocator.FindRoot(StartFolders(drawingDirectory));
        _normsDir = _root is null ? null : DataLocator.Norms(_root);
        var error = "";
        _roomGroups = [];
        _sprinklers = [];
        _pumpSets = [];
        var normsDir = _normsDir;
        if (normsDir is not null)
        {
            _roomGroups = TryLoad(() => InputCatalog.RoomGroups(normsDir), ref error);
            _sprinklers = TryLoad(() => InputCatalog.Sprinklers(normsDir), ref error);
            _pumpSets = TryLoad(() => InputCatalog.PumpSets(normsDir), ref error);
        }

        DataStatusText.Text = DataStatus(ref error);
        DataStatusText.ToolTip = _root;
        if (_normsDir is null)
        {
            ShowSourceBanner("Не найдена папка data/norms: списки групп помещений, оросителей и установок пусты, создание проекта недоступно. Проверьте, что папка data установлена рядом с программой или чертежом.");
        }
        else if (error.Length > 0)
        {
            ShowSourceBanner("Часть списков из data/ прочитать не удалось: " + error + " Остальные поля работают как обычно.");
        }
        else
        {
            SourceBanner.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Подпись справа в шапке: документ и редакция норм (или причина, почему списков нет).</summary>
    private string DataStatus(ref string error)
    {
        if (_normsDir is null)
        {
            return "data/norms не найдена";
        }

        var normsDir = _normsDir;
        var norms = TryLoadOne(() => InputCatalog.Norms(normsDir), ref error);
        return norms is null
            ? "data/norms: нормы не прочитаны"
            : norms.Document + Environment.NewLine + "Редакция: " + norms.Edition;
    }

    private string[] StartFolders(string? drawingDirectory)
    {
        var starts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(drawingDirectory))
        {
            starts.Add(drawingDirectory);
        }

        if (_initialDirectory is not null)
        {
            starts.Add(_initialDirectory);
        }

        starts.Add(AppContext.BaseDirectory);
        starts.Add(Environment.CurrentDirectory);
        return [.. starts];
    }

    /// <summary>Если чертёж лежит рядом с другим корнем data/, списки и подписи перечитываются под него.</summary>
    private void ReloadCatalogsIfRootChanged(string drawingPath)
    {
        var root = DataLocator.FindRoot(StartFolders(DrawingDirectory(drawingPath)));
        if (string.Equals(root, _root, StringComparison.Ordinal))
        {
            return;
        }

        var roomGroup = SelectedRoomGroup();
        var sprinkler = SelectedSprinklerId();
        var pump = SelectedPumpSetDesignation();
        LoadCatalogs(drawingPath);
        ApplyCatalogs(roomGroup, sprinkler, pump);
    }

    /// <summary>Заполнить выпадающие списки; выбор по возможности сохраняется (повторное чтение data/).</summary>
    private void ApplyCatalogs(string? roomGroupId, string? sprinklerId, string? pumpSetDesignation)
    {
        _loadingCatalogs = true;
        try
        {
            RoomGroupBox.ItemsSource = _roomGroups;
            RoomGroupBox.DisplayMemberPath = nameof(InputCatalog.RoomGroupOption.Label);
            RoomGroupBox.IsEditable = _roomGroups.Count == 0; // без data/ группа вводится вручную
            var group = FindRoomGroup(roomGroupId) ?? FindRoomGroup(DefaultRoomGroup) ?? FirstRoomGroup();
            RoomGroupBox.SelectedItem = group;
            if (group is null)
            {
                RoomGroupBox.Text = roomGroupId ?? "";
            }

            var sprinklerChoices = new List<Choice> { new(null, "Автоподбор по каталогу") };
            sprinklerChoices.AddRange(_sprinklers.Select(s => new Choice(s.Id, string.Create(Ru, $"{s.Maker} {s.Label}"))));
            SprinklerBox.ItemsSource = sprinklerChoices;
            SprinklerBox.DisplayMemberPath = nameof(Choice.Label);
            SprinklerBox.SelectedItem = sprinklerChoices.FirstOrDefault(c => string.Equals(c.Id, sprinklerId, StringComparison.Ordinal))
                ?? sprinklerChoices[0];

            var pumpChoices = new List<Choice> { new(null, "Автоподбор по Q и H") };
            pumpChoices.AddRange(_pumpSets.Select(s => new Choice(s.Designation, s.Label)));
            PumpSetBox.ItemsSource = pumpChoices;
            PumpSetBox.DisplayMemberPath = nameof(Choice.Label);
            PumpSetBox.SelectedItem = pumpChoices.FirstOrDefault(c => string.Equals(c.Id, pumpSetDesignation, StringComparison.Ordinal))
                ?? pumpChoices[0];
        }
        finally
        {
            _loadingCatalogs = false;
        }
    }

    private static IReadOnlyList<T> TryLoad<T>(Func<IReadOnlyList<T>> load, ref string error)
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (QuickProject.IsDataError(ex))
        {
            if (error.Length == 0)
            {
                error = ex.Message;
            }

            return [];
        }
    }

    private static T? TryLoadOne<T>(Func<T?> load, ref string error)
        where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (QuickProject.IsDataError(ex))
        {
            if (error.Length == 0)
            {
                error = ex.Message;
            }

            return null;
        }
    }

    private InputCatalog.RoomGroupOption? FindRoomGroup(string? id) =>
        id is null ? null : _roomGroups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));

    private InputCatalog.RoomGroupOption? FirstRoomGroup() => _roomGroups.Count > 0 ? _roomGroups[0] : null;

    // ---------- Поля диалога ----------

    private void OnBrowseDrawing(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = DrawingFilter,
            Title = "Чертёж плана по соглашению о слоях",
            CheckFileExists = true,
        };
        var start = BrowseStartDirectory();
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        var accepted = IsLoaded ? dialog.ShowDialog(this) : dialog.ShowDialog();
        if (accepted != true)
        {
            return;
        }

        DrawingBox.Text = dialog.FileName;
        DrawingBox.CaretIndex = DrawingBox.Text.Length;
        DrawingBox.Focus();
    }

    private string? BrowseStartDirectory()
    {
        var kind = ValidateDrawingPath(out var drawing, out _);
        if (kind == DrawingPathKind.Valid && drawing is not null)
        {
            var directory = DrawingDirectory(drawing);
            if (directory is not null)
            {
                return directory;
            }
        }

        return _initialDirectory;
    }

    private void OnDrawingTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingCatalogs || CreateButton is null)
        {
            return;
        }

        _previewTimer.Stop();
        HideError();
        var kind = ValidateDrawingPath(out var drawing, out var message);
        if (kind == DrawingPathKind.Valid && drawing is not null)
        {
            ReloadCatalogsIfRootChanged(drawing);
            UpdateCreateState();
            _previewVersion++; // ответ прежнего разбора (если ещё идёт) уже неактуален
            SetPreview("Pill.Neutral", "Проверка чертежа…", "Разбираем чертёж: что распознано на плане и какие данные можно подставить.");
            _previewTimer.Start();
            return;
        }

        _previewVersion++;
        UpdateCreateState();
        SetPreview(
            kind == DrawingPathKind.Empty ? "Pill.Neutral" : "Pill.Warning",
            kind == DrawingPathKind.Empty ? "Ожидает чертёж" : "Чертёж не найден",
            message ?? "Выберите файл чертежа — программа покажет, что распознано на плане.");
    }

    private void OnDataSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCatalogs || CreateButton is null)
        {
            return;
        }

        HideError();
        UpdateCreateState();
    }

    /// <summary>Полный путь к чертежу из поля ввода; для ошибки — понятное сообщение на русском.</summary>
    private DrawingPathKind ValidateDrawingPath(out string? fullPath, out string? message)
    {
        fullPath = null;
        message = null;
        var text = (DrawingBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return DrawingPathKind.Empty;
        }

        try
        {
            fullPath = Path.IsPathRooted(text)
                ? Path.GetFullPath(text)
                : Path.GetFullPath(Path.Combine(RelativeBase, text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = null;
            message = "Путь к чертежу некорректен: " + text;
            return DrawingPathKind.Invalid;
        }

        if (!File.Exists(fullPath))
        {
            message = "Файл чертежа не найден: " + text;
            return DrawingPathKind.Invalid;
        }

        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".dwg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".dxf", StringComparison.OrdinalIgnoreCase))
        {
            message = "Нужен чертёж DWG или DXF: проект создаётся по файлу этого формата.";
            return DrawingPathKind.Invalid;
        }

        return DrawingPathKind.Valid;
    }

    private string RelativeBase => _initialDirectory ?? Environment.CurrentDirectory;

    private string SelectedRoomGroup()
    {
        if (RoomGroupBox.SelectedItem is InputCatalog.RoomGroupOption option)
        {
            return option.Id;
        }

        return (RoomGroupBox.Text ?? "").Trim(); // ручной ввод, когда data/norms недоступна
    }

    private string? SelectedSprinklerId() => (SprinklerBox.SelectedItem as Choice)?.Id;

    private string? SelectedPumpSetDesignation() => (PumpSetBox.SelectedItem as Choice)?.Id;

    private InputCatalog.SprinklerOption? SelectedSprinkler()
    {
        var id = SelectedSprinklerId();
        return id is null ? null : _sprinklers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Кнопка создания доступна: есть существующий DWG/DXF, группа помещений и данные норм.</summary>
    private void UpdateCreateState()
    {
        if (CreateButton is null || DrawingBox is null)
        {
            return;
        }

        var kind = ValidateDrawingPath(out var drawing, out var message);
        ShowDrawingError(kind == DrawingPathKind.Invalid ? message : null);
        UpdateExistingBanner(drawing is not null && File.Exists(Path.ChangeExtension(drawing, ".waproj")));
        CreateButton.IsEnabled = kind == DrawingPathKind.Valid && _root is not null && SelectedRoomGroup().Length > 0;
        FooterHint.Text = BuildFooterHint(kind, drawing, _root is not null);
    }

    private static string BuildFooterHint(DrawingPathKind kind, string? drawing, bool dataAvailable)
    {
        if (!dataAvailable)
        {
            return "Создание проекта недоступно: не найдена папка data/norms — без норм и каталогов проект не рассчитать.";
        }

        return kind switch
        {
            DrawingPathKind.Empty => "Укажите файл чертежа DWG/DXF — проект *.waproj создаётся рядом с ним и открывается для расчёта.",
            DrawingPathKind.Invalid => "Проверьте путь к чертежу: проект создаётся по существующему файлу DWG/DXF.",
            _ when drawing is not null => ProjectHint(drawing),
            _ => "",
        };
    }

    private static string ProjectHint(string drawing)
    {
        var projectPath = Path.ChangeExtension(drawing, ".waproj");
        return File.Exists(projectPath)
            ? "Проект уже существует — будет открыт существующий: " + projectPath
            : "Проект будет создан: " + projectPath;
    }

    // ---------- Предпроверка чертежа в фоне ----------

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _ = RefreshPreviewAsync();
    }

    /// <summary>
    /// Разобрать чертёж в фоне по текущим значениям окна и показать компактную сводку. Ответ прежнего запроса
    /// отбрасывается по номеру версии; исключения разбора не роняют окно — они показываются в карточке.
    /// </summary>
    private async Task RefreshPreviewAsync()
    {
        if (_closed || ValidateDrawingPath(out var drawingPath, out _) != DrawingPathKind.Valid || drawingPath is null)
        {
            return;
        }

        var version = ++_previewVersion;
        SetPreview("Pill.Neutral", "Проверка чертежа…", "Разбираем чертёж и сверяем геометрию — это может занять несколько секунд.");
        var project = BuildPreviewProject(drawingPath);
        var normsDir = _normsDir;
        DrawingPreviewResult? result = null;
        string? failure = null;
        try
        {
            result = await Task.Run(() => DrawingPreview.Analyze(project, drawingPath, normsDir)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // предпроверка не должна ронять окно: любой сбой чтения показываем в карточке
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex.Message;
        }

        // Обновление интерфейса — только в потоке окна: разбор идёт в фоне, контролы живут в UI-потоке.
        if (Dispatcher.CheckAccess())
        {
            ApplyPreview(version, result, failure);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => ApplyPreview(version, result, failure));
        }
    }

    private ProjectFile BuildPreviewProject(string drawingPath)
    {
        var group = SelectedRoomGroup();
        var design = new ProjectDesign
        {
            RoomGroup = group.Length > 0 ? group : DefaultRoomGroup,
            PumpStation = new ProjectPumpStation { SetDesignation = SelectedPumpSetDesignation() ?? "" },
        };
        if (SelectedSprinkler() is { } sprinkler)
        {
            design = design with
            {
                SprinklerK = sprinkler.K,
                SprinklerModel = sprinkler.Model,
                SprinklerDeflector = QuickProject.KnownDeflector(sprinkler.Deflector) ?? design.SprinklerDeflector,
            };
        }

        return new ProjectFile
        {
            Name = Path.GetFileNameWithoutExtension(drawingPath),
            Drawing = Path.GetFileName(drawingPath),
            Design = design,
        };
    }

    private void ApplyPreview(int version, DrawingPreviewResult? result, string? failure)
    {
        if (_closed || version != _previewVersion)
        {
            return;
        }

        if (failure is not null)
        {
            SetPreview("Pill.Error", "Проверка не выполнена", "Не удалось разобрать чертёж: " + failure + " Проект можно создать, а данные проверить в мастере.");
            return;
        }

        if (result is null)
        {
            return;
        }

        if (!result.DrawingFound)
        {
            SetPreview(
                "Pill.Warning",
                "Чертёж не найден",
                string.IsNullOrEmpty(result.DrawingPath)
                    ? "Файл не найден — проверьте путь в поле «Файл чертежа»."
                    : "Файл не найден: " + result.DrawingPath + ". Проверьте путь в поле «Файл чертежа».");
            return;
        }

        var errors = result.Issues.Count(issue => issue.Level == IssueLevel.Error);
        var warnings = result.Issues.Count(issue => issue.Level == IssueLevel.Warning);
        var summary = BuildPreviewSummary(result);
        if (errors > 0)
        {
            var first = result.Issues.First(issue => issue.Level == IssueLevel.Error).Message;
            SetPreview("Pill.Error", string.Create(Ru, $"Ошибки: {errors}"), summary + " " + first + " Исправьте ошибки — иначе расчёт остановится.");
        }
        else if (warnings > 0)
        {
            SetPreview("Pill.Warning", string.Create(Ru, $"Предупреждений: {warnings}"), summary + " Замечания появятся в главном окне после расчёта.");
        }
        else
        {
            SetPreview("Pill.Success", "Чертёж распознан", summary);
        }
    }

    /// <summary>Компактная сводка: помещения и площадь, насосная, вводы/выпуски, число предложений по данным.</summary>
    private static string BuildPreviewSummary(DrawingPreviewResult result)
    {
        var parts = new List<string>
        {
            string.Create(Ru, $"помещений {result.Rooms.Count}"),
            string.Create(Ru, $"площадь {result.TotalProtectedAreaM2:0.#} м²"),
        };
        if (result.PumpRoomId is { } pumpRoomId)
        {
            var size = result.PumpRoomLengthM is { } length && result.PumpRoomWidthM is { } width
                ? string.Create(Ru, $"{length:0.#} × {width:0.#} м")
                : "размер не определён";
            parts.Add($"насосная {pumpRoomId} — {size}");
        }
        else
        {
            parts.Add("насосная не найдена");
        }

        parts.Add(string.Create(Ru, $"вводов {result.Inlets} / выпусков {result.PumpRoomOutlets}"));
        parts.Add(string.Create(Ru, $"предложений {result.Suggestions.Count}"));
        return "Распознано: " + string.Join(" · ", parts) + ".";
    }

    private void SetPreview(string styleKey, string status, string text)
    {
        if (PreviewPill is null || PreviewStatusText is null || PreviewText is null)
        {
            return;
        }

        InputTheme.ApplyStyle(PreviewPill, styleKey);
        PreviewStatusText.Text = status;
        PreviewText.Text = text;
    }

    // ---------- Действия ----------

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        HideError();
        var kind = ValidateDrawingPath(out var drawing, out var message);
        if (kind != DrawingPathKind.Valid || drawing is null)
        {
            ShowDrawingError(message ?? "Укажите файл чертежа DWG или DXF.");
            ShowError(message ?? "Укажите файл чертежа DWG или DXF.");
            return;
        }

        var group = SelectedRoomGroup();
        if (group.Length == 0)
        {
            ShowError("Выберите группу помещений: без неё расчёт не начнётся.");
            RoomGroupBox.Focus();
            return;
        }

        if (_root is null)
        {
            ShowError("Создание проекта недоступно: не найдена папка data/norms — без норм и каталогов проект не рассчитать.");
            return;
        }

        // Существующий проект не ошибка: он не перезаписывается, а открывается главным окном.
        UpdateExistingBanner(File.Exists(Path.ChangeExtension(drawing, ".waproj")));
        Request = new QuickProjectRequest(
            drawing,
            group,
            SelectedSprinklerId(),
            SelectedPumpSetDesignation(),
            ApplySuggestionsBox.IsChecked == true);
        CloseWithResult(true);
    }

    /// <summary>В обычном режиме окно модальное; при оффскрин-проверке (Show без ShowDialog) DialogResult недоступен.</summary>
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

    // ---------- Баннеры и строки ошибок ----------

    private void ShowSourceBanner(string text)
    {
        SourceBannerText.Text = text;
        SourceBanner.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorBannerText.Text = "";
    }

    private void ShowDrawingError(string? message)
    {
        DrawingErrorText.Text = message ?? "";
        DrawingErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateExistingBanner(bool exists)
    {
        if (ExistingBanner is null)
        {
            return;
        }

        ExistingBanner.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Пути ----------

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? DrawingDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();
        base.OnClosed(e);
    }
}
