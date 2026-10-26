using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Оборудование на плане насосной: прямоугольник, мм чертежа; Position — номер позиции спецификации; Front — сторона обслуживания.</summary>
public sealed record PumpRoomEquipment(string Label, double X0, double Y0, double X1, double Y1, int? Position = null, (int X, int Y)? Front = null, double FrontDepth = 0)
{
    /// <summary>Марки насосов в составе (Н1, Н2…): рисуются кругами вдоль длинной стороны, между ними — коллекторы.</summary>
    public IReadOnlyList<string> Pumps { get; init; } = [];
}

/// <summary>Трубопровод на плане насосной: ломаная, подпись (DN), уровень (0 — ближе к стене).</summary>
public sealed record PumpRoomPipe(string Label, IReadOnlyList<PlanPoint> Path);

/// <summary>Данные плана насосной: контур, дверь, оборудование, примечания (принятые проходы и допущения), обвязка, ввод и выпуски.</summary>
public sealed record PumpRoomDrawing(Polygon Room, PlanPoint? Door, IReadOnlyList<PumpRoomEquipment> Equipment, IReadOnlyList<string> Notes)
{
    public IReadOnlyList<PumpRoomPipe> Pipes { get; init; } = [];

    /// <summary>Точки на стенах с подписями (ввод, выпуски).</summary>
    public IReadOnlyList<(PlanPoint At, string Text)> WallMarks { get; init; } = [];

    /// <summary>Лист: true — обвязка (трубы, арматура), false — архитектура и оборудование (стены, фундамент, приямок, гильзы, размеры).</summary>
    public bool Piping { get; init; }

    /// <summary>Толщина стен, мм (стены — снаружи контура).</summary>
    public double WallThicknessMm { get; init; }

    public (double X0, double Y0, double X1, double Y1)? Pit { get; init; }

    public string PitLabel { get; init; } = "";

    public (double X0, double Y0, double X1, double Y1)? Foundation { get; init; }

    /// <summary>Гильзы в стенах: точка прохода трубы, наружный диаметр гильзы, мм, подпись.</summary>
    public IReadOnlyList<(PlanPoint At, double DiameterMm, string Label)> Sleeves { get; init; } = [];

    /// <summary>Арматура на трубах: точка, направление трубы (рад), вид (gate, check, vent), номер позиции.</summary>
    public IReadOnlyList<(PlanPoint At, double Angle, string Kind, int? Position)> Valves { get; init; } = [];
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
        // Масштаб 1:50 (насосные — крупно, чтобы читались арматура и подписи), формат — наименьший, где помещаются план
        // и 85 мм под примечания; не помещается и на А1 — 1:100.
        var sheet = new[] { 50.0, 100, 200 }
            .Select(sc => GostSheet.FitAtScale(x0 - 3000, y0 - 2500 - (85 * sc), x1 + 3000, y1 + 1500, sc))
            .FirstOrDefault(f => f is not null) ?? GostSheet.Fit(x0 - 3000, y0 - 20000, x1 + 3000, y1 + 1500);
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

        // Стены: наружный контур на толщину стены; на листе обвязки — без штриховки, тонко.
        if (data.WallThicknessMm > 0)
        {
            var t = data.WallThicknessMm;
            Rect(x0 - t, y0 - t, x1 + t, y1 + t, room);
            if (!data.Piping)
            {
                var hatch = DxfDrawing.AddLayer(doc, "ПТ_НС_СТЕНЫ", 8);
                for (var k = -(y1 - y0 + (2 * t)); k < x1 - x0 + (2 * t); k += 4 * h)
                {
                    // Штриховка стен под 45° — короткие отрезки в пределах толщины на каждой стороне.
                    foreach (var (a0, b0, a1, b1) in new[] { (x0 - t, y0 - t, x1 + t, y0), (x0 - t, y1, x1 + t, y1 + t), (x0 - t, y0, x0, y1), (x1, y0, x1 + t, y1) })
                    {
                        var (sx, sy) = (a0 + k, b0);
                        var lo = Math.Max(0, Math.Max(a0 - sx, b0 - sy));
                        var hi = Math.Min(a1 - sx, b1 - sy);
                        if (hi > lo)
                        {
                            doc.Entities.Add(new Line(P(sx + lo, sy + lo), P(sx + hi, sy + hi)) { Layer = hatch });
                        }
                    }
                }
            }
        }

        if (!data.Piping && data.Foundation is { } fd)
        {
            Rect(fd.X0, fd.Y0, fd.X1, fd.Y1, dims);
            Text(fd.X0 + (0.3 * h), fd.Y0 + (0.3 * h), "фундамент", 0.6 * h);
        }

