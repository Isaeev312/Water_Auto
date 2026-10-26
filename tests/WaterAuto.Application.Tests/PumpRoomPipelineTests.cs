using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Сквозной выпуск с помещением насосной станции. До этого теста ProjectPipeline с слоем ПТ_НС не проверялся:
/// ошибки сборки листов насосной (нет каталога, нет шаблона, исключение) ловились только вручную (аудит PD-15).
/// </summary>
public sealed class PumpRoomPipelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pump-room-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м; помещение насосной 12 × 8 м, дверь, два ввода, выпуск, два патрубка ГМ.
    private string Drawing(bool withPumpRoom = true)
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
        if (!withPumpRoom)
        {
            return Save(doc);
        }

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

        return Save(doc);
    }

    private string Save(CadDocument doc)
    {
        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    private string Project(double inletPressure = 0.1, double pumpRoomHeight = 4.5) =>
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
                InletPressure = inletPressure,
                DesignVelocity = 5,
                PumpRoom = new ProjectPumpRoom
                {
                    FloorElevation = -3,
                    GroundElevation = 0,
                    Height = pumpRoomHeight,
                    Underground = true,
                },
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

    [Fact]
    public void Project_with_pump_room_produces_pump_station_sheets()
    {
        Drawing();
        var result = Run(Project());
        var issues = IssueText(result);
        result.Success.ShouldBeTrue(issues);
        var names = result.Files.Select(Path.GetFileName).ToList();
        names.ShouldContain(ProjectPipeline.PumpRoomPlanFile, issues);
        names.ShouldContain(ProjectPipeline.PumpRoomPipingFile, issues);
        names.ShouldContain(ProjectPipeline.PumpRoomSectionFile, issues);
        names.ShouldContain(ProjectPipeline.PumpRoomAxonometryFile, issues);
        names.ShouldContain(ProjectPipeline.PumpSchemeFile, issues);
        names.ShouldContain(ProjectPipeline.PumpStationSpecFile, issues);
        result.Files.ShouldAllBe(f => new FileInfo(f).Length > 0);

        using (var pns = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.PumpStationSpecFile, StringComparison.Ordinal)), false))
        {
            pns.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Насос");
        }

        using (var note = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.NoteFile, StringComparison.Ordinal)), false))
        {
            note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Насосная станция");
        }

        // Отметки на листах насосной: отрицательные — со знаком минус, без «-+» (аудит PD-9).
        foreach (var dxf in result.Files.Where(f => f.EndsWith(".dxf", StringComparison.Ordinal) && Path.GetFileName(f).StartsWith("pump", StringComparison.Ordinal)))
        {
            File.ReadAllText(dxf).Contains("-+", StringComparison.Ordinal).ShouldBeFalse($"{Path.GetFileName(dxf)}: двойной знак отметки");
        }

        result.Issues.Where(i => i.Code.StartsWith("pump", StringComparison.Ordinal) && i.Level == Core.IssueLevel.Error).ShouldBeEmpty(issues);
    }

    [Fact]
    public void Project_without_pump_room_warns_and_has_no_pump_room_sheets()
    {
        Drawing(withPumpRoom: false);
        var result = Run(Project());
        var issues = IssueText(result);
        result.Success.ShouldBeTrue(issues);
        result.Files.Select(Path.GetFileName).ShouldNotContain(ProjectPipeline.PumpRoomPlanFile);
        result.Files.Select(Path.GetFileName).ShouldContain(ProjectPipeline.PumpSchemeFile);
        result.Issues.ShouldContain(i => i.Code == "pumpRoom.missing" && i.Level == Core.IssueLevel.Warning, issues);
    }

    [Fact]
    public void Pump_room_too_low_for_truck_inlets_stops_the_package()
    {
        // Ось патрубков ГМ — (1,20 ± 0,15) м от отмостки (п. 6.10.18); при высоте помещения 3,0 м от пола −3,000
        // до перекрытия 2,8 м — нормативную высоту обеспечить нельзя, выпуск должен остановиться с Error.
        Drawing();
        var result = Run(Project(pumpRoomHeight: 3.0));
        result.Success.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "pumpRoom.truckElevation" && i.Level == Core.IssueLevel.Error);
    }

    [Fact]
    public void Pump_station_error_stops_the_package()
    {
        // P_вх = 0 — всасывание без подпора: предельная скорость 1,5 м/с, для Q = 30 л/с нужен DN > 150,
        // которого нет во внутреннем сортаменте → pump.pipeTooLarge (Error), документы не выпускаются (аудит PC-5/PC-6).
        Drawing(withPumpRoom: false);
        var result = Run(Project(inletPressure: 0));
        result.Success.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "pump.pipeTooLarge" && i.Level == Core.IssueLevel.Error);
        result.Files.ShouldHaveSingleItem();
        result.Files[0].ShouldEndWith(ProjectPipeline.IssuesFile);
    }
}
