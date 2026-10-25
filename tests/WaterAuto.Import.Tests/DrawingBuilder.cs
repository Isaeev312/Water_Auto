using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;

namespace WaterAuto.Import.Tests;

/// <summary>Строит чертёж по соглашению о слоях и пишет его во временный DXF/DWG — чтобы тесты шли через настоящий файл.</summary>
internal sealed class DrawingBuilder
{
    private readonly CadDocument _doc;
    private readonly Dictionary<string, Layer> _layers = new(StringComparer.Ordinal);

    public DrawingBuilder(ACadVersion version = ACadVersion.AC1032, UnitsType units = UnitsType.Millimeters)
    {
        _doc = new CadDocument(version);
        _doc.Header.InsUnits = units;
    }

    public CadDocument Document => _doc;

    public Layer Layer(string name)
    {
        if (!_layers.TryGetValue(name, out var layer))
        {
            layer = new Layer(name);
            _doc.Layers.Add(layer);
            _layers[name] = layer;
        }

        return layer;
    }

    public LwPolyline Polyline(string layer, bool closed, params (double X, double Y)[] points)
    {
        var pl = new LwPolyline(points.Select(p => new XY(p.X, p.Y))) { IsClosed = closed, Layer = Layer(layer) };
        _doc.Entities.Add(pl);
        return pl;
    }

    public LwPolyline Rect(string layer, double x0, double y0, double x1, double y1) =>
        Polyline(layer, true, (x0, y0), (x1, y0), (x1, y1), (x0, y1));

    public void Text(string layer, double x, double y, string value) =>
        _doc.Entities.Add(new TextEntity(value) { InsertPoint = new XYZ(x, y, 0), Height = 250, Layer = Layer(layer) });

    public void Point(string layer, double x, double y) =>
        _doc.Entities.Add(new Point(new XYZ(x, y, 0)) { Layer = Layer(layer) });

    public void Circle(string layer, double x, double y, double r) =>
        _doc.Entities.Add(new Circle(new XYZ(x, y, 0), r) { Layer = Layer(layer) });

    public void Line(string layer, double x0, double y0, double x1, double y1) =>
        _doc.Entities.Add(new Line(new XYZ(x0, y0, 0), new XYZ(x1, y1, 0)) { Layer = Layer(layer) });

    public Insert Block(string layer, string blockName, double x, double y, double rotationRad, double scale, params (double X, double Y)[] outline) =>
        Block(layer, blockName, x, y, rotationRad, scale, (0, 0), new LwPolyline(outline.Select(p => new XY(p.X, p.Y))) { IsClosed = true });

    public Insert Block(string layer, string blockName, double x, double y, double rotationRad, double scale, (double X, double Y) basePoint, Entity shape)
    {
        var block = new BlockRecord(blockName);
        block.BlockEntity.BasePoint = new XYZ(basePoint.X, basePoint.Y, 0);
        block.Entities.Add(shape);
        _doc.BlockRecords.Add(block);
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(x, y, 0),
            Rotation = rotationRad,
            XScale = scale,
            YScale = scale,
            Layer = Layer(layer),
        };
        _doc.Entities.Add(insert);
        return insert;
    }

    public void MText(string layer, double x, double y, string value) =>
        _doc.Entities.Add(new MText { Value = value, InsertPoint = new XYZ(x, y, 0), Height = 250, Layer = Layer(layer) });

    public string WriteDxf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dxf");
        DxfWriter.Write(path, _doc, false);
        return path;
    }

    public string WriteDwg()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dwg");
        DwgWriter.Write(path, _doc);
        return path;
    }

    /// <summary>Типовой этаж: помещение 30×20 м с именем, колонна, балка, короб, зона запрета, ввод.</summary>
    public static DrawingBuilder ValidFloor()
    {
        var b = new DrawingBuilder();
        b.Rect(FloorPlanImporter.ContourLayer, 0, 0, 30000, 20000);
        b.Text(FloorPlanImporter.ContourLayer, 15000, 10000, "Автостоянка");
        b.Rect(FloorPlanImporter.ColumnLayer, 9800, 9800, 10200, 10200);
        var beam = b.Rect(FloorPlanImporter.BeamLayer, 0, 5900, 30000, 6100);
        beam.Thickness = 350;
        beam.Elevation = -400;
        var duct = b.Rect(FloorPlanImporter.DuctLayer, 2000, 15000, 12000, 16000);
        duct.Elevation = -900;
        b.Rect(FloorPlanImporter.NoSprinklerLayer, 25000, 0, 30000, 4000);
        b.Point(FloorPlanImporter.InletLayer, 1000, 1000);
        return b;
    }
}
