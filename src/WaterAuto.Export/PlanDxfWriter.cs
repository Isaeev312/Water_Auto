using System.Globalization;
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
    public const string SprinklerBlock = "WA_ОРОСИТЕЛЬ";

    public static void Write(string path, SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle? style = null)
    {
        var doc = Build(layout, routes, issues, style ?? new PlanDxfStyle());
        DxfWriter.Write(path, doc, false);
    }

    public static CadDocument Build(SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<Issue> issues, PlanDxfStyle style)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(style);
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        var sprinklers = AddLayer(doc, SprinklerLayer, 1);
        var pipes = AddLayer(doc, PipeLayer, 4);
        var segments = AddLayer(doc, SegmentLayer, 7);
        var marks = AddLayer(doc, IssueLayer, 6);

        var block = new BlockRecord(SprinklerBlock);
        block.Entities.Add(new Circle(XYZ.Zero, style.SprinklerRadiusMm));
        block.Entities.Add(new AttributeDefinition { Tag = "ID", Prompt = "ID оросителя", Height = style.TextHeightMm / 2, InsertPoint = new XYZ(style.SprinklerRadiusMm, style.SprinklerRadiusMm, 0), Flags = AttributeFlags.Hidden });
        doc.BlockRecords.Add(block);
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

                var parentDn = byTo.TryGetValue(seg.From, out var parent) ? parent.Dn : (int?)null;
                if (seg.Role != RouteSegmentRole.Row || parentDn != seg.Dn)
                {
                    doc.Entities.Add(Label(seg, a, b, style, segments));
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

        return doc;
    }

    // Подпись участка «DN32 L=1,50 м» по середине, вдоль трубы.
    private static TextEntity Label(RouteSegment seg, PlanPoint a, PlanPoint b, PlanDxfStyle style, Layer layer)
    {
        var angle = Math.Atan2(b.YMm - a.YMm, b.XMm - a.XMm);
        if (angle > Math.PI / 2 || angle <= -Math.PI / 2)
        {
            angle += Math.PI; // текст не вверх ногами
        }

        var mid = new XYZ(((a.XMm + b.XMm) / 2) - (Math.Sin(angle) * style.TextHeightMm * 0.5), ((a.YMm + b.YMm) / 2) + (Math.Cos(angle) * style.TextHeightMm * 0.5), 0);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return new TextEntity
        {
            Value = string.Create(ru, $"DN{seg.Dn} L={seg.Length.Meters:0.00} м"),
            InsertPoint = mid,
            Height = style.TextHeightMm,
            Rotation = angle,
            Layer = layer,
        };
    }

    private static Layer AddLayer(CadDocument doc, string name, short color)
    {
        var layer = new Layer(name) { Color = new Color(color) };
        doc.Layers.Add(layer);
        return layer;
    }
}
