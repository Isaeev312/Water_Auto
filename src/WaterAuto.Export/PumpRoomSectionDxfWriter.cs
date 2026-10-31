using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Элемент разреза: подпись, отрезок по X, мм, и низ/верх, м (отметки от пола насосной); Pumps — число насосов в составе.</summary>
public sealed record SectionBlock(string Label, double X0, double X1, double Bottom, double Top, int Pumps = 0, int? Position = null);

/// <summary>Труба в разрезе: горизонтальный участок (X0…X1 на отметке Z, м) и вертикали в точках X с отметками, подпись.</summary>
public sealed record SectionPipe(string Label, double X0, double X1, double Z, IReadOnlyList<(double X, double ZFrom, double ZTo)> Verticals);

/// <summary>
/// Данные разреза насосной вдоль оси X (вид со стороны нижней стены): стены, пол, перекрытие, уровень земли, приямок,
/// оборудование и фундамент, трубы с подъёмами и опусками; отметки — в м от пола насосной.
/// </summary>
public sealed record PumpRoomSection(
    double X0,
    double X1,
    double WallMm,
    double FloorElevation,
    double Height,
    double Ground,
    (double X0, double X1, double Depth)? Pit,
    IReadOnlyList<SectionBlock> Blocks,
    IReadOnlyList<SectionPipe> Pipes,
    IReadOnlyList<string> Notes)
{
    /// <summary>Арматура на разрезе: X, мм, отметка оси, м, вид (gate, butterfly, check, vent), номер позиции, направление хода среды (0 — вправо, π — влево).</summary>
    public IReadOnlyList<(double X, double Z, string Kind, int? Position, double Angle)> Valves { get; init; } = [];
}

/// <summary>Разрез 1-1 насосной станции на листе с рамкой и основной надписью по форме 3, масштаб 1:50.</summary>
public static class PumpRoomSectionDxfWriter
{
    public const string BuildingLayer = "ПТ_НС_РАЗРЕЗ_СТРОИТ";

    public static void Write(string path, PumpRoomSection data, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(data, stamp), font);

