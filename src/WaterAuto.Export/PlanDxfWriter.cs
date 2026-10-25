using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Оформление DXF: размеры значков и текста на плане, мм. Это не нормы — только вид чертежа.</summary>
public sealed record PlanDxfStyle(double SprinklerRadiusMm = 100, double TextHeightMm = 200, double IssueRadiusMm = 500);

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
    public const string SprinklerBlock = "WA_ОРОСИТЕЛЬ";

    /// <param name="basePath">Исходный чертёж этажа: план труб рисуется поверх него в одном файле; null — только слои программы.</param>
    public static void Write(string path, SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle? style = null, PlanCalcSummary? summary = null, IReadOnlyList<PlanInletNote>? inlets = null, string? basePath = null)
    {
        var source = basePath is null ? null : DxfDrawing.Read(basePath);
        DxfDrawing.Save(path, Build(layout, routes, issues, style ?? new PlanDxfStyle(), summary, inlets, source));
    }

    public static CadDocument Build(SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle style, PlanCalcSummary? summary = null, IReadOnlyList<PlanInletNote>? inlets = null, CadDocument? source = null)
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
            foreach (var seg in route.Segments)
            {
                var a = nodes[seg.From].Location;
                var b = nodes[seg.To].Location;
                doc.Entities.Add(new Line(new XYZ(a.XMm, a.YMm, 0), new XYZ(b.XMm, b.YMm, 0)) { Layer = pipes });

                if (DxfDrawing.NeedsLabel(seg, byTo))
                {
                    doc.Entities.Add(DxfDrawing.Label(seg, new XY(a.XMm, a.YMm), new XY(b.XMm, b.YMm), style.TextHeightMm, segments));
                }
            }
        }

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
