using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace WaterAuto.Export;

/// <summary>Условные графические обозначения элементов трубопроводов (ГОСТ 21.205-2016; приборы — по ГОСТ 21.208).</summary>
public enum PipeSymbol
{
    /// <summary>Клапан запорный проходной (табл. 6, поз. 1а).</summary>
    ShutOff,

    /// <summary>Задвижка (табл. 6, поз. 10).</summary>
    Gate,

    /// <summary>Затвор дисковый (табл. 6, поз. 12).</summary>
    Butterfly,

    /// <summary>Кран шаровой (табл. 6, поз. 16).</summary>
    Ball,

    /// <summary>Клапан обратный проходной (табл. 6, поз. 5а).</summary>
    Check,

    /// <summary>Фильтр (табл. 1, поз. 1).</summary>
    Filter,

    /// <summary>Воздухоотводчик автоматический — знак программы (круг с буквой В), в легенде листа.</summary>
    Vent,

    /// <summary>Манометр показывающий — прибор PI по ГОСТ 21.208 на отборном устройстве (табл. 1, поз. 9).</summary>
    Gauge,

    /// <summary>Сигнализатор давления — прибор PS по ГОСТ 21.208.</summary>
    PressureSwitch,

    /// <summary>Насос нерегулируемый (табл. 2, поз. 3а): треугольник — в сторону нагнетания.</summary>
    Pump,
}

/// <summary>
/// Рисование знаков ГОСТ 21.205 в локальной системе: центр, направление трубы (рад, для обратного клапана и насоса — по
/// ходу среды), полудлина знака r. Размеры знаков — без масштаба (п. 3.2 ГОСТ 21.205), r задаёт лист по высоте шрифта.
/// </summary>
public static class GostSymbols
{
    public static PipeSymbol FromKind(string kind) => kind switch
    {
        "gate" => PipeSymbol.Gate,
        "butterfly" => PipeSymbol.Butterfly,
        "check" => PipeSymbol.Check,
        "ball" => PipeSymbol.Ball,
        "shutoff" => PipeSymbol.ShutOff,
        "filter" => PipeSymbol.Filter,
        "vent" => PipeSymbol.Vent,
        "gauge" => PipeSymbol.Gauge,
        "switch" => PipeSymbol.PressureSwitch,
        "pump" => PipeSymbol.Pump,
        _ => PipeSymbol.ShutOff,
    };

    /// <summary>Наименование для легенды листа с ссылкой на стандарт.</summary>
    public static string Name(PipeSymbol symbol) => symbol switch
    {
        PipeSymbol.ShutOff => "клапан запорный (ГОСТ 21.205, табл. 6)",
        PipeSymbol.Gate => "задвижка (ГОСТ 21.205, табл. 6)",
        PipeSymbol.Butterfly => "затвор дисковый поворотный (ГОСТ 21.205, табл. 6)",
        PipeSymbol.Ball => "кран шаровой (ГОСТ 21.205, табл. 6)",
        PipeSymbol.Check => "клапан обратный; среда — от белого треугольника к чёрному (ГОСТ 21.205, табл. 6)",
        PipeSymbol.Filter => "фильтр (ГОСТ 21.205, табл. 1)",
        PipeSymbol.Vent => "воздухоотводчик автоматический",
        PipeSymbol.Gauge => "манометр показывающий на отборном устройстве (ГОСТ 21.208)",
        PipeSymbol.PressureSwitch => "сигнализатор давления (ГОСТ 21.208)",
        PipeSymbol.Pump => "насос; треугольник — сторона нагнетания (ГОСТ 21.205, табл. 2)",
        _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
    };

