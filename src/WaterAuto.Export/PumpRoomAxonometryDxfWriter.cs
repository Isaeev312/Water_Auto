using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Точка обвязки в пространстве: план, мм, и отметка, м (от пола насосной).</summary>
public readonly record struct Point3(double XMm, double YMm, double Z);

/// <summary>Трубопровод аксонометрии: ломаная в пространстве, подпись (DN).</summary>
public sealed record AxonoPipe(string Label, IReadOnlyList<Point3> Path);

/// <summary>Оборудование аксонометрии: прямоугольник в плане, мм, низ и верх, м; подпись.</summary>
public sealed record AxonoBlock(string Label, double X0, double Y0, double X1, double Y1, double Bottom, double Top);

/// <summary>Арматура: точка, вид (gate, check, vent), номер позиции.</summary>
public sealed record AxonoValve(Point3 At, string Kind, int? Position);

public sealed record PumpRoomAxonometry(IReadOnlyList<AxonoPipe> Pipes, IReadOnlyList<AxonoBlock> Blocks, IReadOnlyList<AxonoValve> Valves, IReadOnlyList<string> Notes, double FloorElevation, string LineCode = "В2");

/// <summary>
/// Аксонометрическая схема обвязки насосной — фронтальная изометрия по ГОСТ 2.317 (ось Y под 45°, без сокращения):
/// трубы на отметках с подъёмами и опусками, оборудование габаритными параллелепипедами, арматура, подписи DN
/// и отметки у вертикалей; лист с рамкой и основной надписью по форме 3.
/// </summary>
public static class PumpRoomAxonometryDxfWriter
{
    public const string PipeLayer = "ПТ_НС_АКСОН_ТРУБЫ";
    public const string EquipmentLayer = "ПТ_НС_АКСОН_ОБОРУД";

    public static void Write(string path, PumpRoomAxonometry data, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(data, stamp), font);

    public static CadDocument Build(PumpRoomAxonometry data, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 5, LineWeightType.W50);
        var equipment = DxfDrawing.AddLayer(doc, EquipmentLayer, 1, LineWeightType.W25);
        var text = DxfDrawing.AddLayer(doc, PumpRoomDxfWriter.TextLayer, 7);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var (cos, sin) = (Math.Cos(Math.PI / 4), Math.Sin(Math.PI / 4));
        XY Pr(Point3 p) => new(p.XMm + (p.YMm * cos), (p.Z * 1000) + (p.YMm * sin));

        var pts = data.Pipes.SelectMany(p => p.Path).Concat(data.Blocks.SelectMany(b => new[] { new Point3(b.X0, b.Y0, b.Bottom), new Point3(b.X1, b.Y1, b.Top) })).Select(Pr).ToList();
        var (x0, y0, x1, y1) = (pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        var sheet = new[] { 50.0, 100 }
            .Select(sc => GostSheet.FitAtScale(x0 - 3000, y0 - 2000 - (85 * sc), x1 + 3000, y1 + 2000, sc))
            .FirstOrDefault(f => f is not null) ?? GostSheet.Fit(x0 - 3000, y0 - 20000, x1 + 3000, y1 + 2000);
        var h = 2.5 * sheet.Scale;
        GostSheet.Draw(doc, sheet, stamp);
        XYZ Z(XY p) => new(p.X, p.Y, 0);
        void Seg(Point3 a, Point3 b, Layer layer) => doc.Entities.Add(new Line(Z(Pr(a)), Z(Pr(b))) { Layer = layer });
        void Text(XY at, string value, double height, double rotation = 0) =>
            doc.Entities.Add(new TextEntity { Value = value, InsertPoint = Z(at), Height = height, Rotation = rotation, Layer = text });

        // Оборудование — параллелепипеды (видимые рёбра).
        foreach (var b in data.Blocks)
        {
            var c = new[]
            {
                new Point3(b.X0, b.Y0, b.Bottom), new Point3(b.X1, b.Y0, b.Bottom), new Point3(b.X1, b.Y1, b.Bottom), new Point3(b.X0, b.Y1, b.Bottom),
                new Point3(b.X0, b.Y0, b.Top), new Point3(b.X1, b.Y0, b.Top), new Point3(b.X1, b.Y1, b.Top), new Point3(b.X0, b.Y1, b.Top),
            };
            foreach (var (i, j) in new[] { (0, 1), (1, 2), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6) })
            {
                Seg(c[i], c[j], equipment);
            }

            Text(Pr(c[7]) with { Y = Pr(c[7]).Y + (0.5 * h) }, b.Label, 0.7 * h);
        }

