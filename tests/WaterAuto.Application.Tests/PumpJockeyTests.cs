using System.Text.Json.Nodes;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Application;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Жокей-насос — параметры-решение проектировщика (вопрос В-07): норма расход и уставки не задаёт, поэтому они хранятся
/// в проекте (`design.jockey.*`), проверяются как данные (Issue) и попадают в пояснительную записку.
/// </summary>
public sealed class PumpJockeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-jockey-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м; помещение насосной 12 × 8 м, два ввода, выпуск, два патрубка ГМ (как в сквозных тестах насосной).
    private string Drawing()
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
        var floor = Rect(0, 0, 30000, 20000);
        floor.Layer = L(FloorPlanImporter.ContourLayer);
        doc.Entities.Add(floor);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(15000, 10000, 0), Height = 250, Layer = L(FloorPlanImporter.ContourLayer) });
        doc.Entities.Add(new Point(new XYZ(500, 500, 0)) { Layer = L(FloorPlanImporter.InletLayer) });
        var room = Rect(1000, 1000, 13000, 9000);
        room.Layer = L(FloorPlanImporter.PumpRoomLayer);
        doc.Entities.Add(room);
        doc.Entities.Add(new Point(new XYZ(13000, 5000, 0)) { Layer = L(FloorPlanImporter.PumpRoomLayer) });
        foreach (var y in new[] { 3000.0, 5000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(1000, y, 0)) { Layer = L(FloorPlanImporter.PumpInletLayer) });
        }

        doc.Entities.Add(new Point(new XYZ(13000, 3000, 0)) { Layer = L(FloorPlanImporter.PumpOutletLayer) });
        foreach (var y in new[] { 7000.0, 8000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(13000, y, 0)) { Layer = L(FloorPlanImporter.PumpTruckLayer) });
        }

        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    private string Project(ProjectJockey? jockey) =>
        Write("project.waproj", ProjectFile.ToJson(new ProjectFile
        {
            Name = "Тестовая автостоянка с насосной",
            Drawing = "floor.dxf",
            Design = new ProjectDesign
            {
                RoomGroup = "2",
                SprinklerK = 0.47,
                SprinklerModel = "по каталогу",
                PipeElevation = -1.5,
                DictatingAboveAxis = 3.5,
                SupplyLength = 30,
                SupplyDn = 125,
                InletPressure = 0.1,
                DesignVelocity = 5,
                PumpRoom = new ProjectPumpRoom
                {
                    FloorElevation = -3,
                    GroundElevation = 0,
                    Height = 4.5,
                    Underground = true,
                },
                Jockey = jockey,
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

    private static string IssueText(PipelineResult result) =>
        string.Join("\n", result.Issues.Select(i => $"{i.Level} {i.Code}: {i.Message}"));

    private static string FileOf(PipelineResult result, string name) =>
        result.Files.Single(f => f.EndsWith(name, StringComparison.Ordinal));

    private static string DocxText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart!.Document.Body!.InnerText;
    }

    [Fact]
    public void Jockey_settings_round_trip_through_the_project_file()
    {
        var path = Path.Combine(_dir, "project.waproj");
        var project = new ProjectFile
        {
            Name = "Пробный",
            Drawing = "floor.dwg",
            Design = new ProjectDesign
            {
                SprinklerK = 0.47,
                Jockey = new ProjectJockey { FlowLitersPerSecond = 0.5, StartPressureMpa = 0.3, StopPressureMpa = 0.25 },
            },
        };

        File.WriteAllText(path, ProjectFile.ToJson(project));

        var loaded = ProjectFile.Load(path);
        loaded.Design.Jockey.ShouldNotBeNull();
        loaded.Design.Jockey!.FlowLitersPerSecond.ShouldBe(0.5);
        loaded.Design.Jockey.StartPressureMpa.ShouldBe(0.3);
        loaded.Design.Jockey.StopPressureMpa.ShouldBe(0.25);
        var json = File.ReadAllText(path);
        json.ShouldContain("\"jockey\"");
        json.ShouldContain("\"flowLitersPerSecond\"");
        json.ShouldContain("\"startPressureMpa\"");
        json.ShouldContain("\"stopPressureMpa\"");
    }

    [Fact]
    public void LoadMerged_keeps_the_optional_jockey_section_and_reads_it_back()
    {
        var path = Path.Combine(_dir, "project.waproj");
        File.WriteAllText(path, """{ "schemaVersion": 1, "name": "Пробный", "drawing": "floor.dwg", "design": { "sprinklerK": 0.47 } }""");

        var root = ProjectJson.LoadMerged(path);
        root["design"]!.AsObject().ContainsKey("jockey").ShouldBeTrue();
        root["design"]!["jockey"].ShouldBeNull();

        root["design"]!["jockey"] = new JsonObject
        {
            ["flowLitersPerSecond"] = 0.5,
            ["startPressureMpa"] = 0.3,
            ["stopPressureMpa"] = 0.25,
        };
        ProjectJson.Save(path, root);

        var loaded = ProjectFile.Load(path);
        loaded.Design.Jockey.ShouldNotBeNull();
        loaded.Design.Jockey!.FlowLitersPerSecond.ShouldBe(0.5);
        loaded.Design.Jockey.StartPressureMpa.ShouldBe(0.3);
        loaded.Design.Jockey.StopPressureMpa.ShouldBe(0.25);
    }

    [Fact]
    public void Valid_jockey_settings_are_a_manual_decision_and_go_to_the_note()
    {
        Drawing();
        var result = Run(Project(new ProjectJockey { FlowLitersPerSecond = 0.5, StartPressureMpa = 0.3, StopPressureMpa = 0.35 }));
        result.Success.ShouldBeTrue(IssueText(result));

        var info = result.Issues.Single(i => i.Code == "jockey.manual");
        info.Level.ShouldBe(Core.IssueLevel.Info);
        info.Message.ShouldContain("решением проектировщика");
        info.Message.ShouldContain("В-07");
        info.Message.ShouldContain("0,5");

        var note = DocxText(FileOf(result, ProjectPipeline.NoteFile));
        note.ShouldContain("Жокей-насос");
        note.ShouldContain("расход 0,5 л/с");
        note.ShouldContain("В-07");
        note.ShouldContain("п. 6.9.4");
    }

    [Fact]
    public void Negative_jockey_flow_is_an_error_and_does_not_stop_the_package()
    {
        Drawing();
        var result = Run(Project(new ProjectJockey { FlowLitersPerSecond = -0.5 }));

        result.Success.ShouldBeTrue(IssueText(result));
        var error = result.Issues.Single(i => i.Code == "jockey.flow");
        error.Level.ShouldBe(Core.IssueLevel.Error);
        error.Message.ShouldContain("больше 0");
        error.Message.ShouldContain("В-07");
        result.Issues.ShouldNotContain(i => i.Code == "jockey.manual");
    }

    [Fact]
    public void Jockey_start_not_less_than_stop_is_a_warning_with_numbers()
    {
        Drawing();
        var result = Run(Project(new ProjectJockey { FlowLitersPerSecond = 0.5, StartPressureMpa = 0.4, StopPressureMpa = 0.4 }));

        var warning = result.Issues.Single(i => i.Code == "jockey.settings");
        warning.Level.ShouldBe(Core.IssueLevel.Warning);
        warning.Message.ShouldContain("0,4");
        result.Issues.ShouldNotContain(i => i.Code == "jockey.manual");
    }

    [Fact]
    public void Jockey_stop_pressure_without_start_pressure_is_a_warning()
    {
        Drawing();
        var result = Run(Project(new ProjectJockey { StopPressureMpa = 0.25 }));

        var warning = result.Issues.Single(i => i.Code == "jockey.settings");
        warning.Level.ShouldBe(Core.IssueLevel.Warning);
        warning.Message.ShouldContain("без давления включения");
    }

    [Fact]
    public void Project_without_jockey_keeps_the_previous_note_text_and_has_no_jockey_issues()
    {
        Drawing();
        var result = Run(Project(null));
        result.Success.ShouldBeTrue(IssueText(result));

        result.Issues.ShouldNotContain(i => i.Code.StartsWith("jockey.", StringComparison.Ordinal));
        var note = DocxText(FileOf(result, ProjectPipeline.NoteFile));
        note.ShouldContain("автоматический водопитатель");
        note.ShouldNotContain("Принято решением проектировщика");
    }
}
