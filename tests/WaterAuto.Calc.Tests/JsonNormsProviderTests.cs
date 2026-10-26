using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// TODO: перенести в tests/WaterAuto.Norms.Tests, когда проект появится (T8). На ветке T6
// проекта тестов норм ещё нет, поэтому тест лежит временно здесь.
public class JsonNormsProviderTests
{
    [Fact]
    public void Load_without_edition_throws_when_one_document_has_two_editions()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.Write("table_B_2_2020.json", edition: "ред. 2020", id: "test.tB_2.ed2020", dn: 1);
        normsDir.Write("table_B_2_2026.json", edition: "ред. 2026", id: "test.tB_2.ed2026", dn: 2);

        var error = Should.Throw<InvalidDataException>(() => JsonNormsProvider.Load(normsDir.Root));

        error.Message.ShouldContain("ред. 2020");
        error.Message.ShouldContain("ред. 2026");
    }

    [Fact]
    public void Load_with_edition_reads_only_files_of_that_edition()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.Write("table_B_2_2020.json", edition: "ред. 2020", id: "test.tB_2.ed2020", dn: 1);
        normsDir.Write("table_B_2_2026.json", edition: "ред. 2026", id: "test.tB_2.ed2026", dn: 2);

        var norms = JsonNormsProvider.Load(normsDir.Root, "ред. 2020");

        norms.Find("test.tB_2.ed2020").ShouldNotBeNull().Values["dn"].ShouldBe(1);
        norms.Find("test.tB_2.ed2026").ShouldBeNull();
        norms.ByClause("табл. Б.2").Count().ShouldBe(1);
    }
}

/// <summary>Временная папка с файлами норм минимального формата data/schemas/norms.schema.json.</summary>
internal sealed class TemporaryNormsDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WaterAuto.NormsTests." + Guid.NewGuid().ToString("N"));

    public TemporaryNormsDirectory() => Directory.CreateDirectory(Root);

    public void Write(string fileName, string edition, string id, int dn) =>
        File.WriteAllText(
            Path.Combine(Root, fileName),
            $$"""
            {
              "schemaVersion": 1,
              "doc": "СП 485.1311500.2020",
              "edition": "{{edition}}",
              "clause": "табл. Б.2",
              "records": [
                {
                  "id": "{{id}}",
                  "clause": "табл. Б.2",
                  "values": { "dn": {{dn}} },
                  "units": { "dn": "DN" }
                }
              ]
            }
            """);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка: если файл ещё занят, ОС уберёт её сама. Тест на результат не влияет.
        }
    }
}
