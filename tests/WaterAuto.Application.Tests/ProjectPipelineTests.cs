using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

public sealed class ProjectPipelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м: помещение с именем, колонна, ввод — по соглашению о слоях (README §7).
    private string Drawing(bool withInlet = true)
    {
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        Layer L(string name)
        {
            if (!doc.Layers.TryGetValue(name, out var layer))
            {
                layer = new Layer(name);
                doc.Layers.Add(layer);
            }

            return layer;
        }

        LwPolyline Rect(double x0, double y0, double x1, double y1) =>
            new([new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1)]) { IsClosed = true };
        var room = Rect(0, 0, 30000, 20000);
        room.Layer = L(FloorPlanImporter.ContourLayer);
        doc.Entities.Add(room);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(15000, 10000, 0), Height = 250, Layer = L(FloorPlanImporter.ContourLayer) });
        var column = Rect(7300, 7300, 7700, 7700);
        column.Layer = L(FloorPlanImporter.ColumnLayer);
        doc.Entities.Add(column);
        if (withInlet)
        {
            doc.Entities.Add(new Point(new XYZ(500, 500, 0)) { Layer = L(FloorPlanImporter.InletLayer) });
        }

        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    private string Project(double k = 0.47, ProjectHydrants? hydrants = null, bool ring = false) =>
        Write("project.waproj", ProjectFile.ToJson(new ProjectFile
        {
            Name = "Тестовая автостоянка",
            Drawing = "floor.dxf",
            Design = new ProjectDesign
            {
                RoomGroup = "2",
                SprinklerK = k,
                SprinklerModel = "по каталогу",
                PipeElevation = -1.5,
                DictatingAboveAxis = 3.5,
                SupplyLength = 30,
                SupplyDn = 125,
                InletPressure = 0.1,
                DesignVelocity = 5,
                Hydrants = hydrants,
                Ring = ring,
            },
        }));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private PipelineResult Run(string project) =>
        ProjectPipeline.Run(project, Path.Combine(_dir, "out"), RepoPaths.Norms, RepoPaths.Registry);

    [Fact]
    public void Drawing_project_produces_the_whole_package()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Files.Select(Path.GetFileName).ShouldBe([
            ProjectPipeline.PlanFile, ProjectPipeline.PlanDxfFile, ProjectPipeline.AxonometryFile,
            ProjectPipeline.SpecificationFile, ProjectPipeline.NoteFile, ProjectPipeline.IssuesFile,
        ]);
        result.Files.ShouldAllBe(f => new FileInfo(f).Length > 0);
        result.Design!.PumpHead.Meters.ShouldBeGreaterThan(0);

        DwgReader.Read(result.Files[0]).Entities.Count(e => e.Layer.Name == "ПТ_ОРОСИТЕЛИ").ShouldBeGreaterThan(0);
        using var note = WordprocessingDocument.Open(result.Files[4], false);
        var text = note.MainDocumentPart!.Document.Body!.InnerText;
        text.ShouldContain("Тестовая автостоянка");
        text.ShouldContain("Б.30");
    }

    [Fact]
    public void Hydrants_from_the_project_enter_the_pump_flow_and_the_note()
    {
        Drawing();
        var plain = Run(Project()).Design!;
        var result = Run(Project(hydrants: new ProjectHydrants
        {
            FireResistance = "II", Category = "В", HazardClass = "С0", Volume = 40, BuildingHeight = 12, RoomHeight = 3.3,
            AboveAxis = 1.35, PipeLength = 15, PipeDn = 65,
        }));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Design!.Hydrants.ShouldNotBeNull();
        result.Design.PumpFlow.LitersPerSecond.ShouldBeGreaterThan(plain.PumpFlow.LitersPerSecond + 5);
        using var note = WordprocessingDocument.Open(result.Files[4], false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("СН КР 40-04:2025");
    }

    [Fact]
    public void Without_K_the_sprinkler_is_chosen_from_the_catalog()
    {
        Drawing();
        var result = Run(Project(k: 0));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Issues.ShouldContain(i => i.Code == "sprinkler.chosen");
        using var note = WordprocessingDocument.Open(result.Files[4], false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Подбор оросителя");
    }

    [Fact]
    public void Import_errors_stop_with_locations_in_the_issue_file()
    {
        Drawing(withInlet: false);
        var result = Run(Project());
        result.Success.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "import.noInlet");
        File.ReadAllText(result.Files.Single()).ShouldContain("import.noInlet");
    }

    [Fact]
    public void Hydrant_set_for_one_hydrant_follows_the_catalog_template()
    {
        var s = new WaterAuto.Calc.Hydraulics.HydrantSettings("II", "В", "С0", 40, new Core.Length(12), new Core.Length(3.3), 65, 19, 20, new Core.Length(1.35), new Core.Length(15), 80);
        var items = HydrantEquipment.Items(HydrantEquipment.PathFor(RepoPaths.Norms), s);
        items.ShouldContain(i => i.Name == "Клапан пожарный запорный угловой DN65" && i.Quantity == 1);
        items.ShouldContain(i => i.Name.Contains("РС-70", StringComparison.Ordinal) && i.Name.Contains("19 мм", StringComparison.Ordinal));
        items.ShouldContain(i => i.Name.Contains("длина 20 м", StringComparison.Ordinal));
        items.ShouldAllBe(i => i.Group == Core.TakeoffGroup.Hydrants && i.Note == "на один пожарный кран");
    }

    [Fact]
    public void Ring_project_reports_both_variants_in_the_note()
    {
        Drawing();
        var result = Run(Project(ring: true));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Design!.Ring.ShouldNotBeNull();
        using var note = WordprocessingDocument.Open(result.Files[4], false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Б.1.2.16.1");
    }
}