        if (data.Pit is { } pit)
        {
            Rect(pit.X0, pit.Y0, pit.X1, pit.Y1, room);
            doc.Entities.Add(new Line(P(pit.X0, pit.Y0), P(pit.X1, pit.Y1)) { Layer = room });
            doc.Entities.Add(new Line(P(pit.X0, pit.Y1), P(pit.X1, pit.Y0)) { Layer = room });
            if (!data.Piping)
            {
                var (cx, cy) = ((pit.X0 + pit.X1) / 2, (pit.Y0 + pit.Y1) / 2);
                var dirX = cx < (x0 + x1) / 2 ? 1 : -1;
                Text(cx + (dirX > 0 ? 0.6 * (pit.X1 - pit.X0) : -12 * h), cy + (1.5 * h), data.PitLabel, 0.7 * h, 12 * h);
            }
        }

        foreach (var (at, dia, label) in data.Sleeves.Where(_ => !data.Piping))
        {
            var t = Math.Max(data.WallThicknessMm, 100);
            var vertical = Math.Min(Math.Abs(at.XMm - x0), Math.Abs(at.XMm - x1)) < Math.Min(Math.Abs(at.YMm - y0), Math.Abs(at.YMm - y1));
            var outward = vertical ? (Math.Abs(at.XMm - x0) < Math.Abs(at.XMm - x1) ? -1 : 1) : (Math.Abs(at.YMm - y0) < Math.Abs(at.YMm - y1) ? -1 : 1);
            if (vertical)
            {
                Rect(Math.Min(at.XMm, at.XMm + (outward * t)), at.YMm - (dia / 2), Math.Max(at.XMm, at.XMm + (outward * t)), at.YMm + (dia / 2), equipment);
            }
            else
            {
                Rect(at.XMm - (dia / 2), Math.Min(at.YMm, at.YMm + (outward * t)), at.XMm + (dia / 2), Math.Max(at.YMm, at.YMm + (outward * t)), equipment);
            }

            Text(at.XMm + (vertical ? outward * (t + h) - (outward < 0 ? 6 * h : 0) : h), at.YMm + (vertical ? h : outward * (t + h)), label, 0.6 * h);
        }

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

        var pipeLayerForSets = DxfDrawing.AddLayer(doc, PipeLayer, 5, LineWeightType.W50);
        void Valve(XYZ at, double angle, string kind, double r)
        {
            var (c, sn) = (Math.Cos(angle), Math.Sin(angle));
            XY Q(double a, double b) => new(at.X + (a * c) - (b * sn), at.Y + (a * sn) + (b * c));
            if (kind == "check")
            {
                doc.Entities.Add(new LwPolyline([Q(-r, -r), Q(r, 0), Q(-r, r)]) { IsClosed = true, Layer = equipment });
                doc.Entities.Add(new Line(new XYZ(Q(r, -r).X, Q(r, -r).Y, 0), new XYZ(Q(r, r).X, Q(r, r).Y, 0)) { Layer = equipment });
            }
            else
            {
                doc.Entities.Add(new LwPolyline([Q(-r, -r), Q(r, r), Q(r, -r), Q(-r, r)]) { IsClosed = true, Layer = equipment });
            }
        }

