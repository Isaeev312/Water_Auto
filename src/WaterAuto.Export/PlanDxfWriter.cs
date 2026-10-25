using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Оформление DXF: размеры значков и текста на плане, мм. Это не нормы — только вид чертежа.</summary>
public sealed record PlanDxfStyle(double SprinklerRadiusMm = 100, double TextHeightMm = 200, double IssueRadiusMm = 500, string Font = DocumentStyle.DefaultFont);

/// <summary>
/// Сводка расчёта для листа плана: строки «параметр — значение» и оросители диктующей зоны.
/// Собирается из результата расчёта (Application): Export не знает про Calc.
/// </summary>
/// <param name="ZoneMarginMm">Отступ контура зоны от крайних оросителей, мм.</param>
public sealed record PlanCalcSummary(
    string Title,
    IReadOnlyList<(string Label, string Value)> Rows,
    IReadOnlyList<string> ZoneSprinklerIds,
    string ZoneLabel,
    double ZoneMarginMm);

/// <summary>
/// Размерная цепочка привязок: точки по одной прямой (стена, оросители, стена); размерная линия — со смещением Offset, мм,
/// влево от направления от первой точки к последней (отрицательное — вправо).
/// </summary>
public sealed record PlanDimensionChain(IReadOnlyList<PlanPoint> Points, double Offset);

/// <summary>Выноска у точки ввода: строки текста (DN, отметка, ссылка на раздел НС).</summary>
public sealed record PlanInletNote(PlanPoint At, IReadOnlyList<string> Lines);

/// <summary>
/// DXF-наложение на чертёж пользователя (README §7): в тех же координатах, в мм.
/// ПТ_ОРОСИТЕЛИ — блок с атрибутом ID; ПТ_ТРУБЫ — участки трассы; ПТ_УЧАСТКИ — DN и длины
/// на подводящем, питающем и там, где диаметр меняется; ПТ_ЗАМЕЧАНИЯ — замечания с координатами
/// (кружок и текст), остальные — списком над планом.
/// </summary>
public static class PlanDxfWriter
{
    public const string SprinklerLayer = "ПТ_ОРОСИТЕЛИ";
    public const string PipeLayer = "ПТ_ТРУБЫ";
    public const string SegmentLayer = "ПТ_УЧАСТКИ";
    public const string IssueLayer = "ПТ_ЗАМЕЧАНИЯ";
    public const string ZoneLayer = "ПТ_ДИКТ_ЗОНА";
    public const string SummaryLayer = "ПТ_РАСЧЁТ";
    public const string SupportLayer = "ПТ_КРЕПЛЕНИЯ";
    public const string DimensionLayer = "ПТ_ПРИВЯЗКИ";
    public const string HydrantLayer = "ПТ_ПК";
    public const string SprinklerBlock = "WA_ОРОСИТЕЛЬ";

    /// <param name="basePath">Исходный чертёж этажа: план труб рисуется поверх него в одном файле; null — только слои программы.</param>
    public static void Write(string path, SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle? style = null, PlanCalcSummary? summary = null, IReadOnlyList<PlanInletNote>? inlets = null, string? basePath = null, IReadOnlyList<SupportPoint>? supports = null, IReadOnlyList<PlanDimensionChain>? dimensions = null, IReadOnlyList<PlanPoint>? hydrants = null)
    {
        var source = basePath is null ? null : DxfDrawing.Read(basePath);
        style ??= new PlanDxfStyle();
        DxfDrawing.Save(path, Build(layout, routes, issues, style, summary, inlets, source, supports, dimensions, hydrants), style.Font);
    }

