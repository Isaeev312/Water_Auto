using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// Структурная схема АПС/СОУЭ в DXF (лист в рамке ГОСТ Р 21.101, формат по габаритам схемы): слева источники питания и
/// контроллеры, от каждого контроллера — кольцевая линия с устройствами в порядке обхода (ряды условных знаков с обозначениями
/// и подписью), модуль сигналов АУПТ и релейные блоки — в конце линии; ниже — блок речевого оповещения с линиями
/// громкоговорителей. Размеры — миллиметры листа (масштаб 1:1), как у принципиальной схемы насосной.
/// </summary>
public static class AlarmSchemeDxfWriter
{
    public const string LineLayer = "АПС_СХЕМА_ЛИНИИ";
    public const string DeviceLayer = "АПС_СХЕМА_ПРИБОРЫ";
    public const string TextLayer = "АПС_СХЕМА_ТЕКСТ";

    private const double BoxW = 9, BoxH = 6, Pitch = 10.5, RowPitch = 11;
    private const int PerRow = 30;

    public static void Write(string path, AlarmScheme scheme, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(scheme, stamp), font);

    public static CadDocument Build(AlarmScheme scheme, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(stamp);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var doc = new CadDocument();
        var lines = DxfDrawing.AddLayer(doc, LineLayer, 5, LineWeightType.W35);
        var devices = DxfDrawing.AddLayer(doc, DeviceLayer, 1, LineWeightType.W25);
        var text = DxfDrawing.AddLayer(doc, TextLayer, 7);
        var d = new Drawer(doc, lines, devices, text);

        // Левая колонка: РИП с АКБ, под ними контроллеры; устройства — рядами справа от шины контроллера.
        const double xSupply = 20, xCtrl = 20, xDev0 = 75, wCtrl = 40, hCtrl = 10;
        var y = 0.0;
        for (var k = 0; k < Math.Max(1, scheme.Supplies); k++)
        {
            d.Box(xSupply, y - 10, wCtrl, 10, $"{scheme.Supply} №{k + 1}", 2.0);
            d.Text(xSupply + wCtrl + 2, y - 7, $"АКБ {scheme.Battery}; 220 В от ВРУ, I кат.", 2.0);
            y -= 13;
        }

        d.Text(xSupply, y - 3, "12 В к контроллерам; RS-485 — к пульту/ППКП (по проекту)", 1.8);
        y -= 8;

        var maxX = xDev0;
        foreach (var loop in scheme.Loops)
        {
            var top = y;
            d.Box(xCtrl, y - hCtrl, wCtrl, hCtrl, $"{loop.Controller} №{loop.Number}", 2.2);
            var rows = Math.Max(1, (int)Math.Ceiling((double)loop.Devices.Count / PerRow));
            var busY = y - (hCtrl / 2);
            d.Line(xCtrl + wCtrl, busY, xDev0 - 2, busY);
            d.Text(xCtrl + wCtrl + 1, busY + 1, $"ДПЛС{loop.Number}", 1.8);
            for (var r = 0; r < rows; r++)
            {
                var rowY = top - (hCtrl / 2) - (r * RowPitch);
                var inRow = loop.Devices.Skip(r * PerRow).Take(PerRow).ToList();
                var x = xDev0;
                // Шина ряда; переход на следующий ряд — вертикалью у правого края, назад к началу — у левого (змейкой).
                d.Line(xDev0 - 2, rowY, xDev0 + (inRow.Count * Pitch) - 1.5, rowY);
                foreach (var dev in inRow)
                {
                    d.Device(x, rowY - (BoxH / 2), dev);
                    x += Pitch;
                }

                maxX = Math.Max(maxX, x + 2);
                if (r < rows - 1)
                {
                    var xEnd = xDev0 + (inRow.Count * Pitch) - 1.5;
                    d.Line(xEnd, rowY, xEnd, rowY - RowPitch);
                    d.Line(xDev0 - 2, rowY - RowPitch, xEnd, rowY - RowPitch);
                }
            }

            // Возврат кольца к контроллеру — по левому краю; модуль насосной и релейные блоки — в конце линии.
            var lastY = top - (hCtrl / 2) - ((rows - 1) * RowPitch);
            var tailX = xDev0 + (Math.Min(PerRow, loop.Devices.Count - ((rows - 1) * PerRow)) * Pitch);
            var tailY = lastY;
            if (loop.PumpModule is { } module)
            {
                d.Box(tailX, tailY - 4, 26, 8, module, 1.8);
                d.Text(tailX, tailY - 7, "сигналы АУПТ, насосная", 1.6);
                d.Line(tailX - 1.5, tailY, tailX, tailY);
                tailX += 28;
                maxX = Math.Max(maxX, tailX + 2);
            }

            if (loop.Relays > 0 && loop.RelayName is { } relay)
            {
                d.Box(tailX, tailY - 4, 26, 8, $"{relay} × {loop.Relays}", 1.8);
                d.Text(tailX, tailY - 7, "команды АППЗ, линии СОУЭ", 1.6);
                d.Line(tailX - 2, tailY, tailX, tailY);
                tailX += 28;
                maxX = Math.Max(maxX, tailX + 2);
            }

            d.Line(tailX, tailY, tailX, tailY - 5);
            d.Line(tailX, tailY - 5, xCtrl + (wCtrl / 2), tailY - 5);
            d.Line(xCtrl + (wCtrl / 2), tailY - 5, xCtrl + (wCtrl / 2), y - hCtrl);
            d.Text(xCtrl + wCtrl + 1, tailY - 8.5, string.Create(ru, $"{loop.Devices.Count} адресных устройств, кольцо"), 1.6);
            y = tailY - 16;
        }

        if (scheme.Amplifier is { } amp && scheme.Amplifiers > 0)
        {
            y -= 4;
            d.Box(xCtrl, y - hCtrl, wCtrl, hCtrl, $"{amp} × {scheme.Amplifiers}", 2.2);
            d.Text(xCtrl, y - hCtrl - 3, "речевое оповещение, 220 В, встроенные АКБ; RS-485", 1.6);
            var ly = y - (hCtrl / 2);
            foreach (var line in scheme.SpeechLines)
            {
                d.Line(xCtrl + wCtrl, ly, xDev0 + 40, ly);
                d.Text(xCtrl + wCtrl + 1, ly + 1, $"ЛО{line.Number}", 1.8);
                d.Box(xDev0 + 40, ly - 4, 70, 8, string.Create(ru, $"{line.Speakers} × {line.Speaker} ({line.Group}), {line.ConsumedW:0.#} Вт"), 1.8);
                if (line.Monitor is { } mon)
                {
                    d.Line(xDev0 + 110, ly, xDev0 + 116, ly);
                    d.Box(xDev0 + 116, ly - 4, 24, 8, mon, 1.6);
                }

                maxX = Math.Max(maxX, xDev0 + 142);
                ly -= 11;
            }

            y = ly - 6;
        }

        if (scheme.Note is { } note)
        {
            d.MText(xCtrl, y - 2, note, 2.0, 200);
            y -= 16;
        }

        Legend(d, xCtrl, y - 4);
        y -= 30;

        var sheet = GostSheet.FitAtScale(xCtrl - 5, y - 5, Math.Max(maxX, xCtrl + 230) + 5, 5, 1) ?? GostSheet.Fit(xCtrl - 5, y - 5, Math.Max(maxX, xCtrl + 230) + 5, 5);
        GostSheet.Draw(doc, sheet, stamp);
        return doc;
    }

