using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Оборудование на плане насосной: прямоугольник, мм чертежа; Position — номер позиции спецификации; Front — сторона обслуживания.</summary>
public sealed record PumpRoomEquipment(string Label, double X0, double Y0, double X1, double Y1, int? Position = null, (int X, int Y)? Front = null, double FrontDepth = 0);

/// <summary>Трубопровод на плане насосной: ломаная, подпись (DN), уровень (0 — ближе к стене).</summary>
public sealed record PumpRoomPipe(string Label, IReadOnlyList<PlanPoint> Path);

/// <summary>Данные плана насосной: контур, дверь, оборудование, примечания (принятые проходы и допущения), обвязка, ввод и выпуски.</summary>
public sealed record PumpRoomDrawing(Polygon Room, PlanPoint? Door, IReadOnlyList<PumpRoomEquipment> Equipment, IReadOnlyList<string> Notes)
{
    public IReadOnlyList<PumpRoomPipe> Pipes { get; init; } = [];

    /// <summary>Точки на стенах с подписями (ввод, выпуски).</summary>
    public IReadOnlyList<(PlanPoint At, string Text)> WallMarks { get; init; } = [];
}

/// <summary>
/// План насосной станции на листе с рамкой и основной надписью по форме 3 (формат и масштаб — по габаритам, <see cref="GostSheet.Fit"/>):
/// контур помещения, дверь, оборудование с номерами позиций, зона обслуживания перед шкафом управления,
/// цепочки размеров по граням оборудования до стен, примечания. Координаты — мм чертежа этажа.
/// </summary>
public static class PumpRoomDxfWriter
{
    public const string RoomLayer = "ПТ_НС_КОНТУР";
    public const string EquipmentLayer = "ПТ_НС_ОБОРУД";
    public const string TextLayer = "ПТ_НС_ТЕКСТ";
    public const string PipeLayer = "ПТ_НС_ТРУБЫ";

    public static void Write(string path, PumpRoomDrawing data, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(data, stamp), font);

