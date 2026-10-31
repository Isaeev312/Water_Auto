using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// Данные принципиальной схемы насосной станции: число агрегатов и вводов, DN обвязки, патрубки для пожарной техники, подписи.
/// Собирается из расчёта (Application): Export не знает про Calc. Positions — номер позиции спецификации по роли элемента
/// (pump, valveS, teeS…); у элемента без номера выноска не ставится. Outlets — выходы гребёнки к сети секций (подпись точки
/// ввода на плане); по одному узлу управления на выход. Inlets — число входных всасывающих вводов (п. 6.10.27): каждый
/// рисуется отдельной линией со своей задвижкой; по умолчанию 1. Marks — обозначения устройств по роли из перечня сигналов АПС
/// (ЗД1, УУ1…) в порядке устройств: вводы, агрегаты, у секции — затвор до и после узла управления; пишутся у устройства.
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
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, int>? Positions = null,
    IReadOnlyList<string>? Outlets = null,
    int Inlets = 1,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Marks = null);

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

        // Коллекторы: всасывающий снизу, напорный сверху; ветви агрегатов — вертикально между ними; торцы коллекторов — заглушки.
        const double yS = 100, yD = 190, x0 = 60;
        // Шаг ветвей агрегатов — по их числу: схема строится для любой подобранной установки (2, 3, 4… агрегата).
        var spacing = data.Pumps > 1 ? Math.Min(70, 120.0 / (data.Pumps - 1)) : 70;
        var branches = Enumerable.Range(0, data.Pumps).Select(k => 105.0 + (k * spacing)).ToList();
        var xJ = branches[^1] + 65;
        var xEnd = xJ + 40;
        void Pos(double x, double y, string role, int dir = 1)
        {
            if (data.Positions?.TryGetValue(role, out var n) == true)
            {
                d.Leader(x, y, n.ToString(System.Globalization.CultureInfo.InvariantCulture), dir);
            }
        }

        // Обозначение устройства из перечня сигналов АПС (index — номер устройства роли с нуля).
        void Mark(double x, double y, string role, int index)
        {
            if (data.Marks?.TryGetValue(role, out var list) == true && index < list.Count)
            {
                d.Text(x, y, list[index], 2.5);
            }
        }

        string BoxMark(string role, int index, string fallback) =>
            data.Marks?.TryGetValue(role, out var list) == true && index < list.Count ? list[index] : fallback;

        // Вводы (п. 6.10.27): всасывающий коллектор; каждый ввод — отдельная линия со своей задвижкой до коллектора.
        d.Pipe(25, yS, xJ + 8, yS);
        d.Cap(xJ + 8, yS, horizontal: true);
        Pos(xJ + 8, yS, "capS");
        d.Pipe(x0, yD, xEnd + (Math.Max(1, data.Outlets?.Count ?? 1) - 1) * 30, yD);
        d.Cap(x0, yD, horizontal: true, left: true);
        Pos(x0, yD, "capD", -1);
        d.Text(27, yS + 3, $"{data.SourceText}, DN{data.SuctionDn}", 2.5);
        d.Arrow(40, yS, 1, 0);
        d.Valve(85, yS, vertical: false, sensor: true);
        Pos(85, yS, "inletValve", -1);
        Mark(80, yS - 7, "inletValve", 0);
        for (var k = 1; k < Math.Max(1, data.Inlets); k++)
        {
            // Второй и следующие вводы — отдельными ветвями слева; задвижка — на ветви до общего коллектора.
            var yIn = yS + (12 * k);
            d.Pipe(25, yIn, 95, yIn);
            d.Pipe(95, yIn, 95, yS);
            d.Arrow(34, yIn, 1, 0);
            d.Valve(45, yIn, vertical: false, sensor: true);
            d.Text(27, yIn + 3, $"Ввод {k + 1}", 2.5);
            Pos(45, yIn, "inletValve");
            Mark(40, yIn - 6, "inletValve", k);
        }

        for (var k = 0; k < branches.Count; k++)
        {
            var x = branches[k];
            d.Pipe(x, yS, x, yD);
            d.Valve(x, 111, vertical: true, sensor: true);
            d.Insert(x, 120);
            d.Gauge(x, 126, "Р", vacuum: true);
            d.Reducer(x, 133, wideBelow: true);
            d.Pump(x, 145, 8);
            d.Text(x + 10, 145, $"Н{k + 1}", 3.5);
            d.Reducer(x, 157, wideBelow: false);
            d.Insert(x, 163);
            d.Gauge(x, 168, "Р", vacuum: false);
            d.Check(x, 175);
            d.Valve(x, 183, vertical: true, sensor: true);
            Mark(x - 13, 112, "valveS", k);
            Mark(x - 13, 186, "valveD", k);
            d.Text(x + 6, 104, $"DN{data.SuctionDn}", 2.5);
            d.Text(x - 16, 182, $"DN{data.DischargeDn}", 2.5);
            Pos(x - 8, 145, "pump", -1);
            if (k == 0)
            {
                // Номера позиций — у первого агрегата; второй одинаков.
                Pos(x, yS, "teeS", -1);
                Pos(x + 3, 111, "valveS");
                Pos(x + 2, 120, "insertS");
                Pos(x - 11, 126, "vacuum", -1);
                Pos(x + 1.5, 133, "reducerS");
                Pos(x + 1.5, 157, "reducerD");
                Pos(x + 2, 163, "insertD");
                Pos(x - 11, 168, "gauge", -1);
                Pos(x - 6, 168, "cock", -1);
                Pos(x + 3, 175, "checkD");
                Pos(x + 3, 181, "valveD");
                Pos(x, yD, "teeD", -1);
            }
        }

        d.Text(26, 160, data.PumpText, 2.5, width: 62);

        // Шкаф управления насосами — у агрегатов.
        d.Box(branches[0] + 22, 140, BoxMark("cabinet", 0, "ШУ"));
        Pos(branches[0] + 36, 140, "cabinet");

        // Жокей-насос с мембранным баком — между коллекторами.
        d.Pipe(xJ, yS, xJ, yD);
        d.Valve(xJ, 113, vertical: true, sensor: false);
        d.Pump(xJ, 145, 5);
        d.Text(xJ + 7, 145, "НЖ", 3.5);
        Pos(xJ - 5, 145, "jockey", -1);
        d.Check(xJ, 170);
        d.Valve(xJ, 180, vertical: true, sensor: false);
        d.Pipe(xJ, 158, xJ + 18, 158);
        d.Tank(xJ + 18, 152);
        d.Text(xJ + 7, 128, data.JockeyText, 2.5, width: 40);

        // Напорный коллектор — гребёнка: по ветви на секцию с затворами до и после узла управления и манометрами;
        // выход — к точке ввода сети секции на плане.
        var outlets = data.Outlets is { Count: > 0 } o ? o : ["сеть АУП"];
        for (var k = 0; k < outlets.Count; k++)
        {
            var x = xEnd + (k * 30);
            d.Pipe(x, yD, x, 250);
            d.Valve(x, 199, vertical: true, sensor: true);
            d.Gauge(x, 206, "Р", vacuum: false);
            d.Box(x - 7, 216, BoxMark("cu", k, "УУ"));
            Mark(x - 14, 200, "cuValve", 2 * k);
            Mark(x - 14, 234, "cuValve", (2 * k) + 1);
            d.Valve(x, 233, vertical: true, sensor: true);
            d.Gauge(x, 239, "Р", vacuum: false);
            d.Arrow(x, 250, 0, 1);
            d.Text(x - 3, 253, $"Секция {k + 1}", 2.5);
            d.Text(x - 3, 257.5, $"→ {outlets[k]}", 2.5);
            d.Text(x + 2, 245, $"DN{data.SupplyDn}", 2);
            if (k == 0)
            {
                Pos(x + 7, 216, "cu");
                Pos(x + 3, 233, "cuValve");
                Pos(x - 11, 239, "cuGauge", -1);
            }

            if (k > 0)
            {
                Pos(x, yD, "teeCU");
            }
        }

        // Патрубки для мобильной пожарной техники — сверху в напорный коллектор; у фасада — отвод и головка.
        for (var k = 0; k < data.TruckInlets; k++)
        {
            var x = x0 + 20 + (k * 22);
            d.Pipe(x, yD, x, 244);
            d.Pipe(x, 244, x - 8, 244);
            d.Pipe(x - 8, 244, x - 8, 252);
            d.Check(x, 210, down: true);
            d.Valve(x, 225, vertical: true, sensor: false);
            d.Head(x - 8, 252);
            if (k == data.TruckInlets - 1)
            {
                Pos(x + 6, yD, "teeT");
                Pos(x + 3, 210, "checkT");
                Pos(x + 3, 225, "valveT");
                Pos(x, 244, "elbowT");
                Pos(x - 5, 253, "head");
            }
        }

        d.Text(x0 + 10, 262, $"От мобильной пожарной техники, ГМ-{data.TruckDn} (п. 6.10.17)", 2.5);
        d.Text(x0 + 30 + (data.TruckInlets * 22), 222, "задвижки опломбированы «открыто»", 2);

        d.Text(150, 280, "Принципиальная схема насосной станции пожаротушения", 5);
        Legend(d, 262, 104);
        var y = 92.0;
        foreach (var note in data.Notes.Select((n, k) => $"{k + 1}. {n}"))
        {
            d.Text(25, y, note, 2.5, width: 200);
            y -= 9.5;
        }

        return doc;
    }

    // Таблица условных обозначений — над основной надписью, в два столбца.
    private static void Legend(Drawer d, double x, double top)
    {
        d.Text(x, top, "Условные обозначения", 3);
        var rows = new (string Text, Action<double, double> Draw)[]
        {
            ("Затвор (задвижка) с датчиком положения", (cx, cy) => d.Valve(cx, cy, vertical: false, sensor: false)),
            ("Клапан обратный", (cx, cy) => d.Check(cx, cy, horizontal: true)),
            ("Насос центробежный", (cx, cy) => d.Pump(cx, cy, 3)),
            ("Вставка виброизолирующая", (cx, cy) => d.Insert(cx, cy, horizontal: true)),
            ("Манометр", (cx, cy) => d.Gauge(cx + 4, cy, "Р", vacuum: false)),
            ("Переход", (cx, cy) => d.Reducer(cx, cy, wideBelow: true)),
            ("Головка соединительная", (cx, cy) => d.Head(cx, cy)),
            ("Трубопровод", (cx, cy) => d.Pipe(cx - 5, cy, cx + 5, cy)),
        };
        for (var k = 0; k < rows.Length; k++)
        {
            var (cx, cy) = (x + 6 + ((k / 4) * 78), top - 8 - ((k % 4) * 8));
            rows[k].Draw(cx, cy);
            d.Text(cx + 9, cy - 1, rows[k].Text, 2.2);
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

        // Выноска с полкой и номером позиции над полкой.
        public void Leader(double x, double y, string number, int dir)
        {
            var (kx, ky) = (x + (dir * 5), y + 5);
            var end = kx + (dir * 5);
            L(x, y, kx, ky);
            L(kx, ky, end, ky);
            Text(Math.Min(kx, end) + 1, ky + 0.8, number, 2.5);
        }

        // Переход: трапеция по оси вертикальной трубы, широкая сторона — к большему DN.
        public void Reducer(double x, double y, bool wideBelow)
        {
            var (bottom, top) = wideBelow ? (2.5, 1.5) : (1.5, 2.5);
            L(x - bottom, y - 2, x + bottom, y - 2); L(x + bottom, y - 2, x + top, y + 2); L(x + top, y + 2, x - top, y + 2); L(x - top, y + 2, x - bottom, y - 2);
        }

        // Заглушка: дуга на торце трубы.
        public void Cap(double x, double y, bool horizontal, bool left = false)
        {
            var start = horizontal ? (left ? Math.PI / 2 : -Math.PI / 2) : 0;
            doc.Entities.Add(new Arc { Center = P(x, y), Radius = 2, StartAngle = start, EndAngle = start + Math.PI, Layer = equipment });
        }

        public void Arrow(double x, double y, double dx, double dy)
        {
            var (nx, ny) = (-dy, dx);
            L(x, y, x - (dx * 4) + (nx * 1.5), y - (dy * 4) + (ny * 1.5));
            L(x, y, x - (dx * 4) - (nx * 1.5), y - (dy * 4) - (ny * 1.5));
        }
    }
}
