using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// Оформление аксонометрии, мм. Проекция — фронтальная изометрическая без искажения по осям: X плана — горизонтально,
/// отметка — вертикально, Y плана — под углом <paramref name="YAxisAngleDeg"/>. Угол и направление оси Y сверить
/// с требованиями к листу (ГОСТ 21.601) — это параметр оформления. Остальные размеры — в мм модели;
/// на листе они пересчитываются от высоты подписей 2,5 мм.
/// </summary>
public sealed record AxonometryStyle(
    double YAxisAngleDeg = 45,
    double TextHeightMm = 200,
    double SprinklerRadiusMm = 100,
    double RiserLengthMm = 1000,
    string Font = DocumentStyle.DefaultFont,
    bool SprinklersUp = true,
    string InletNote = "");

/// <summary>
/// Результаты расчёта для схемы (собираются в Application: Export не знает про Calc): трасса диктующей зоны (помещение и ввод), обозначения
/// расчётных точек (узел → «1», «а», «ВВ» — как в таблице гидравлического расчёта ПЗ), участки расчётного направления
/// и оросители диктующей зоны (идентификаторы оросителей раскладки).
/// </summary>
public sealed record AxonometryCalc(
    string RoomId,
    string InletId,
    IReadOnlyDictionary<string, string> PointLabels,
    IReadOnlyCollection<string> PathSegmentIds,
    IReadOnlyCollection<string> ZoneSprinklerIds);

/// <summary>
/// Аксонометрическая схема (лист ПТ-5) из трассы: трубы, оросители на отводах (розеткой вверх или вниз), подписи DN
/// (с длиной — на подводящем и питающих), стояк ввода, знаки отметок оси трубопровода у ввода и в конце дальнего питающего.
/// По расчёту — расчётное направление утолщённой линией, номера расчётных точек как в ПЗ, оросители диктующей зоны.
/// Где трубы пересекаются на схеме не в общем узле, труба меньшего диаметра прерывается; подписи не накладываются друг на друга.
/// На листе — заголовок, условные обозначения и примечания.
/// Отметка одна на всю трассу — разные уровни и стояки появятся с этапом 3.
/// </summary>
public static class AxonometryDxfWriter
{
    public const string PipeLayer = "ПТ_АКСОН_ТРУБЫ";
    public const string SprinklerLayer = "ПТ_АКСОН_ОРОСИТЕЛИ";
    public const string LabelLayer = "ПТ_АКСОН_ПОДПИСИ";
    public const string MarkLayer = "ПТ_АКСОН_ОТМЕТКИ";
    public const string PathLayer = "ПТ_АКСОН_РАСЧЁТ";
    public const string ZoneLayer = "ПТ_АКСОН_ДИКТ_ЗОНА";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static XY Project(PlanPoint p, double elevationMm, AxonometryStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var a = style.YAxisAngleDeg * Math.PI / 180;
        return new XY(p.XMm + (p.YMm * Math.Cos(a)), elevationMm + (p.YMm * Math.Sin(a)));
    }

    public static void Write(string path, IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle? style = null, TitleBlock? stamp = null, AxonometryCalc? calc = null)
    {
        style ??= new AxonometryStyle();
        DxfDrawing.Save(path, Build(routes, pipeElevation, style, stamp, calc), style.Font);
    }