    public static CadDocument Build(PumpRoomDrawing data, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var room = DxfDrawing.AddLayer(doc, RoomLayer, 7, LineWeightType.W70);
        var equipment = DxfDrawing.AddLayer(doc, EquipmentLayer, 1, LineWeightType.W35);
        var text = DxfDrawing.AddLayer(doc, TextLayer, 7);
        var dims = DxfDrawing.AddLayer(doc, PlanDxfWriter.DimensionLayer, 3);

        var v = data.Room.Vertices;
        var (x0, y0, x1, y1) = (v.Min(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.XMm), v.Max(p => p.YMm));
        // Снизу — место под примечания и экспликацию.
        var sheet = GostSheet.Fit(x0 - 1500, y0 - (0.9 * (y1 - y0)), x1 + 1500, y1 + 1000);
        var s = sheet.Scale;
        var h = 2.5 * s;
        GostSheet.Draw(doc, sheet, stamp);
        XYZ P(double x, double y) => new(x, y, 0);
        void Rect(double a0, double b0, double a1, double b1, Layer layer)
        {
            doc.Entities.Add(new LwPolyline([new XY(a0, b0), new XY(a1, b0), new XY(a1, b1), new XY(a0, b1)]) { IsClosed = true, Layer = layer });
        }

        void Text(double x, double y, string value, double height, double? width = null)
        {
            if (width is { } w)
            {
                doc.Entities.Add(new MText { Value = value, InsertPoint = P(x, y), Height = height, RectangleWidth = w, Layer = text });
            }
            else
            {
                doc.Entities.Add(new TextEntity { Value = value, InsertPoint = P(x, y), Height = height, Layer = text });
            }
        }

        doc.Entities.Add(new LwPolyline([.. v.Select(p => new XY(p.XMm, p.YMm))]) { IsClosed = true, Layer = room });

        // Дверь: проём 900 мм по стене и дуга открывания внутрь.
        if (data.Door is { } d)
        {
            var onBottom = Math.Abs(d.YMm - y0) < 1;
            var onTop = Math.Abs(d.YMm - y1) < 1;
            var into = onBottom ? 1 : onTop ? -1 : 0;
            if (into != 0)
            {
                doc.Entities.Add(new Line(P(d.XMm - 450, d.YMm), P(d.XMm - 450, d.YMm + (into * 900))) { Layer = room });
                doc.Entities.Add(new Arc { Center = P(d.XMm - 450, d.YMm), Radius = 900, StartAngle = into > 0 ? 0 : -Math.PI / 2, EndAngle = into > 0 ? Math.PI / 2 : 0, Layer = room });
            }
            else
            {
                var side = Math.Abs(d.XMm - x0) < 1 ? 1 : -1;
                doc.Entities.Add(new Line(P(d.XMm, d.YMm - 450), P(d.XMm + (side * 900), d.YMm - 450)) { Layer = room });
                doc.Entities.Add(new Arc { Center = P(d.XMm, d.YMm - 450), Radius = 900, StartAngle = side > 0 ? 0 : Math.PI / 2, EndAngle = side > 0 ? Math.PI / 2 : Math.PI, Layer = room });
            }
        }

        foreach (var e in data.Equipment)
        {
            Rect(e.X0, e.Y0, e.X1, e.Y1, equipment);

            // Диагонали — обозначение оборудования в плане; наименование — в экспликации (примечания).
            doc.Entities.Add(new Line(P(e.X0, e.Y0), P(e.X1, e.Y1)) { Layer = equipment });
            if (e.Position is { } n)
            {
                var (cx, cy) = ((e.X0 + e.X1) / 2, (e.Y0 + e.Y1) / 2);
                doc.Entities.Add(new Line(P(cx, cy), P(cx + (2 * h), cy + (2 * h))) { Layer = text });
                doc.Entities.Add(new Line(P(cx + (2 * h), cy + (2 * h)), P(cx + (4 * h), cy + (2 * h))) { Layer = text });
                Text(cx + (2.2 * h), cy + (2.3 * h), n.ToString(System.Globalization.CultureInfo.InvariantCulture), h);
            }

            // Зона обслуживания перед шкафом — тонкий прямоугольник с подписью.
            if (e.Front is { } f && e.FrontDepth > 0)
            {
                var (a0, b0, a1, b1) = f switch
                {
                    (1, 0) => (e.X1, e.Y0, e.X1 + e.FrontDepth, e.Y1),
                    (-1, 0) => (e.X0 - e.FrontDepth, e.Y0, e.X0, e.Y1),
                    (0, 1) => (e.X0, e.Y1, e.X1, e.Y1 + e.FrontDepth),
                    _ => (e.X0, e.Y0 - e.FrontDepth, e.X1, e.Y0),
                };
                Rect(a0, b0, a1, b1, dims);
                Text(a0 + (0.3 * h), b1 - (1.2 * h), "2,0 м — обслуж.", 0.6 * h);
            }
        }

        // Обвязка: трубы — толстые линии, подпись DN над самым длинным участком.
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 5, LineWeightType.W50);
        foreach (var pipe in data.Pipes.Where(p => p.Path.Count >= 2))
        {
            doc.Entities.Add(new LwPolyline([.. pipe.Path.Select(p => new XY(p.XMm, p.YMm))]) { Layer = pipes });
            var (a, b) = pipe.Path.Zip(pipe.Path.Skip(1)).MaxBy(s => Math.Abs(s.Second.XMm - s.First.XMm) + Math.Abs(s.Second.YMm - s.First.YMm));
            var vertical = Math.Abs(b.XMm - a.XMm) < Math.Abs(b.YMm - a.YMm);
            doc.Entities.Add(new TextEntity
            {
                Value = pipe.Label,
                InsertPoint = P(((a.XMm + b.XMm) / 2) + (vertical ? -0.4 * h : 0), ((a.YMm + b.YMm) / 2) + (vertical ? 0 : 0.4 * h)),
                Height = 0.8 * h,
                Rotation = vertical ? Math.PI / 2 : 0,
                Layer = text,
            });
        }

        foreach (var (at, label) in data.WallMarks)
        {
            doc.Entities.Add(new Circle { Center = P(at.XMm, at.YMm), Radius = 0.6 * h, Layer = pipes });
            Text(at.XMm + h, at.YMm - (2 * h), label, 0.8 * h);
        }

        // Цепочки размеров: по X — у нижней стены, по Y — у левой; точки — стены и грани первого (основного) оборудования
        // и узлов управления — привязка к стенам; остальное — по координатам на чертеже.
        var style = new PlanDxfStyle(TextHeightMm: h);
        var main = data.Equipment.Take(1).ToList();
        var xs = main.SelectMany(e => new[] { e.X0, e.X1 }).Append(x0).Append(x1).Select(x => Math.Round(x)).Distinct().Order().ToList();
        var ys = main.SelectMany(e => new[] { e.Y0, e.Y1 }).Append(y0).Append(y1).Select(x => Math.Round(x)).Distinct().Order().ToList();
        PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. xs.Select(x => new PlanPoint(x, y0))], -6 * h), style, dims);
        PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(x0, y0), new PlanPoint(x1, y0)], -10 * h), style, dims);
        PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. ys.Select(y => new PlanPoint(x0, y))], 6 * h), style, dims);
        PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(x0, y0), new PlanPoint(x0, y1)], 10 * h), style, dims);

        var top = sheet.At(0, sheet.Format.HeightMm - 15);
        Text(sheet.At(30, 0).X, top.Y, "План насосной станции пожаротушения", 2 * h);
        var ny = sheet.At(0, 92).Y;
        foreach (var (note, k) in data.Notes.Select((n, k) => (n, k)))
        {
            Text(sheet.At(25, 0).X, ny - (k * 3.6 * h), $"{k + 1}. {note}", 0.9 * h, sheet.Format.WidthMm * s * 0.45);
        }

        return doc;
    }
}
