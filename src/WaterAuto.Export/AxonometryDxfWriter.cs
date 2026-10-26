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
    string Font = DocumentStyle.DefaultFont);

/// <summary>
/// Аксонометрическая схема (лист ПТ-5) из трассы: трубы, оросители, подписи DN (с длиной — на подводящем и питающих),
/// стояк ввода, знаки отметок оси трубопровода у ввода и в конце дальнего питающего трубопровода. Где трубы пересекаются
/// на схеме не в общем узле, труба меньшего диаметра прерывается. На листе — заголовок и таблица условных обозначений.
/// Отметка одна на всю трассу — разные уровни и стояки появятся с этапом 3.
/// </summary>
public static class AxonometryDxfWriter
{
    public const string PipeLayer = "ПТ_АКСОН_ТРУБЫ";
    public const string SprinklerLayer = "ПТ_АКСОН_ОРОСИТЕЛИ";
    public const string LabelLayer = "ПТ_АКСОН_ПОДПИСИ";
    public const string MarkLayer = "ПТ_АКСОН_ОТМЕТКИ";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static XY Project(PlanPoint p, double elevationMm, AxonometryStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var a = style.YAxisAngleDeg * Math.PI / 180;
        return new XY(p.XMm + (p.YMm * Math.Cos(a)), elevationMm + (p.YMm * Math.Sin(a)));
    }

    public static void Write(string path, IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle? style = null, TitleBlock? stamp = null)
    {
        style ??= new AxonometryStyle();
        DxfDrawing.Save(path, Build(routes, pipeElevation, style, stamp), style.Font);
    }

    /// <summary>
    /// Схема в мм модели. С основной надписью — лист по ГОСТ Р 21.101 (<see cref="GostSheet"/>): формат и масштаб подбираются
    /// по габаритам схемы, высота подписей — 2,5 мм на листе, оросители — Ø2 мм, стояк ввода — 15 мм.
    /// </summary>
    public static CadDocument Build(IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle style, TitleBlock? stamp = null)
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
        var sprinklers = DxfDrawing.AddLayer(doc, SprinklerLayer, 1);
        var labels = DxfDrawing.AddLayer(doc, LabelLayer, 7);
        var marks = DxfDrawing.AddLayer(doc, MarkLayer, 7);
        var z = pipeElevation.Millimeters;
        var unit = style.TextHeightMm / 2.5; // мм модели на 1 мм листа
        XYZ At(XY p) => new(p.X, p.Y, 0);

