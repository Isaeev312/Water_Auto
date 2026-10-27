using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WaterAuto.Application;

namespace WaterAuto.App.InputData;

/// <summary>
/// Окно мастера как хост для полей: даёт источникам данных доступ к справочникам data/, к папке проекта
/// и позволяет полям подставлять значения друг другу (выбор оросителя заполняет модель, K и положение розетки).
/// </summary>
internal interface IInputFieldHost
{
    InputSources Sources { get; }

    string ProjectDirectory { get; }

    /// <summary>Значение другого поля по пути и ключу JSON (текущее, ещё не записанное в файл).</summary>
    string GetFieldValue(string path, string key);

    /// <summary>Подставить значение в другое поле (автозаполнение).</summary>
    void SetFieldValue(string path, string key, string value);

    /// <summary>Пользователь изменил значение поля: пересчитать счётчик заполнения, снять подсветку ошибок.</summary>
    void OnFieldValueChanged(InputFieldController controller);
}

/// <summary>
/// Одно поле мастера: создаёт контрол по <see cref="FieldSpec"/>, читает текущее значение модели проекта,
/// пишет его в JSON при сохранении, проверяет ввод и подсвечивает ошибку. Является и представлением (View),
/// которое окно кладёт в сетку карточки.
/// </summary>
internal sealed class InputFieldController
{
    private const string CustomStandard = "__custom";

    /// <summary>Ширина контрола в карточке: растягивается по колонке, но не шире — длинные подписи читаются целиком.</summary>
    private const double FieldWidth = 620;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly FieldSpec _field;
    private readonly JsonObject _root;
    private readonly IInputFieldHost _host;
    private readonly InputSources _sources;

    private FrameworkElement? _view;
    private Control? _control;
    private TextBlock? _caption;
    private TextBox? _customStandardBox;
    private string? _hintOverride;
    private string? _error;
    private bool _loading = true;

    public InputFieldController(FieldSpec field, JsonObject root, IInputFieldHost host)
    {
        _field = field;
        _root = root;
        _host = host;
        _sources = host.Sources;
        Build();
        RefreshCaption();
        _loading = false;
    }

    public FieldSpec Field => _field;

    /// <summary>Представление поля: контрол и подсказка-подпись под ним.</summary>
    public FrameworkElement View => _view!;

    /// <summary>Текущее значение в том виде, в каком его увидит другой контрол (для автозаполнения).</summary>
    public string CurrentTextValue => CurrentText();

    /// <summary>Есть ли в поле значение (для счётчика «Заполнено N полей»).</summary>
    public bool IsFilled => _field.Kind switch
    {
        FieldKind.Bool => _control is CheckBox { IsChecked: true },
        FieldKind.Sprinkler => _control is ComboBox { SelectedItem: ComboItem { Tag: not null } },
        _ => CurrentText().Length > 0,
    };

    /// <summary>Запись значения поля в JSON проекта: пустое значение удаляет ключ (действует умолчание модели).</summary>
    public bool TryWrite(List<string> errors)
    {
        ClearError();
        var value = CurrentText();
        switch (_field.Kind)
        {
            case FieldKind.Bool:
                SetValue(JsonValue.Create(_control is CheckBox { IsChecked: true }));
                return true;

            case FieldKind.Sprinkler:
                // Поле-помощник: в JSON пишутся модель, K и положение розетки, которые оно заполняет.
                return true;

            case FieldKind.Number:
            case FieldKind.NullableNumber:
                if (value.Length == 0)
                {
                    RemoveValue();
                    return true;
                }

                if (!TryParseDouble(value, out var number))
                {
                    return Fail(errors, "ожидается число, введено «" + value + "».");
                }

                SetValue(JsonValue.Create(number));
                return true;

            case FieldKind.Int:
            case FieldKind.NullableInt:
            case FieldKind.Dn:
            case FieldKind.ChoiceInt:
                if (value.Length == 0)
                {
                    RemoveValue();
                    return true;
                }

                if (!TryParseInt(value, out var integer))
                {
                    return Fail(errors, "ожидается целое число, введено «" + value + "».");
                }

                SetValue(JsonValue.Create(integer));
                return true;

            default:
                if (value.Length == 0)
                {
                    if (_field.Required)
                    {
                        return Fail(errors, "поле обязательно: без него проект не откроется.");
                    }

                    RemoveValue();
                    return true;
                }

                SetValue(JsonValue.Create(value));
                return true;
        }
    }