    public static CadDocument Build(SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle style, PlanCalcSummary? summary = null, IReadOnlyList<PlanInletNote>? inlets = null, CadDocument? source = null, IReadOnlyList<SupportPoint>? supports = null, IReadOnlyList<PlanDimensionChain>? dimensions = null, IReadOnlyList<PlanPoint>? hydrants = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(style);
        var doc = source ?? new CadDocument(ACadVersion.AC1032);
        if (source is null)
        {
            doc.Header.InsUnits = UnitsType.Millimeters;
        }

        var sprinklers = DxfDrawing.AddLayer(doc, SprinklerLayer, 1);
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 4, LineWeightType.W40); // трубопроводы — 0,40 мм
        var segments = DxfDrawing.AddLayer(doc, SegmentLayer, 7);
        var marks = DxfDrawing.AddLayer(doc, IssueLayer, 6);
        var zoneLayer = DxfDrawing.AddLayer(doc, ZoneLayer, 2);
        var summaryLayer = DxfDrawing.AddLayer(doc, SummaryLayer, 7);

        if (!doc.BlockRecords.TryGetValue(SprinklerBlock, out var block))
        {
            block = new BlockRecord(SprinklerBlock);
            block.Entities.Add(new Circle(XYZ.Zero, style.SprinklerRadiusMm));
            block.Entities.Add(new AttributeDefinition { Tag = "ID", Prompt = "ID оросителя", Height = style.TextHeightMm / 2, InsertPoint = new XYZ(style.SprinklerRadiusMm, style.SprinklerRadiusMm, 0), Flags = AttributeFlags.Hidden });
            doc.BlockRecords.Add(block);
        }
        foreach (var s in layout.Sprinklers)
        {
            var insert = new Insert(block) { InsertPoint = new XYZ(s.Location.XMm, s.Location.YMm, 0), Layer = sprinklers };
            doc.Entities.Add(insert);
            var id = insert.Attributes.FirstOrDefault(a => a.Tag == "ID");
            if (id is null)
            {
                id = new AttributeEntity { Tag = "ID", Height = style.TextHeightMm / 2 };
                insert.Attributes.Add(id);
            }

            id.Value = s.Id;
            id.InsertPoint = insert.InsertPoint + new XYZ(style.SprinklerRadiusMm, style.SprinklerRadiusMm, 0);
            id.Layer = sprinklers;
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var byTo = route.Segments.ToDictionary(s => s.To);
            var loops = route.Loops.Select(s => s.Id).ToHashSet();
            foreach (var seg in route.Segments.Concat(route.Loops))
            {
                var a = nodes[seg.From].Location;
                var b = nodes[seg.To].Location;
                doc.Entities.Add(new Line(new XYZ(a.XMm, a.YMm, 0), new XYZ(b.XMm, b.YMm, 0)) { Layer = pipes });

                if (loops.Contains(seg.Id) || DxfDrawing.NeedsLabel(seg, byTo))
                {
                    doc.Entities.Add(DxfDrawing.Label(seg, new XY(a.XMm, a.YMm), new XY(b.XMm, b.YMm), style.TextHeightMm, segments));
                }
            }
        }

        // Информационные замечания на план не выносятся (остаются в issues.txt), чтобы не загромождать лист.
        issues = issues.Where(i => i.Level != IssueLevel.Info).ToList();
        foreach (var issue in issues.Where(i => i.Location is not null))
        {
            var at = issue.Location!.Value;
            doc.Entities.Add(new Circle(new XYZ(at.XMm, at.YMm, 0), style.IssueRadiusMm) { Layer = marks });
            doc.Entities.Add(new MText
            {
                Value = $"{issue.Level}: {issue.Code} — {issue.Message}",
                InsertPoint = new XYZ(at.XMm + style.IssueRadiusMm, at.YMm + style.IssueRadiusMm, 0),
                Height = style.TextHeightMm,
                RectangleWidth = 40 * style.TextHeightMm,
                Layer = marks,
            });
        }

        var unlocated = issues.Where(i => i.Location is null).ToList();
        if (unlocated.Count > 0)
        {
            var points = layout.Sprinklers.Select(s => s.Location)
                .Concat(routes.SelectMany(r => r.Nodes.Select(n => n.Location)))
                .DefaultIfEmpty(new PlanPoint(0, 0))
                .ToList();
            doc.Entities.Add(new MText
            {
                Value = string.Join("\\P", unlocated.Select(i => $"{i.Level}: {i.Code} — {i.Message}")),
                InsertPoint = new XYZ(points.Min(p => p.XMm), points.Max(p => p.YMm) + (unlocated.Count + 2) * 2 * style.TextHeightMm, 0),
                Height = style.TextHeightMm,
                RectangleWidth = 60 * style.TextHeightMm,
                Layer = marks,
            });
        }

