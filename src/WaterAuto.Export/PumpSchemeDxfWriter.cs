using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// Данные принципиальной схемы насосной станции: число агрегатов, DN обвязки, патрубки для пожарной техники, подписи.
/// Собирается из расчёта (Application): Export не знает про Calc.
/// </summary>
public sealed record PumpSchemeData(
    int Pumps,
    int SuctionDn,
    int DischargeDn,
    int SupplyDn,
    int TruckInlets,
    int TruckDn,
    string PumpText,
    string JockeyText,
    string SourceText,
    IReadOnlyList<string> Notes);

/// <summary>
/// Принципиальная схема насосной станции пожаротушения на листе A3 с рамкой и основной надписью по форме 3:
/// всасывающий коллектор, агрегаты (рабочие и резервный) с обвязкой, жокей-насос с мембранным баком, напорный коллектор,
/// узел управления, патрубки с соединительными головками для мобильной техники. Условные обозначения — упрощённо по
/// ГОСТ 2.785 и ГОСТ 21.206, их таблица — на листе. Координаты — мм листа (масштаб 1:1, схема без масштаба).
/// </summary>
public static class PumpSchemeDxfWriter
{
    public const string PipeLayer = "ПТ_СХЕМА_ТРУБЫ";
    public const string EquipmentLayer = "ПТ_СХЕМА_ОБОРУД";
    public const string TextLayer = "ПТ_СХЕМА_ТЕКСТ";

    public static void Write(string path, PumpSchemeData data, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(data, stamp), font);

    public static CadDocument Build(PumpSchemeData data, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 5, LineWeightType.W50);
        var equipment = DxfDrawing.AddLayer(doc, EquipmentLayer, 1, LineWeightType.W35);
        var text = DxfDrawing.AddLayer(doc, TextLayer, 7);
        var sheet = new SheetPlacement(SheetFormat.A3, 1, new XY(0, 0));
        GostSheet.Draw(doc, sheet, stamp);
        var d = new Drawer(doc, pipes, equipment, text);

        // Коллекторы: всасывающий снизу, напорный сверху; ветви агрегатов — вертикально между ними.
        const double yS = 100, yD = 190, x0 = 60;
        var branches = Enumerable.Range(0, data.Pumps).Select(k => 105.0 + (k * 70)).ToList();
        var xJ = branches[^1] + 65;
        var xEnd = xJ + 40;
        d.Pipe(25, yS, xJ, yS);
        d.Pipe(x0, yD, xEnd + 60, yD);
        d.Text(27, yS + 3, $"{data.SourceText}, DN{data.SuctionDn}", 2.5);
        d.Arrow(40, yS, 1, 0);

        for (var k = 0; k < branches.Count; k++)
        {
            var x = branches[k];
            d.Pipe(x, yS, x, yD);
            d.Valve(x, 113, vertical: true, sensor: true);
            d.Insert(x, 124);
            d.Gauge(x, 131, "Р", vacuum: true);
            d.Pump(x, 145, 8);
            d.Text(x + 10, 145, $"Н{k + 1}", 3.5);
            d.Insert(x, 162);
            d.Gauge(x, 168, "Р", vacuum: false);
            d.Check(x, 175);
            d.Valve(x, 183, vertical: true, sensor: true);
            d.Text(x + 6, 104, $"DN{data.SuctionDn}", 2.5);
            d.Text(x + 6, 186, $"DN{data.DischargeDn}", 2.5);
        }

        d.Text(26, 160, data.PumpText, 2.5, width: 62);

        // Жокей-насос с мембранным баком — между коллекторами.
        d.Pipe(xJ, yS, xJ, yD);
        d.Valve(xJ, 113, vertical: true, sensor: false);
        d.Pump(xJ, 145, 5);
        d.Text(xJ + 7, 145, "НЖ", 3.5);
        d.Check(xJ, 170);
        d.Valve(xJ, 180, vertical: true, sensor: false);
        d.Pipe(xJ, 158, xJ + 18, 158);
        d.Tank(xJ + 18, 152);
        d.Text(xJ + 7, 128, data.JockeyText, 2.5, width: 40);

