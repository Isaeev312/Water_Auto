using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Ввод исходных данных проекта без правки JSON: окно строится из списка полей (см. <see cref="Groups"/>),
/// читает и пишет только поля модели проекта, не трогая остальные ключи файла. Нормы здесь не редактируются.
/// </summary>
public partial class InputDataWindow : Window
{
    private enum FieldKind
    {
        Text,
        Number,
        Int,
        NullableNumber,
        NullableInt,
        Bool,
        Choice,
        ChoiceInt,
    }

    private sealed record Field(string Key, string Label, FieldKind Kind, string[] Options, string? Hint = null);

    private sealed record Group(string Title, string Path, string? ToggleLabel, Field[] Fields);

    private sealed record Entry(Group Group, Field Field, Control Control);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    // Пути — от корня проекта: "design", "design.pumpRoom", "design.hydrants", "design.tank", "title" или "" (корень).
    private static readonly Group[] Groups =
    [
        new("Проект", "", null, [
            new("name", "Название объекта", FieldKind.Text, []),
            new("drawing", "Чертёж плана (DWG/DXF, относительно проекта)", FieldKind.Text, []),
            new("font", "Шрифт выпуска", FieldKind.Text, [], "Единый шрифт записки, спецификаций и чертежей."),
        ]),
        new("Установка", "design", null, [
            new("roomGroup", "Группа помещений по прил. А СП 485", FieldKind.Text, [], "Строка табл. 6.1, например «2»."),
            new("constructionClass", "Класс пожарной опасности конструкций", FieldKind.Choice, ["K0", "K1", "K2", "K3", "Unrated"]),
            new("maxSpacing", "Шаг оросителей строже табл. 6.1, м", FieldKind.NullableNumber, [], "Пусто — по табл. 6.1."),
            new("sprinklerName", "Наименование оросителя", FieldKind.Text, []),
            new("sprinklerModel", "Модель оросителя", FieldKind.Text, []),
            new("sprinklerK", "K оросителя, л/(с·МПа^0,5)", FieldKind.Number, [], "По паспорту; 0 — автоподбор из каталога data/catalog."),
            new("maxAmbientTemperature", "Максимальная температура среды в зоне оросителей, °C", FieldKind.NullableNumber, [], "Нужна для выбора номинальной температуры срабатывания (пп. 6.2.16–6.2.17)."),
            new("sprinklerDeflector", "Положение розетки оросителя", FieldKind.Choice, ["up", "down", "horizontal", "concealed"]),
            new("pipeStandard", "Стандарт труб (до DN из поля ниже)", FieldKind.Text, []),
            new("pipeStandardLarge", "Стандарт труб начиная с DN", FieldKind.Text, [], "Пусто — все трубы по первому стандарту."),
            new("pipeStandardLargeFromDn", "DN, с которого действует второй стандарт", FieldKind.Int, []),
            new("roomHeight", "Высота помещения, м", FieldKind.NullableNumber, [], "Для табл. 6.3 и примечания 3 к табл. 6.2; пусто — до 10 м."),
            new("storageHeight", "Высота складирования, м", FieldKind.NullableNumber, [], "Только для складов (группы 5–7, табл. 6.2)."),
            new("floorElevation", "Отметка пола этажа, м", FieldKind.NullableNumber, [], "Для условия п. 6.2.14; пусто — условие считается выполненным."),
            new("pipeElevation", "Отметка трубопроводов, м", FieldKind.Number, [], "Аксонометрия и отметки на плане."),
            new("dictatingAboveAxis", "Высота диктующего оросителя над осью насоса H, м", FieldKind.Number, []),
            new("supplyLength", "Длина подводящего трубопровода, м", FieldKind.Number, []),
            new("supplyDn", "DN подводящего трубопровода", FieldKind.Int, []),
            new("inletPressure", "Давление на входе насоса по ТУ, МПа", FieldKind.Number, [], "0 — без подпора."),
            new("designVelocity", "Скорость подбора диаметров по Б.5, м/с", FieldKind.NullableNumber, [], "Пусто — из data/catalog/pipe_sizes.json (defaultDesignVelocity)."),
            new("hangerRodLength", "Длина шпильки подвеса, мм", FieldKind.Number, []),
            new("inletReference", "Ссылка в выноске у ввода", FieldKind.Text, []),
            new("ring", "Кольцевой питающий трубопровод", FieldKind.Bool, []),
            new("ringValvesClosed", "На кольце нормально закрытые разделительные задвижки", FieldKind.Bool, []),
            new("maxSprinklersPerBranch", "Предел оросителей на ветви (решение проекта)", FieldKind.NullableInt, [], "Пусто — без проверки."),
        ]),
        new("Насосная", "design.pumpRoom", null, [
            new("floorElevation", "Отметка пола насосной, м", FieldKind.Number, []),
            new("groundElevation", "Отметка уровня земли (отмостки), м", FieldKind.Number, [], "Для патрубков ГМ на фасаде (п. 6.10.18)."),
            new("inletDepth", "Заглубление ввода от уровня земли, м", FieldKind.Number, []),
            new("height", "Высота помещения до низа перекрытия, м", FieldKind.Number, []),
            new("wallThickness", "Толщина стен, м", FieldKind.Number, []),
            new("underground", "Помещение подземное/заглублённое", FieldKind.Bool, [], "П. 6.10.23: не менее двух дренажных насосов."),
            new("pitSize", "Приямок в плане, м", FieldKind.Number, []),
            new("pitDepth", "Глубина приямка, м", FieldKind.Number, []),
            new("inletRouteElevation", "Отметка трассы ввода, м", FieldKind.NullableNumber, [], "Пусто — под перекрытием."),
            new("inletRouteClearance", "Отступ трассы ввода от колонн, м", FieldKind.Number, []),
        ]),
        new("ВПВ", "design.hydrants", "Учитывать внутренний противопожарный водопровод (совмещённый с АУП)", [
            new("fireResistance", "Степень огнестойкости здания", FieldKind.Choice, ["", "I", "II", "III", "IV", "V"]),
            new("category", "Категория по пожарной опасности", FieldKind.Choice, ["", "А", "Б", "В", "Г", "Д"]),
            new("hazardClass", "Класс конструктивной пожарной опасности", FieldKind.Choice, ["", "С0", "С1", "С2", "С3"]),
            new("volume", "Объём здания (пожарного отсека), тыс. м³", FieldKind.Number, []),
            new("buildingHeight", "Высота здания, м", FieldKind.Number, []),
            new("roomHeight", "Высота помещения у диктующего ПК, м", FieldKind.NullableNumber, [], "Пусто — высота помещения из design."),
            new("valveDn", "DN клапана пожарного крана", FieldKind.Int, []),
            new("nozzle", "Диаметр спрыска ствола, мм", FieldKind.ChoiceInt, ["13", "16", "19"]),
            new("hoseLength", "Длина рукава, м", FieldKind.ChoiceInt, ["10", "15", "20"]),
            new("aboveAxis", "Отметка клапана диктующего ПК над осью насоса, м", FieldKind.Number, []),
            new("pipeLength", "Длина трубопровода до диктующего ПК, м", FieldKind.Number, []),
            new("pipeDn", "DN трубопровода до диктующего ПК", FieldKind.Int, []),
            new("isParking", "Отапливаемая автостоянка закрытого типа", FieldKind.Bool, [], "П. 6.4 СНиП 21-02-99* (ADR 0010)."),
            new("zh1Row", "Строка табл. Ж.1 СН КР 40-04:2025", FieldKind.Text, [], "Например «g4.seats_over300»; пусто — не жилое/общественное."),
            new("extraJet", "Зал с массовым пребыванием людей (на одну струю больше)", FieldKind.Bool, []),
            new("reach", "Радиус действия ПК для расстановки, м", FieldKind.NullableNumber, [], "Пусто — рукав + радиус компактной струи (В-22)."),
            new("place", "Расставлять краны на плане", FieldKind.Bool, []),
        ]),
        new("Резервуар", "design.tank", "Сравнивать с резервуаром пользователя", [
            new("length", "Длина в плане, м", FieldKind.Number, []),
            new("width", "Ширина в плане, м", FieldKind.Number, []),
            new("height", "Высота воды, м", FieldKind.Number, []),
            new("deadLayer", "Мёртвый слой, м", FieldKind.Number, []),
        ]),
        new("Титульный лист", "title", null, [
            new("organization", "Организация", FieldKind.Text, []),
            new("subject", "Тема", FieldKind.Text, []),
            new("document", "Наименование документа", FieldKind.Text, []),
            new("author", "Разработал", FieldKind.Text, []),
            new("supervisor", "Руководитель", FieldKind.Text, []),
            new("cityYear", "Город, год", FieldKind.Text, []),
            new("code", "Шифр проекта", FieldKind.Text, []),
            new("stage", "Стадия", FieldKind.Text, []),
            new("building", "Наименование здания", FieldKind.Text, []),
        ]),
    ];

