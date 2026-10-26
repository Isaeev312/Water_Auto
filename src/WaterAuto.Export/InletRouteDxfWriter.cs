using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Трасса ввода на плане: ломаная, подпись (DN, отметка), длина, мм.</summary>
public sealed record InletRouteLine(string Label, IReadOnlyList<PlanPoint> Path, double LengthMm);

/// <summary>Данные плана трассы от ввода в здание до насосной: этаж (помещения, колонны), насосная, трассы, примечания.</summary>
public sealed record InletRouteDrawing(
    IReadOnlyList<Polygon> Rooms,
    IReadOnlyList<Polygon> Columns,
    Polygon? PumpRoom,
    IReadOnlyList<InletRouteLine> Routes,
    IReadOnlyList<string> Notes,
    string LineCode = "В2");

/// <summary>
/// План трассы водопровода от ввода в здание до насосной станции: контуры помещений и колонн, насосная (штриховка),
/// трассы с обозначением трубопровода в разрывах, подписями DN, отметки и длины, привязки поворотов к ближайшим колоннам или
/// стенам по осям; лист с рамкой и основной надписью по форме 3, масштаб 1:100 (не помещается — 1:200).
/// </summary>
public static class InletRouteDxfWriter
{
    public const string FloorLayer = "ПТ_ВВОД_ЭТАЖ";
    public const string RouteLayer = "ПТ_ВВОД_ТРАССА";

    public static void Write(string path, InletRouteDrawing data, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(data, stamp), font);

    public static CadDocument Build(InletRouteDrawing data, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var floor = DxfDrawing.AddLayer(doc, FloorLayer, 8);
        var route = DxfDrawing.AddLayer(doc, RouteLayer, 5, LineWeightType.W50);
        var text = DxfDrawing.AddLayer(doc, PumpRoomDxfWriter.TextLayer, 7);
        var dims = DxfDrawing.AddLayer(doc, PlanDxfWriter.DimensionLayer, 3);
        var ru = CultureInfo.GetCultureInfo("ru-RU");

        var all = data.Rooms.SelectMany(r => r.Vertices).ToList();
        var (x0, y0, x1, y1) = (all.Min(p => p.XMm), all.Min(p => p.YMm), all.Max(p => p.XMm), all.Max(p => p.YMm));
        var sheet = new[] { 100.0, 200 }
            .Select(sc => GostSheet.FitAtScale(x0 - 3000, y0 - 3000 - (85 * sc), x1 + 3000, y1 + 2000, sc))
            .FirstOrDefault(f => f is not null) ?? GostSheet.Fit(x0 - 3000, y0 - 20000, x1 + 3000, y1 + 2000);
        var h = 2.5 * sheet.Scale;
        GostSheet.Draw(doc, sheet, stamp);
        XYZ P(double x, double y) => new(x, y, 0);
        void Poly(IEnumerable<PlanPoint> pts, Layer layer, bool closed) =>
            doc.Entities.Add(new LwPolyline([.. pts.Select(p => new XY(p.XMm, p.YMm))]) { IsClosed = closed, Layer = layer });
        void Text(double x, double y, string value, double height, double rotation = 0) =>
            doc.Entities.Add(new TextEntity { Value = value, InsertPoint = P(x, y), Height = height, Rotation = rotation, Layer = text });

        foreach (var r in data.Rooms)
        {
            Poly(r.Vertices, floor, true);
        }

        foreach (var c in data.Columns)
        {
            Poly(c.Vertices, floor, true);
        }

        if (data.PumpRoom is { } pr)
        {
            Poly(pr.Vertices, route, true);
            var v = pr.Vertices;
            var (px0, py0, px1, py1) = (v.Min(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.XMm), v.Max(p => p.YMm));
            for (var k = -(py1 - py0); k < px1 - px0; k += 3 * h)
            {
                var (sx, sy) = (px0 + k, py0);
                var lo = Math.Max(0, Math.Max(px0 - sx, py0 - sy));
                var hi = Math.Min(px1 - sx, py1 - sy);
                if (hi > lo)
                {
                    doc.Entities.Add(new Line(P(sx + lo, sy + lo), P(sx + hi, sy + hi)) { Layer = floor });
                }
            }

            Text(px0 + h, py1 - (2 * h), "Насосная станция (см. план насосной)", 0.9 * h);
        }

        foreach (var (line, n) in data.Routes.Select((r, k) => (r, k + 1)))
        {
            // Трасса с разрывами под обозначение трубопровода через ~60 h.
            foreach (var (a, b) in line.Path.Zip(line.Path.Skip(1)))
            {
                var len = Math.Abs(b.XMm - a.XMm) + Math.Abs(b.YMm - a.YMm);
                var vertical = Math.Abs(b.XMm - a.XMm) < 1;
                var pos = 0.0;
                for (var d = 30 * h; d < len - (8 * h); d += 60 * h)
                {
                    var t0 = (d - (1.2 * h)) / len;
                    doc.Entities.Add(new Line(At(a, b, pos), At(a, b, t0)) { Layer = route });
                    var c = At(a, b, d / len);
                    Text(vertical ? c.X + (0.35 * h) : c.X - (0.7 * h), vertical ? c.Y - (0.7 * h) : c.Y - (0.35 * h), data.LineCode, 0.7 * h, vertical ? Math.PI / 2 : 0);
                    pos = (d + (1.2 * h)) / len;
                }

                doc.Entities.Add(new Line(At(a, b, pos), P(b.XMm, b.YMm)) { Layer = route });
            }

            // Ввод в здание: кружок, выноска с маркой ввода; подпись трассы — у самого длинного участка.
            var start = line.Path[0];
            doc.Entities.Add(new Circle { Center = P(start.XMm, start.YMm), Radius = 0.8 * h, Layer = route });
            doc.Entities.Add(new Line(P(start.XMm, start.YMm), P(start.XMm - (4 * h), start.YMm + (4 * h))) { Layer = text });
            doc.Entities.Add(new Line(P(start.XMm - (4 * h), start.YMm + (4 * h)), P(start.XMm - (14 * h), start.YMm + (4 * h))) { Layer = text });
            Text(start.XMm - (13.8 * h), start.YMm + (4.3 * h), $"Ввод {n}", 1.0 * h);
            var (sa, sb) = line.Path.Zip(line.Path.Skip(1)).MaxBy(s => Math.Abs(s.Second.XMm - s.First.XMm) + Math.Abs(s.Second.YMm - s.First.YMm));
            var vert = Math.Abs(sb.XMm - sa.XMm) < 1;
            Text(((sa.XMm + sb.XMm) / 2) + (vert ? -0.5 * h : 2 * h), ((sa.YMm + sb.YMm) / 2) + (vert ? 2 * h : 0.5 * h), string.Create(ru, $"{line.Label}, L = {line.LengthMm / 1000:0.0} м"), 0.8 * h, vert ? Math.PI / 2 : 0);

            // Привязки поворотов: каждый угол трассы — к ближайшей стене помещения по X и по Y.
            var style = new PlanDxfStyle(TextHeightMm: 0.7 * h);
            foreach (var corner in line.Path.Skip(1).Take(line.Path.Count - 2))
            {
                var xs = data.Rooms.SelectMany(r => r.Vertices.Zip(r.Vertices.Skip(1).Append(r.Vertices[0]))).Where(e => Math.Abs(e.First.XMm - e.Second.XMm) < 1 && corner.YMm >= Math.Min(e.First.YMm, e.Second.YMm) && corner.YMm <= Math.Max(e.First.YMm, e.Second.YMm)).Select(e => e.First.XMm).ToList();
                var ys = data.Rooms.SelectMany(r => r.Vertices.Zip(r.Vertices.Skip(1).Append(r.Vertices[0]))).Where(e => Math.Abs(e.First.YMm - e.Second.YMm) < 1 && corner.XMm >= Math.Min(e.First.XMm, e.Second.XMm) && corner.XMm <= Math.Max(e.First.XMm, e.Second.XMm)).Select(e => e.First.YMm).ToList();
                if (xs.Count > 0)
                {
                    var wx = xs.MinBy(x => Math.Abs(x - corner.XMm));
                    PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(Math.Min(wx, corner.XMm), corner.YMm), new PlanPoint(Math.Max(wx, corner.XMm), corner.YMm)], 1.5 * h), style, dims);
                }

