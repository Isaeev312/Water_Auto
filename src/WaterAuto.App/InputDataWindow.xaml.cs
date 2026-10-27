using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using WaterAuto.App.InputData;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Мастер исходных данных проекта: разделы слева, карточки полей справа. Пользователь заполняет только данные
/// объекта и решения проектировщика, списки значений приходят из норм и каталогов data/. Сохранение — в *.waproj
/// через <see cref="ProjectJson"/>: прочие ключи файла и значения полей, которых нет в окне, не теряются.
/// <para>
/// В разделе «Проект» окно показывает карточку предпроверки чертежа: разбор идёт в фоне
/// (<see cref="DrawingPreview"/> не блокирует интерфейс), предложения по исходным данным применяются по кнопке.
/// В режиме «только стандартные данные» экспертные поля и карточки скрыты, но их значения из JSON сохраняются как есть.
/// </para>
/// </summary>
public partial class InputDataWindow : Window
{
    private const string PumpStationSection = "Насосная станция";

    private sealed class FieldRow
    {
        public required FieldSpec Field { get; init; }

        public required TextBlock Label { get; init; }

        public required InputFieldController Controller { get; init; }
    }

    private sealed class CardView
    {
        public required CardSpec Spec { get; init; }

        public required Border Element { get; init; }

        public List<FieldRow> Rows { get; } = [];
    }

    private sealed class SectionView
    {
        public required SectionSpec Spec { get; init; }

        public required StackPanel Panel { get; init; }

        public CheckBox? Toggle { get; init; }

        public required FrameworkElement FieldsHost { get; init; }

        public List<InputFieldController> Controllers { get; } = [];

        public List<CardView> Cards { get; } = [];
    }

    private sealed record PreviewRequest(string ProjectJson, string? DrawingPath, string? DrawingProblem);

    private readonly string _path;
    private readonly string _projectDirectory;
    private readonly InputSources _sources;
    private readonly JsonObject _root;
    private readonly IInputFieldHost _host;
    private readonly Dictionary<string, InputFieldController> _controllers = new(StringComparer.Ordinal);
    private readonly List<SectionView> _sections = [];
    private readonly List<NavSectionItem> _navItems = [];
    private DrawingPreviewCard _previewCard = null!;
    private Border? _pumpRoomHint;
    private string? _readError;
    private bool _navigating;
    private bool _closed;
    private int _previewVersion;

    public InputDataWindow(string projectPath)
    {
        InitializeComponent();
        _path = projectPath ?? "";
        _projectDirectory = DirectoryOf(_path);
        _root = LoadProject();
        _sources = InputSources.Load(_path);
        _host = new HostAdapter(this);
        BuildHeader();
        BuildSections();
        if (_readError is { } readError)
        {
            ShowErrors([readError], []);
            SaveButton.IsEnabled = false;
            _previewCard.ShowUnavailable(readError);
        }
        else
        {
            QueuePreviewRefresh();
        }
    }