    /// <summary>Снять подсветку ошибки и вернуть обычную подсказку под полем.</summary>
    public void ClearError()
    {
        if (_error is null)
        {
            return;
        }

        _error = null;
        ResetErrorBorder();
        RefreshCaption();
    }

    /// <summary>Перевести фокус в поле (после неудачного сохранения) и прокрутить его в видимую область.</summary>
    public void FocusField()
    {
        _control?.Focus();
        if (_control is FrameworkElement element)
        {
            element.BringIntoView();
        }
    }

    /// <summary>Подставить значение из автозаполнения, не создавая нового поля.</summary>
    public void SetExternalValue(string value)
    {
        switch (_control)
        {
            case TextBox box:
                box.Text = value;
                break;
            case ComboBox combo when combo.ItemsSource is List<ComboItem> items:
                if (items.All(i => i.Value != value))
                {
                    items.Insert(0, new ComboItem(value, value.Length == 0 ? "Не задано" : "«" + value + "» — значение из проекта"));
                    combo.ItemsSource = items;
                }

                combo.SelectedValue = value;
                break;
            default:
                break;
        }
    }

    private void Build()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var content = _field.Kind switch
        {
            FieldKind.Bool => BuildCheckBox(),
            FieldKind.Number or FieldKind.NullableNumber => BuildNumber(),
            FieldKind.Int or FieldKind.NullableInt => BuildNumber(),
            FieldKind.Choice => BuildChoice(),
            FieldKind.ChoiceInt => BuildChoiceInt(),
            FieldKind.Dn => BuildDn(),
            FieldKind.RoomGroup => BuildRoomGroup(),
            FieldKind.Sprinkler => BuildSprinkler(),
            FieldKind.PipeStandard => BuildPipeStandard(),
            FieldKind.PumpSet => BuildPumpSet(),
            FieldKind.HydrantRow => BuildHydrantRow(),
            FieldKind.DrawingPath => BuildDrawing(),
            _ => BuildText(),
        };
        panel.Children.Add(content);
        _caption = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        InputTheme.ApplyStyle(_caption, "Text.Caption");
        panel.Children.Add(_caption);
        _view = panel;
    }

    private CheckBox BuildCheckBox()
    {
        var box = new CheckBox
        {
            IsChecked = ReadNode()?.GetValueKind() == JsonValueKind.True,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AttachToolTip(box);
        box.Checked += (_, _) => OnControlChanged();
        box.Unchecked += (_, _) => OnControlChanged();
        _control = box;
        return box;
    }

    private TextBox BuildText()
    {
        var box = new TextBox
        {
            Text = ReadText(),
            MaxWidth = FieldWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AttachToolTip(box);
        box.TextChanged += (_, _) => OnControlChanged();
        _control = box;
        return box;
    }

    private TextBox BuildNumber()
    {
        var box = new TextBox
        {
            Text = ReadText(),
            MaxWidth = FieldWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AttachToolTip(box);
        box.TextChanged += (_, _) => OnControlChanged();
        _control = box;
        return box;
    }

    private ComboBox BuildChoice()
    {
        var current = ReadText();
        var items = new List<ComboItem>();
        foreach (var option in _field.Options ?? [])
        {
            items.Add(new ComboItem(option.Value, option.Label));
        }

        if (items.All(i => i.Value != current))
        {
            items.Insert(0, new ComboItem(current, current.Length == 0 ? "Не задано" : "«" + current + "» — значение из проекта"));
        }

        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private Control BuildChoiceInt()
    {
        var current = ReadText();
        var items = new List<ComboItem>();
        foreach (var option in _field.Options ?? [])
        {
            items.Add(new ComboItem(option.Value, option.Label));
        }

        if (items.Count == 0)
        {
            return BuildNumber();
        }

        if (items.All(i => i.Value != current))
        {
            items.Insert(0, new ComboItem(current, current.Length == 0 ? "Не задано" : current));
        }

        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private ComboBox BuildDn()
    {
        var current = ReadText();
        var items = new List<ComboItem>();
        foreach (var dn in _sources.DnOptions)
        {
            var text = dn.ToString(CultureInfo.InvariantCulture);
            items.Add(new ComboItem(text, text));
        }

        if (current.Length == 0)
        {
            items.Insert(0, new ComboItem("", "Не задано"));
        }
        else if (items.All(i => i.Value != current))
        {
            items.Add(new ComboItem(current, current + " — значение из проекта"));
        }

        items = [.. items.OrderBy(i => DnOrder(i.Value))];
        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private Control BuildRoomGroup()
    {
        var current = ReadText();
        if (_sources.RoomGroups.Count == 0)
        {
            _hintOverride = "Список групп помещений не загружен (нет data/norms): введите номер строки табл. 6.1, например 2.";
            return BuildText();
        }

        var items = _sources.RoomGroups.Select(g => new ComboItem(g.Id, g.Label)).ToList();
        if (current.Length > 0 && items.All(i => i.Value != current))
        {
            items.Insert(0, new ComboItem(current, "Группа " + current + " — нет в data/norms"));
        }

        if (current.Length == 0)
        {
            items.Insert(0, new ComboItem("", "Не задана"));
        }

        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private Control BuildSprinkler()
    {
        var k = TryParseDouble(ReadValueOf("design", "sprinklerK"), out var parsedK) ? parsedK : 0;
        var model = ReadValueOf("design", "sprinklerModel");
        if (_sources.Sprinklers.Count == 0)
        {
            _hintOverride = "Каталог оросителей не загружен (нет data/catalog): задайте модель и K вручную.";
            return BuildText();
        }

        var items = new List<ComboItem> { new("", "Автоподбор по каталогу (K = 0)") };
        items.AddRange(_sources.Sprinklers.Select(s => new ComboItem(s.Id, s.Label, s)));
        var selected = "";
        if (k > 0)
        {
            var match = _sources.Sprinklers.FirstOrDefault(s => string.Equals(s.Model, model, StringComparison.OrdinalIgnoreCase))
                ?? _sources.Sprinklers.FirstOrDefault(s => Math.Abs(s.K - k) < 1e-9);
            if (match is not null)
            {
                selected = match.Id;
            }
            else
            {
                var label = model.Length == 0
                    ? string.Create(Ru, $"По проекту: K = {k:0.###}")
                    : string.Create(Ru, $"По проекту: {model} (K = {k:0.###})");
                items.Insert(1, new ComboItem("__project", label));
                selected = "__project";
            }
        }

        var combo = BuildComboBox(items, selected);
        combo.SelectionChanged += OnSprinklerSelected;
        _control = combo;
        return combo;
    }

    private FrameworkElement BuildPipeStandard()
    {
        var current = ReadText();
        if (_sources.PipeStandards.Count == 0)
        {
            _hintOverride = "Список стандартов не загружен (нет data/catalog/documents.json): введите обозначение вручную.";
            return BuildText();
        }

        var items = _sources.PipeStandards.Select(s => new ComboItem(s.Designation, s.Designation)).ToList();
        items.Add(new ComboItem(CustomStandard, "Другое обозначение…"));
        var isCustom = current.Length > 0 && _sources.PipeStandards.All(s => s.Designation != current);
        var combo = BuildComboBox(items, isCustom ? CustomStandard : current);
        combo.SelectionChanged += (_, _) => OnPipeStandardSelected();
        _customStandardBox = new TextBox
        {
            Text = isCustom ? current : "",
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = FieldWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed,
        };
        _customStandardBox.TextChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboItem { Value: CustomStandard })
            {
                ClearError();
                RefreshCaption();
                _host.OnFieldValueChanged(this);
            }
        };
        _control = combo;
        var panel = new StackPanel();
        panel.Children.Add(combo);
        panel.Children.Add(_customStandardBox);
        return panel;
    }

    private Control BuildPumpSet()
    {
        var current = ReadText();
        if (_sources.PumpSets.Count == 0)
        {
            _hintOverride = "Каталог установок не загружен (нет data/catalog/pump_sets_*.json): введите обозначение вручную " +
                "или оставьте пустым для автоподбора.";
            return BuildText();
        }

        var items = new List<ComboItem> { new("", "Автоподбор по Q и H") };
        items.AddRange(_sources.PumpSets.Select(s => new ComboItem(s.Designation, s.Label, s)));
        if (current.Length > 0 && items.All(i => i.Value != current))
        {
            items.Insert(1, new ComboItem(current, "«" + current + "» — нет в каталоге"));
        }

        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private Control BuildHydrantRow()
    {
        var current = ReadText();
        if (_sources.HydrantRows.Count == 0)
        {
            _hintOverride = "Список табл. Ж.1 не загружен (нет data/norms): введите ID строки, например g4.seats300, " +
                "или оставьте пустым.";
            return BuildText();
        }

        var items = new List<ComboItem> { new("", "Не применяется (не жилое и не общественное здание)") };
        items.AddRange(_sources.HydrantRows.Select(r => new ComboItem(r.Id, r.Label)));
        if (current.Length > 0 && items.All(i => i.Value != current))
        {
            items.Insert(1, new ComboItem(current, "«" + current + "» — нет в data/norms"));
        }

        var combo = BuildComboBox(items, current);
        _control = combo;
        return combo;
    }

    private Grid BuildDrawing()
    {
        var grid = new Grid { MaxWidth = FieldWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Text = ReadText() };
        AttachToolTip(box);
        box.TextChanged += (_, _) => OnControlChanged();
        var button = new Button { Content = "Выбрать…", Margin = new Thickness(8, 0, 0, 0) };
        InputTheme.ApplyStyle(button, "Button.Secondary");
        AttachToolTip(button);
        button.Click += OnBrowseDrawing;
        Grid.SetColumn(button, 1);
        grid.Children.Add(box);
        grid.Children.Add(button);
        _control = box;
        return grid;
    }

    private ComboBox BuildComboBox(List<ComboItem> items, string selected)
    {
        var combo = new ComboBox
        {
            ItemsSource = items,
            DisplayMemberPath = nameof(ComboItem.Label),
            SelectedValuePath = nameof(ComboItem.Value),
            MaxWidth = FieldWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AttachToolTip(combo);
        if (items.Count > 0)
        {
            combo.SelectedValue = selected;
            if (combo.SelectedIndex < 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        combo.SelectionChanged += (_, _) => OnControlChanged();
        return combo;
    }

    private void OnSprinklerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _control is not ComboBox { SelectedItem: ComboItem item })
        {
            return;
        }

        if (item.Tag is InputCatalog.SprinklerOption option)
        {
            if (_host.GetFieldValue("design", "sprinklerName").Length == 0)
            {
                _host.SetFieldValue("design", "sprinklerName", "Ороситель спринклерный водяной");
            }

            _host.SetFieldValue("design", "sprinklerModel", option.Model);
            _host.SetFieldValue("design", "sprinklerK", InputJson.FormatNumber(option.K));
            if (option.Deflector is "up" or "down" or "horizontal" or "concealed")
            {
                _host.SetFieldValue("design", "sprinklerDeflector", option.Deflector);
            }
        }
        else if (item.Value.Length == 0)
        {
            _host.SetFieldValue("design", "sprinklerK", "0");
            _host.SetFieldValue("design", "sprinklerModel", "");
        }

        _host.OnFieldValueChanged(this);
    }

    private void OnPipeStandardSelected()
    {
        if (_loading || _customStandardBox is null || _control is not ComboBox combo)
        {
            return;
        }

        var isCustom = combo.SelectedItem is ComboItem { Value: CustomStandard };
        _customStandardBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (isCustom)
        {
            _customStandardBox.Focus();
        }
    }

    private void OnBrowseDrawing(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Чертёж плана",
            Filter = "Чертежи (*.dwg;*.dxf)|*.dwg;*.dxf|Все файлы (*.*)|*.*",
            InitialDirectory = Directory.Exists(_host.ProjectDirectory) ? _host.ProjectDirectory : "",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var relative = Path.GetRelativePath(_host.ProjectDirectory, dialog.FileName).Replace('\\', '/');
            if (_control is TextBox box)
            {
                box.Text = relative;
            }
        }
        catch (ArgumentException)
        {
            // Путь не удалось представить относительно проекта — оставляем как есть, без исключения.
        }
    }

    private void OnControlChanged()
    {
        if (_loading)
        {
            return;
        }

        ClearError();
        RefreshCaption();
        _host.OnFieldValueChanged(this);
    }

    private void AttachToolTip(FrameworkElement element)
    {
        if (_field.Hint is not null)
        {
            element.ToolTip = _field.Hint;
        }
    }

    private JsonNode? ReadNode()
    {
        if (InputJson.Find(_root, _field.Path, _field.Key) is { } own)
        {
            return own;
        }

        if (InputSources.DefaultsFor(_field.Path) is { } optional && optional[_field.Key] is { } defaultValue)
        {
            return defaultValue;
        }

        return InputJson.Find(_sources.ModelDefaults, _field.Path, _field.Key);
    }

    private string ReadValueOf(string path, string key) => InputJson.Text(InputJson.Find(_root, path, key));

    private string ReadText() => InputJson.Text(ReadNode());

    private string CurrentText()
    {
        if (_field.Kind == FieldKind.PipeStandard)
        {
            return PipeStandardValue();
        }

        return _control switch
        {
            CheckBox box => box.IsChecked == true ? "true" : "false",
            TextBox text => text.Text.Trim(),
            ComboBox combo => (combo.SelectedItem as ComboItem)?.Value ?? "",
            _ => "",
        };
    }

    private string PipeStandardValue()
    {
        if (_control is not ComboBox combo)
        {
            return _control is TextBox text ? text.Text.Trim() : "";
        }

        return combo.SelectedItem is ComboItem { Value: CustomStandard }
            ? _customStandardBox?.Text.Trim() ?? ""
            : (combo.SelectedItem as ComboItem)?.Value ?? "";
    }

    private void SetValue(JsonNode? node) => InputJson.EnsureObject(_root, _field.Path)[_field.Key] = node;

    private void RemoveValue()
    {
        var target = InputJson.FindObject(_root, _field.Path);
        target?.Remove(_field.Key);
        if (target is { Count: 0 } && _field.Path.Length > 0)
        {
            InputJson.RemovePath(_root, _field.Path);
        }
    }

    private bool Fail(List<string> errors, string message)
    {
        _error = message;
        errors.Add(_field.Label + ": " + message);
        if (_control is not null)
        {
            _control.SetResourceReference(Control.BorderBrushProperty, "Brush.Error");
            _control.SetValue(Control.BorderThicknessProperty, new Thickness(1));
            _control.ToolTip = message;
        }

        RefreshCaption();
        return false;
    }

    private void ResetErrorBorder()
    {
        if (_control is null)
        {
            return;
        }

        _control.ClearValue(Control.BorderBrushProperty);
        _control.ClearValue(Control.BorderThicknessProperty);
        _control.ToolTip = _field.Hint;
    }

    private void RefreshCaption()
    {
        if (_caption is null)
        {
            return;
        }

        if (_error is { } error)
        {
            _caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
            _caption.Text = "Ошибка: " + error;
            return;
        }

        _caption.ClearValue(TextBlock.ForegroundProperty);
        UpdateComboToolTip();
        if (_field.Kind == FieldKind.DrawingPath)
        {
            _caption.Text = DrawingCaption(out var brushKey);
            if (brushKey is not null)
            {
                _caption.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            }

            return;
        }

        _caption.Text = CaptionText();
    }

    private string CaptionText() => _field.Kind switch
    {
        FieldKind.RoomGroup => RoomGroupCaption(),
        FieldKind.Sprinkler => SprinklerCaption(),
        FieldKind.PipeStandard => PipeStandardCaption(),
        FieldKind.PumpSet => PumpSetCaption(),
        FieldKind.HydrantRow => HydrantRowCaption(),
        _ => BaseCaption(),
    };

    private string BaseCaption()
    {
        var hint = _hintOverride ?? _field.Hint;
        var note = DefaultNote();
        if (hint is { Length: > 0 } && note is { Length: > 0 })
        {
            return hint + " " + note;
        }

        return hint ?? note ?? "";
    }

    private string? DefaultNote()
    {
        if (_field.Kind is FieldKind.Bool or FieldKind.Sprinkler or FieldKind.RoomGroup or FieldKind.PipeStandard
            or FieldKind.PumpSet or FieldKind.HydrantRow or FieldKind.DrawingPath)
        {
            return null;
        }

        var node = DefaultNode();
        if (node is null)
        {
            return null;
        }

        var text = InputJson.Text(node);
        if (text.Length == 0)
        {
            return null;
        }

        if (_field.Kind is FieldKind.Number or FieldKind.Int && TryParseDouble(text, out var number) && number == 0)
        {
            return null;
        }

        if (_field.Kind is FieldKind.Choice && _field.Options is { Length: > 0 } options
            && string.Equals(text, options[0].Value, StringComparison.Ordinal))
        {
            return null;
        }

        return "По умолчанию — " + text + ".";
    }

    private JsonNode? DefaultNode()
    {
        if (InputSources.DefaultsFor(_field.Path) is { } optional && optional[_field.Key] is { } value)
        {
            return value;
        }

        return InputJson.Find(_sources.ModelDefaults, _field.Path, _field.Key);
    }

    // Полный текст выбранного пункта — в подсказке к полю: длинные подписи (группа, установка, строка Ж.1)
    // не теряются, когда ComboBox обрезает текст по ширине.
    private void UpdateComboToolTip()
    {
        if (_control is not ComboBox combo
            || _field.Kind is not (FieldKind.RoomGroup or FieldKind.Sprinkler or FieldKind.PumpSet or FieldKind.HydrantRow))
        {
            return;
        }

        combo.ToolTip = combo.SelectedItem is ComboItem { Label.Length: > 0 } item ? item.Label : _field.Hint;
    }

    private string RoomGroupCaption()
    {
        if (_control is TextBox)
        {
            return _hintOverride ?? "";
        }

        return _control is ComboBox { SelectedItem: ComboItem item } && item.Value.Length == 0
            ? "Группа не задана — программа возьмёт значение по умолчанию."
            : "Параметры строки — по табл. 6.1 СП 485; полный текст — в подсказке к полю.";
    }

    private string SprinklerCaption()
    {
        if (_control is TextBox)
        {
            return _hintOverride ?? "Каталог не загружен: введите модель вручную, K — в поле ниже.";
        }

        return _control is ComboBox { SelectedItem: ComboItem { Tag: InputCatalog.SprinklerOption option } }
            ? "Изготовитель: " + option.Maker + ". Данные каталога data/catalog/sprinklers.json."
            : "Автоподбор: программа переберёт модели каталога data/catalog/sprinklers.json.";
    }

    private string PipeStandardCaption()
    {
        if (_control is not ComboBox combo)
        {
            return _hintOverride ?? "";
        }

        if (combo.SelectedItem is ComboItem { Value: CustomStandard })
        {
            return "Обозначение из проекта; сверьте его со стандартом в data/catalog/documents.json.";
        }

        var designation = (combo.SelectedItem as ComboItem)?.Value ?? "";
        return _sources.PipeStandards.FirstOrDefault(s => s.Designation == designation)?.Name
            ?? "Стандарт из data/catalog/documents.json.";
    }

    private string PumpSetCaption()
    {
        if (_control is not ComboBox combo)
        {
            return _hintOverride ?? "";
        }

        if (combo.SelectedItem is ComboItem { Tag: InputCatalog.PumpSetOption set })
        {
            return string.Create(
                Ru,
                $"Насос {set.Pump}; DN коллектора {set.CollectorDn}; изготовитель {set.Maker}. Полное описание установки — в подсказке к полю.");
        }

        return "Автоподбор: программа выберет комплектную установку по расчётным Q и H насоса.";
    }

    private string HydrantRowCaption()
    {
        if (_control is TextBox)
        {
            return _hintOverride ?? "";
        }

        return _control is ComboBox { SelectedItem: ComboItem item } && item.Value.Length == 0
            ? "Табл. Ж.1 не применяется: здание не жилое и не общественное."
            : "Строка табл. Ж.1 СН КР 40-04:2025; полный текст — в подсказке к полю.";
    }

    private string DrawingCaption(out string? brushKey)
    {
        brushKey = null;
        var text = CurrentText();
        if (text.Length == 0)
        {
            brushKey = "Brush.Warning";
            return "Чертёж не задан: без него проект не откроется.";
        }

        var full = Path.IsPathRooted(text) ? text : Path.Combine(_host.ProjectDirectory, text.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            brushKey = "Brush.Success";
            return "Файл найден: " + text;
        }

        brushKey = "Brush.Error";
        return "Файл не найден: " + text;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        var normalized = text.Replace(" ", "", StringComparison.Ordinal).Replace('\u00A0', ' ').Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    // «Не задано» — первым, числовые DN по возрастанию, нечисловое значение из файла — в конце (не падаем на нём).
    private static int DnOrder(string value) =>
        value.Length == 0
            ? -1
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dn) ? dn : int.MaxValue;
}

/// <summary>Элемент выпадающего списка: что записать в JSON (Value), что показать (Label) и объект каталога (Tag).</summary>
internal sealed record ComboItem(string Value, string Label, object? Tag = null)
{
    public override string ToString() => Label;
}
