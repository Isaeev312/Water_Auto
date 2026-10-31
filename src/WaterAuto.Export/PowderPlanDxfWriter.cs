using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Помещение на плане АУПП: контур, имя и модули (точки на потолке с обозначениями «помещение.Pn»).</summary>
public sealed record PowderPlanRoom(string Name, Polygon Outline, IReadOnlyList<(string Id, PlanPoint At)> Modules, string Summary);

/// <summary>
/// План расстановки модулей порошкового пожаротушения на листе в рамке ГОСТ Р 21.101 (масштаб 1:100 или мельче):
/// контуры защищаемых помещений, модули — квадрат с диагоналями и обозначением, условные обозначения и примечания под планом.
/// </summary>
public static class PowderPlanDxfWriter
{
    public const string ModuleLayer = "АПТ_МОДУЛИ";
    public const string ContourLayer = "АПТ_КОНТУР";
    public const string TextLayer = "АПТ_ТЕКСТ";

    public static void Write(string path, IReadOnlyList<PowderPlanRoom> rooms, string moduleName, int? position, IReadOnlyList<string> notes, TitleBlock stamp, string font = DocumentStyle.DefaultFont) =>
        DxfDrawing.Save(path, Build(rooms, moduleName, position, notes, stamp), font);

    public static CadDocument Build(IReadOnlyList<PowderPlanRoom> rooms, string moduleName, int? position, IReadOnlyList<string> notes, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(stamp);
        var doc = new CadDocument();
        var modules = DxfDrawing.AddLayer(doc, ModuleLayer, 1, LineWeightType.W35);
        var contours = DxfDrawing.AddLayer(doc, ContourLayer, 7, LineWeightType.W50);
        var text = DxfDrawing.AddLayer(doc, TextLayer, 7);
        var vs = rooms.SelectMany(r => r.Outline.Vertices).ToList();
        var (x0, y0, x1, y1) = (vs.Min(v => v.XMm), vs.Min(v => v.YMm), vs.Max(v => v.XMm), vs.Max(v => v.YMm));
        var sheet = new[] { 100.0, 200, 500 }.Select(sc => GostSheet.FitAtScale(x0 - 2000, y0 - 2000 - (70 * sc), x1 + 2000, y1 + 2000, sc)).FirstOrDefault(f => f is not null)
            ?? GostSheet.Fit(x0 - 2000, y0 - 2000 - 70000, x1 + 2000, y1 + 2000);
        var h = 2.5 * sheet.Scale;
        GostSheet.Draw(doc, sheet, stamp);
        void Text(double x, double y, string value, double height) => doc.Entities.Add(new TextEntity { Value = value, InsertPoint = new XYZ(x, y, 0), Height = height, Layer = text });
        void Module(double x, double y, double r)
        {
            doc.Entities.Add(new LwPolyline([new XY(x - r, y - r), new XY(x + r, y - r), new XY(x + r, y + r), new XY(x - r, y + r)]) { IsClosed = true, Layer = modules });
            doc.Entities.Add(new Line(new XYZ(x - r, y - r, 0), new XYZ(x + r, y + r, 0)) { Layer = modules });
            doc.Entities.Add(new Line(new XYZ(x - r, y + r, 0), new XYZ(x + r, y - r, 0)) { Layer = modules });
        }

        foreach (var room in rooms)
        {
            doc.Entities.Add(new LwPolyline([.. room.Outline.Vertices.Select(v => new XY(v.XMm, v.YMm))]) { IsClosed = true, Layer = contours });
            var c = room.Outline.Centroid;
            Text(c.XMm, c.YMm + (1.5 * h), room.Name, 1.4 * h);
            foreach (var (id, at) in room.Modules)
            {
                Module(at.XMm, at.YMm, 0.7 * h);
                Text(at.XMm + (0.9 * h), at.YMm + (0.9 * h), id, 0.6 * h);
            }
        }

        // Условные обозначения и примечания — под планом.
        var lx = sheet.At(25, 0).X;
        var ly = y0 - 2000 - (2 * h);
        Text(lx, ly, "Условные обозначения", 1.0 * h);
        Module(lx + (1.5 * h), ly - (2.4 * h), 0.7 * h);
        Text(lx + (3.5 * h), ly - (2.75 * h), $"модуль порошкового пожаротушения {moduleName}{(position is { } p ? $", поз. {p}" : "")}, на потолке (обозначение — помещение.номер)", 0.8 * h);
        var ny = ly - (5.5 * h);
        var width = (sheet.Format.WidthMm - 220) * sheet.Scale;
        foreach (var (note, k) in rooms.Select(r => r.Summary).Concat(notes).Select((n, k) => (n, k)))
        {
            doc.Entities.Add(new MText { Value = $"{k + 1}. {note}", InsertPoint = new XYZ(lx, ny, 0), Height = 0.8 * h, RectangleWidth = width, Layer = text });
            ny -= ((Math.Ceiling((note.Length + 4) / Math.Max(20, width / (0.8 * h * 0.62))) * 1.5) + 0.6) * 0.8 * h;
        }

        return doc;
    }
}