    private JsonObject LoadProject()
    {
        try
        {
            return ProjectJson.LoadMerged(_path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _readError = "Не удалось прочитать файл проекта: " + ex.Message;
            return new JsonObject();
        }
    }

    private static string DirectoryOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Directory.GetCurrentDirectory();
        }
    }

    private void BuildHeader()
    {
        var name = InputJson.Text(InputJson.Find(_root, "", "name")).Trim();
        ProjectNameText.Text = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(_path);
        ProjectPathText.Text = _path;
        NormsText.Text = _sources.Norms is { } norms
            ? norms.Document + Environment.NewLine + "Редакция: " + norms.Edition
            : "data/norms не найдена";
        if (!_sources.HasNorms)
        {
            ShowSourceBanner("Не найдена папка data/norms: выпадающие списки недоступны, значения вводятся вручную. " +
                "Проверьте, что папка data установлена рядом с программой или проектом.");
        }
        else if (_sources.LoadError is { } error)
        {
            ShowSourceBanner("Часть списков из data/ прочитать не удалось: " + error + " Остальные поля работают как обычно.");
        }
    }

    private void BuildSections()
    {
        _previewCard = new DrawingPreviewCard(QueuePreviewRefresh, ApplySuggestion, ApplySuggestions);
        var specs = InputFields.All;
        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            var panel = new StackPanel { Visibility = Visibility.Collapsed };

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var title = new TextBlock { Text = spec.NavTitle };
            InputTheme.ApplyStyle(title, "Text.H2");
            header.Children.Add(title);
            var description = new TextBlock { Text = spec.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            InputTheme.ApplyStyle(description, "Text.Caption");
            header.Children.Add(description);
            panel.Children.Add(header);

            if (index == 0)
            {
                panel.Children.Add(_previewCard.View);
            }

            CheckBox? toggle = null;
            if (spec.OptionalPath is not null)
            {
                panel.Children.Add(BuildOptionalCard(spec, out toggle));
            }

            if (string.Equals(spec.NavTitle, PumpStationSection, StringComparison.Ordinal))
            {
                _pumpRoomHint = BuildPumpRoomHint();
                panel.Children.Add(_pumpRoomHint);
            }

            var fieldsHost = new StackPanel();
            var section = new SectionView
            {
                Spec = spec,
                Panel = panel,
                Toggle = toggle,
                FieldsHost = fieldsHost,
            };

            foreach (var card in spec.Cards)
            {
                var view = BuildCard(card, section.Controllers);
                fieldsHost.Children.Add(view.Element);
                section.Cards.Add(view);
            }

            panel.Children.Add(fieldsHost);
            if (toggle is not null)
            {
                fieldsHost.IsEnabled = toggle.IsChecked == true;
                toggle.Checked += (_, _) =>
                {
                    fieldsHost.IsEnabled = true;
                    ApplyAdvancedVisibility();
                    RefreshCounters();
                };
                toggle.Unchecked += (_, _) =>
                {
                    fieldsHost.IsEnabled = false;
                    ApplyAdvancedVisibility();
                    RefreshCounters();
                };
            }

            SectionHost.Children.Add(panel);
            _sections.Add(section);
            foreach (var controller in section.Controllers)
            {
                _controllers[controller.Field.Id] = controller;
            }
        }

        _navItems.Clear();
        foreach (var section in _sections)
        {
            _navItems.Add(new NavSectionItem(section.Spec.NavTitle, NavIcon(section.Spec.NavTitle)));
        }

        Nav.ItemsSource = _navItems.Select(item => item.View).ToList();
        ApplyAdvancedVisibility();
        RefreshCounters();
        if (_sections.Count > 0)
        {
            SelectSection(0);
        }
    }

    // Линейные иконки 16×16: документ, капля, трубы, насос, пожарный кран, резервуар, титульный лист.
    private static string NavIcon(string title) => title switch
    {
        "Проект" => "M4,1.5 H9.5 L12.5,4.5 V14.5 H4 Z M9.5,1.5 V4.5 H12.5",
        "Установка" => "M8,1.5 C8,1.5 3.5,6.5 3.5,9.5 A4.5,4.5 0 1 0 12.5,9.5 C12.5,6.5 8,1.5 8,1.5 Z",
        "Трубы и скорости" => "M2,5 H14 M2,11 H14 M5,5 V11 M11,5 V11",
        "Насосная станция" => "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M5,8 H11 M11,8 L9,6 M11,8 L9,10",
        "ВПВ" => "M5,2.5 V1.5 H11 V2.5 M4.5,4.5 H11.5 V14.5 H4.5 Z M8,4.5 V14.5 M6.5,2.5 H9.5",
        "Резервуар" => "M2,5 H14 V13.5 H2 Z M2,8.5 H14",
        _ => "M4,1.5 H9.5 L12.5,4.5 V14.5 H4 Z M6.5,7.5 H10 M6.5,10 H10",
    };

    private Border BuildOptionalCard(SectionSpec spec, out CheckBox toggle)
    {
        var card = new Border { Margin = new Thickness(0, 0, 0, 16) };
        InputTheme.ApplyStyle(card, "Card");
        var stack = new StackPanel();
        toggle = new CheckBox
        {
            Content = spec.OptionalLabel ?? "Учитывать раздел",
            IsChecked = spec.OptionalPath is { } path && InputJson.FindObject(_root, path) is not null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(toggle);
        if (spec.OptionalHint is { Length: > 0 } hint)
        {
            var caption = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            InputTheme.ApplyStyle(caption, "Text.Caption");
            stack.Children.Add(caption);
        }

        card.Child = stack;
        return card;
    }

    private static Border BuildPumpRoomHint()
    {
        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 16),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        InputTheme.ApplyStyle(border, "Card.Inset");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Warning");

        var stack = new StackPanel();
        var title = new TextBlock { Text = "На чертеже не найдено помещение насосной" };
        InputTheme.ApplyStyle(title, "Text.Label");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        stack.Children.Add(title);
        var text = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Листы насосной и ПТ2.СО не выпускаются. Разметьте контур помещения слоем ПТ_НС и нажмите " +
                "«Проверить чертёж» в разделе «Проект».",
        };
        InputTheme.ApplyStyle(text, "Text.Caption");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        stack.Children.Add(text);
        border.Child = stack;
        return border;
    }

    private CardView BuildCard(CardSpec card, List<InputFieldController> controllers)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 16) };
        InputTheme.ApplyStyle(border, "Card");
        var view = new CardView { Spec = card, Element = border };

        var stack = new StackPanel();
        var title = new TextBlock { Text = card.Title };
        InputTheme.ApplyStyle(title, "Text.H3");
        stack.Children.Add(title);

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var field in card.Fields)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var controller = new InputFieldController(field, _root, _host);
            controllers.Add(controller);

            var label = new TextBlock
            {
                Text = field.Label,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 16),
            };
            InputTheme.ApplyStyle(label, "Text.Label");
            if (field.Hint is not null)
            {
                label.ToolTip = field.Hint;
            }

            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            Grid.SetRow(controller.View, row);
            Grid.SetColumn(controller.View, 1);
            grid.Children.Add(label);
            grid.Children.Add(controller.View);
            view.Rows.Add(new FieldRow { Field = field, Label = label, Controller = controller });
            row++;
        }

        stack.Children.Add(grid);
        if (card.Info is { Length: > 0 } info)
        {
            var inset = new Border { Margin = new Thickness(0, 4, 0, 0) };
            InputTheme.ApplyStyle(inset, "Card.Inset");
            var text = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap };
            InputTheme.ApplyStyle(text, "Text.Caption");
            inset.Child = text;
            stack.Children.Add(inset);
        }

        border.Child = stack;
        return view;
    }

    /// <summary>
    /// Режим «только стандартные данные»: скрыть экспертные поля и карточки, карточку без видимых полей не показывать.
    /// Значения скрытых контролов остаются в окне и сохраняются в файл проекта как есть.
    /// </summary>
    private void ApplyAdvancedVisibility()
    {
        var showAdvanced = AdvancedToggle.IsChecked == true;
        var shownFields = 0;
        var totalFields = 0;
        foreach (var section in _sections)
        {
            if (section.Spec.OptionalPath is not null && section.Toggle?.IsChecked != true)
            {
                continue;
            }

            foreach (var card in section.Cards)
            {
                var cardShown = !card.Spec.Advanced || showAdvanced;
                card.Element.Visibility = cardShown ? Visibility.Visible : Visibility.Collapsed;
                foreach (var field in card.Rows)
                {
                    totalFields++;
                    var fieldShown = cardShown && (showAdvanced || !field.Field.Advanced);
                    field.Label.Visibility = fieldShown ? Visibility.Visible : Visibility.Collapsed;
                    field.Controller.View.Visibility = fieldShown ? Visibility.Visible : Visibility.Collapsed;
                    if (fieldShown)
                    {
                        shownFields++;
                    }
                }
            }
        }

        VisibleCountText.Text = showAdvanced
            ? $"Показаны все {totalFields} параметров"
            : $"Показано {shownFields} из {totalFields} параметров — включите дополнительные, если нужно.";
    }

    private void OnAdvancedToggled(object sender, RoutedEventArgs e)
    {
        ApplyAdvancedVisibility();
        RefreshCounters();
    }

    /// <summary>
    /// Пересчитать прогресс мастера: счётчик заполнения, обязательные поля (мини-прогрессбар в нижней панели)
    /// и статусы разделов в навигации. Учитываются только видимые поля: в простом режиме скрытые экспертные
    /// карточки и поля не считаются, выключенные необязательные разделы (ВПВ, резервуар) — тоже.
    /// </summary>
    private void RefreshCounters()
    {
        var filled = 0;
        var total = 0;
        var requiredFilled = 0;
        var requiredTotal = 0;
        for (var index = 0; index < _sections.Count; index++)
        {
            var section = _sections[index];
            var sectionActive = section.Spec.OptionalPath is null || section.Toggle?.IsChecked == true;
            var sectionRequired = 0;
            var sectionMissing = 0;
            if (sectionActive)
            {
                foreach (var card in section.Cards)
                {
                    if (card.Element.Visibility != Visibility.Visible)
                    {
                        continue;
                    }

                    foreach (var field in card.Rows)
                    {
                        if (field.Controller.View.Visibility != Visibility.Visible)
                        {
                            continue;
                        }

                        total++;
                        var isFilled = field.Controller.IsFilled;
                        if (isFilled)
                        {
                            filled++;
                        }

                        if (!field.Field.Required)
                        {
                            continue;
                        }

                        sectionRequired++;
                        requiredTotal++;
                        if (isFilled)
                        {
                            requiredFilled++;
                        }
                        else
                        {
                            sectionMissing++;
                        }
                    }
                }
            }

            _navItems[index].SetStatus(
                !sectionActive || sectionRequired == 0
                    ? NavSectionStatus.None
                    : sectionMissing > 0 ? NavSectionStatus.Missing : NavSectionStatus.Ok,
                sectionRequired == 0
                    ? null
                    : sectionMissing > 0
                        ? $"Осталось обязательных полей: {sectionMissing}"
                        : "Обязательные поля раздела заполнены");
        }

        FillCountText.Text = $"Заполнено {filled} из {total} полей";
        RequiredProgress.Value = requiredTotal == 0 ? 1 : (double)requiredFilled / requiredTotal;
        RequiredText.Text = requiredTotal == 0
            ? "Нет обязательных полей"
            : requiredFilled == requiredTotal
                ? "Обязательные данные заполнены"
                : $"Обязательные: {requiredFilled} из {requiredTotal}";
    }

    private void SelectSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].Panel.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (Nav.SelectedIndex != index)
        {
            _navigating = true;
            Nav.SelectedIndex = index;
            _navigating = false;
        }

        SectionScroll.ScrollToTop();

        // Статусы разделов могли измениться (например, поле заполнили в другом разделе) — обновляем при переходе.
        RefreshCounters();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navigating)
        {
            SelectSection(Nav.SelectedIndex);
        }
    }

    private void ShowSourceBanner(string text)
    {
        SourceBannerText.Text = text;
        SourceBanner.Visibility = Visibility.Visible;
    }

    private void ShowErrors(List<string> messages, List<InputFieldController> failed)
    {
        ErrorBannerText.Text = string.Join(Environment.NewLine, messages);
        ErrorBanner.Visibility = Visibility.Visible;
        if (failed.Count == 0)
        {
            return;
        }

        var first = failed[0];
        for (var i = 0; i < _sections.Count; i++)
        {
            if (_sections[i].Controllers.Contains(first))
            {
                SelectSection(i);
                break;
            }
        }

        // Поле с ошибкой могло быть скрыто режимом «только стандартные данные» — показываем его пользователю.
        if (AdvancedToggle.IsChecked != true && IsHiddenInSimpleMode(first))
        {
            AdvancedToggle.IsChecked = true;
        }

        first.FocusField();
    }

    private bool IsHiddenInSimpleMode(InputFieldController controller)
    {
        foreach (var section in _sections)
        {
            foreach (var card in section.Cards)
            {
                if (card.Rows.Any(field => ReferenceEquals(field.Controller, controller)))
                {
                    return card.Spec.Advanced || controller.Field.Advanced;
                }
            }
        }

        return false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_readError is not null)
        {
            return;
        }

        ErrorBanner.Visibility = Visibility.Collapsed;
        var errors = new List<string>();
        var failed = new List<InputFieldController>();

        foreach (var section in _sections)
        {
            if (section.Spec.OptionalPath is { } optionalPath && section.Toggle?.IsChecked != true)
            {
                // Прежнее поведение: выключенный необязательный объект удаляется из файла проекта.
                InputJson.RemovePath(_root, optionalPath);
                foreach (var controller in section.Controllers)
                {
                    controller.ClearError();
                }

                continue;
            }

            // Скрытые в простом режиме поля сохраняются с теми значениями, что были в файле проекта.
            foreach (var controller in section.Controllers)
            {
                if (!controller.TryWrite(errors))
                {
                    failed.Add(controller);
                }
            }
        }

        if (errors.Count > 0)
        {
            ShowErrors(errors, failed);
            return;
        }

        try
        {
            ProjectJson.Save(_path, _root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or ArgumentException)
        {
            ShowErrors(["Не удалось сохранить проект: " + ex.Message], []);
            return;
        }

        // Сохранение завершает мастер; при следующем открытии карточка предпроверки построится уже
        // по сохранённым значениям. Пока окно открыто, чертёж перепроверяется по кнопке в карточке.
        DialogResult = true;
        Close();
    }

    private void OnFieldValueChanged(InputFieldController controller)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        foreach (var item in _controllers.Values)
        {
            item.ClearError();
        }

        RefreshCounters();
    }

    private void SetFieldValue(string path, string key, string value)
    {
        if (_controllers.TryGetValue(path + "|" + key, out var controller))
        {
            controller.SetExternalValue(value);
        }
    }

    private string GetFieldValue(string path, string key) =>
        _controllers.TryGetValue(path + "|" + key, out var controller) ? controller.CurrentTextValue : "";

    // ---- Предпроверка чертежа ---------------------------------------------------------------------------------

    private void QueuePreviewRefresh() => _ = RefreshDrawingPreviewAsync();

    /// <summary>
    /// Разобрать чертёж в фоне по текущим (ещё не сохранённым) значениям окна и обновить карточку.
    /// Исключения чтения чертежа и данных не роняют окно — они показываются в карточке.
    /// </summary>
    private async Task RefreshDrawingPreviewAsync()
    {
        if (_readError is not null)
        {
            _previewCard.ShowUnavailable(_readError);
            return;
        }

        var request = BuildPreviewRequest();
        if (request.DrawingPath is null)
        {
            _previewCard.ShowNoDrawing(request.DrawingProblem ?? "Чертёж не задан — укажите путь в поле «Чертёж плана».");
            UpdatePumpRoomHint(null);
            return;
        }

        var version = ++_previewVersion;
        _previewCard.SetBusy();
        var projectJson = request.ProjectJson;
        var drawingPath = request.DrawingPath;
        var normsDirectory = _sources.NormsDirectory;
        DrawingPreviewResult? result = null;
        string? failure = null;
        try
        {
            result = await Task.Run(() => AnalyzeProject(projectJson, drawingPath, normsDirectory)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // предпроверка не должна ронять окно: любой сбой чтения показываем в карточке
        catch (Exception ex)
        {
            failure = ex.Message;
        }
#pragma warning restore CA1031

        // Обновление интерфейса — только в потоке окна: разбор идёт в фоне, а карточка живёт в UI-потоке.
        if (Dispatcher.CheckAccess())
        {
            ApplyPreviewUpdate(version, result, failure);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => ApplyPreviewUpdate(version, result, failure));
        }
    }

    private void ApplyPreviewUpdate(int version, DrawingPreviewResult? result, string? failure)
    {
        if (_closed || version != _previewVersion)
        {
            return;
        }

        if (failure is not null)
        {
            _previewCard.ShowAnalysisError(failure);
            UpdatePumpRoomHint(null);
            return;
        }

        if (result is null)
        {
            return;
        }

        _previewCard.ShowResult(result);
        UpdatePumpRoomHint(result);
    }

    private static DrawingPreviewResult AnalyzeProject(string projectJson, string drawingPath, string? normsDirectory) =>
        DrawingPreview.Analyze(ProjectFile.FromJson(projectJson), drawingPath, normsDirectory);

    /// <summary>Снимок текущих значений окна и полный путь к чертежу (относительный путь считается от папки проекта).</summary>
    private PreviewRequest BuildPreviewRequest()
    {
        var snapshot = (JsonObject)_root.DeepClone();
        foreach (var section in _sections)
        {
            if (section.Spec.OptionalPath is { } optionalPath && section.Toggle?.IsChecked != true)
            {
                InputJson.RemovePath(snapshot, optionalPath);
                continue;
            }

            foreach (var controller in section.Controllers)
            {
                controller.WriteSnapshot(snapshot);
            }
        }

        var json = snapshot.ToJsonString();
        var drawing = InputJson.Text(InputJson.Find(snapshot, "", "drawing")).Trim();
        if (drawing.Length == 0)
        {
            return new PreviewRequest(json, null, "Чертёж не задан — укажите путь в поле «Чертёж плана».");
        }

        try
        {
            var full = Path.IsPathRooted(drawing)
                ? Path.GetFullPath(drawing)
                : Path.GetFullPath(Path.Combine(_projectDirectory, drawing.Replace('/', Path.DirectorySeparatorChar)));
            return new PreviewRequest(json, full, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PreviewRequest(json, null, "Путь к чертежу некорректен: " + drawing + ".");
        }
    }

    private void ApplySuggestion(DrawingSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (!_controllers.TryGetValue(SuggestionFieldId(suggestion), out var controller))
        {
            return;
        }

        controller.SetExternalValue(InputJson.FormatNumber(suggestion.Value));
        OnFieldValueChanged(controller);
    }

    private void ApplySuggestions(IReadOnlyList<DrawingSuggestion> suggestions)
    {
        foreach (var suggestion in suggestions)
        {
            ApplySuggestion(suggestion);
        }
    }

    /// <summary>Куда писать предложение чертежа: ключ DrawingSuggestion → «путь|ключ» поля мастера.</summary>
    private static string SuggestionFieldId(DrawingSuggestion suggestion) => suggestion.Key switch
    {
        "pumpRoom.height" => "design.pumpRoom|height",
        _ => "design|" + suggestion.Key,
    };

    private void UpdatePumpRoomHint(DrawingPreviewResult? result)
    {
        if (_pumpRoomHint is null)
        {
            return;
        }

        _pumpRoomHint.Visibility = result is { DrawingFound: true, PumpRoomId: null }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>Мост между окном и полями: справочники данных, папка проекта и автозаполнение соседних полей.</summary>
    private sealed class HostAdapter : IInputFieldHost
    {
        private readonly InputDataWindow _window;

        public HostAdapter(InputDataWindow window) => _window = window;

        public InputSources Sources => _window._sources;

        public string ProjectDirectory => _window._projectDirectory;

        public string GetFieldValue(string path, string key) => _window.GetFieldValue(path, key);

        public void SetFieldValue(string path, string key, string value) => _window.SetFieldValue(path, key, value);

        public void OnFieldValueChanged(InputFieldController controller) => _window.OnFieldValueChanged(controller);
    }
}