        if (summary is not null)
        {
            AddSummary(doc, layout, routes, summary, style, zoneLayer, summaryLayer);
        }

        // Опоры: косой крест на трубе (слой ПТ_КРЕПЛЕНИЯ).
        if (supports is { Count: > 0 })
        {
            var supportLayer = DxfDrawing.AddLayer(doc, SupportLayer, 8);
            var d = style.SprinklerRadiusMm * 0.75;
            foreach (var s in supports)
            {
                doc.Entities.Add(new Line(new XYZ(s.At.XMm - d, s.At.YMm - d, 0), new XYZ(s.At.XMm + d, s.At.YMm + d, 0)) { Layer = supportLayer });
                doc.Entities.Add(new Line(new XYZ(s.At.XMm - d, s.At.YMm + d, 0), new XYZ(s.At.XMm + d, s.At.YMm - d, 0)) { Layer = supportLayer });
            }
        }

        // Пожарные краны: кружок и подпись ПК-n (слой ПТ_ПК).
        if (hydrants is { Count: > 0 })
        {
            var hydrantLayer = DxfDrawing.AddLayer(doc, HydrantLayer, 1);
            for (var k = 0; k < hydrants.Count; k++)
            {
                var h = hydrants[k];
                doc.Entities.Add(new Circle(new XYZ(h.XMm, h.YMm, 0), style.SprinklerRadiusMm * 2.5) { Layer = hydrantLayer });
                doc.Entities.Add(new TextEntity
                {
                    Value = $"ПК-{k + 1}",
                    InsertPoint = new XYZ(h.XMm + (style.SprinklerRadiusMm * 3), h.YMm + (style.SprinklerRadiusMm * 3), 0),
                    Height = style.TextHeightMm,
                    Layer = hydrantLayer,
                });
            }
        }

        if (dimensions is { Count: > 0 })
        {
            var dimLayer = DxfDrawing.AddLayer(doc, DimensionLayer, 3);
            foreach (var chain in dimensions.Where(c => c.Points.Count >= 2))
            {
                AddChain(doc, chain, style, dimLayer);
            }
        }

        // Выноска у ввода: наклонная линия и полка, над полкой — текст (DN, отметка, ссылка на раздел НС).
        foreach (var note in inlets ?? [])
        {
            var h = style.TextHeightMm;
            var corner = new XY(note.At.XMm + (7.5 * h), note.At.YMm + (7.5 * h));
            var width = note.Lines.Max(l => l.Length) * 0.75 * h;
            doc.Entities.Add(new LwPolyline([new XY(note.At.XMm, note.At.YMm), corner, new XY(corner.X + width, corner.Y)]) { Layer = segments });
            doc.Entities.Add(new MText
            {
                Value = string.Join("\\P", note.Lines),
                InsertPoint = new XYZ(corner.X, corner.Y + ((note.Lines.Count + 0.5) * 1.6 * h), 0),
                Height = h,
                RectangleWidth = width,
                Layer = segments,
            });
        }