                if (ys.Count > 0)
                {
                    var wy = ys.MinBy(y => Math.Abs(y - corner.YMm));
                    PlanDxfWriter.AddChain(doc, new PlanDimensionChain([new PlanPoint(corner.XMm, Math.Min(wy, corner.YMm)), new PlanPoint(corner.XMm, Math.Max(wy, corner.YMm))], -1.5 * h), style, dims);
                }
            }
        }

        Text(sheet.At(30, 0).X, sheet.At(0, sheet.Format.HeightMm - 15).Y, "План трассы водопровода от ввода в здание до насосной станции", 2 * h);

        // Условные обозначения и примечания — внизу листа.
        var ly = sheet.At(0, 92).Y;
        var lx = sheet.At(25, 0).X;
        doc.Entities.Add(new Line(P(lx, ly), P(lx + (2 * h), ly)) { Layer = route });
        doc.Entities.Add(new Line(P(lx + (4 * h), ly), P(lx + (6 * h), ly)) { Layer = route });
        Text(lx + (2.3 * h), ly - (0.35 * h), data.LineCode, 0.7 * h);
        Text(lx + (7 * h), ly - (0.35 * h), $"трубопровод противопожарного водопровода ({data.LineCode}), ГОСТ 21.205", 0.8 * h);
        doc.Entities.Add(new Circle { Center = P(lx + (3 * h), ly - (2.5 * h)), Radius = 0.8 * h, Layer = route });
        Text(lx + (7 * h), ly - (2.85 * h), "ввод водопровода в здание", 0.8 * h);
        var ny = ly - (5.5 * h);
        var noteWidth = sheet.Format.WidthMm * sheet.Scale * 0.45;
        var perLine = Math.Max(20, noteWidth / (0.9 * h * 0.62));
        foreach (var (note, k) in data.Notes.Select((n, k) => (n, k)))
        {
            doc.Entities.Add(new MText { Value = $"{k + 1}. {note}", InsertPoint = P(lx, ny), Height = 0.9 * h, RectangleWidth = noteWidth, Layer = text });
            ny -= ((Math.Ceiling((note.Length + 4) / perLine) * 1.5) + 0.6) * 0.9 * h;
        }

        return doc;
    }

    private static XYZ At(PlanPoint a, PlanPoint b, double t) => new(a.XMm + ((b.XMm - a.XMm) * t), a.YMm + ((b.YMm - a.YMm) * t), 0);
}