        // Трубы: ломаные; у каждой вертикали — отметки верха и низа; подпись DN у самого длинного горизонтального участка.
        foreach (var p in data.Pipes)
        {
            for (var k = 1; k < p.Path.Count; k++)
            {
                Seg(p.Path[k - 1], p.Path[k], pipes);
                var (a, b) = (p.Path[k - 1], p.Path[k]);
                if (Math.Abs(a.Z - b.Z) > 0.01 && Math.Abs(a.XMm - b.XMm) < 1 && Math.Abs(a.YMm - b.YMm) < 1)
                {
                    var lo = Pr(a.Z < b.Z ? a : b);
                    var hi = Pr(a.Z < b.Z ? b : a);
                    Text(new XY(hi.X + (0.4 * h), hi.Y - (1.2 * h)), string.Create(ru, $"{Math.Max(a.Z, b.Z) + data.FloorElevation:+0.000;−0.000}"), 0.6 * h);
                    Text(new XY(lo.X + (0.4 * h), lo.Y + (0.3 * h)), string.Create(ru, $"{Math.Min(a.Z, b.Z) + data.FloorElevation:+0.000;−0.000}"), 0.6 * h);
                }
            }

            var horiz = p.Path.Zip(p.Path.Skip(1)).Where(s => Math.Abs(s.First.Z - s.Second.Z) < 0.01).ToList();
            if (horiz.Count > 0)
            {
                var (sa, sb) = horiz.MaxBy(s => Math.Abs(s.Second.XMm - s.First.XMm) + Math.Abs(s.Second.YMm - s.First.YMm));
                var (pa, pb) = (Pr(sa), Pr(sb));
                Text(new XY(((pa.X + pb.X) / 2) - (2 * h), ((pa.Y + pb.Y) / 2) + (0.4 * h)), $"{p.Label} ({data.LineCode})", 0.7 * h, Math.Atan2(pb.Y - pa.Y, pb.X - pa.X) is var ang && Math.Abs(ang) > Math.PI / 2 ? ang + Math.PI : ang);
            }
        }

        // Арматура: знак ГОСТ 21.205 по направлению трубы в изометрии (ход среды — по порядку точек трубы), номер позиции на выноске.
        double AngleAt(Point3 at)
        {
            foreach (var p in data.Pipes)
            {
                foreach (var (a, b) in p.Path.Zip(p.Path.Skip(1)))
                {
                    var len = Math.Abs(b.XMm - a.XMm) + Math.Abs(b.YMm - a.YMm) + (Math.Abs(b.Z - a.Z) * 1000);
                    var via = Math.Abs(at.XMm - a.XMm) + Math.Abs(at.YMm - a.YMm) + (Math.Abs(at.Z - a.Z) * 1000) + Math.Abs(b.XMm - at.XMm) + Math.Abs(b.YMm - at.YMm) + (Math.Abs(b.Z - at.Z) * 1000);
                    if (len > 1 && Math.Abs(via - len) < 1)
                    {
                        var (pa, pb) = (Pr(a), Pr(b));
                        return Math.Atan2(pb.Y - pa.Y, pb.X - pa.X);
                    }
                }
            }

            return 0;
        }

        foreach (var v in data.Valves)
        {
            var c = Pr(v.At);
            var r = 0.7 * h;
            GostSymbols.Draw(doc, equipment, text, c, AngleAt(v.At), r, GostSymbols.FromKind(v.Kind));

            if (v.Position is { } n)
            {
                doc.Entities.Add(new Line(Z(new XY(c.X + r, c.Y + r)), Z(new XY(c.X + (2.5 * r), c.Y + (2.5 * r)))) { Layer = text });
                Text(new XY(c.X + (2.6 * r), c.Y + (2.6 * r)), n.ToString(CultureInfo.InvariantCulture), 0.7 * h);
            }
        }

        Text(new XY(sheet.At(30, 0).X, sheet.At(0, sheet.Format.HeightMm - 15).Y), "Аксонометрическая схема обвязки насосной станции", 2 * h);

        // Условные обозначения — справа над основной надписью: только виды арматуры, что есть на схеме.
        if (data.Valves.Count > 0)
        {
            var lx = sheet.At(sheet.Format.WidthMm - 190, 0).X;
            var ly = sheet.At(0, sheet.Format.HeightMm - 15).Y;
            GostSymbols.Legend(doc, equipment, text, lx, ly, h, data.Valves.Select(v => GostSymbols.FromKind(v.Kind)));
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