        // Узел управления и выход в сеть.
        d.Box(xEnd, yD, "УУ");
        d.Valve(xEnd - 14, yD, vertical: false, sensor: true);
        d.Arrow(xEnd + 50, yD, 1, 0);
        d.Text(xEnd + 18, yD + 3, $"В сеть АУП DN{data.SupplyDn}", 2.5);

        // Патрубки для мобильной пожарной техники — сверху в напорный коллектор.
        for (var k = 0; k < data.TruckInlets; k++)
        {
            var x = x0 + 20 + (k * 22);
            d.Pipe(x, yD, x, 252);
            d.Check(x, 215, down: true);
            d.Valve(x, 228, vertical: true, sensor: false);
            d.Head(x, 252);
        }

        d.Text(x0 + 10, 262, $"От мобильной пожарной техники, ГМ-{data.TruckDn} (п. 6.10.17)", 2.5);
        d.Text(x0 + 20 + (data.TruckInlets * 22), 229, "задвижки опломбированы «открыто»", 2);

        d.Text(150, 280, "Принципиальная схема насосной станции пожаротушения", 5);
        Legend(d, 300, 268);
        var y = 85.0;
        foreach (var note in data.Notes.Select((n, k) => $"{k + 1}. {n}"))
        {
            d.Text(25, y, note, 2.5, width: 330);
            y -= 7;
        }

