using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Import;

namespace WaterAuto.Export.Tests;

public sealed class LayerTemplateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-template-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("WaterAuto.dwt")]
    [InlineData("WaterAuto.dwg")]
    [InlineData("WaterAuto.dxf")]
    public void Template_has_the_layer_convention_in_millimetres(string name)
    {
        var path = Path.Combine(_dir, name);
        LayerTemplate.Write(path);
        var doc = name.EndsWith(".dxf", StringComparison.Ordinal) ? DxfReader.Read(path) : DwgReader.Read(path);
        doc.Header.InsUnits.ShouldBe(UnitsType.Millimeters);
        foreach (var layer in new[]
        {
            FloorPlanImporter.ContourLayer, FloorPlanImporter.ColumnLayer, FloorPlanImporter.BeamLayer,
            FloorPlanImporter.DuctLayer, FloorPlanImporter.NoSprinklerLayer, FloorPlanImporter.InletLayer,
            PlanDxfWriter.SprinklerLayer, PlanDxfWriter.PipeLayer, PlanDxfWriter.SegmentLayer, PlanDxfWriter.IssueLayer,
        })
        {
            doc.Layers.Contains(layer).ShouldBeTrue(layer);
        }

        doc.BlockRecords.Contains(LayerTemplate.InletBlock).ShouldBeTrue();
        doc.Entities.OfType<MText>().ShouldContain(t => t.Value.Contains(FloorPlanImporter.ContourLayer, StringComparison.Ordinal));
    }

    [Fact]
    public void Drawing_started_from_the_template_imports()
    {
        // Чертёж по шаблону: контур, имя, ввод — блоком из шаблона. Памятка на слое 0 импорту не мешает.
        var doc = LayerTemplate.Build();
        var room = new LwPolyline([new XY(0, 0), new XY(20000, 0), new XY(20000, 10000), new XY(0, 10000)]) { IsClosed = true, Layer = doc.Layers[FloorPlanImporter.ContourLayer] };
        doc.Entities.Add(room);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(5000, 5000, 0), Height = 300, Layer = doc.Layers[FloorPlanImporter.ContourLayer] });
        doc.Entities.Add(new Insert(doc.BlockRecords[LayerTemplate.InletBlock]) { InsertPoint = new XYZ(500, 500, 0), Layer = doc.Layers[FloorPlanImporter.InletLayer] });
        var path = Path.Combine(_dir, "floor.dwg");
        DwgWriter.Write(path, doc);

        var result = FloorPlanImporter.Import(path);
        result.Issues.Where(i => i.Level == WaterAuto.Core.IssueLevel.Error).ShouldBeEmpty(string.Join("; ", result.Issues.Select(i => i.Message)));
        result.Plan!.Rooms.ShouldHaveSingleItem().Name.ShouldBe("Автостоянка");
        result.Plan.Inlets.ShouldHaveSingleItem().Location.ShouldBe(new WaterAuto.Core.PlanPoint(500, 500));
    }
}