        // Участки всех трасс в проекции; на пересечениях не в узле прерывается меньший участок.
        var drawn = new List<Piece>();
        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            foreach (var seg in route.Segments.Concat(route.Loops))
            {
                drawn.Add(new Piece(Project(nodes[seg.From].Location, z, style), Project(nodes[seg.To].Location, z, style), seg.Dn, seg.Role == RouteSegmentRole.Row));
            }
        }

        foreach (var (a, b) in Broken(drawn, 1.5 * unit))
        {
            doc.Entities.Add(new Line(At(a), At(b)) { Layer = pipes });
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var byTo = route.Segments.ToDictionary(s => s.To);
            var loops = route.Loops.Select(s => s.Id).ToHashSet();
            foreach (var seg in route.Segments.Concat(route.Loops))
            {
                var a = Project(nodes[seg.From].Location, z, style);
                var b = Project(nodes[seg.To].Location, z, style);

                // Подпись — где меняется диаметр (на питающих — и назначение участка); на рядках — только DN (длины — в таблице
                // расчёта), чтобы подписи коротких участков между оросителями не наползали друг на друга.
                var hasParent = byTo.TryGetValue(seg.From, out var parent);
                var changed = !hasParent || parent!.Dn != seg.Dn || (seg.Role != RouteSegmentRole.Row && parent.Role != seg.Role);
                if (loops.Contains(seg.Id) || changed)
                {
                    var text = seg.Role == RouteSegmentRole.Row ? $"DN{seg.Dn}" : null;
                    doc.Entities.Add(DxfDrawing.Label(seg, a, b, style.TextHeightMm, labels, text));
                }
            }

            foreach (var n in route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler))
            {
                doc.Entities.Add(new Circle(At(Project(n.Location, z, style)), style.SprinklerRadiusMm) { Layer = sprinklers });
            }

            // Стояк ввода вниз, подпись ввода и отметка оси трубопровода у ввода.
            var inlet = Project(nodes[route.InletId].Location, z, style);
            doc.Entities.Add(new Line(At(inlet), new XYZ(inlet.X, inlet.Y - style.RiserLengthMm, 0)) { Layer = pipes });
            var supplyDn = route.Segments.FirstOrDefault(s => s.From == route.InletId)?.Dn;
            doc.Entities.Add(new TextEntity
            {
                Value = supplyDn is { } dn ? $"Ввод DN{dn}" : "Ввод",
                InsertPoint = new XYZ(inlet.X + style.TextHeightMm, inlet.Y - style.RiserLengthMm, 0),
                Height = style.TextHeightMm,
                Layer = labels,
            });
            Mark(doc, marks, inlet, $"отм. {Elevation(pipeElevation)}", unit, left: true);

            // Вторая отметка — в конце питающего трубопровода, дальнего на схеме (верхнего): у остальных концов отметка та же,
            // а знаки рядом с подписями DN загромождают схему.
            var mainFrom = route.Segments.Where(s => s.Role == RouteSegmentRole.Main).Select(s => s.From).ToHashSet();
            var ends = route.Segments.Where(s => s.Role == RouteSegmentRole.Main && !mainFrom.Contains(s.To))
                .Select(s => Project(nodes[s.To].Location, z, style)).ToList();
            if (ends.Count > 0)
            {
                var far = ends.MaxBy(p => p.Y);
                Mark(doc, marks, new XY(far.X, far.Y + (3 * unit)), Elevation(pipeElevation), unit);
            }
        }

        if (sheet is not null)
        {
            GostSheet.Draw(doc, sheet, stamp!);
            Title(doc, sheet, stamp!.Name, labels);
            Legend(doc, sheet, pipes, sprinklers, marks, labels);
        }

        return doc;
    }

    // Участок в проекции: диаметр и признак рядка — кто уступает на пересечении.
    private sealed record Piece(XY A, XY B, int Dn, bool Row);

    /// <summary>
    /// Отрезки для черчения. Трубы на одной отметке пересекаются на схеме только там, где пересекаются на плане, — одна
    /// из них проходит в обход другой; условно прерывается труба меньшего диаметра (при равных — рядок, затем более поздний участок)
    /// на <paramref name="gap"/> в каждую сторону от точки пересечения.
    /// </summary>
    private static List<(XY A, XY B)> Broken(List<Piece> pieces, double gap)
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

        var result = new List<(XY, XY)>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var start = 0.0;
            foreach (var (from, to) in cuts[i].OrderBy(c => c.From))
            {
                if (from > start)
                {
                    result.Add((Lerp(p, start), Lerp(p, from)));
                }

                start = Math.Max(start, to);
            }

            if (start < 1)
            {
                result.Add((Lerp(p, start), p.B));
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
    /// Знак отметки: стрелка с углом 90° острием к оси трубопровода, выносная линия вверх и полка с отметкой над ней.
    /// Размеры — в мм листа, <paramref name="unit"/> — мм модели на 1 мм листа.
    /// </summary>
    private static void Mark(CadDocument doc, Layer layer, XY tip, string text, double unit, bool left = false)
    {
        var dir = left ? -1 : 1;
        var top = tip.Y + (8 * unit);
        var shelf = (2.5 * 0.7 * text.Length * unit) + (2 * unit);
        XYZ P(double x, double y) => new(x, y, 0);
        doc.Entities.Add(new Line(P(tip.X - (2 * unit), tip.Y + (2 * unit)), P(tip.X, tip.Y)) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, tip.Y), P(tip.X + (2 * unit), tip.Y + (2 * unit))) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, tip.Y), P(tip.X, top)) { Layer = layer });
        doc.Entities.Add(new Line(P(tip.X, top), P(tip.X + (dir * shelf), top)) { Layer = layer });
        doc.Entities.Add(new TextEntity
        {
            Value = text,
            InsertPoint = P(left ? tip.X - shelf + unit : tip.X + unit, top + (0.8 * unit)),
            Height = 2.5 * unit,
            Layer = layer,
        });
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

    // Условные обозначения — таблица слева от основной надписи: изображение (25 мм) и наименование (95 мм), строки по 8 мм.
    private static void Legend(CadDocument doc, SheetPlacement sheet, Layer pipes, Layer sprinklers, Layer marks, Layer labels)
    {
        const double x0 = 30;
        const double x1 = 55;
        const double x2 = 150;
        const double rowH = 8;
        string[] names =
        [
            "Трубопровод установки водяного пожаротушения",
            "Ороситель спринклерный",
            "Отметка оси трубопровода, м",
            "Условный проход трубопровода, мм",
        ];
        var top = 10 + (rowH * (names.Length + 1));
        var s = sheet.Scale;
        void Seg(double ax, double ay, double bx, double by) => doc.Entities.Add(new Line(sheet.At(ax, ay), sheet.At(bx, by)) { Layer = labels });

        doc.Entities.Add(new MText
        {
            Value = "Условные обозначения",
            InsertPoint = sheet.At((x0 + x2) / 2, top - (rowH / 2)),
            Height = 3.5 * s,
            AttachmentPoint = AttachmentPointType.MiddleCenter,
            Layer = labels,
        });
        for (var k = 0; k <= names.Length; k++)
        {
            Seg(x0, top - (rowH * (k + 1)), x2, top - (rowH * (k + 1)));
        }

        Seg(x0, top - rowH, x0, 10);
        Seg(x1, top - rowH, x1, 10);
        Seg(x2, top - rowH, x2, 10);

        for (var k = 0; k < names.Length; k++)
        {
            var y = top - (rowH * (k + 1.5));
            var cx = (x0 + x1) / 2;
            switch (k)
            {
                case 0:
                    doc.Entities.Add(new Line(sheet.At(x0 + 3, y), sheet.At(x1 - 3, y)) { Layer = pipes });
                    break;
                case 1:
                    doc.Entities.Add(new Line(sheet.At(x0 + 3, y), sheet.At(x1 - 3, y)) { Layer = pipes });
                    doc.Entities.Add(new Circle(sheet.At(cx, y), 1 * s) { Layer = sprinklers });
                    break;
                case 2:
                    doc.Entities.Add(new Line(sheet.At(cx - 2, y + 1), sheet.At(cx, y - 1)) { Layer = marks });
                    doc.Entities.Add(new Line(sheet.At(cx, y - 1), sheet.At(cx + 2, y + 1)) { Layer = marks });
                    doc.Entities.Add(new Line(sheet.At(cx, y - 1), sheet.At(cx, y + 2.5)) { Layer = marks });
                    doc.Entities.Add(new Line(sheet.At(cx, y + 2.5), sheet.At(cx + 9, y + 2.5)) { Layer = marks });
                    break;
                default:
                    doc.Entities.Add(new MText { Value = "DN25", InsertPoint = sheet.At(cx, y), Height = 2.5 * s, AttachmentPoint = AttachmentPointType.MiddleCenter, Layer = labels });
                    break;
            }

            doc.Entities.Add(new MText { Value = names[k], InsertPoint = sheet.At(x1 + 2, y), Height = 2.5 * s, AttachmentPoint = AttachmentPointType.MiddleLeft, Layer = labels });
        }
    }

    // Отметка в метрах со знаком, как на чертежах: ±0,000 (код AutoCAD %%p — знак «±» в любом шрифте), +3,300, −1,500.
    private static string Elevation(Length z) =>
        (z.Meters < 0 ? "−" : z.Meters > 0 ? "+" : "%%p") + Math.Abs(z.Meters).ToString("0.000", Ru);
}
