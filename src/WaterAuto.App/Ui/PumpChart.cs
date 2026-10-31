using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Мини-график Q–H в карточке насосной установки главного окна: кривая установки (паспортная кривая одного
/// насоса, приведённая к подаче установки умножением Q на число основных насосов), рабочая точка при расчётных
/// Q и H и линия требуемого напора. Рисуется на <see cref="Canvas"/> кодом, без сторонних библиотек.
/// Цвета — только токены темы (<c>Brush.Accent</c>, <c>Brush.Text.Secondary</c>, <c>Brush.Border</c>), поэтому
/// график читается в светлой и тёмной теме и следует за сменой акцентного цвета.
/// </summary>
public static class PumpChart
{
    /// <summary>Ширина графика по умолчанию, px (по контракту карточки — 240…280).</summary>
    public const double PreferredWidth = 260;

    /// <summary>Высота графика по умолчанию, px (по контракту карточки — 110…130).</summary>
    public const double PreferredHeight = 120;

    private const double PadLeft = 30;
    private const double PadRight = 12;
    private const double PadTop = 22;
    private const double PadBottom = 28;
    private const string AccentBrushKey = "Brush.Accent";
    private const string SecondaryBrushKey = "Brush.Text.Secondary";
    private const string BorderBrushKey = "Brush.Border";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Перерисовывает график: очищает холст и заново строит оси, кривую, рабочую точку и линию требуемого напора.
    /// Если кривой нет, остаются только подписи осей.
    /// </summary>
    public static void Draw(Canvas canvas, PumpSetSummary pump)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(pump);
        canvas.Children.Clear();

        var width = double.IsFinite(canvas.Width) && canvas.Width > 0 ? canvas.Width : PreferredWidth;
        var height = double.IsFinite(canvas.Height) && canvas.Height > 0 ? canvas.Height : PreferredHeight;
        var plotWidth = width - PadLeft - PadRight;
        var plotHeight = height - PadTop - PadBottom;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        // Кривая установки: паспортная кривая одного насоса по подаче установки (N основных насосов работают параллельно).
        var mainPumps = Math.Max(1, pump.MainPumps);
        var curve = pump.Curve
            .OrderBy(p => p.QM3h)
            .Select(p => (Q: p.QM3h * mainPumps, H: p.HM))
            .ToList();
        var requiredFlow = Math.Max(0, pump.RequiredFlowM3h);
        var requiredHead = pump.RequiredHeadMeters;
        var dutyHead = pump.HeadAtDutyMeters;

        // Границы осей: по данным кривой, рабочей точки и требуемого напора; с «круглыми» делениями.
        var qHigh = Math.Max(requiredFlow, curve.Count > 0 ? curve.Max(p => p.Q) : 0);
        var qMax = NiceCeiling(Math.Max(qHigh * 1.06, 1), 5);
        var heads = new List<double> { requiredHead, dutyHead };
        heads.AddRange(curve.Select(p => p.H));
        var hLow = heads.Min();
        var hHigh = heads.Max();
        var hSpan = Math.Max(hHigh - hLow, 1);
        var hStep = hSpan > 50 ? 20 : hSpan > 20 ? 10 : hSpan > 5 ? 5 : 1;
        var hMin = Math.Max(0, Math.Floor((hLow - (hSpan * 0.12)) / hStep) * hStep);
        var hMax = NiceCeiling(hHigh + (hSpan * 0.10), hStep);
        if (hMax - hMin < 1)
        {
            hMax = hMin + 1;
        }

        double X(double q) => PadLeft + (q / qMax * plotWidth);
        double Y(double h) => PadTop + ((hMax - h) / (hMax - hMin) * plotHeight);

        var plotBottom = PadTop + plotHeight;
        var plotRight = PadLeft + plotWidth;

        // Оси и деления — Brush.Border.
        AddLine(canvas, PadLeft, PadTop, PadLeft, plotBottom, BorderBrushKey, 1);
        AddLine(canvas, PadLeft, plotBottom, plotRight, plotBottom, BorderBrushKey, 1);
        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var x = PadLeft + (plotWidth * fraction);
            AddLine(canvas, x, plotBottom, x, plotBottom + 3, BorderBrushKey, 1);
            var h = hMin + ((hMax - hMin) * fraction);
            var y = Y(h);
            AddLine(canvas, PadLeft - 3, y, PadLeft, y, BorderBrushKey, 1);
        }

        // Линия требуемого напора — Brush.Text.Secondary пунктиром.
        var requiredY = Y(requiredHead);
        AddLine(canvas, PadLeft, requiredY, plotRight, requiredY, SecondaryBrushKey, 1, new DoubleCollection { 4, 3 });

        // Паспортная кривая — Brush.Accent.
        if (curve.Count > 1)
        {
            var polyline = new Polyline
            {
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = [.. curve.Select(p => new Point(X(p.Q), Y(p.H)))],
            };
            polyline.SetResourceReference(Shape.StrokeProperty, AccentBrushKey);
            canvas.Children.Add(polyline);
        }

        // Рабочая точка — кружок акцентом с полупрозрачным ореолом (читается и на линии кривой).
        var dutyX = X(requiredFlow);
        var dutyY = Y(dutyHead);
        var halo = new Ellipse
        {
            Width = 15,
            Height = 15,
            StrokeThickness = 1.2,
            Opacity = 0.35,
        };
        halo.SetResourceReference(Shape.StrokeProperty, AccentBrushKey);
        Canvas.SetLeft(halo, dutyX - 7.5);
        Canvas.SetTop(halo, dutyY - 7.5);
        var dot = new Ellipse { Width = 8, Height = 8 };
        dot.SetResourceReference(Shape.FillProperty, AccentBrushKey);
        Canvas.SetLeft(dot, dutyX - 4);
        Canvas.SetTop(dot, dutyY - 4);
        canvas.Children.Add(halo);
        canvas.Children.Add(dot);

        // Подписи осей и значений — Brush.Text.Secondary.
        AddCaption(canvas, "H, м", width - 50, 1, 48, TextAlignment.Right);
        AddCaption(canvas, "Q, м³/ч", width - 58, plotBottom + 14, 58, TextAlignment.Right);
        AddCaption(canvas, "0", PadLeft - 12, plotBottom + 2, 24, TextAlignment.Center);
        AddCaption(canvas, qMax.ToString("0.#", Ru), plotRight - 44, plotBottom + 2, 44, TextAlignment.Right);
        AddCaption(canvas, hMax.ToString("0.#", Ru), 0, Y(hMax) - 6, PadLeft - 5, TextAlignment.Right);
        AddCaption(canvas, hMin.ToString("0.#", Ru), 0, Y(hMin) - 6, PadLeft - 5, TextAlignment.Right);

        canvas.ToolTip = string.Create(
            Ru,
            $"Установка {pump.Designation}: {pump.MainPumps} осн. + {pump.StandbyPumps} рез. насосов, кривая Q–H одного насоса, приведённая к подаче установки. Рабочая точка: Q = {requiredFlow:0.0} м³/ч, H = {dutyHead:0.0} м; требуется H = {requiredHead:0.0} м.");
    }

    /// <summary>Очищает холст (установка не подобрана — график скрыт).</summary>
    public static void Clear(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Children.Clear();
        canvas.ToolTip = null;
    }

    private static void AddLine(
        Canvas canvas,
        double x1,
        double y1,
        double x2,
        double y2,
        string brushKey,
        double thickness,
        DoubleCollection? dashArray = null)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            StrokeThickness = thickness,
            SnapsToDevicePixels = true,
        };
        if (dashArray is not null)
        {
            line.StrokeDashArray = dashArray;
        }

        line.SetResourceReference(Shape.StrokeProperty, brushKey);
        canvas.Children.Add(line);
    }

    private static void AddCaption(Canvas canvas, string text, double x, double y, double width, TextAlignment alignment)
    {
        var caption = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Width = width,
            TextAlignment = alignment,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, SecondaryBrushKey);
        Canvas.SetLeft(caption, x);
        Canvas.SetTop(caption, y);
        canvas.Children.Add(caption);
    }

    // Округление вверх до шага «круглых» значений оси; шаг 1 — запасной для малых величин.
    private static double NiceCeiling(double value, double step) =>
        step <= 0 ? value : Math.Ceiling(value / step) * step;
}