    /// <summary>
    /// Схема в мм модели. С основной надписью — лист по ГОСТ Р 21.101 (<see cref="GostSheet"/>): формат и масштаб подбираются
    /// по габаритам схемы, высота подписей — 2,5 мм на листе, оросители — Ø2 мм, стояк ввода — 15 мм.
    /// </summary>
    public static CadDocument Build(IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle style, TitleBlock? stamp = null, AxonometryCalc? calc = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(style);
        SheetPlacement? sheet = null;
        if (stamp is not null)
        {
            var points = routes.SelectMany(r => r.Nodes).Select(n => Project(n.Location, pipeElevation.Millimeters, style)).ToList();
            if (points.Count > 0)
            {
                // Габарит с запасом на стояк ввода, отметки и подписи (в масштабе 1:100 — 20 и 10 мм листа).
                sheet = GostSheet.Fit(points.Min(p => p.X) - 500, points.Min(p => p.Y) - 2000, points.Max(p => p.X) + 1500, points.Max(p => p.Y) + 1000);
                style = style with { TextHeightMm = 2.5 * sheet.Scale, SprinklerRadiusMm = 1 * sheet.Scale, RiserLengthMm = 15 * sheet.Scale };
            }
        }

        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 4, LineWeightType.W40); // трубопроводы — 0,40 мм
        var path = DxfDrawing.AddLayer(doc, PathLayer, 4, LineWeightType.W70); // расчётное направление — утолщённой линией
        var sprinklers = DxfDrawing.AddLayer(doc, SprinklerLayer, 1);
        var zone = DxfDrawing.AddLayer(doc, ZoneLayer, 1);
        var labels = DxfDrawing.AddLayer(doc, LabelLayer, 7);
        var marks = DxfDrawing.AddLayer(doc, MarkLayer, 7);
        var z = pipeElevation.Millimeters;
        var unit = style.TextHeightMm / 2.5; // мм модели на 1 мм листа
        var stub = (style.SprinklersUp ? 1 : -1) * 3 * unit; // отвод к оросителю — 3 мм листа вверх или вниз
        var pathIds = calc?.PathSegmentIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var zoneIds = calc?.ZoneSprinklerIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var placed = new List<Quad>();
        XYZ At(XY p) => new(p.X, p.Y, 0);

        // Участки всех трасс в проекции; на пересечениях не в узле прерывается меньший участок.
        var drawn = new List<Piece>();
        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var onPath = calc is not null && calc.RoomId == route.RoomId && calc.InletId == route.InletId;
            foreach (var seg in route.Segments.Concat(route.Loops))
            {
                drawn.Add(new Piece(Project(nodes[seg.From].Location, z, style), Project(nodes[seg.To].Location, z, style), seg.Dn, seg.Role == RouteSegmentRole.Row, onPath && pathIds.Contains(seg.Id)));
            }