        return doc;
    }

    // Размерная цепочка по ГОСТ 2.307: выносные линии, размерная линия с засечками под 45°, размер в мм над линией.
    private static void AddChain(CadDocument doc, PlanDimensionChain chain, PlanDxfStyle style, Layer layer)
    {
        var a = chain.Points[0];
        var b = chain.Points[^1];
        var length = Math.Sqrt(((b.XMm - a.XMm) * (b.XMm - a.XMm)) + ((b.YMm - a.YMm) * (b.YMm - a.YMm)));
        if (length < 1)
        {
            return;
        }

        double ux = (b.XMm - a.XMm) / length, uy = (b.YMm - a.YMm) / length;
        double nx = -uy, ny = ux;
        var h = style.TextHeightMm;
        var over = h * 0.5;
        var tick = h * 0.5;
        XYZ On(PlanPoint p, double shift) => new(p.XMm + (nx * shift), p.YMm + (ny * shift), 0);
        var sign = Math.Sign(chain.Offset) is var s && s != 0 ? s : 1;

        doc.Entities.Add(new Line(On(a, chain.Offset), On(b, chain.Offset)) { Layer = layer });
        foreach (var p in chain.Points)
        {
            doc.Entities.Add(new Line(On(p, sign * h * 0.25), On(p, chain.Offset + (sign * over))) { Layer = layer });
            var c = On(p, chain.Offset);
            doc.Entities.Add(new Line(new XYZ(c.X - ((ux + nx) * tick), c.Y - ((uy + ny) * tick), 0), new XYZ(c.X + ((ux + nx) * tick), c.Y + ((uy + ny) * tick), 0)) { Layer = layer });
        }

        var angle = Math.Atan2(uy, ux);
        if (angle > Math.PI / 2 || angle <= -Math.PI / 2)
        {
            angle += Math.PI;
        }

        for (var k = 1; k < chain.Points.Count; k++)
        {
            var (p, q) = (chain.Points[k - 1], chain.Points[k]);
            var span = Math.Sqrt(((q.XMm - p.XMm) * (q.XMm - p.XMm)) + ((q.YMm - p.YMm) * (q.YMm - p.YMm)));
            var mid = new PlanPoint((p.XMm + q.XMm) / 2, (p.YMm + q.YMm) / 2);
            var at = On(mid, chain.Offset + (h * 0.3));
            doc.Entities.Add(new TextEntity
            {
                Value = Math.Round(span).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                InsertPoint = at,
                AlignmentPoint = at,
                HorizontalAlignment = TextHorizontalAlignment.Center,
                Height = h,
                Rotation = angle,
                Layer = layer,
            });
        }
    }

    // Диктующая зона: контур по крайним оросителям с отступом, кружки у оросителей зоны и подпись;
    // сводка расчёта — многострочный текст справа от плана (приём из зарубежных комплектов, docs/foreign_drawings_ideas.md).
    private static void AddSummary(CadDocument doc, SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, PlanCalcSummary summary, PlanDxfStyle style, Layer zoneLayer, Layer summaryLayer)
    {
        var ids = summary.ZoneSprinklerIds.ToHashSet(StringComparer.Ordinal);
        var zone = layout.Sprinklers.Where(s => ids.Contains(s.Id)).Select(s => s.Location).ToList();
        if (zone.Count > 0)
        {
            var m = summary.ZoneMarginMm;
            double x0 = zone.Min(p => p.XMm) - m, x1 = zone.Max(p => p.XMm) + m, y0 = zone.Min(p => p.YMm) - m, y1 = zone.Max(p => p.YMm) + m;
            doc.Entities.Add(new LwPolyline([new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1)]) { IsClosed = true, Layer = zoneLayer });
            foreach (var p in zone)
            {
                doc.Entities.Add(new Circle(new XYZ(p.XMm, p.YMm, 0), 2 * style.SprinklerRadiusMm) { Layer = zoneLayer });
            }

            doc.Entities.Add(new MText
            {
                Value = summary.ZoneLabel,
                InsertPoint = new XYZ(x0, y1 + (2 * style.TextHeightMm), 0),
                Height = style.TextHeightMm,
                RectangleWidth = Math.Max(x1 - x0, 30 * style.TextHeightMm),
                Layer = zoneLayer,
            });
        }

        var all = layout.Sprinklers.Select(s => s.Location).Concat(routes.SelectMany(r => r.Nodes.Select(n => n.Location))).DefaultIfEmpty(new PlanPoint(0, 0)).ToList();
        doc.Entities.Add(new MText
        {
            Value = $"{summary.Title}\\P" + string.Join("\\P", summary.Rows.Select(r => $"{r.Label} — {r.Value}")),
            InsertPoint = new XYZ(all.Max(p => p.XMm) + (10 * style.TextHeightMm), all.Max(p => p.YMm), 0),
            Height = style.TextHeightMm,
            RectangleWidth = 60 * style.TextHeightMm,
            Layer = summaryLayer,
        });
    }
}
