using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// План расстановки пожарных извещателей (ADR 0012, этап 2) в DXF: слои АПС_ параллельно ПТ_ (ADR 0012, п. 7).
/// АПС_ИЗВЕЩАТЕЛИ — блок с атрибутом ID (дымовой — круг, тепловой — круг с точкой); АПС_ЗАМЕЧАНИЯ — метки замечаний
/// раскладки; АПС_РАСЧЁТ — сводка по помещениям. Контуры помещений — на АПС_КОНТУР, чтобы лист читался и без подложки.
/// </summary>
public static class AlarmPlanDxfWriter
{
    public const string DetectorLayer = "АПС_ИЗВЕЩАТЕЛИ";
    public const string ContourLayer = "АПС_КОНТУР";
    public const string IssueLayer = "АПС_ЗАМЕЧАНИЯ";
    public const string SummaryLayer = "АПС_РАСЧЁТ";
    public const string SmokeBlock = "АПС_ИП_ДЫМ";
    public const string HeatBlock = "АПС_ИП_ТЕПЛ";
    public const string ManualBlock = "АПС_ИПР";
    public const string ManualLayer = "АПС_ИПР";

    public static void Write(string path, DetectorLayout layout, IReadOnlyList<DetectorZone> zones, IReadOnlyList<Issue> issues, string font = DocumentStyle.DefaultFont, double textHeightMm = 250, IReadOnlyList<PlacedManual>? manuals = null, IReadOnlyDictionary<string, string>? zoneOf = null) =>
        DxfDrawing.Save(path, Build(layout, zones, issues, textHeightMm, manuals, zoneOf), font);