    public static CadDocument Build(PumpRoomSection data, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var building = DxfDrawing.AddLayer(doc, BuildingLayer, 7, LineWeightType.W70);
        var equipment = DxfDrawing.AddLayer(doc, PumpRoomDxfWriter.EquipmentLayer, 1, LineWeightType.W35);
        var pipes = DxfDrawing.AddLayer(doc, PumpRoomDxfWriter.PipeLayer, 5, LineWeightType.W50);
        var text = DxfDrawing.AddLayer(doc, PumpRoomDxfWriter.TextLayer, 7);
        var marks = DxfDrawing.AddLayer(doc, "ПТ_НС_ОТМЕТКИ", 3);
        var ru = CultureInfo.GetCultureInfo("ru-RU");

        // Вертикаль в мм модели: z (м) → y = z·1000.
        var (x0, x1, t) = (data.X0, data.X1, data.WallMm);
        var zMin = -(data.Pit?.Depth ?? 0) - 0.3;
        var zMax = Math.Max(data.Height, data.Ground) + 0.6;
        var sheet = new[] { 50.0, 100 }
            .Select(sc => GostSheet.FitAtScale(x0 - t - 4000, (zMin * 1000) - 1500 - (85 * sc), x1 + t + 2500, (zMax * 1000) + 1500, sc))
            .FirstOrDefault(f => f is not null) ?? GostSheet.Fit(x0 - t - 4000, (zMin * 1000) - 9000, x1 + t + 2500, (zMax * 1000) + 1500);
        var h = 2.5 * sheet.Scale;
        GostSheet.Draw(doc, sheet, stamp);
        XYZ P(double x, double zM) => new(x, zM * 1000, 0);
        void L(double ax, double az, double bx, double bz, Layer layer) => doc.Entities.Add(new Line(P(ax, az), P(bx, bz)) { Layer = layer });
        void Text(double x, double y, string value, double height, double rotation = 0) =>
            doc.Entities.Add(new TextEntity { Value = value, InsertPoint = new XYZ(x, y, 0), Height = height, Rotation = rotation, Layer = text });

        // Отметка: треугольник с полкой и текстом, у левой стороны.
        void Mark(double x, double z, string caption)
        {
            var y = z * 1000;
            doc.Entities.Add(new LwPolyline([new XY(x, y), new XY(x - (0.6 * h), y + h), new XY(x + (0.6 * h), y + h)]) { IsClosed = true, Layer = marks });
            doc.Entities.Add(new Line(new XYZ(x, y + h, 0), new XYZ(x + (9 * h), y + h, 0)) { Layer = marks });
            Text(x + (0.8 * h), y + (1.3 * h), string.Create(ru, $"{z + data.FloorElevation:+0.000;−0.000;0.000}") + (caption.Length > 0 ? " " + caption : ""), 0.8 * h);
        }

        // Стены, пол, перекрытие, уровень земли (снаружи, пунктиром — короткими штрихами).
        foreach (var wx in new[] { x0 - t, x1 })
        {
            doc.Entities.Add(new LwPolyline([new XY(wx, (zMin + 0.3) * 1000), new XY(wx + t, (zMin + 0.3) * 1000), new XY(wx + t, (data.Height + 0.25) * 1000), new XY(wx, (data.Height + 0.25) * 1000)]) { IsClosed = true, Layer = building });
        }

        L(x0, 0, x1, 0, building);
        L(x0 - t, data.Height, x1 + t, data.Height, building);
        L(x0 - t, data.Height + 0.25, x1 + t, data.Height + 0.25, building);
        for (var gx = x0 - t - 3000; gx < x0 - t; gx += 3 * h)
        {
            L(gx, data.Ground, Math.Min(gx + (1.5 * h), x0 - t), data.Ground, building);
        }

        for (var gx = x1 + t; gx < x1 + t + 1500; gx += 3 * h)
        {
            L(gx, data.Ground, Math.Min(gx + (1.5 * h), x1 + t + 1500), data.Ground, building);
        }

        if (data.Pit is { } pit)
        {
            L(pit.X0, 0, pit.X0, -pit.Depth, building);
            L(pit.X0, -pit.Depth, pit.X1, -pit.Depth, building);
            L(pit.X1, -pit.Depth, pit.X1, 0, building);
            Text(pit.X0, (-pit.Depth * 1000) - (1.3 * h), "приямок", 0.7 * h);
        }

        foreach (var b in data.Blocks)
        {
            doc.Entities.Add(new LwPolyline([new XY(b.X0, b.Bottom * 1000), new XY(b.X1, b.Bottom * 1000), new XY(b.X1, b.Top * 1000), new XY(b.X0, b.Top * 1000)]) { IsClosed = true, Layer = equipment });
            for (var k = 0; k < b.Pumps; k++)
            {
                // Насосы в составе — вертикальные агрегаты: корпус и двигатель над ним.
                var cx = b.X0 + ((b.X1 - b.X0) * (k + 0.5) / b.Pumps);
                var w = Math.Min((b.X1 - b.X0) / b.Pumps * 0.5, 300);
                var bodyTop = b.Bottom + ((b.Top - b.Bottom) * 0.45);
                doc.Entities.Add(new LwPolyline([new XY(cx - (w / 2), (b.Bottom + 0.15) * 1000), new XY(cx + (w / 2), (b.Bottom + 0.15) * 1000), new XY(cx + (w / 2), bodyTop * 1000), new XY(cx - (w / 2), bodyTop * 1000)]) { IsClosed = true, Layer = equipment });
                doc.Entities.Add(new LwPolyline([new XY(cx - (w * 0.4), bodyTop * 1000), new XY(cx + (w * 0.4), bodyTop * 1000), new XY(cx + (w * 0.4), b.Top * 1000), new XY(cx - (w * 0.4), b.Top * 1000)]) { IsClosed = true, Layer = equipment });
            }

            Text(b.X0, (b.Top * 1000) + (0.5 * h), b.Position is { } n ? $"{n}. {b.Label}" : b.Label, 0.7 * h);
        }

        foreach (var p in data.Pipes)
        {
            if (p.X1 - p.X0 > 1)
            {
                L(p.X0, p.Z, p.X1, p.Z, pipes);
                Text(((p.X0 + p.X1) / 2) - (3 * h), (p.Z * 1000) + (0.4 * h), p.Label, 0.7 * h);
            }

            foreach (var (vx, za, zb) in p.Verticals)
            {
                L(vx, za, vx, zb, pipes);
            }
        }

        // Арматура — знаками ГОСТ 21.205 на оси трубы, номер позиции на выноске.
        foreach (var (vx, vz, kind, pos, angle) in data.Valves)
        {
            var r = 0.7 * h;
            GostSymbols.Draw(doc, equipment, text, new XY(vx, vz * 1000), angle, r, GostSymbols.FromKind(kind));
            if (pos is { } n)
            {
                doc.Entities.Add(new Line(new XYZ(vx + r, (vz * 1000) + r, 0), new XYZ(vx + (2.5 * r), (vz * 1000) + (2.5 * r), 0)) { Layer = text });
                Text(vx + (2.6 * r), (vz * 1000) + (2.6 * r), n.ToString(CultureInfo.InvariantCulture), 0.7 * h);
            }
        }

        // Насосы установки — знак насоса ГОСТ 21.205 у корпуса агрегата.
        foreach (var b in data.Blocks.Where(b => b.Pumps > 0))
        {
            for (var k = 0; k < b.Pumps; k++)
            {
                var cx = b.X0 + ((b.X1 - b.X0) * (k + 0.5) / b.Pumps);
                GostSymbols.Draw(doc, equipment, text, new XY(cx, (b.Bottom + 0.15 + ((b.Top - b.Bottom) * 0.15)) * 1000), Math.PI / 2, 0.8 * h, PipeSymbol.Pump);
            }
        }

        // Отметки: пол, перекрытие, земля, трубы (по одной на отметку).
        var markX = x0 - t - 3500;
        Mark(markX, 0, "пол");
        Mark(markX, data.Height, "низ перекрытия");
        Mark(markX, data.Ground, "ур. земли");
        foreach (var z in data.Pipes.Select(p => Math.Round(p.Z, 3)).Distinct().Order())
        {
            Mark(x1 + t + 300, z, "ось трубы");
        }

        Text(sheet.At(30, 0).X, sheet.At(0, sheet.Format.HeightMm - 15).Y, "Разрез 1-1 насосной станции", 2 * h);
        var legendKinds = data.Valves.Select(v => GostSymbols.FromKind(v.Kind)).Concat(data.Blocks.Any(b => b.Pumps > 0) ? [PipeSymbol.Pump] : []).ToList();
        if (legendKinds.Count > 0)
        {
            GostSymbols.Legend(doc, equipment, text, sheet.At(sheet.Format.WidthMm - 190, 0).X, sheet.At(0, sheet.Format.HeightMm - 15).Y, h, legendKinds);
        }
        var ny = sheet.At(0, 92).Y;
        var noteWidth = sheet.Format.WidthMm * sheet.Scale * 0.45;
        var perLine = Math.Max(20, noteWidth / (0.9 * h * 0.62));
        foreach (var (note, k) in data.Notes.Select((n, k) => (n, k)))
        {
            doc.Entities.Add(new MText { Value = $"{k + 1}. {note}", InsertPoint = new XYZ(sheet.At(25, 0).X, ny, 0), Height = 0.9 * h, RectangleWidth = noteWidth, Layer = text });
            ny -= ((Math.Ceiling((note.Length + 4) / perLine) * 1.5) + 0.6) * 0.9 * h;
        }

        return doc;
    }
}