    private readonly string _path;
    private readonly List<Entry> _entries = [];
    private readonly List<(Group Group, CheckBox Box)> _optional = [];
    private JsonObject _root = new();

    public InputDataWindow(string projectPath)
    {
        InitializeComponent();
        _path = projectPath;
        LoadProject();
        BuildTabs();
    }

    private void LoadProject()
    {
        _root = ProjectJson.LoadMerged(_path);
    }

    private void BuildTabs()
    {
        foreach (var group in Groups)
        {
            var tab = new TabItem { Header = group.Title };
            var panel = new StackPanel { Margin = new Thickness(8) };
            var isOptional = group.ToggleLabel is not null;
            var defaults = isOptional ? DefaultsFor(group.Path) : null;
            CheckBox? toggle = null;
            if (isOptional)
            {
                toggle = new CheckBox
                {
                    Content = group.ToggleLabel,
                    IsChecked = FindObject(group.Path) is not null,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                panel.Children.Add(toggle);
                var box = toggle;
                var entries = group;
                box.Checked += (_, _) => SetEnabled(entries, true);
                box.Unchecked += (_, _) => SetEnabled(entries, false);
            }

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < group.Fields.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var i = 0; i < group.Fields.Length; i++)
            {
                var field = group.Fields[i];
                var label = new TextBlock
                {
                    Text = field.Label,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 12, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = field.Hint,
                };
                var control = CreateControl(group, field, defaults);
                control.Margin = new Thickness(0, 4, 0, 4);
                if (field.Hint is not null)
                {
                    control.ToolTip = field.Hint;
                }

                Grid.SetRow(label, i);
                Grid.SetColumn(label, 0);
                Grid.SetRow(control, i);
                Grid.SetColumn(control, 1);
                grid.Children.Add(label);
                grid.Children.Add(control);
                _entries.Add(new Entry(group, field, control));
            }

            panel.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (toggle is not null)
            {
                _optional.Add((group, toggle));
                SetEnabled(group, toggle.IsChecked == true);
            }

            tab.Content = panel;
            Tabs.Items.Add(tab);
        }
    }