    /// <param name="manuals">Ручные извещатели — квадратный блок на слое АПС_ИПР.</param>
    /// <param name="zoneOf">ЗКСПС каждого ИП — пишется в атрибут блока и в сводку помещения.</param>
    public static CadDocument Build(DetectorLayout layout, IReadOnlyList<DetectorZone> zones, IReadOnlyList<Issue> issues, double textHeightMm = 250, IReadOnlyList<PlacedManual>? manuals = null, IReadOnlyDictionary<string, string>? zoneOf = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(issues);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var doc = new CadDocument();
        var detectors = DxfDrawing.AddLayer(doc, DetectorLayer, 1);
        var contours = DxfDrawing.AddLayer(doc, ContourLayer, 3);
        var marks = DxfDrawing.AddLayer(doc, IssueLayer, 6);
        var summary = DxfDrawing.AddLayer(doc, SummaryLayer, 7);
        var radius = textHeightMm * 0.6;

        var smoke = Block(doc, SmokeBlock, radius, dot: false, textHeightMm);
        var heat = Block(doc, HeatBlock, radius, dot: true, textHeightMm);

        foreach (var z in zones)
        {
            doc.Entities.Add(new LwPolyline(z.Outline.Vertices.Select(v => new XY(v.XMm, v.YMm)).ToList()) { IsClosed = true, Layer = contours });
            var c = z.Outline.Centroid;
            var count = layout.CountByRoom.GetValueOrDefault(z.RoomId);
            var zonesText = zoneOf is null ? "" : string.Join(", ", layout.Detectors.Where(d => d.RoomId == z.RoomId && zoneOf.ContainsKey(d.Id)).Select(d => zoneOf[d.Id]).Distinct());
            var manualCount = manuals?.Count(m => m.RoomId == z.RoomId) ?? 0;
            doc.Entities.Add(new TextEntity(string.Create(ru, $"{z.RoomName}: {KindText(z.Kind)} ИП {z.ModelName ?? ""} — {count} шт., алгоритм {z.Algorithm}, {(z.Addressable ? "адресные" : "безадресные")}, H = {z.CeilingHeight.Meters:0.##} м{(manualCount > 0 ? $"; ИПР — {manualCount} шт." : "")}{(zonesText.Length > 0 ? $"; {zonesText}" : "")}"))
            {
                InsertPoint = new XYZ(c.XMm, c.YMm - (2 * textHeightMm), 0),
                Height = textHeightMm,
                Layer = summary,
            });
        }

        foreach (var d in layout.Detectors)
        {
            var insert = new Insert(d.Kind == DetectorKind.Smoke ? smoke : heat) { InsertPoint = new XYZ(d.Location.XMm, d.Location.YMm, 0), Layer = detectors };
            doc.Entities.Add(insert);
            var id = insert.Attributes.FirstOrDefault(a => a.Tag == "ID");
            if (id is null)
            {
                id = new AttributeEntity { Tag = "ID", Height = textHeightMm / 2 };
                insert.Attributes.Add(id);
            }

            id.Value = d.Id;
            id.InsertPoint = new XYZ(d.Location.XMm + radius, d.Location.YMm + radius, 0);
            id.Layer = detectors;
        }

        if (manuals is { Count: > 0 })
        {
            var manualLayer = DxfDrawing.AddLayer(doc, ManualLayer, 1);
            var block = ManualBlockRecord(doc, radius, textHeightMm);
            foreach (var m in manuals)
            {
                var insert = new Insert(block) { InsertPoint = new XYZ(m.Location.XMm, m.Location.YMm, 0), Layer = manualLayer };
                doc.Entities.Add(insert);
                var id = insert.Attributes.FirstOrDefault(a => a.Tag == "ID");
                if (id is null)
                {
                    id = new AttributeEntity { Tag = "ID", Height = textHeightMm / 2 };
                    insert.Attributes.Add(id);
                }

                id.Value = m.Id;
                id.InsertPoint = new XYZ(m.Location.XMm + radius, m.Location.YMm + radius, 0);
                id.Layer = manualLayer;
            }
        }

        foreach (var issue in issues.Where(i => i.Location is not null && (i.Code.StartsWith("aps.layout", StringComparison.Ordinal) || i.Code.StartsWith("aps.zone", StringComparison.Ordinal))))
        {
            var at = issue.Location!.Value;
            doc.Entities.Add(new Circle(new XYZ(at.XMm, at.YMm, 0), 2 * radius) { Layer = marks });
            doc.Entities.Add(new TextEntity($"{issue.Level}: {issue.Message}") { InsertPoint = new XYZ(at.XMm + (2 * radius), at.YMm, 0), Height = textHeightMm * 0.8, Layer = marks });
        }

        return doc;
    }

    private static BlockRecord Block(CadDocument doc, string name, double radius, bool dot, double textHeightMm)
    {
        if (doc.BlockRecords.TryGetValue(name, out var block))
        {
            return block;
        }

        block = new BlockRecord(name);
        block.Entities.Add(new Circle(XYZ.Zero, radius));
        if (dot)
        {
            block.Entities.Add(new Circle(XYZ.Zero, radius / 4));
        }

        block.Entities.Add(new AttributeDefinition { Tag = "ID", Prompt = "ID извещателя", Height = textHeightMm / 2, InsertPoint = new XYZ(radius, radius, 0), Flags = AttributeFlags.Hidden });
        doc.BlockRecords.Add(block);
        return block;
    }

    private static BlockRecord ManualBlockRecord(CadDocument doc, double radius, double textHeightMm)
    {
        if (doc.BlockRecords.TryGetValue(ManualBlock, out var block))
        {
            return block;
        }

        block = new BlockRecord(ManualBlock);
        block.Entities.Add(new LwPolyline([new XY(-radius, -radius), new XY(radius, -radius), new XY(radius, radius), new XY(-radius, radius)]) { IsClosed = true });
        block.Entities.Add(new AttributeDefinition { Tag = "ID", Prompt = "ID ИПР", Height = textHeightMm / 2, InsertPoint = new XYZ(radius, radius, 0), Flags = AttributeFlags.Hidden });
        doc.BlockRecords.Add(block);
        return block;
    }

    private static string KindText(DetectorKind kind) => kind == DetectorKind.Smoke ? "дымовые" : "тепловые";
}
