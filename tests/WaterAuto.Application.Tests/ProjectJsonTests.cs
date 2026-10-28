using System.IO;
using System.Text.Json.Nodes;
using WaterAuto.Application;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Ввод исходных данных в программе (окно «Исходные данные») сохраняет проект через <see cref="ProjectJson"/>:
/// значения по умолчанию видны и явны, сохранённый файл читается моделью, ошибочный файл не перезаписывает проект.
/// </summary>
public sealed class ProjectJsonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-project-json-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void LoadMerged_fills_defaults_and_keeps_project_values()
    {
        var path = Write("""
        {
          "schemaVersion": 1,
          "name": "Пробный",
          "drawing": "floor.dwg",
          "design": { "sprinklerK": 0.47, "maxAmbientTemperature": 35, "supplyDn": 100 }
        }
        """);

        var root = ProjectJson.LoadMerged(path);
        root["design"]!["sprinklerK"]!.GetValue<double>().ShouldBe(0.47);
        root["design"]!["maxAmbientTemperature"]!.GetValue<double>().ShouldBe(35);
        root["design"]!["pumpRoom"]!["height"]!.GetValue<double>().ShouldBe(3.6); // умолчание модели
        root["title"]!["organization"]!.GetValue<string>().ShouldBe("[Наименование организации]");
    }

    [Fact]
    public void Temperature_set_in_input_window_is_saved_and_read_back_by_the_model()
    {
        var path = Write("""{ "schemaVersion": 1, "name": "Пробный", "drawing": "floor.dwg", "design": { "sprinklerK": 0.47 } }""");
        var root = ProjectJson.LoadMerged(path);
        root["design"]!["maxAmbientTemperature"] = 40;
        root["design"]!["pumpRoom"]!["groundElevation"] = -0.5;

        ProjectJson.Save(path, root);

        var project = ProjectFile.Load(path);
        project.Design.MaxAmbientTemperature.ShouldBe(40);
        project.Design.PumpRoom.GroundElevation.ShouldBe(-0.5);
        File.ReadAllText(path).ShouldContain("maxAmbientTemperature");
    }

    [Fact]
    public void Invalid_project_is_not_written_over()
    {
        var path = Write("""{ "schemaVersion": 1, "name": "Пробный", "drawing": "floor.dwg" }""");
        var before = File.ReadAllText(path);
        var root = ProjectJson.LoadMerged(path);
        root["drawing"] = "";

        Should.Throw<InvalidDataException>(() => ProjectJson.Save(path, root));
        File.ReadAllText(path).ShouldBe(before);
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "project.waproj");
        File.WriteAllText(path, json);
        return path;
    }
}