        foreach (var e in data.Equipment)
        {
            // Насосы в составе установки: круги вдоль длинной стороны, коллекторы — линии по обе стороны от них.
            if (e.Pumps.Count > 0)
            {
                var alongX = e.X1 - e.X0 >= e.Y1 - e.Y0;
                var (len, wid) = alongX ? (e.X1 - e.X0, e.Y1 - e.Y0) : (e.Y1 - e.Y0, e.X1 - e.X0);
                var r = Math.Min(len / (e.Pumps.Count * 2.6), wid * 0.22);
                for (var k = 0; k < e.Pumps.Count; k++)
                {
                    var a = (len * (k + 0.5) / e.Pumps.Count) + (alongX ? e.X0 : e.Y0);
                    var c = alongX ? P(a, (e.Y0 + e.Y1) / 2) : P((e.X0 + e.X1) / 2, a);
                    doc.Entities.Add(new Circle { Center = c, Radius = r, Layer = equipment });
                    doc.Entities.Add(new Circle { Center = c, Radius = r * 0.35, Layer = equipment });
                    Text(c.X + r + (0.3 * h), c.Y + (0.3 * h), e.Pumps[k], 0.8 * h);

                    // Обвязка агрегата (у установки с несколькими насосами): ветка к всасывающему коллектору с затвором,
                    // к напорному — с обратным клапаном и затвором (обратный клапан — между насосом и запорной арматурой).
                    if (e.Pumps.Count > 1)
                    {
                        var off = (wid / 2) - (wid * 0.12);
                        foreach (var side in new[] { -1, 1 })
                        {
                            var (from, to) = alongX
                                ? (P(c.X, c.Y + (side * r)), P(c.X, ((e.Y0 + e.Y1) / 2) + (side * off)))
                                : (P(c.X + (side * r), c.Y), P(((e.X0 + e.X1) / 2) + (side * off), c.Y));
                            doc.Entities.Add(new Line(from, to) { Layer = pipeLayerForSets });
                            var ang = Math.Atan2(to.Y - from.Y, to.X - from.X);
                            var seg = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
                            XYZ At(double frac) => P(from.X + ((to.X - from.X) * frac), from.Y + ((to.Y - from.Y) * frac));
                            var vr = Math.Min(seg * 0.18, 0.5 * h);
                            if (side < 0)
                            {
                                Valve(At(0.6), ang, "gate", vr);
                            }
                            else
                            {
                                Valve(At(0.35), ang, "check", vr);
                                Valve(At(0.75), ang, "gate", vr);
                            }
                        }
                    }
                }

                foreach (var side in new[] { -1, 1 })
                {
                    var off = (wid / 2) - (wid * 0.12);
                    if (alongX)
                    {
                        doc.Entities.Add(new Line(P(e.X0 + (0.05 * len), ((e.Y0 + e.Y1) / 2) + (side * off)), P(e.X1 - (0.05 * len), ((e.Y0 + e.Y1) / 2) + (side * off))) { Layer = equipment });
                    }
                    else
                    {
                        doc.Entities.Add(new Line(P(((e.X0 + e.X1) / 2) + (side * off), e.Y0 + (0.05 * len)), P(((e.X0 + e.X1) / 2) + (side * off), e.Y1 - (0.05 * len))) { Layer = equipment });
                    }
                }
            }

            if (data.Piping)
            {
                // На листе обвязки оборудование — тонким контуром с номером позиции.
                Rect(e.X0, e.Y0, e.X1, e.Y1, dims);
                if (e.Position is { } pn)
                {
                    Text(e.X0 + (0.3 * h), e.Y0 + (0.3 * h), pn.ToString(System.Globalization.CultureInfo.InvariantCulture), 0.8 * h);
                }

                continue;
            }

            Rect(e.X0, e.Y0, e.X1, e.Y1, equipment);

            // Оборудование без насосов — с диагональю; наименование — в экспликации (примечания).
            if (e.Pumps.Count == 0)
            {
                doc.Entities.Add(new Line(P(e.X0, e.Y0), P(e.X1, e.Y1)) { Layer = equipment });
            }
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
        var labelled = new List<XY>();
        foreach (var pipe in data.Pipes.Where(p => p.Path.Count >= 2 && data.Piping))
        {
            doc.Entities.Add(new LwPolyline([.. pipe.Path.Select(p => new XY(p.XMm, p.YMm))]) { Layer = pipes });
            var (a, b) = pipe.Path.Zip(pipe.Path.Skip(1)).MaxBy(s => Math.Abs(s.Second.XMm - s.First.XMm) + Math.Abs(s.Second.YMm - s.First.YMm));
            var vertical = Math.Abs(b.XMm - a.XMm) < Math.Abs(b.YMm - a.YMm);
            var mid = new XY((a.XMm + b.XMm) / 2, (a.YMm + b.YMm) / 2);

            // Подпись DN не ставится, если рядом уже есть подпись (трубы одного яруса идут рядом).
            if (labelled.Any(l => Math.Abs(l.X - mid.X) < 6 * h && Math.Abs(l.Y - mid.Y) < 2 * h))
            {
                continue;
            }

            labelled.Add(mid);
            doc.Entities.Add(new TextEntity
            {
                Value = pipe.Label,
                InsertPoint = P(((a.XMm + b.XMm) / 2) + (vertical ? -0.4 * h : 0), ((a.YMm + b.YMm) / 2) + (vertical ? 0 : 0.4 * h)),
                Height = 0.8 * h,
                Rotation = vertical ? Math.PI / 2 : 0,
                Layer = text,
            });
        }

        // Отметки на стенах (ввод, выпуски, ГМ) — подпись снаружи помещения, за ближайшей стеной.
        foreach (var (at, label) in data.WallMarks)
        {
            doc.Entities.Add(new Circle { Center = P(at.XMm, at.YMm), Radius = 0.6 * h, Layer = pipes });
            var width = 0.8 * h * 0.75 * label.Length;
            var (dl, dr, db, dt) = (at.XMm - x0, x1 - at.XMm, at.YMm - y0, y1 - at.YMm);
            var m = new[] { dl, dr, db, dt }.Min();
            var (tx, ty) = m == dl ? (at.XMm - width - h, at.YMm - (0.4 * h))
                : m == dr ? (at.XMm + h, at.YMm - (0.4 * h))
                : m == db ? (at.XMm - (width / 2), at.YMm - (2 * h))
                : (at.XMm - (width / 2), at.YMm + h);
            Text(tx, ty, label, 0.8 * h);
        }

        // Арматура: затвор (задвижка) — два треугольника, обратный клапан — треугольник с чертой, воздухоотводчик — кружок с «В».
        foreach (var (at, angle, kind, pos) in data.Valves.Where(_ => data.Piping))
        {
            var (c, sn) = (Math.Cos(angle), Math.Sin(angle));
            var r = 0.8 * h;
            XYZ Q(double a, double b) => P(at.XMm + (a * c) - (b * sn), at.YMm + (a * sn) + (b * c));
            switch (kind)
            {
                case "vent":
                    doc.Entities.Add(new Circle { Center = P(at.XMm, at.YMm), Radius = r, Layer = equipment });
                    Text(at.XMm - (0.3 * r), at.YMm - (0.4 * r), "В", 0.6 * h);
                    break;
                case "check":
                    doc.Entities.Add(new LwPolyline([new XY(Q(-r, -r).X, Q(-r, -r).Y), new XY(Q(r, 0).X, Q(r, 0).Y), new XY(Q(-r, r).X, Q(-r, r).Y)]) { IsClosed = true, Layer = equipment });
                    doc.Entities.Add(new Line(Q(r, -r), Q(r, r)) { Layer = equipment });
                    break;
                default:
                    doc.Entities.Add(new LwPolyline([new XY(Q(-r, -r).X, Q(-r, -r).Y), new XY(Q(r, r).X, Q(r, r).Y), new XY(Q(r, -r).X, Q(r, -r).Y), new XY(Q(-r, r).X, Q(-r, r).Y)]) { IsClosed = true, Layer = equipment });
                    break;
            }

            if (pos is { } n)
            {
                // Номер позиции на выноске под 45° от символа.
                doc.Entities.Add(new Line(P(at.XMm + r, at.YMm + r), P(at.XMm + (2.2 * r), at.YMm + (2.2 * r))) { Layer = text });
                Text(at.XMm + (2.3 * r), at.YMm + (2.3 * r), n.ToString(System.Globalization.CultureInfo.InvariantCulture), 0.7 * h);
            }
        }

        // Цепочки размеров: по X — у нижней стены, по Y — у левой; точки — стены и грани первого (основного) оборудования
        // и узлов управления — привязка к стенам; остальное — по координатам на чертеже.
        var style = new PlanDxfStyle(TextHeightMm: h);
        if (!data.Piping)
        {
            var main = data.Equipment.Take(1).ToList();
            var xs = main.SelectMany(e => new[] { e.X0, e.X1 }).Append(x0).Append(x1).Select(x => Math.Round(x)).Distinct().Order().ToList();
            var ys = main.SelectMany(e => new[] { e.Y0, e.Y1 }).Append(y0).Append(y1).Select(x => Math.Round(x)).Distinct().Order().ToList();
            var wt = data.WallThicknessMm;
            PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. xs.Select(x => new PlanPoint(x, y0 - wt))], -6 * h), style, dims);
            PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(x0, y0 - wt), new PlanPoint(x1, y0 - wt)], -10 * h), style, dims);
            PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. ys.Select(y => new PlanPoint(x0 - wt, y))], 6 * h), style, dims);
            PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(x0 - wt, y0), new PlanPoint(x0 - wt, y1)], 10 * h), style, dims);
        }

        var top = sheet.At(0, sheet.Format.HeightMm - 15);
        Text(sheet.At(30, 0).X, top.Y, data.Piping ? "План обвязки насосной станции" : "План насосной станции пожаротушения", 2 * h);
        var ny = sheet.At(0, 92).Y;
        // Примечания — подряд; шаг — по числу строк переноса (≈ 0,6 высоты на знак).
        var noteWidth = sheet.Format.WidthMm * s * 0.45;
        var perLine = Math.Max(20, noteWidth / (0.9 * h * 0.62));
        foreach (var (note, k) in data.Notes.Select((n, k) => (n, k)))
        {
            var lines = Math.Ceiling((note.Length + 4) / perLine);
            Text(sheet.At(25, 0).X, ny, $"{k + 1}. {note}", 0.9 * h, noteWidth);
            ny -= ((lines * 1.5) + 0.6) * 0.9 * h;
        }

        return doc;
    }
}