    private void SetEnabled(Group group, bool enabled)
    {
        foreach (var entry in _entries.Where(e => ReferenceEquals(e.Group, group)))
        {
            entry.Control.IsEnabled = enabled;
        }
    }

    private static JsonObject DefaultsFor(string path) => path switch
    {
        "design.hydrants" => JsonSerializer.SerializeToNode(new ProjectHydrants(), JsonOptions) as JsonObject ?? new JsonObject(),
        "design.tank" => JsonSerializer.SerializeToNode(new ProjectTank(), JsonOptions) as JsonObject ?? new JsonObject(),
        _ => new JsonObject(),
    };

    private Control CreateControl(Group group, Field field, JsonObject? defaults)
    {
        var value = ReadValue(group, field, defaults);
        switch (field.Kind)
        {
            case FieldKind.Bool:
                return new CheckBox { IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase), VerticalAlignment = VerticalAlignment.Center };
            case FieldKind.Choice:
                {
                    var combo = new ComboBox { ItemsSource = field.Options };
                    combo.SelectedItem = field.Options.Contains(value) ? value : field.Options.FirstOrDefault();
                    return combo;
                }

            case FieldKind.ChoiceInt:
                {
                    var combo = new ComboBox { ItemsSource = field.Options };
                    combo.SelectedItem = field.Options.Contains(value) ? value : field.Options.FirstOrDefault();
                    return combo;
                }

            default:
                return new TextBox { Text = value, Padding = new Thickness(4, 2, 4, 2) };
        }
    }

    private string ReadValue(Group group, Field field, JsonObject? defaults)
    {
        var node = FindObject(group.Path)?[field.Key] ?? defaults?[field.Key];
        if (node is null)
        {
            return "";
        }

        return field.Kind switch
        {
            FieldKind.Bool => node.GetValue<bool>() ? "true" : "false",
            FieldKind.Number or FieldKind.NullableNumber => node.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
            FieldKind.Int or FieldKind.NullableInt => node.GetValue<int>().ToString(CultureInfo.InvariantCulture),
            FieldKind.ChoiceInt => node.GetValue<int>().ToString(CultureInfo.InvariantCulture),
            _ => node.GetValue<string>(),
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        foreach (var (group, toggle) in _optional)
        {
            if (toggle.IsChecked != true)
            {
                RemovePath(group.Path);
            }
            else
            {
                EnsureObject(group.Path);
            }
        }

        foreach (var entry in _entries)
        {
            var toggle = _optional.FirstOrDefault(o => ReferenceEquals(o.Group, entry.Group)).Box;
            if (entry.Group.ToggleLabel is not null && toggle is { IsChecked: not true })
            {
                continue;
            }

            var target = EnsureObject(entry.Group.Path);
            if (!Write(entry.Field, entry.Control, target, errors))
            {
                continue;
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Проверьте исходные данные", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ProjectJson.Save(_path, _root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Не удалось сохранить проект: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private static bool Write(Field field, Control control, JsonObject target, List<string> errors)
    {
        if (field.Kind == FieldKind.Bool)
        {
            target[field.Key] = (control as CheckBox)?.IsChecked == true;
            return true;
        }

        if (control is not ComboBox and not TextBox)
        {
            return true;
        }

        var text = (control as TextBox)?.Text.Trim() ?? (control as ComboBox)?.SelectedItem as string ?? "";
        if (text.Length == 0)
        {
            target.Remove(field.Key);
            return true;
        }

        switch (field.Kind)
        {
            case FieldKind.Text:
                target[field.Key] = text;
                return true;
            case FieldKind.Choice:
                target[field.Key] = text;
                return true;
            case FieldKind.ChoiceInt:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var choice))
                {
                    target[field.Key] = choice;
                    return true;
                }

                errors.Add($"{field.Label}: ожидается целое число.");
                return false;
            case FieldKind.Int or FieldKind.NullableInt:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    target[field.Key] = integer;
                    return true;
                }

                errors.Add($"{field.Label}: ожидается целое число, введено «{text}».");
                return false;
            default:
                if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    target[field.Key] = number;
                    return true;
                }

                errors.Add($"{field.Label}: ожидается число, введено «{text}».");
                return false;
        }
    }

    private JsonObject? FindObject(string path)
    {
        if (path.Length == 0)
        {
            return _root;
        }

        JsonNode? node = _root;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node) || node is null)
            {
                return null;
            }
        }

        return node as JsonObject;
    }

    private JsonObject EnsureObject(string path)
    {
        if (path.Length == 0)
        {
            return _root;
        }

        var node = _root;
        foreach (var part in path.Split('.'))
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }

            node = child;
        }

        return node;
    }

    private void RemovePath(string path)
    {
        var last = path.LastIndexOf('.');
        var parent = last < 0 ? _root : FindObject(path[..last]);
        parent?.Remove(last < 0 ? path : path[(last + 1)..]);
    }
}
