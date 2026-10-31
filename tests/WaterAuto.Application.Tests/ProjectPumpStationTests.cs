using System.IO;
using WaterAuto.Application;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Ручной выбор комплектной насосной установки в проекте (`design.pumpStation.setDesignation`): обозначение сохраняется
/// и читается моделью, а пустое значение (умолчание) означает автоподбор по требуемым подаче и напору.
/// </summary>
public sealed class ProjectPumpStationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pump-station-project-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Pump_station_designation_round_trips_through_the_project_file()
    {
        var path = Path.Combine(_dir, "project.waproj");
        var project = new ProjectFile
        {
            Name = "Пробный",
            Drawing = "floor.dwg",
            Design = new ProjectDesign
            {
                SprinklerK = 0.47,
                PumpStation = new ProjectPumpStation { SetDesignation = "Hydro MX-A 2/1 CR 45-4" },
            },
        };

        File.WriteAllText(path, ProjectFile.ToJson(project));

        var loaded = ProjectFile.Load(path);
        loaded.Design.PumpStation.SetDesignation.ShouldBe("Hydro MX-A 2/1 CR 45-4");
        var json = File.ReadAllText(path);
        json.ShouldContain("\"pumpStation\"");
        json.ShouldContain("\"setDesignation\"");
    }

    [Fact]
    public void LoadMerged_fills_the_pump_station_section_by_default()
    {
        var path = Path.Combine(_dir, "project.waproj");
        File.WriteAllText(path, """{ "schemaVersion": 1, "name": "Пробный", "drawing": "floor.dwg", "design": { "sprinklerK": 0.47 } }""");

        var root = ProjectJson.LoadMerged(path);
        root["design"]!["pumpStation"]!["setDesignation"]!.GetValue<string>().ShouldBe("");

        root["design"]!["pumpStation"]!["setDesignation"] = "Hydro MX-A 1/1 CR 10-1";
        ProjectJson.Save(path, root);
        ProjectFile.Load(path).Design.PumpStation.SetDesignation.ShouldBe("Hydro MX-A 1/1 CR 10-1");
    }
}