            // Отводы к оросителям — тоже участки: на пересечении с трубой прерываются они (диаметр условно 0).
            foreach (var n in route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler))
            {
                var p = Project(n.Location, z, style);
                drawn.Add(new Piece(p, new XY(p.X, p.Y + stub - (Math.Sign(stub) * style.SprinklerRadiusMm)), 0, false, false, Stub: true));
            }
        }

        foreach (var (a, b, src) in Broken(drawn, 1.5 * unit))
        {
            doc.Entities.Add(new Line(At(a), At(b)) { Layer = src.Stub ? sprinklers : src.OnPath ? path : pipes });
            placed.Add(Quad.Line(a, b, 0.05 * style.TextHeightMm));
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);

            // Оросители — на отводах от трубы (розеткой вверх или вниз); диктующей зоны — залитые.
            foreach (var n in route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler))
            {
                var p = Project(n.Location, z, style);
                var head = new XY(p.X, p.Y + stub);
                doc.Entities.Add(new Circle(At(head), style.SprinklerRadiusMm) { Layer = sprinklers });
                if (zoneIds.Contains(n.SprinklerId ?? n.Id))
                {
                    doc.Entities.Add(Disc(head, style.SprinklerRadiusMm, zone));
                }

                placed.Add(Quad.Around(head, style.SprinklerRadiusMm));
                placed.Add(Quad.Line(p, head, 0.05 * style.TextHeightMm));
            }
        }

        // Номера расчётных точек — у узлов направления, со стороны, противоположной подписям DN.
        if (calc is not null && routes.FirstOrDefault(r => r.RoomId == calc.RoomId && r.InletId == calc.InletId) is { } calcRoute)
        {
            var nodes = calcRoute.Nodes.ToDictionary(n => n.Id);
            foreach (var (id, label) in calc.PointLabels)
            {
                if (!nodes.TryGetValue(id, out var n))
                {
                    continue;
                }

                var p = Project(n.Location, z, style);
                var anchor = n.Kind == RouteNodeKind.Sprinkler ? new XY(p.X, p.Y + stub) : p;
                var at = new XY(anchor.X - (1.2 * unit), anchor.Y + (Math.Sign(stub) * 1.2 * unit));
                placed.Add(Quad.Text(at, label.Length, 2.5 * unit, rightAligned: true, above: stub > 0));
                doc.Entities.Add(new MText
                {
                    Value = label,
                    InsertPoint = At(at),
                    Height = 2.5 * unit,
                    AttachmentPoint = stub > 0 ? AttachmentPointType.BottomRight : AttachmentPointType.TopRight,
                    Layer = zone,
                });
            }
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var byTo = route.Segments.ToDictionary(s => s.To);
            var loops = route.Loops.Select(s => s.Id).ToHashSet();

            // Подписи — одна на отрезок одного диаметра и назначения: питающие и подводящий — «DN… L=…» (длина — сумма участков,
            // ставятся первыми: им места важнее), рядки — «DN…»; нет места у трубы — на выноске с полкой.
            XY P(string id) => Project(nodes[id].Location, z, style);

            // Рядки — только DN (длины — в таблице расчёта), одна подпись на отрезок одного диаметра.
            var runs = Runs(route).OrderBy(r => r[0].Role == RouteSegmentRole.Row).ToList();
            runs.AddRange(route.Loops.Select(s => new List<RouteSegment> { s }));
            foreach (var run in runs)
            {
                var text = run[0].Role == RouteSegmentRole.Row
                    ? $"DN{run[0].Dn}"
                    : string.Create(Ru, $"DN{run[0].Dn} L={run.Sum(s => s.Length.Meters):0.00} м");

                // Прямой отрезок — подпись в любом месте по его длине; с поворотами — на самом длинном участке.
                var (a, b) = Straight(run, P) ? (P(run[0].From), P(run[^1].To)) : (P(run.MaxBy(s => s.Length.Meters)!.From), P(run.MaxBy(s => s.Length.Meters)!.To));
                if (!PlaceLabel(doc, labels, placed, a, b, text, style.TextHeightMm, preferBelow: stub > 0))
                {
                    PlaceLeader(doc, labels, placed, Lerp(new Piece(a, b, 0, false, false), 0.5), text, style.TextHeightMm);
                }
            }

            // Стояк ввода вниз, подпись ввода (со ссылкой на раздел) и отметка оси трубопровода у ввода.
            var inlet = Project(nodes[route.InletId].Location, z, style);
            doc.Entities.Add(new Line(At(inlet), new XYZ(inlet.X, inlet.Y - style.RiserLengthMm, 0)) { Layer = pipes });
            var supplyDn = route.Segments.FirstOrDefault(s => s.From == route.InletId)?.Dn;
            var inletText = (supplyDn is { } dn ? $"Ввод DN{dn}" : "Ввод") + (style.InletNote.Length > 0 ? $"\\P{style.InletNote}" : "");
            doc.Entities.Add(new MText
            {
                Value = inletText,
                InsertPoint = new XYZ(inlet.X + unit, inlet.Y - style.RiserLengthMm, 0),
                Height = style.TextHeightMm,
                AttachmentPoint = AttachmentPointType.BottomLeft,
                Layer = labels,
            });
            TryMark(doc, marks, placed, inlet, $"отм. {Elevation(pipeElevation)}", unit, force: true, preferLeft: true);

            // Отметки в концах питающих трубопроводов — где знак помещается без наложений (иначе пропускается: отметка та же,
            // что у ввода). Дальний (верхний) конец — обязательно.
            var mainFrom = route.Segments.Where(s => s.Role == RouteSegmentRole.Main).Select(s => s.From).ToHashSet();
            var ends = route.Segments.Where(s => s.Role == RouteSegmentRole.Main && !mainFrom.Contains(s.To))
                .Select(s => Project(nodes[s.To].Location, z, style)).OrderByDescending(p => p.Y).ToList();
            for (var k = 0; k < ends.Count; k++)
            {
                TryMark(doc, marks, placed, ends[k], Elevation(pipeElevation), unit, force: k == 0);
            }
        }

        if (sheet is not null)
        {
            GostSheet.Draw(doc, sheet, stamp!);
            // Заголовок, условные обозначения и примечания — на слое листа; образцы линий и знаков — на своих слоях.
            var format = DxfDrawing.AddLayer(doc, GostSheet.Layer, 7);
            Title(doc, sheet, stamp!.Name, format);
            Legend(doc, sheet, new LegendLayers(pipes, path, sprinklers, zone, marks, format), calc is not null, style.SprinklersUp);
            Notes(doc, sheet, format, calc is not null);
        }

        return doc;
    }

    // Участок в проекции: диаметр и признак рядка — кто уступает на пересечении; признак расчётного направления — для слоя.
    private sealed record Piece(XY A, XY B, int Dn, bool Row, bool OnPath, bool Stub = false);

    /// <summary>
    /// Отрезки для черчения. Трубы на одной отметке пересекаются на схеме только там, где пересекаются на плане, — одна
    /// из них проходит в обход другой; условно прерывается труба меньшего диаметра (при равных — рядок, затем более поздний участок)
    /// на <paramref name="gap"/> в каждую сторону от точки пересечения.
    /// </summary>
    private static List<(XY A, XY B, Piece Source)> Broken(List<Piece> pieces, double gap)
    {
        var cuts = pieces.Select(_ => new List<(double From, double To)>()).ToList();
        for (var i = 0; i < pieces.Count; i++)
        {
            for (var j = i + 1; j < pieces.Count; j++)
            {
                if (Cross(pieces[i], pieces[j]) is not { } c)
                {
                    continue;
                }

                var iYields = pieces[i].Dn < pieces[j].Dn || (pieces[i].Dn == pieces[j].Dn && pieces[i].Row && !pieces[j].Row);
                var (k, t) = iYields ? (i, c.T) : (j, c.U);
                var length = Length2(pieces[k]);
                cuts[k].Add((t - (gap / length), t + (gap / length)));
            }
        }

        var result = new List<(XY, XY, Piece)>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var start = 0.0;
            foreach (var (from, to) in cuts[i].OrderBy(c => c.From))
            {
                if (from > start)
                {
                    result.Add((Lerp(p, start), Lerp(p, from), p));
                }

                start = Math.Max(start, to);
            }

            if (start < 1)
            {
                result.Add((Lerp(p, start), p.B, p));
            }
        }

        return result;
    }

    // Пересечение внутри обоих отрезков (не в концах): параметры t и u вдоль первого и второго.
    private static (double T, double U)? Cross(Piece p, Piece q)
    {
        const double eps = 1e-6;
        var (x1, y1, x2, y2) = (p.A.X, p.A.Y, p.B.X, p.B.Y);
        var (x3, y3, x4, y4) = (q.A.X, q.A.Y, q.B.X, q.B.Y);
        if (Math.Max(x1, x2) < Math.Min(x3, x4) || Math.Max(x3, x4) < Math.Min(x1, x2) || Math.Max(y1, y2) < Math.Min(y3, y4) || Math.Max(y3, y4) < Math.Min(y1, y2))
        {
            return null;
        }

        var den = ((x1 - x2) * (y3 - y4)) - ((y1 - y2) * (x3 - x4));
        if (Math.Abs(den) < eps)
        {
            return null;
        }

        var t = (((x1 - x3) * (y3 - y4)) - ((y1 - y3) * (x3 - x4))) / den;
        var u = -(((x1 - x2) * (y1 - y3)) - ((y1 - y2) * (x1 - x3))) / den;
        return t > eps && t < 1 - eps && u > eps && u < 1 - eps ? (t, u) : null;
    }

    private static double Length2(Piece p) => Math.Max(Math.Sqrt(Math.Pow(p.B.X - p.A.X, 2) + Math.Pow(p.B.Y - p.A.Y, 2)), 1e-9);

    private static XY Lerp(Piece p, double t) => new(p.A.X + (t * (p.B.X - p.A.X)), p.A.Y + (t * (p.B.Y - p.A.Y)));

    /// <summary>
    /// Знак отметки: стрелка с углом 90° острием к оси трубопровода, выносная линия и полка с отметкой над ней.
    /// Выносная линия — вверх или вниз, полка — вправо или влево: первое положение без наложений; если такого нет —
    /// при <paramref name="force"/> ставится в первом, иначе пропускается. Размеры — в мм листа, <paramref name="unit"/> — мм модели на 1 мм листа.
    /// </summary>
    private static bool TryMark(CadDocument doc, Layer layer, List<Quad> placed, XY tip, string text, double unit, bool force, bool preferLeft = false)
    {
        var shelf = (2.5 * 0.8 * text.Length * unit) + (2 * unit);
        var options = new List<(int Up, int Dir)>();
        foreach (var up in new[] { 1, -1 })
        {
            options.AddRange(preferLeft ? [(up, -1), (up, 1)] : [(up, 1), (up, -1)]);
        }

        List<Quad> Shapes((int Up, int Dir) o)
        {
            var top = tip.Y + (o.Up * 8 * unit);
            var x1 = tip.X + (o.Dir * shelf);
            return
            [
                Quad.Line(new XY(tip.X, tip.Y + (o.Up * 1.5 * unit)), new XY(tip.X, top), 0.3 * unit),
                Quad.Line(new XY(tip.X, top), new XY(x1, top), 0.3 * unit),
                new Quad(new XY((tip.X + x1) / 2, top + (2 * unit)), 1, 0, shelf / 2, 1.7 * unit),
            ];
        }

        var choice = options.FirstOrDefault(o => !Shapes(o).Any(s => placed.Any(s.Hits)));
        if (choice == default)
        {
            if (!force)
            {
                return false;
            }

            choice = options[0];
        }

        placed.AddRange(Shapes(choice));
        var (dirUp, dir) = choice;
        var topY = tip.Y + (dirUp * 8 * unit);
        XYZ P(double x, double y) => new(x, y, 0);

        // Стрелка всегда острием к оси трубы: при выносной линии вниз — перевёрнута.
        doc.Entities.Add(new Line(P(tip.X - (2 * unit), tip.Y + (dirUp * 2 * unit)), P(tip.X, tip.Y)) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, tip.Y), P(tip.X + (2 * unit), tip.Y + (dirUp * 2 * unit))) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, tip.Y), P(tip.X, topY)) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, topY), P(tip.X + (dir * shelf), topY)) { Layer = layer });
        doc.Entities.Add(new TextEntity
        {
            Value = text,
            InsertPoint = P(dir < 0 ? tip.X - shelf + unit : tip.X + unit, topY + (0.8 * unit)),
            Height = 2.5 * unit,
            Layer = layer,
        });
        return true;
    }

    // Заголовок схемы над изображением, по центру поля листа.
    private static void Title(CadDocument doc, SheetPlacement sheet, string name, Layer layer) =>
        doc.Entities.Add(new MText
        {
            Value = name,
            InsertPoint = sheet.At((20 + sheet.Format.WidthMm - 5) / 2, sheet.Format.HeightMm - 12),
            Height = 5 * sheet.Scale,
            AttachmentPoint = AttachmentPointType.MiddleCenter,
            Layer = layer,
        });

    private sealed record LegendLayers(Layer Pipes, Layer Path, Layer Sprinklers, Layer Zone, Layer Marks, Layer Labels);

    // Условные обозначения — таблица слева от основной надписи: изображение (25 мм) и наименование (95 мм), строки по 7 мм.
    private static void Legend(CadDocument doc, SheetPlacement sheet, LegendLayers l, bool withCalc, bool up)
    {
        const double x0 = 30;
        const double x1 = 55;
        const double x2 = 150;
        const double bottom = 8;
        const double rowH = 7;
        var rows = new List<(string Name, Action<double, double> Draw)>
        {
            ("Трубопровод установки водяного пожаротушения", (cx, y) => Add(new Line(sheet.At(cx - 9, y), sheet.At(cx + 9, y)) { Layer = l.Pipes })),
        };
        if (withCalc)
        {
            rows.Add(("Трубопровод расчётного направления", (cx, y) => Add(new Line(sheet.At(cx - 9, y), sheet.At(cx + 9, y)) { Layer = l.Path })));
        }

        var dir = up ? 1 : -1;
        rows.Add(($"Ороситель спринклерный, розеткой {(up ? "вверх" : "вниз")}", (cx, y) => Sprinkler(cx, y - (dir * 1.5), false)));
        if (withCalc)
        {
            rows.Add(("Ороситель диктующей зоны", (cx, y) => Sprinkler(cx, y - (dir * 1.5), true)));
            rows.Add(("Номер расчётной точки", (cx, y) => Add(new MText { Value = "1, а, ВВ", InsertPoint = sheet.At(cx, y), Height = 2.5 * sheet.Scale, AttachmentPoint = AttachmentPointType.MiddleCenter, Layer = l.Zone })));
        }

        rows.Add(("Отметка оси трубопровода, м", (cx, y) =>
        {
            Add(new Line(sheet.At(cx - 2, y + 1), sheet.At(cx, y - 1)) { Layer = l.Marks });
            Add(new Line(sheet.At(cx, y - 1), sheet.At(cx + 2, y + 1)) { Layer = l.Marks });
            Add(new Line(sheet.At(cx, y - 1), sheet.At(cx, y + 2)) { Layer = l.Marks });
            Add(new Line(sheet.At(cx, y + 2), sheet.At(cx + 9, y + 2)) { Layer = l.Marks });
        }));
        rows.Add(("Условный проход трубопровода, мм", (cx, y) => Add(new MText { Value = "DN25", InsertPoint = sheet.At(cx, y), Height = 2.5 * sheet.Scale, AttachmentPoint = AttachmentPointType.MiddleCenter, Layer = l.Labels })));

        var top = bottom + (rowH * (rows.Count + 1));
        void Add(Entity e) => doc.Entities.Add(e);
        void Seg(double ax, double ay, double bx, double by) => Add(new Line(sheet.At(ax, ay), sheet.At(bx, by)) { Layer = l.Labels });

        // Ороситель на отводе: линия трубы, отвод 3 мм, окружность Ø2 мм; диктующей зоны — залитый.
        void Sprinkler(double cx, double y, bool inZone)
        {
            Add(new Line(sheet.At(cx - 9, y), sheet.At(cx + 9, y)) { Layer = l.Pipes });
            Add(new Line(sheet.At(cx, y), sheet.At(cx, y + (dir * 2))) { Layer = l.Sprinklers });
            var head = sheet.At(cx, y + (dir * 3));
            Add(new Circle(head, sheet.Scale) { Layer = l.Sprinklers });
            if (inZone)
            {
                Add(Disc(new XY(head.X, head.Y), sheet.Scale, l.Zone));
            }
        }

        Add(new MText
        {
            Value = "Условные обозначения",
            InsertPoint = sheet.At((x0 + x2) / 2, top - (rowH / 2)),
            Height = 3.5 * sheet.Scale,
            AttachmentPoint = AttachmentPointType.MiddleCenter,
            Layer = l.Labels,
        });
        for (var k = 0; k <= rows.Count; k++)
        {
            Seg(x0, top - (rowH * (k + 1)), x2, top - (rowH * (k + 1)));
        }

        Seg(x0, top - rowH, x0, bottom);
        Seg(x1, top - rowH, x1, bottom);
        Seg(x2, top - rowH, x2, bottom);
        for (var k = 0; k < rows.Count; k++)
        {
            var y = top - (rowH * (k + 1.5));
            rows[k].Draw((x0 + x1) / 2, y);
            Add(new MText { Value = rows[k].Name, InsertPoint = sheet.At(x1 + 2, y), Height = 2.5 * sheet.Scale, RectangleWidth = (x2 - x1 - 3) * sheet.Scale, AttachmentPoint = AttachmentPointType.MiddleLeft, Layer = l.Labels });
        }
    }

    // Примечания — между условными обозначениями и основной надписью.
    private static void Notes(CadDocument doc, SheetPlacement sheet, Layer layer, bool withCalc)
    {
        var notes = new List<string>
        {
            "Отметки даны в метрах по оси трубопроводов, диаметры — условный проход DN, мм.",
            "Трубопроводы, пересекающиеся на схеме не в узле, показаны с разрывом трубы меньшего диаметра.",
        };
        if (withCalc)
        {
            notes.Add("Номера расчётных точек и диаметры участков расчётного направления — по таблице гидравлического расчёта пояснительной записки.");
        }

        const double x0 = 160;
        var x1 = sheet.Format.WidthMm - 5 - 185 - 10;
        if (x1 - x0 < 50)
        {
            return;
        }

        var text = "ПРИМЕЧАНИЯ\\P" + string.Join("\\P", notes.Select((n, k) => $"{k + 1} {n}"));
        doc.Entities.Add(new MText
        {
            Value = text,
            InsertPoint = sheet.At(x0, 62),
            Height = 2.5 * sheet.Scale,
            RectangleWidth = (x1 - x0) * sheet.Scale,
            AttachmentPoint = AttachmentPointType.TopLeft,
            Layer = layer,
        });
    }

    // Залитый круг радиуса r: замкнутая полилиния из двух дуг шириной r по окружности радиуса r/2 (как DONUT в AutoCAD).
    private static LwPolyline Disc(XY c, double r, Layer layer) => new(
    [
        new LwPolyline.Vertex(new XY(c.X - (r / 2), c.Y)) { Bulge = 1 },
        new LwPolyline.Vertex(new XY(c.X + (r / 2), c.Y)) { Bulge = 1 },
    ])
    { IsClosed = true, ConstantWidth = r, Layer = layer };

    /// <summary>
    /// Отрезки одного диаметра и назначения (рядок, питающий, подводящий): от участка, где диаметр или назначение меняется,
    /// по цепочке продолжений той же роли и диаметра (в дереве — пока продолжение одно).
    /// </summary>
    private static List<List<RouteSegment>> Runs(PipeRoute route)
    {
        var byTo = route.Segments.ToDictionary(s => s.To);
        var children = route.Segments.ToLookup(s => s.From);
        bool Same(RouteSegment a, RouteSegment b) => a.Role == b.Role && a.Dn == b.Dn;
        var runs = new List<List<RouteSegment>>();
        foreach (var start in route.Segments.Where(s => !byTo.TryGetValue(s.From, out var p) || !Same(p, s)))
        {
            var run = new List<RouteSegment> { start };
            while (children[run[^1].To].Where(c => Same(c, start)).ToList() is [var next])
            {
                run.Add(next);
            }

            runs.Add(run);
        }

        return runs;
    }

    // Все участки отрезка — на одной прямой (в проекции).
    private static bool Straight(List<RouteSegment> run, Func<string, XY> at)
    {
        var a = at(run[0].From);
        var b = at(run[^1].To);
        var length = Math.Max(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)), 1e-9);
        return run.All(s =>
        {
            var p = at(s.To);
            return Math.Abs(((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X))) / length < 1;
        });
    }

    /// <summary>Занятое место на схеме (мм модели) — повёрнутый прямоугольник: центр, направление, полуширина и полувысота.</summary>
    private readonly record struct Quad(XY C, double Ux, double Uy, double Hw, double Hh)
    {
        public static Quad Around(XY c, double r) => new(c, 1, 0, r, r);

        // Отрезок линии как тонкий прямоугольник полутолщиной w.
        public static Quad Line(XY a, XY b, double w)
        {
            var length = Math.Max(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)), 1e-9);
            return new(new XY((a.X + b.X) / 2, (a.Y + b.Y) / 2), (b.X - a.X) / length, (b.Y - a.Y) / length, length / 2, w);
        }

        // Текст высотой h и длиной n знаков (ширина знака ≈ 0,8 h) у точки привязки, горизонтальный.
        public static Quad Text(XY at, int n, double h, bool rightAligned, bool above)
        {
            var w = 0.8 * h * n;
            return new(new XY(at.X + ((rightAligned ? -1 : 1) * w / 2), at.Y + ((above ? 1 : -1) * h / 2)), 1, 0, w / 2, h / 2);
        }

        private XY[] Corners()
        {
            var (ux, uy, nx, ny) = (Ux * Hw, Uy * Hw, -Uy * Hh, Ux * Hh);
            return [new(C.X + ux + nx, C.Y + uy + ny), new(C.X - ux + nx, C.Y - uy + ny), new(C.X - ux - nx, C.Y - uy - ny), new(C.X + ux - nx, C.Y + uy - ny)];
        }

        // Наложение по теореме о разделяющей оси: оси — стороны обоих прямоугольников.
        public bool Hits(Quad o)
        {
            var a = Corners();
            var b = o.Corners();
            foreach (var (ax, ay) in new[] { (Ux, Uy), (-Uy, Ux), (o.Ux, o.Uy), (-o.Uy, o.Ux) })
            {
                var (amin, amax) = (a.Min(p => (p.X * ax) + (p.Y * ay)), a.Max(p => (p.X * ax) + (p.Y * ay)));
                var (bmin, bmax) = (b.Min(p => (p.X * ax) + (p.Y * ay)), b.Max(p => (p.X * ax) + (p.Y * ay)));
                if (amax <= bmin || bmax <= amin)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Подпись участка вдоль трубы, не вверх ногами: сначала с предпочтительной стороны (под трубой, если оросители вверх),
    /// затем с другой и со сдвигом вдоль участка; ставится в первое место, где не накладывается на другие подписи и оросители.
    /// Нет места — false (подпись рядка тогда не ставится: на соседнем участке того же диаметра она есть или будет в таблице).
    /// </summary>
    private static bool PlaceLabel(CadDocument doc, Layer layer, List<Quad> placed, XY a, XY b, string text, double h, bool preferBelow)
    {
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        if (angle > Math.PI / 2 || angle <= -Math.PI / 2)
        {
            angle += Math.PI;
        }

        var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
        var (nx, ny) = (-uy, ux);
        var length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        var width = 0.8 * h * text.Length;
        var pad = 0.3 * h;
        var sides = preferBelow ? new[] { -1.0, 1.0 } : [1.0, -1.0];
        foreach (var shift in new[] { 0.0, -0.15, 0.15, -0.3, 0.3, -0.4, 0.4 })
        {
            // Подпись не выходит за свой участок: иначе её можно отнести к соседнему участку другого диаметра.
            if (shift != 0 && (Math.Abs(shift) * length) + (width / 2) > length / 2)
            {
                continue;
            }

            foreach (var side in sides)
            {
                var mx = ((a.X + b.X) / 2) + (shift * length * ux);
                var my = ((a.Y + b.Y) / 2) + (shift * length * uy);

                // Центр текста — на 0,8 h от оси трубы; начало строки — на половину ширины назад вдоль трубы.
                var cx = mx + (side * nx * 0.8 * h);
                var cy = my + (side * ny * 0.8 * h);
                var box = new Quad(new XY(cx, cy), ux, uy, (width / 2) + pad, (h / 2) + (0.2 * h));
                if (placed.Any(box.Hits))
                {
                    continue;
                }

                placed.Add(box);
                doc.Entities.Add(new TextEntity
                {
                    Value = text,
                    InsertPoint = new XYZ(cx - (ux * width / 2) - (nx * h / 2), cy - (uy * width / 2) - (ny * h / 2), 0),
                    Height = h,
                    Rotation = angle,
                    Layer = layer,
                });
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Подпись на выноске с полкой (как на листах ВК): наклонная линия от трубы под 45° и горизонтальная полка с текстом над ней.
    /// Перебираются четыре направления и пять длин выноски; проверяются текст с полкой и наклонная линия; если чистого места нет —
    /// ставится в положении с наименьшим числом наложений (подпись важнее: смена диаметра без подписи — ошибка чертежа).
    /// </summary>
    private static void PlaceLeader(CadDocument doc, Layer layer, List<Quad> placed, XY at, string text, double h)
    {
        var width = 0.8 * h * text.Length;
        var unit = h / 2.5;
        (XY Knee, int Dir, Quad Box, int Score)? best = null;
        foreach (var reach in new[] { 6.0, 10.0, 14.0, 18.0, 24.0 })
        {
            foreach (var (dx, dy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
            {
                var knee = new XY(at.X + (dx * reach * unit), at.Y + (dy * reach * unit));
                var box = new Quad(new XY(knee.X + (dx * ((width / 2) + unit)), knee.Y + (0.7 * h)), 1, 0, (width / 2) + unit, 0.8 * h);

                // Наклонная линия проверяется от 1 мм листа после трубы, на которой начинается. Наложение текста хуже,
                // чем пересечение линией выноски.
                var from = new XY(at.X + (dx * unit / Math.Sqrt(2)), at.Y + (dy * unit / Math.Sqrt(2)));
                var leader = Quad.Line(from, knee, 0.05 * h);
                var score = (3 * placed.Count(box.Hits)) + placed.Count(leader.Hits);
                if (score == 0)
                {
                    Draw(knee, dx, box);
                    return;
                }

                if (best is null || score < best.Value.Score)
                {
                    best = (knee, dx, box, score);
                }
            }
        }

        Draw(best!.Value.Knee, best.Value.Dir, best.Value.Box);

        void Draw(XY knee, int dir, Quad box)
        {
            placed.Add(box);
            placed.Add(Quad.Line(at, knee, 0.05 * h));
            var end = knee.X + (dir * (width + (2 * unit)));
            doc.Entities.Add(new Line(new XYZ(at.X, at.Y, 0), new XYZ(knee.X, knee.Y, 0)) { Layer = layer });
            doc.Entities.Add(new Line(new XYZ(knee.X, knee.Y, 0), new XYZ(end, knee.Y, 0)) { Layer = layer });
            doc.Entities.Add(new TextEntity
            {
                Value = text,
                InsertPoint = new XYZ(Math.Min(knee.X, end) + unit, knee.Y + (0.6 * unit), 0),
                Height = h,
                Layer = layer,
            });
        }
    }

    // Отметка в метрах со знаком, как на чертежах: ±0,000 (код AutoCAD %%p — знак «±» в любом шрифте), +3,300, −1,500.
    private static string Elevation(Length z) =>
        (z.Meters < 0 ? "−" : z.Meters > 0 ? "+" : "%%p") + Math.Abs(z.Meters).ToString("0.000", Ru);
}