    public static void Draw(CadDocument doc, Layer layer, Layer textLayer, XY center, double angle, double r, PipeSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(textLayer);
        var (c, s) = (Math.Cos(angle), Math.Sin(angle));
        XY Q(double a, double b) => new(center.X + (a * c) - (b * s), center.Y + (a * s) + (b * c));
        XYZ P(double a, double b) => new(Q(a, b).X, Q(a, b).Y, 0);
        void L(double a0, double b0, double a1, double b1) => doc.Entities.Add(new Line(P(a0, b0), P(a1, b1)) { Layer = layer });
        void Poly(params (double A, double B)[] pts) => doc.Entities.Add(new LwPolyline([.. pts.Select(p => Q(p.A, p.B))]) { IsClosed = true, Layer = layer });
        void Fill((double A, double B) p1, (double A, double B) p2, (double A, double B) p3) =>
            doc.Entities.Add(new Solid { FirstCorner = P(p1.A, p1.B), SecondCorner = P(p2.A, p2.B), ThirdCorner = P(p3.A, p3.B), FourthCorner = P(p3.A, p3.B), Layer = layer });
        var h = 0.6 * r;

        // Два треугольника вершинами к центру (основа знаков арматуры табл. 6).
        void Bowtie()
        {
            Poly((-r, -h), (-r, h), (0, 0));
            Poly((r, -h), (r, h), (0, 0));
        }

        switch (symbol)
        {
            case PipeSymbol.ShutOff:
                Bowtie();
                break;
            case PipeSymbol.Gate:
                Bowtie();
                L(0, -h, 0, h);
                break;
            case PipeSymbol.Check:
                Bowtie();
                Fill((r, -h), (r, h), (0, 0));
                break;
            case PipeSymbol.Ball:
                Poly((-r, -h), (-r, h), (-0.3 * r, 0.2 * h));
                Poly((r, -h), (r, h), (0.3 * r, 0.2 * h));
                doc.Entities.Add(new Circle { Center = P(0, 0), Radius = 0.3 * r, Layer = layer });
                break;
            case PipeSymbol.Butterfly:
                Poly((-r, -h), (r, -h), (r, h), (-r, h));
                L(-0.55 * r, 0.85 * h, 0.55 * r, -0.85 * h);
                doc.Entities.Add(new Circle { Center = P(0, 0), Radius = 0.08 * r, Layer = layer });
                break;
            case PipeSymbol.Filter:
                Poly((-r, 0), (0, r), (r, 0), (0, -r));
                L(0, 0.8 * r, 0, 0.45 * r);
                L(0, 0.2 * r, 0, -0.2 * r);
                L(0, -0.45 * r, 0, -0.8 * r);
                break;
            case PipeSymbol.Vent:
                doc.Entities.Add(new Circle { Center = P(0, 0), Radius = r, Layer = layer });
                doc.Entities.Add(new TextEntity { Value = "В", InsertPoint = P(-0.35 * r, -0.45 * r), Height = 0.9 * r, Layer = textLayer });
                break;
            case PipeSymbol.Gauge:
            case PipeSymbol.PressureSwitch:
                // Отборное устройство на трубе, отвод вверх, круг прибора с буквенным обозначением (ГОСТ 21.208).
                Poly((-0.2 * r, -0.2 * r), (0.2 * r, -0.2 * r), (0.2 * r, 0.2 * r), (-0.2 * r, 0.2 * r));
                Fill((-0.2 * r, -0.2 * r), (0.2 * r, -0.2 * r), (0.2 * r, 0.2 * r));
                Fill((-0.2 * r, -0.2 * r), (0.2 * r, 0.2 * r), (-0.2 * r, 0.2 * r));
                L(0, 0.2 * r, 0, 1.6 * r);
                doc.Entities.Add(new Circle { Center = P(0, 2.6 * r), Radius = r, Layer = layer });
                doc.Entities.Add(new TextEntity { Value = symbol == PipeSymbol.Gauge ? "PI" : "PS", InsertPoint = new XYZ(Q(0, 2.6 * r).X - (0.55 * r), Q(0, 2.6 * r).Y - (0.35 * r), 0), Height = 0.7 * r, Layer = textLayer });
                break;
            case PipeSymbol.Pump:
                doc.Entities.Add(new Circle { Center = P(0, 0), Radius = r, Layer = layer });
                Fill((r, 0), (0.45 * r, 0.32 * r), (0.45 * r, -0.32 * r));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(symbol));
        }
    }

    /// <summary>Легенда: строки «знак — наименование» сверху вниз от точки (x, y); шаг строки 2,4·h, h — высота шрифта листа.</summary>
    public static double Legend(CadDocument doc, Layer layer, Layer textLayer, double x, double y, double h, IEnumerable<PipeSymbol> symbols, string title = "Условные обозначения")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(symbols);
        doc.Entities.Add(new TextEntity { Value = title, InsertPoint = new XYZ(x, y, 0), Height = h, Layer = textLayer });
        var cy = y - (2.4 * h);
        foreach (var symbol in symbols.Distinct())
        {
            var tall = symbol is PipeSymbol.Gauge or PipeSymbol.PressureSwitch;
            var symY = tall ? cy - (1.5 * h) : cy;
            doc.Entities.Add(new Line(new XYZ(x, symY, 0), new XYZ(x + (4 * h), symY, 0)) { Layer = layer });
            Draw(doc, layer, textLayer, new XY(x + (2 * h), symY), 0, 0.6 * h, symbol);
            doc.Entities.Add(new TextEntity { Value = Name(symbol), InsertPoint = new XYZ(x + (5 * h), symY - (0.35 * h), 0), Height = 0.7 * h, Layer = textLayer });
            cy -= (tall ? 4.2 : 2.4) * h;
        }

        return cy;
    }
}
