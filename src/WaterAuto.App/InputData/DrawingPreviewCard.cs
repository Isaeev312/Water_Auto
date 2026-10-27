using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.App.InputData;

/// <summary>
/// Карточка раздела «Проект» — предпроверка чертежа до расчёта: статус (готов / предупреждения / ошибки / не найден),
/// что распознано на плане, замечания импорта и предпроверки, а также предложения по исходным данным с кнопками
/// «Применить». Тяжёлый разбор чертежа выполняет окно в фоне и передаёт готовый <see cref="DrawingPreviewResult"/>;
/// карточка отвечает только за вид, поэтому её можно безопасно обновлять из UI-потока.
/// </summary>
internal sealed class DrawingPreviewCard
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Action _refresh;
    private readonly Action<DrawingSuggestion> _apply;
    private readonly Action<IReadOnlyList<DrawingSuggestion>> _applyAll;

    private readonly Border _card;
    private readonly Border _statusPill;
    private readonly TextBlock _statusText;
    private readonly Button _refreshButton;
    private readonly ProgressBar _progress;
    private readonly TextBlock _stateText;
    private readonly TextBlock _errorText;
    private readonly Grid _stats;
    private readonly StackPanel _issuesBlock;
    private readonly TextBlock _issuesTitle;
    private readonly StackPanel _issues;
    private readonly StackPanel _suggestionsBlock;
    private readonly TextBlock _suggestionsTitle;
    private readonly Button _applyAllButton;
    private readonly TextBlock _suggestionsEmpty;
    private readonly StackPanel _suggestions;
    private readonly List<(DrawingSuggestion Suggestion, Button Button)> _suggestionRows = [];
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private int _statCount;

    public DrawingPreviewCard(
        Action refresh,
        Action<DrawingSuggestion> apply,
        Action<IReadOnlyList<DrawingSuggestion>> applyAll)
    {
        _refresh = refresh;
        _apply = apply;
        _applyAll = applyAll;

        _card = new Border { Margin = new Thickness(0, 0, 0, 16) };
        InputTheme.ApplyStyle(_card, "Card");
        var stack = new StackPanel();
        _card.Child = stack;

        // Шапка: заголовок, статус-пилюля и кнопка повторной проверки.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { Text = "Проверка чертежа" };
        InputTheme.ApplyStyle(title, "Text.H3");
        titles.Children.Add(title);
        var subtitle = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Автоматическая предпроверка плана: что распознано на чертеже и какие исходные данные можно заполнить по нему.",
        };
        InputTheme.ApplyStyle(subtitle, "Text.Caption");
        titles.Children.Add(subtitle);
        header.Children.Add(titles);

        var actions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _statusPill = new Border { HorizontalAlignment = HorizontalAlignment.Right };
        InputTheme.ApplyStyle(_statusPill, "Pill.Neutral");
        _statusText = new TextBlock { Text = "Ожидает проверки" };
        InputTheme.ApplyStyle(_statusText, "Text.Caption");
        _statusPill.Child = _statusText;
        actions.Children.Add(_statusPill);

        _refreshButton = new Button
        {
            Content = "Проверить чертёж",
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Перечитать чертёж с текущими значениями окна",
        };
        InputTheme.ApplyStyle(_refreshButton, "Button.Secondary");
        _refreshButton.Click += (_, _) => _refresh();
        actions.Children.Add(_refreshButton);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        stack.Children.Add(header);

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_progress);

        _stateText = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        InputTheme.ApplyStyle(_stateText, "Text.Caption");
        stack.Children.Add(_stateText);

        _errorText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        InputTheme.ApplyStyle(_errorText, "Text.Caption");
        _errorText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
        stack.Children.Add(_errorText);

        // Что распознано: компактная сетка «параметр — значение» в две колонки, чтобы карточка не выталкивала поля.
        _stats = new Grid { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(_stats);

        // Замечания импорта и предпроверки: прокручиваемый список, чтобы карточка не выталкивала поля.
        _issuesBlock = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        _issuesTitle = new TextBlock();
        InputTheme.ApplyStyle(_issuesTitle, "Text.Label");
        _issuesBlock.Children.Add(_issuesTitle);
        var issuesInset = new Border { Margin = new Thickness(0, 6, 0, 0) };
        InputTheme.ApplyStyle(issuesInset, "Card.Inset");
        var issuesScroll = new ScrollViewer
        {
            MaxHeight = 170,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _issues = new StackPanel();
        issuesScroll.Content = _issues;
        issuesInset.Child = issuesScroll;
        _issuesBlock.Children.Add(issuesInset);
        stack.Children.Add(_issuesBlock);

        // Предложения по исходным данным: значение, обязательно видимое основание и кнопка «Применить».
        _suggestionsBlock = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var suggestionsHeader = new Grid();
        suggestionsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        suggestionsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _suggestionsTitle = new TextBlock();
        InputTheme.ApplyStyle(_suggestionsTitle, "Text.Label");
        suggestionsHeader.Children.Add(_suggestionsTitle);
        _applyAllButton = new Button { Content = "Применить все", Visibility = Visibility.Collapsed };
        InputTheme.ApplyStyle(_applyAllButton, "Button.Secondary");
        _applyAllButton.Click += (_, _) => ApplyAll();
        Grid.SetColumn(_applyAllButton, 1);
        suggestionsHeader.Children.Add(_applyAllButton);
        _suggestionsBlock.Children.Add(suggestionsHeader);

        _suggestionsEmpty = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        InputTheme.ApplyStyle(_suggestionsEmpty, "Text.Caption");
        _suggestionsBlock.Children.Add(_suggestionsEmpty);

        _suggestions = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        _suggestionsBlock.Children.Add(_suggestions);
        stack.Children.Add(_suggestionsBlock);
    }

    /// <summary>Представление карточки для окна мастера.</summary>
    public FrameworkElement View => _card;

    /// <summary>Идёт разбор чертежа: показать прогресс, скрыть прежний результат.</summary>
    public void SetBusy()
    {
        ClearResult();
        _progress.Visibility = Visibility.Visible;
        _refreshButton.IsEnabled = false;
        SetStatus("Pill.Neutral", "Проверка чертежа…");
        _stateText.Text = "Разбираем чертёж и сверяем его с текущими значениями окна — это может занять несколько секунд.";
    }

    /// <summary>Чертёж не задан или путь к нему не разрешается: проверять нечего, но окно работает дальше.</summary>
    public void ShowNoDrawing(string message)
    {
        ClearResult();
        _progress.Visibility = Visibility.Collapsed;
        _refreshButton.IsEnabled = true;
        SetStatus("Pill.Warning", "Чертёж не найден");
        _stateText.Text = message;
    }

    /// <summary>Файл проекта не прочитан — проверка невозможна (сообщение уже показано в окне).</summary>
    public void ShowUnavailable(string reason)
    {
        ClearResult();
        _progress.Visibility = Visibility.Collapsed;
        _refreshButton.IsEnabled = false;
        SetStatus("Pill.Neutral", "Проверка недоступна");
        _stateText.Text = "Файл проекта не прочитан: " + reason;
    }

    /// <summary>Ошибка самого анализа (повреждённый DWG, битые данные каталога): не роняем окно, показываем в карточке.</summary>
    public void ShowAnalysisError(string message)
    {
        ClearResult();
        _progress.Visibility = Visibility.Collapsed;
        _refreshButton.IsEnabled = true;
        SetStatus("Pill.Error", "Проверка не выполнена");
        _stateText.Text = "Не удалось выполнить предпроверку — исходные данные проекта остались без изменений.";
        _errorText.Text = "Ошибка анализа: " + message;
        _errorText.Visibility = Visibility.Visible;
    }

    /// <summary>Показать результат предпроверки чертежа.</summary>
    public void ShowResult(DrawingPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ClearResult();
        _progress.Visibility = Visibility.Collapsed;
        _refreshButton.IsEnabled = true;

        if (!result.DrawingFound)
        {
            SetStatus("Pill.Warning", "Чертёж не найден");
            _stateText.Text = result.DrawingPath.Length > 0
                ? "Файл не найден: " + result.DrawingPath + ". Проверьте путь в поле «Чертёж плана»."
                : "Чертёж не задан — укажите путь в поле «Чертёж плана».";
            FillIssues(result.Issues);
            return;
        }

        var errors = result.Issues.Count(issue => issue.Level == IssueLevel.Error);
        var warnings = result.Issues.Count(issue => issue.Level == IssueLevel.Warning);
        var infos = result.Issues.Count(issue => issue.Level == IssueLevel.Info);
        if (errors > 0)
        {
            SetStatus("Pill.Error", $"Есть ошибки ({errors})");
            _stateText.Text = "Чертёж не готов к расчёту: исправьте ошибки и проверьте снова.";
        }
        else if (warnings > 0)
        {
            SetStatus("Pill.Warning", $"Есть предупреждения ({warnings})");
            _stateText.Text = "Чертёж можно использовать в расчёте, но проверьте замечания ниже.";
        }
        else
        {
            SetStatus("Pill.Success", "Чертёж готов к расчёту");
            _stateText.Text = "Геометрия чертежа распознана, критичных замечаний нет.";
        }

        if (infos > 0)
        {
            _stateText.Text += $" Информационных замечаний: {infos}.";
        }

        _stats.Visibility = Visibility.Visible;
        FillStats(result);
        FillIssues(result.Issues);

        _suggestionsBlock.Visibility = Visibility.Visible;
        FillSuggestions(result.Suggestions);
    }

    private void ClearResult()
    {
        _errorText.Visibility = Visibility.Collapsed;
        _errorText.Text = "";
        _stats.Visibility = Visibility.Collapsed;
        _stats.Children.Clear();
        _stats.RowDefinitions.Clear();
        _statCount = 0;
        _issuesBlock.Visibility = Visibility.Collapsed;
        _issues.Children.Clear();
        _suggestionsBlock.Visibility = Visibility.Collapsed;
        _suggestions.Children.Clear();
        _suggestionRows.Clear();
        _applied.Clear();
        _applyAllButton.Visibility = Visibility.Collapsed;
        _applyAllButton.IsEnabled = true;
        _suggestionsEmpty.Visibility = Visibility.Collapsed;
        _stateText.Text = "";
    }

    private void SetStatus(string styleKey, string text)
    {
        InputTheme.ApplyStyle(_statusPill, styleKey);
        _statusText.Text = text;
    }

    private void FillStats(DrawingPreviewResult result)
    {
        _stats.Children.Clear();
        _stats.RowDefinitions.Clear();

        AddStat("Помещения", $"{result.Rooms.Count} · {Fmt(result.TotalProtectedAreaM2)} м²", RoomNames(result.Rooms));
        if (result.PumpRoomId is { } pumpRoomId)
        {
            var size = result.PumpRoomLengthM is { } length && result.PumpRoomWidthM is { } width
                ? $"{Fmt(length)} × {Fmt(width)} м"
                : "размер не определён";
            var area = result.PumpRoomAreaM2 is { } pumpArea ? $" · {Fmt(pumpArea)} м²" : "";
            AddStat("Насосная", $"{pumpRoomId} · {size}{area}");
            AddStat("Обвязка насосной",
                $"вводов {result.PumpRoomInlets} · выпусков {result.PumpRoomOutlets}",
                $"патрубки ГМ: {result.TruckInlets} · дверь: {(result.PumpRoomHasDoor ? "есть" : "нет")}");
        }
        else
        {
            AddStat("Насосная", "не найдена на чертеже (слой ПТ_НС)");
        }

        AddStat("Конструкции на плане", $"колонн {result.Columns} · балок {result.Beams}",
            $"коробов {result.Ducts} · зон без оросителей {result.NoSprinklerZones}");
        AddStat("Точки ввода", $"ПТ_ВВОД: {result.Inlets}", $"вводов здания: {result.BuildingInlets}");
        AddStat("Отметки",
            "низ перекрытия: " + (result.SlabBottomM is { } slab ? $"{Fmt(slab)} м" : "не задан на чертеже"),
            "низ короба: " + (result.DuctBottomM is { } duct ? $"{Fmt(duct)} м" : "не задан на чертеже"));
    }

    private void AddStat(string label, string value, string? detail = null)
    {
        var index = _statCount++;
        if (index % 2 == 0)
        {
            _stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var cell = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var labelText = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        InputTheme.ApplyStyle(labelText, "Text.Caption");
        cell.Children.Add(labelText);
        var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        InputTheme.ApplyStyle(valueText, "Text.Value");
        cell.Children.Add(valueText);
        if (detail is { Length: > 0 })
        {
            var detailText = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            InputTheme.ApplyStyle(detailText, "Text.Caption");
            cell.Children.Add(detailText);
        }

        Grid.SetRow(cell, index / 2);
        Grid.SetColumn(cell, index % 2 == 0 ? 0 : 2);
        _stats.Children.Add(cell);
    }

    private void FillIssues(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        _issuesBlock.Visibility = Visibility.Visible;
        _issuesTitle.Text = $"Замечания предпроверки ({issues.Count})";
        foreach (var issue in issues.OrderByDescending(issue => issue.Level))
        {
            AddIssue(issue);
        }
    }

    private void AddIssue(Issue issue)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pill = new Border { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
        InputTheme.ApplyStyle(pill, issue.Level switch
        {
            IssueLevel.Error => "Pill.Error",
            IssueLevel.Warning => "Pill.Warning",
            _ => "Pill.Info",
        });
        var levelText = new TextBlock { Text = LevelName(issue.Level) };
        InputTheme.ApplyStyle(levelText, "Text.Caption");
        pill.Child = levelText;
        row.Children.Add(pill);

        var text = new StackPanel();
        var message = new TextBlock { Text = issue.Message, TextWrapping = TextWrapping.Wrap };
        InputTheme.ApplyStyle(message, "Text.Body");
        text.Children.Add(message);
        var meta = new List<string>();
        if (issue.Clause is { Length: > 0 } clause)
        {
            meta.Add("Пункт: " + clause);
        }

        if (issue.ElementId is { Length: > 0 } element)
        {
            meta.Add("Элемент: " + element);
        }

        if (meta.Count > 0)
        {
            var metaText = new TextBlock
            {
                Text = string.Join(" · ", meta),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            };
            InputTheme.ApplyStyle(metaText, "Text.Caption");
            text.Children.Add(metaText);
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        _issues.Children.Add(row);
    }

    private void FillSuggestions(IReadOnlyList<DrawingSuggestion> suggestions)
    {
        _suggestionsTitle.Text = suggestions.Count > 0
            ? $"Предложения по исходным данным ({suggestions.Count})"
            : "Предложения по исходным данным";
        if (suggestions.Count == 0)
        {
            _suggestionsEmpty.Text = "Предложений нет: на чертеже не хватает геометрии (например, слоя ПТ_БАЛКИ) " +
                "или данные окна уже заполнены.";
            _suggestionsEmpty.Visibility = Visibility.Visible;
            return;
        }

        _applyAllButton.Visibility = suggestions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var suggestion in suggestions)
        {
            AddSuggestion(suggestion);
        }
    }

    private void AddSuggestion(DrawingSuggestion suggestion)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        var line = new TextBlock
        {
            Text = $"{suggestion.Label} — {InputJson.FormatNumber(suggestion.Value)} {suggestion.Unit}",
            TextWrapping = TextWrapping.Wrap,
        };
        InputTheme.ApplyStyle(line, "Text.Value");
        text.Children.Add(line);
        var basis = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Основание: " + suggestion.Basis + (suggestion.Source.Length > 0 ? " · источник: " + suggestion.Source : ""),
        };
        InputTheme.ApplyStyle(basis, "Text.Caption");
        text.Children.Add(basis);
        row.Children.Add(text);

        var button = new Button
        {
            Content = "Применить",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 28,
            Padding = new Thickness(12, 2, 12, 2),
        };
        InputTheme.ApplyStyle(button, "Button.Secondary");
        button.Click += (_, _) =>
        {
            _apply(suggestion);
            MarkApplied(suggestion);
        };
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        _suggestionRows.Add((suggestion, button));
        _suggestions.Children.Add(row);
    }

    private void ApplyAll()
    {
        var pending = _suggestionRows
            .Where(row => !_applied.Contains(row.Suggestion.Key))
            .Select(row => row.Suggestion)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        _applyAll(pending);
        foreach (var suggestion in pending)
        {
            MarkApplied(suggestion);
        }
    }

    private void MarkApplied(DrawingSuggestion suggestion)
    {
        _applied.Add(suggestion.Key);
        foreach (var (appliedSuggestion, button) in _suggestionRows)
        {
            if (!string.Equals(appliedSuggestion.Key, suggestion.Key, StringComparison.Ordinal))
            {
                continue;
            }

            button.Content = "Применено";
            button.IsEnabled = false;
            button.ToolTip = "Значение уже подставлено в поле ввода";
        }

        _applyAllButton.IsEnabled = _suggestionRows.Any(row => !_applied.Contains(row.Suggestion.Key));
        if (!_applyAllButton.IsEnabled)
        {
            _applyAllButton.Content = "Все применены";
        }
    }

    private static string LevelName(IssueLevel level) => level switch
    {
        IssueLevel.Error => "Ошибка",
        IssueLevel.Warning => "Предупреждение",
        _ => "Информация",
    };

    private static string? RoomNames(IReadOnlyList<DetectedRoom> rooms)
    {
        if (rooms.Count == 0)
        {
            return null;
        }

        const int shown = 8;
        var names = string.Join("; ", rooms.Take(shown).Select(room => $"{room.Name} — {Fmt(room.AreaM2)} м²"));
        if (rooms.Count > shown)
        {
            names += $" и ещё {rooms.Count - shown}";
        }

        return names;
    }

    private static string Fmt(double value) => value.ToString("0.##", Ru);
}
