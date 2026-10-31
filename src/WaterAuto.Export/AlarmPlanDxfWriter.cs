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

    public static void Write(string path, DetectorLayout layout, IReadOnlyList<DetectorZone> zones, IReadOnlyList<Issue> issues, string font = DocumentStyle.DefaultFont, double textHeightMm = 250, IReadOnlyList<PlacedManual>? manuals = null, IReadOnlyDictionary<string, string>? zoneOf = null, TitleBlock? stamp = null) =>
        DxfDrawing.Save(path, Build(layout, zones, issues, textHeightMm, manuals, zoneOf, stamp), font);

    /// <param name="manuals">Ручные извещатели — квадратный блок на слое АПС_ИПР.</param>
    /// <param name="zoneOf">ЗКСПС каждого ИП — пишется в сводку помещения.</param>
    /// <param name="stamp">Основная надпись: с ней план оформляется листом в рамке (ГОСТ Р 21.101) в масштабе 1:100 или мельче, с условными обозначениями и примечаниями под планом.</param>
    public static CadDocument Build(DetectorLayout layout, IReadOnlyList<DetectorZone> zones, IReadOnlyList<Issue> issues, double textHeightMm = 250, IReadOnlyList<PlacedManual>? manuals = null, IReadOnlyDictionary<string, string>? zoneOf = null, TitleBlock? stamp = null)
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

        // Лист: под планом — 70 мм бумаги на условные обозначения и примечания; масштаб 1:100, не помещается — мельче.
        SheetPlacement? sheet = null;
        var notesBottom = 0.0;
        if (stamp is not null && zones.Count > 0)
        {
            var vs = zones.SelectMany(z => z.Outline.Vertices).ToList();
            var (x0, y0, x1, y1) = (vs.Min(v => v.XMm), vs.Min(v => v.YMm), vs.Max(v => v.XMm), vs.Max(v => v.YMm));
            sheet = new[] { 100.0, 200, 500 }.Select(sc => GostSheet.FitAtScale(x0 - 2000, y0 - 2000 - (70 * sc), x1 + 2000, y1 + 2000, sc)).FirstOrDefault(f => f is not null)
                ?? GostSheet.Fit(x0 - 2000, y0 - 2000 - 70000, x1 + 2000, y1 + 2000);
            textHeightMm = 2.5 * sheet.Scale;
            notesBottom = y0 - 2000;
            GostSheet.Draw(doc, sheet, stamp);
        }

        var radius = textHeightMm * 0.6;

        var smoke = Block(doc, SmokeBlock, radius, dot: false, textHeightMm);
        var heat = Block(doc, HeatBlock, radius, dot: true, textHeightMm);

        var summaryLines = new List<string>();
        foreach (var z in zones)
        {
            doc.Entities.Add(new LwPolyline(z.Outline.Vertices.Select(v => new XY(v.XMm, v.YMm)).ToList()) { IsClosed = true, Layer = contours });
            var c = z.Outline.Centroid;
            var count = layout.CountByRoom.GetValueOrDefault(z.RoomId);
            var zonesText = zoneOf is null ? "" : string.Join(", ", layout.Detectors.Where(d => d.RoomId == z.RoomId && zoneOf.ContainsKey(d.Id)).Select(d => zoneOf[d.Id]).Distinct());
            var manualCount = manuals?.Count(m => m.RoomId == z.RoomId) ?? 0;
            var line = string.Create(ru, $"{z.RoomName}: {KindText(z.Kind)} ИП {z.ModelName ?? ""} — {count} шт., алгоритм {z.Algorithm}, {(z.Addressable ? "адресные" : "безадресные")}, H = {z.CeilingHeight.Meters:0.##} м{(manualCount > 0 ? $"; ИПР — {manualCount} шт." : "")}{(zonesText.Length > 0 ? $"; {zonesText}" : "")}");
            if (sheet is null)
            {
                doc.Entities.Add(new TextEntity(line) { InsertPoint = new XYZ(c.XMm, c.YMm - (2 * textHeightMm), 0), Height = textHeightMm, Layer = summary });
            }
            else
            {
                // На листе — подпись помещения на плане, сводка — в примечаниях под планом.
                doc.Entities.Add(new TextEntity(z.RoomName) { InsertPoint = new XYZ(c.XMm, c.YMm, 0), Height = textHeightMm * 1.4, Layer = summary });
                summaryLines.Add(line);
            }
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

        var planIssues = issues.Where(i => i.Location is not null && (i.Code.StartsWith("aps.layout", StringComparison.Ordinal) || i.Code.StartsWith("aps.zone", StringComparison.Ordinal))).ToList();
        for (var k = 0; k < planIssues.Count; k++)
        {
            var at = planIssues[k].Location!.Value;
            doc.Entities.Add(new Circle(new XYZ(at.XMm, at.YMm, 0), 2 * radius) { Layer = marks });
            // На листе у метки — только номер, текст — в примечаниях; без листа — текст рядом с меткой.
            doc.Entities.Add(new TextEntity(sheet is null ? $"{planIssues[k].Level}: {planIssues[k].Message}" : (k + 1).ToString(ru)) { InsertPoint = new XYZ(at.XMm + (2 * radius), at.YMm, 0), Height = textHeightMm * 0.8, Layer = marks });
        }

        if (sheet is not null)
        {
            Legend(doc, sheet, summary, textHeightMm, radius, manuals is { Count: > 0 }, zones.Any(z => z.Kind == DetectorKind.Heat), zones.Any(z => z.Kind == DetectorKind.Smoke));
            var notes = summaryLines.Concat(planIssues.Select((i, k) => $"{k + 1}. {i.Level}: {i.Message}")).ToList();
            if (notes.Count > 0)
            {
                var x = sheet.At(100, 0).X;
                var width = sheet.At(sheet.Format.WidthMm - 15, 0).X - x;
                doc.Entities.Add(new MText
                {
                    Value = "Примечания:\\P" + string.Join("\\P", notes.Select((n, k) => $"{k + 1}. {n}")),
                    InsertPoint = new XYZ(x, notesBottom - (2 * textHeightMm), 0),
                    Height = textHeightMm * 0.8,
                    RectangleWidth = width,
                    Layer = summary,
                });
            }
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

    /// <summary>Условные обозначения — слева под планом: дымовой ИП, тепловой ИП, ИПР.</summary>
    private static void Legend(CadDocument doc, SheetPlacement sheet, Layer layer, double textHeightMm, double radius, bool manuals, bool heat, bool smoke)
    {
        var x = sheet.At(35, 0).X;
        var y = sheet.At(0, 115).Y;
        var step = textHeightMm * 2.5;
        doc.Entities.Add(new TextEntity("Условные обозначения") { InsertPoint = new XYZ(x, y, 0), Height = textHeightMm, Layer = layer });
        y -= step;
        if (smoke)
        {
            doc.Entities.Add(new Circle(new XYZ(x + radius, y + (textHeightMm / 2), 0), radius) { Layer = layer });
            doc.Entities.Add(new TextEntity("— извещатель пожарный дымовой точечный адресный") { InsertPoint = new XYZ(x + (4 * radius), y, 0), Height = textHeightMm * 0.8, Layer = layer });
            y -= step;
        }

        if (heat)
        {
            doc.Entities.Add(new Circle(new XYZ(x + radius, y + (textHeightMm / 2), 0), radius) { Layer = layer });
            doc.Entities.Add(new Circle(new XYZ(x + radius, y + (textHeightMm / 2), 0), radius / 4) { Layer = layer });
            doc.Entities.Add(new TextEntity("— извещатель пожарный тепловой точечный адресный") { InsertPoint = new XYZ(x + (4 * radius), y, 0), Height = textHeightMm * 0.8, Layer = layer });
            y -= step;
        }

        if (manuals)
        {
            var cy = y + (textHeightMm / 2);
            doc.Entities.Add(new LwPolyline([new XY(x, cy - radius), new XY(x + (2 * radius), cy - radius), new XY(x + (2 * radius), cy + radius), new XY(x, cy + radius)]) { IsClosed = true, Layer = layer });
            doc.Entities.Add(new TextEntity("— извещатель пожарный ручной адресный, h = 1,5 м") { InsertPoint = new XYZ(x + (4 * radius), y, 0), Height = textHeightMm * 0.8, Layer = layer });
        }
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
