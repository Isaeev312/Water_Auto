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
public sealed record PumpRoomPipe(string Label, IReadOnlyList<PlanPoint> Path)
{
    /// <summary>Отметка прокладки, м: на пересечении в плане прерывается нижняя труба.</summary>
    public double Height { get; init; }

    /// <summary>Подъёмы и опуски (стояки): точка в плане, отметки верха и низа, м.</summary>
    public IReadOnlyList<(PlanPoint At, double Top, double Bottom)> Risers { get; init; } = [];
}

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

    /// <summary>Экспликация помещения: номер, наименование, площадь, м², высота, м, категория.</summary>
    public (string Number, string Name, double Area, double Height, string Category)? Explication { get; init; }

    /// <summary>Буквенное обозначение трубопровода в разрывах линии (ГОСТ 21.205: В2 — противопожарный водопровод).</summary>
    public string LineCode { get; init; } = "В2";

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

    // Точка отрезка a→b при доле t.
    private static XYZ At(PlanPoint a, PlanPoint b, double t) => new(a.XMm + ((b.XMm - a.XMm) * t), a.YMm + ((b.YMm - a.YMm) * t), 0);

    // Доля t на отрезке a→b, где его пересекает отрезок c→d (внутри обоих, не в концах); null — не пересекаются.
    private static double? Cross(PlanPoint a, PlanPoint b, PlanPoint c, PlanPoint d)
    {
        double rx = b.XMm - a.XMm, ry = b.YMm - a.YMm, sx = d.XMm - c.XMm, sy = d.YMm - c.YMm;
        var den = (rx * sy) - (ry * sx);
        if (Math.Abs(den) < 1e-9)
        {
            return null;
        }

        var t = (((c.XMm - a.XMm) * sy) - ((c.YMm - a.YMm) * sx)) / den;
        var u = (((c.XMm - a.XMm) * ry) - ((c.YMm - a.YMm) * rx)) / den;
        return t > 0.02 && t < 0.98 && u > 0.02 && u < 0.98 ? t : null;
    }

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
        // Арматура — знаками ГОСТ 21.205 (задвижка, затвор дисковый, обратный клапан…), вид — по роли в спецификации.
        void Valve(XYZ at, double angle, string kind, double r) =>
            GostSymbols.Draw(doc, equipment, text, new XY(at.X, at.Y), angle, r, GostSymbols.FromKind(kind));

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
        // Трубы рисуются отрезками с разрывами: на пересечении в плане прерывается нижняя труба (разрыв 1,2 h), через ~40 h —
        // разрыв с буквенным обозначением трубопровода.
        var allSegments = data.Pipes.SelectMany((p, k) => p.Path.Zip(p.Path.Skip(1)).Select(s => (Pipe: k, p.Height, A: s.First, B: s.Second))).ToList();
        foreach (var (pipe, k) in data.Pipes.Select((p, k) => (p, k)).Where(x => x.p.Path.Count >= 2 && data.Piping))
        {
            foreach (var (a0, b0) in pipe.Path.Zip(pipe.Path.Skip(1)))
            {
                var len = Math.Sqrt(Math.Pow(b0.XMm - a0.XMm, 2) + Math.Pow(b0.YMm - a0.YMm, 2));
                if (len < 1)
                {
                    continue;
                }

                var cuts = new List<(double T0, double T1)>();
                foreach (var o in allSegments.Where(o => o.Pipe != k && o.Height > pipe.Height + 1e-6))
                {
                    if (Cross(a0, b0, o.A, o.B) is { } tc)
                    {
                        var g = 0.6 * h / len;
                        cuts.Add((tc - g, tc + g));
                    }
                }

                // Обозначение трубопровода: по одному на каждые ~40 h длины, не ближе 6 h к концам участка.
                var codes = new List<double>();
                for (var dd = 20 * h; dd < len - (6 * h); dd += 40 * h)
                {
                    var tc = dd / len;
                    if (cuts.All(c => tc < c.T0 - (2 * h / len) || tc > c.T1 + (2 * h / len)))
                    {
                        codes.Add(tc);
                        cuts.Add((tc - (1.0 * h / len), tc + (1.0 * h / len)));
                    }
                }

                var pos = 0.0;
                foreach (var (c0, c1) in cuts.OrderBy(c => c.T0))
                {
                    if (c0 > pos)
                    {
                        doc.Entities.Add(new Line(At(a0, b0, pos), At(a0, b0, Math.Max(pos, c0))) { Layer = pipes });
                    }

                    pos = Math.Max(pos, c1);
                }

                if (pos < 1)
                {
                    doc.Entities.Add(new Line(At(a0, b0, pos), P(b0.XMm, b0.YMm)) { Layer = pipes });
                }

                foreach (var tc in codes)
                {
                    var c = At(a0, b0, tc);
                    var vert = Math.Abs(b0.XMm - a0.XMm) < Math.Abs(b0.YMm - a0.YMm);
                    doc.Entities.Add(new TextEntity
                    {
                        Value = data.LineCode,
                        InsertPoint = vert ? P(c.X + (0.35 * h), c.Y - (0.7 * h)) : P(c.X - (0.7 * h), c.Y - (0.35 * h)),
                        Height = 0.7 * h,
                        Rotation = vert ? Math.PI / 2 : 0,
                        Layer = pipes,
                    });
                }
            }

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
        // Подъёмы и опуски: стояк — кружок в точке перехода, отметки верха и низа на выноске.
        if (data.Piping)
        {
            var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            foreach (var (at, zTop, zBottom) in data.Pipes.SelectMany(p => p.Risers))
            {
                doc.Entities.Add(new Circle { Center = P(at.XMm, at.YMm), Radius = 0.45 * h, Layer = equipment });
                doc.Entities.Add(new Line(P(at.XMm, at.YMm), P(at.XMm - (2.5 * h), at.YMm - (2.5 * h))) { Layer = text });
                doc.Entities.Add(new Line(P(at.XMm - (2.5 * h), at.YMm - (2.5 * h)), P(at.XMm - (9.5 * h), at.YMm - (2.5 * h))) { Layer = text });
                Text(at.XMm - (9.3 * h), at.YMm - (2.3 * h), string.Create(ru, $"отм. {zTop:+0.000;−0.000} / {zBottom:+0.000;−0.000}"), 0.6 * h);
            }

            // Привязки осей труб к стенам: у каждой стены — цепочка «стена — оси труб, идущих вдоль неё».
            var style2 = new PlanDxfStyle(TextHeightMm: 0.7 * h);
            var segments = data.Pipes.SelectMany(p => p.Path.Zip(p.Path.Skip(1))).ToList();
            var near = 2500.0;
            var left = segments.Where(s => Math.Abs(s.First.XMm - s.Second.XMm) < 1 && s.First.XMm - x0 < near).Select(s => s.First.XMm).Distinct().Order().ToList();
            var right = segments.Where(s => Math.Abs(s.First.XMm - s.Second.XMm) < 1 && x1 - s.First.XMm < near).Select(s => s.First.XMm).Distinct().Order().ToList();
            var bottomSide = segments.Where(s => Math.Abs(s.First.YMm - s.Second.YMm) < 1 && s.First.YMm - y0 < near).Select(s => s.First.YMm).Distinct().Order().ToList();
            var topSide = segments.Where(s => Math.Abs(s.First.YMm - s.Second.YMm) < 1 && y1 - s.First.YMm < near).Select(s => s.First.YMm).Distinct().Order().ToList();
            var midY = (y0 + y1) / 2;
            var midX = (x0 + x1) / 2;
            if (left.Count > 0)
            {
                PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(x0, y0 + (0.15 * (y1 - y0))), .. left.Select(x => new PlanPoint(x, y0 + (0.15 * (y1 - y0))))], 0), style2, dims);
            }

            if (right.Count > 0)
            {
                PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. right.Select(x => new PlanPoint(x, midY)), new PlanPoint(x1, midY)], 0), style2, dims);
            }

            if (bottomSide.Count > 0)
            {
                PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(midX, y0), .. bottomSide.Select(y => new PlanPoint(midX, y))], 0), style2, dims);
            }

            if (topSide.Count > 0)
            {
                PlanDxfWriter.AddChain(doc, new PlanDimensionChain([.. topSide.Select(y => new PlanPoint(x0 + (0.7 * (x1 - x0)), y)), new PlanPoint(x0 + (0.7 * (x1 - x0)), y1)], 0), style2, dims);
            }
        }

        // Условные обозначения на листе обвязки — справа от плана.
        if (data.Piping)
        {
            var lx = x1 + (data.WallThicknessMm) + (12 * h);
            var ly = y1;
            // Знаки арматуры — по ГОСТ 21.205 только тех видов, что есть на листе; линия, подъёмы и отметки на стенах — ниже.
            var used = data.Valves.Select(v => GostSymbols.FromKind(v.Kind)).Concat(data.Equipment.Any(e => e.Pumps.Count > 1) ? [PipeSymbol.Gate, PipeSymbol.Check] : []).Distinct().ToList();
            var cy = GostSymbols.Legend(doc, equipment, text, lx, ly, h, used);
            var rows = new (string Kind, string Label)[]
            {
                ("line", $"трубопровод противопожарного водопровода ({data.LineCode}), ГОСТ 21.205"),
                ("riser", "подъём (опуск) трубопровода, отметки верха / низа"),
                ("mark", "ввод, выпуск, патрубок ГМ на стене"),
            };
            foreach (var (kind, label) in rows)
            {
                var c = P(lx + (2 * h), cy);
                switch (kind)
                {
                    case "line":
                        doc.Entities.Add(new Line(P(lx, cy), P(lx + (1.3 * h), cy)) { Layer = pipes });
                        doc.Entities.Add(new Line(P(lx + (2.7 * h), cy), P(lx + (4 * h), cy)) { Layer = pipes });
                        Text(lx + (1.4 * h), cy - (0.35 * h), data.LineCode, 0.7 * h);
                        break;
                    case "riser":
                        doc.Entities.Add(new Circle { Center = c, Radius = 0.45 * h, Layer = equipment });
                        break;
                    default:
                        doc.Entities.Add(new Circle { Center = c, Radius = 0.6 * h, Layer = pipes });
                        break;
                }

                Text(lx + (5 * h), cy - (0.35 * h), label, 0.7 * h);
                cy -= 2.4 * h;
            }
        }

        // Экспликация помещения — таблица справа от плана (архитектурный лист).
        if (!data.Piping && data.Explication is { } ex)
        {
            var tx = x1 + data.WallThicknessMm + (12 * h);
            var ty = y1;
            double[] widths = [6 * h, 22 * h, 8 * h, 7 * h, 22 * h];
            string[] head = ["№", "Наименование", "Площадь, м²", "Высота, м", "Категория"];
            string[] row = [ex.Number, ex.Name, ex.Area.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), ex.Height.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), ex.Category];
            Text(tx, ty + (1.0 * h), "Экспликация помещения", 1.0 * h);
            var rowH = 3.2 * h;
            for (var r = 0; r < 2; r++)
            {
                var cx = tx;
                for (var c = 0; c < widths.Length; c++)
                {
                    Rect(cx, ty - ((r + 1) * rowH), cx + widths[c], ty - (r * rowH), text);
                    Text(cx + (0.3 * h), ty - ((r + 1) * rowH) + (1.0 * h), r == 0 ? head[c] : row[c], 0.65 * h, widths[c] - (0.6 * h));
                    cx += widths[c];
                }
            }
        }

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
            // Привязки каждой единицы оборудования к ближайшим стенам по X и по Y (короткие размеры у его угла).
            var style3 = new PlanDxfStyle(TextHeightMm: 0.7 * h);
            foreach (var e in data.Equipment.Skip(1))
            {
                var (toLeft, toRight) = (e.X0 - x0, x1 - e.X1);
                var (toBottom, toTop) = (e.Y0 - y0, y1 - e.Y1);
                var yAt = (e.Y0 + e.Y1) / 2;
                var xAt = (e.X0 + e.X1) / 2;
                PlanDxfWriter.AddChain(doc, toLeft <= toRight
                    ? new PlanDimensionChain([new PlanPoint(x0, yAt), new PlanPoint(e.X0, yAt)], 0)
                    : new PlanDimensionChain([new PlanPoint(e.X1, yAt), new PlanPoint(x1, yAt)], 0), style3, dims);
                PlanDxfWriter.AddChain(doc, toBottom <= toTop
                    ? new PlanDimensionChain([new PlanPoint(xAt, y0), new PlanPoint(xAt, e.Y0)], 0)
                    : new PlanDimensionChain([new PlanPoint(xAt, e.Y1), new PlanPoint(xAt, y1)], 0), style3, dims);
            }

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