    private static void Legend(Drawer d, double x, double top)
    {
        d.Text(x, top, "Условные обозначения", 2.5);
        var items = new (SchemeDeviceKind Kind, string Text)[]
        {
            (SchemeDeviceKind.SmokeDetector, "извещатель пожарный дымовой адресный"),
            (SchemeDeviceKind.HeatDetector, "извещатель пожарный тепловой адресный"),
            (SchemeDeviceKind.ManualCallPoint, "извещатель пожарный ручной"),
            (SchemeDeviceKind.Sounder, "оповещатель звуковой (речевой)"),
            (SchemeDeviceKind.Light, "оповещатель световой «Выход»"),
        };
        var yy = top - 6;
        foreach (var (kind, label) in items)
        {
            d.Device(x, yy - (BoxH / 2), new SchemeDevice("", "", kind));
            d.Text(x + BoxW + 3, yy - 1, label, 2.0);
            yy -= 8;
        }
    }

    private sealed class Drawer(CadDocument doc, Layer lines, Layer devices, Layer text)
    {
        private static XYZ P(double x, double y) => new(x, y, 0);

        public void Line(double ax, double ay, double bx, double by) => doc.Entities.Add(new ACadSharp.Entities.Line(P(ax, ay), P(bx, by)) { Layer = lines });

