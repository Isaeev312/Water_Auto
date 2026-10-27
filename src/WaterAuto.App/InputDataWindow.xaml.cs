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
/// </summary>
public partial class InputDataWindow : Window
{
    private sealed record SectionView(
        SectionSpec Spec,
        StackPanel Panel,
        CheckBox? Toggle,
        FrameworkElement FieldsHost,
        List<InputFieldController> Controllers);

    private readonly string _path;
    private readonly string _projectDirectory;
    private readonly InputSources _sources;
    private readonly JsonObject _root;
    private readonly IInputFieldHost _host;
    private readonly Dictionary<string, InputFieldController> _controllers = new(StringComparer.Ordinal);
    private readonly List<SectionView> _sections = [];
    private string? _readError;
    private bool _navigating;

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
        UpdateFillCounter();
        if (_readError is { } readError)
        {
            ShowErrors([readError], []);
            SaveButton.IsEnabled = false;
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
        foreach (var spec in InputFields.All)
        {
            var panel = new StackPanel { Visibility = Visibility.Collapsed };

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var title = new TextBlock { Text = spec.NavTitle };
            InputTheme.ApplyStyle(title, "Text.H2");
            header.Children.Add(title);
            var description = new TextBlock { Text = spec.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            InputTheme.ApplyStyle(description, "Text.Caption");
            header.Children.Add(description);
            panel.Children.Add(header);

            CheckBox? toggle = null;
            if (spec.OptionalPath is not null)
            {
                panel.Children.Add(BuildOptionalCard(spec, out toggle));
            }

            var fieldsHost = new StackPanel();
            var controllers = new List<InputFieldController>();
            foreach (var card in spec.Cards)
            {
                fieldsHost.Children.Add(BuildCard(card, controllers));
            }

            panel.Children.Add(fieldsHost);
            if (toggle is not null)
            {
                fieldsHost.IsEnabled = toggle.IsChecked == true;
                toggle.Checked += (_, _) =>
                {
                    fieldsHost.IsEnabled = true;
                    UpdateFillCounter();
                };
                toggle.Unchecked += (_, _) =>
                {
                    fieldsHost.IsEnabled = false;
                    UpdateFillCounter();
                };
            }

            SectionHost.Children.Add(panel);
            _sections.Add(new SectionView(spec, panel, toggle, fieldsHost, controllers));
            foreach (var controller in controllers)
            {
                _controllers[controller.Field.Id] = controller;
            }
        }

        Nav.ItemsSource = _sections.Select(section => section.Spec.NavTitle).ToList();
        if (_sections.Count > 0)
        {
            SelectSection(0);
        }
    }

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

    private Border BuildCard(CardSpec card, List<InputFieldController> controllers)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 16) };
        InputTheme.ApplyStyle(border, "Card");

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
        return border;
    }

    private void UpdateFillCounter()
    {
        var filled = 0;
        var total = 0;
        foreach (var section in _sections)
        {
            if (section.Spec.OptionalPath is not null && section.Toggle?.IsChecked != true)
            {
                continue;
            }

            foreach (var controller in section.Controllers)
            {
                total++;
                if (controller.IsFilled)
                {
                    filled++;
                }
            }
        }

        FillCountText.Text = $"Заполнено {filled} из {total} полей";
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

        first.FocusField();
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

        UpdateFillCounter();
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
