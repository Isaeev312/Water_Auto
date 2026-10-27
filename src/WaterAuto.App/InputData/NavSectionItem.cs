using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WaterAuto.App.InputData;

/// <summary>Статус раздела навигации по обязательным полям.</summary>
internal enum NavSectionStatus
{
    /// <summary>В разделе нет видимых обязательных полей — индикатор не показывается.</summary>
    None,

    /// <summary>Все видимые обязательные поля раздела заполнены.</summary>
    Ok,

    /// <summary>Есть незаполненные обязательные поля раздела.</summary>
    Missing,
}

/// <summary>
/// Пункт навигации мастера: векторная иконка раздела, название и маленький индикатор состояния обязательных полей —
/// акцентная галочка, когда всё заполнено, и точка Warning, когда остались обязательные поля. Строка статуса
/// обновляется окном при изменении значений и переключении режима; сам пункт ничего не считает.
/// </summary>
internal sealed class NavSectionItem
{
    private static readonly Geometry CheckGeometry = Geometry.Parse("M2.5,6.5 L5.8,9.8 L12,3.2");

    private readonly Path _check;
    private readonly Ellipse _missingDot;
    private readonly Grid _statusHost;

    public NavSectionItem(string title, string iconData)
    {
        Title = title;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Path
        {
            Data = Geometry.Parse(iconData),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.StrokeProperty, "Brush.Text.Secondary");
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            Text = title,
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        _statusHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        _check = new Path
        {
            Data = CheckGeometry,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _check.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _statusHost.Children.Add(_check);
        _missingDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _missingDot.SetResourceReference(Shape.FillProperty, "Brush.Warning");
        _statusHost.Children.Add(_missingDot);
        Grid.SetColumn(_statusHost, 2);
        grid.Children.Add(_statusHost);

        View = grid;
    }

    /// <summary>Название раздела мастера.</summary>
    public string Title { get; }

    /// <summary>Представление пункта для ListBox навигации.</summary>
    public Grid View { get; }

    /// <summary>Текущий статус — для обновления из окна и проверки в тестах.</summary>
    public NavSectionStatus Status { get; private set; }

    /// <summary>Показать галочку (всё заполнено), точку Warning (остались поля) или ничего.</summary>
    public void SetStatus(NavSectionStatus status, string? toolTip = null)
    {
        Status = status;
        _check.Visibility = status == NavSectionStatus.Ok ? Visibility.Visible : Visibility.Collapsed;
        _missingDot.Visibility = status == NavSectionStatus.Missing ? Visibility.Visible : Visibility.Collapsed;
        _statusHost.ToolTip = toolTip;
    }
}