        public void Text(double x, double y, string value, double h) => doc.Entities.Add(new TextEntity { Value = value, InsertPoint = P(x, y), Height = h, Layer = text });

        public void MText(double x, double y, string value, double h, double width) => doc.Entities.Add(new ACadSharp.Entities.MText { Value = value, InsertPoint = P(x, y), Height = h, RectangleWidth = width, Layer = text });

        public void Box(double x, double y, double w, double h, string label, double textH)
        {
            doc.Entities.Add(new LwPolyline([new XY(x, y), new XY(x + w, y), new XY(x + w, y + h), new XY(x, y + h)]) { IsClosed = true, Layer = devices });
            doc.Entities.Add(new TextEntity { Value = label, InsertPoint = P(x + 1.5, y + (h / 2) - (textH / 2)), Height = textH, Layer = text });
        }

        /// <summary>Условный знак устройства в рамке: ИП дымовой — круг, тепловой — круг с точкой, ИПР — квадрат, звуковой — треугольник, световой — прямоугольник; обозначение — под знаком.</summary>
        public void Device(double x, double y, SchemeDevice dev)
        {
            doc.Entities.Add(new LwPolyline([new XY(x, y), new XY(x + BoxW, y), new XY(x + BoxW, y + BoxH), new XY(x, y + BoxH)]) { IsClosed = true, Layer = devices });
            var (cx, cy) = (x + (BoxW / 2), y + (BoxH / 2));
            switch (dev.Kind)
            {
                case SchemeDeviceKind.SmokeDetector:
                    doc.Entities.Add(new Circle { Center = P(cx, cy), Radius = 2, Layer = devices });
                    break;
                case SchemeDeviceKind.HeatDetector:
                    doc.Entities.Add(new Circle { Center = P(cx, cy), Radius = 2, Layer = devices });
                    doc.Entities.Add(new Circle { Center = P(cx, cy), Radius = 0.5, Layer = devices });
                    break;
                case SchemeDeviceKind.ManualCallPoint:
                    doc.Entities.Add(new LwPolyline([new XY(cx - 2, cy - 2), new XY(cx + 2, cy - 2), new XY(cx + 2, cy + 2), new XY(cx - 2, cy + 2)]) { IsClosed = true, Layer = devices });
                    break;
                case SchemeDeviceKind.Sounder:
                    doc.Entities.Add(new LwPolyline([new XY(cx - 2.2, cy - 2), new XY(cx + 2.2, cy - 2), new XY(cx, cy + 2.2)]) { IsClosed = true, Layer = devices });
                    break;
                case SchemeDeviceKind.Light:
                    doc.Entities.Add(new LwPolyline([new XY(cx - 3, cy - 1.5), new XY(cx + 3, cy - 1.5), new XY(cx + 3, cy + 1.5), new XY(cx - 3, cy + 1.5)]) { IsClosed = true, Layer = devices });
                    break;
                default:
                    doc.Entities.Add(new ACadSharp.Entities.Line(P(cx - 2, cy - 2), P(cx + 2, cy + 2)) { Layer = devices });
                    break;
            }

            if (dev.Id.Length > 0)
            {
                doc.Entities.Add(new TextEntity { Value = dev.Id, InsertPoint = P(x, y - 2.2), Height = 1.4, Layer = text });
            }
        }
    }
}