        return doc;
    }

    // Таблица условных обозначений.
    private static void Legend(Drawer d, double x, double top)
    {
        d.Text(x, top, "Условные обозначения", 3.5);
        var rows = new (string Text, Action<double, double> Draw)[]
        {
            ("Затвор (задвижка) с датчиком положения", (cx, cy) => d.Valve(cx, cy, vertical: false, sensor: true)),
            ("Затвор (задвижка)", (cx, cy) => d.Valve(cx, cy, vertical: false, sensor: false)),
            ("Клапан обратный", (cx, cy) => d.Check(cx, cy, horizontal: true)),
            ("Насос центробежный", (cx, cy) => d.Pump(cx, cy, 3)),
            ("Вставка виброизолирующая", (cx, cy) => d.Insert(cx, cy, horizontal: true)),
            ("Манометр (мановакуумметр)", (cx, cy) => d.Gauge(cx - 3, cy, "Р", vacuum: false)),
            ("Головка соединительная", (cx, cy) => d.Head(cx, cy)),
            ("Трубопровод", (cx, cy) => d.Pipe(cx - 6, cy, cx + 6, cy)),
        };
        var y = top - 8;
        foreach (var (label, draw) in rows)
        {
            draw(x + 8, y);
            d.Text(x + 20, y - 1.2, label, 2.5);
            y -= 7.5;
        }
    }

    private sealed class Drawer(CadDocument doc, Layer pipes, Layer equipment, Layer text)
    {
        private static XYZ P(double x, double y) => new(x, y, 0);

        public void Pipe(double ax, double ay, double bx, double by) => doc.Entities.Add(new Line(P(ax, ay), P(bx, by)) { Layer = pipes });

        private void L(double ax, double ay, double bx, double by) => doc.Entities.Add(new Line(P(ax, ay), P(bx, by)) { Layer = equipment });

        public void Text(double x, double y, string value, double h, double? width = null)
        {
            if (width is { } w)
            {
                doc.Entities.Add(new MText { Value = value, InsertPoint = P(x, y), Height = h, RectangleWidth = w, Layer = text });
                return;
            }

            doc.Entities.Add(new TextEntity { Value = value, InsertPoint = P(x, y), Height = h, Layer = text });
        }

        // Два треугольника вершинами друг к другу; датчик положения — шток с кружком.
        public void Valve(double x, double y, bool vertical, bool sensor)
        {
            const double s = 3;
            if (vertical)
            {
                L(x - s, y - s, x + s, y - s); L(x - s, y - s, x + s, y + s); L(x + s, y - s, x - s, y + s); L(x - s, y + s, x + s, y + s);
            }
            else
            {
                L(x - s, y - s, x - s, y + s); L(x - s, y - s, x + s, y + s); L(x - s, y + s, x + s, y - s); L(x + s, y - s, x + s, y + s);
            }

            if (sensor)
            {
                var (sx, sy) = vertical ? (x + s + 3, y) : (x, y + s + 3);
                L(x, y, sx, sy);
                doc.Entities.Add(new Circle { Center = P(sx + (vertical ? 1.2 : 0), sy + (vertical ? 0 : 1.2)), Radius = 1.2, Layer = equipment });
            }
        }

        // Обратный клапан: треугольник по направлению потока и черта у вершины.
        public void Check(double x, double y, bool down = false, bool horizontal = false)
        {
            const double s = 3;
            if (horizontal)
            {
                L(x - s, y - s, x - s, y + s); L(x - s, y - s, x + s, y); L(x - s, y + s, x + s, y); L(x + s, y - s, x + s, y + s);
                return;
            }

            var dir = down ? -1 : 1;
            L(x - s, y - (dir * s), x + s, y - (dir * s)); L(x - s, y - (dir * s), x, y + (dir * s)); L(x + s, y - (dir * s), x, y + (dir * s)); L(x - s, y + (dir * s), x + s, y + (dir * s));
        }

        // Насос центробежный: окружность и треугольник вершиной к напорному патрубку.
        public void Pump(double x, double y, double r)
        {
            doc.Entities.Add(new Circle { Center = P(x, y), Radius = r, Layer = equipment });
            L(x - (0.6 * r), y - (0.5 * r), x, y + (0.7 * r)); L(x + (0.6 * r), y - (0.5 * r), x, y + (0.7 * r)); L(x - (0.6 * r), y - (0.5 * r), x + (0.6 * r), y - (0.5 * r));
        }

        // Виброизолирующая вставка: прямоугольник с диагоналями.
        public void Insert(double x, double y, bool horizontal = false)
        {
            var (w, h) = horizontal ? (3.0, 2.0) : (2.0, 3.0);
            L(x - w, y - h, x + w, y - h); L(x + w, y - h, x + w, y + h); L(x + w, y + h, x - w, y + h); L(x - w, y + h, x - w, y - h);
            L(x - w, y - h, x + w, y + h); L(x - w, y + h, x + w, y - h);
        }

        // Манометр: отвод от трубы и окружность с буквой.
        public void Gauge(double x, double y, string mark, bool vacuum)
        {
            L(x, y, x - 6, y);
            doc.Entities.Add(new Circle { Center = P(x - 8.5, y), Radius = 2.5, Layer = equipment });
            Text(x - 9.7, y - 1, vacuum ? "МВ" : mark, 1.8);
        }

        // Соединительная головка: полуокружность на конце патрубка.
        public void Head(double x, double y)
        {
            doc.Entities.Add(new Arc { Center = P(x, y), Radius = 3, StartAngle = 0, EndAngle = Math.PI, Layer = equipment });
            L(x - 3, y, x + 3, y);
        }

        // Мембранный бак: прямоугольник со скруглёнными торцами (упрощённо).
        public void Tank(double x, double y)
        {
            L(x, y, x + 10, y); L(x, y + 12, x + 10, y + 12); L(x, y, x, y + 12); L(x + 10, y, x + 10, y + 12);
            L(x, y + 6, x + 10, y + 6);
            Text(x + 12, y + 4, "Бак мембранный", 2);
        }

        // Узел управления: прямоугольник с маркой.
        public void Box(double x, double y, string mark)
        {
            L(x, y - 6, x + 14, y - 6); L(x + 14, y - 6, x + 14, y + 6); L(x + 14, y + 6, x, y + 6); L(x, y + 6, x, y - 6);
            Text(x + 3, y - 1.5, mark, 3.5);
        }

        public void Arrow(double x, double y, double dx, double dy)
        {
            var (nx, ny) = (-dy, dx);
            L(x, y, x - (dx * 4) + (nx * 1.5), y - (dy * 4) + (ny * 1.5));
            L(x, y, x - (dx * 4) - (nx * 1.5), y - (dy * 4) - (ny * 1.5));
        }
    }
}
