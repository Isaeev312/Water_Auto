using WaterAuto.Norms;

namespace WaterAuto.Norms.Tests;

public class JsonNormsProviderTests
{
    private static readonly string[] ExactByClauseIds = ["test.clause.a", "test.clause.b"];

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

    [Fact]
    public void Load_throws_with_id_when_same_id_is_in_two_files()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.Write("table_B_2_a.json", edition: "ред. 2020", id: "test.tB_2.duplicate", dn: 1);
        normsDir.Write("table_B_2_b.json", edition: "ред. 2020", id: "test.tB_2.duplicate", dn: 2);

        var error = Should.Throw<InvalidDataException>(() => JsonNormsProvider.Load(normsDir.Root));

        error.Message.ShouldContain("test.tB_2.duplicate");
    }

    [Fact]
    public void Load_reads_null_value_as_null_and_does_not_replace_it_with_zero()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.WriteRaw(
            "table_B_2.json",
            """
            {
              "schemaVersion": 1,
              "doc": "СП 485.1311500.2020",
              "edition": "ред. 2020",
              "clause": "табл. Б.2",
              "records": [
                {
                  "id": "test.tB_2.nullKt",
                  "clause": "табл. Б.2",
                  "values": { "dn": 15, "Kt": null },
                  "units": { "dn": "DN", "Kt": "л²/с²" }
                }
              ]
            }
            """);

        var record = JsonNormsProvider.Load(normsDir.Root).Find("test.tB_2.nullKt").ShouldNotBeNull();

        record.Values["Kt"].ShouldBeNull();
        record.Values["dn"].ShouldBe(15.0);
    }

    [Fact]
    public void Load_keeps_non_string_attrs_as_raw_json_text()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.WriteRaw(
            "table_B_2.json",
            """
            {
              "schemaVersion": 1,
              "doc": "СП 485.1311500.2020",
              "edition": "ред. 2020",
              "clause": "табл. Б.2",
              "records": [
                {
                  "id": "test.tB_2.attrs",
                  "clause": "табл. Б.2",
                  "values": { "dn": 15 },
                  "units": { "dn": "DN" },
                  "attrs": { "standard": "ГОСТ 10704", "rows": 2, "verified": true }
                }
              ]
            }
            """);

        var record = JsonNormsProvider.Load(normsDir.Root).Find("test.tB_2.attrs").ShouldNotBeNull();

        record.Attributes["standard"].ShouldBe("ГОСТ 10704");
        record.Attributes["rows"].ShouldBe("2");
        record.Attributes["verified"].ShouldBe("true");
    }

    [Fact]
    public void Load_inherits_file_clause_and_record_clause_wins()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.WriteRaw(
            "appendix_B_clauses.json",
            """
            {
              "schemaVersion": 1,
              "doc": "СП 485.1311500.2020",
              "edition": "ред. 2020",
              "clause": "табл. Б.2",
              "records": [
                {
                  "id": "test.clause.inherited",
                  "values": { "dn": 15 },
                  "units": { "dn": "DN" }
                },
                {
                  "id": "test.clause.own",
                  "clause": "п. Б.1.2",
                  "values": { "dn": 20 },
                  "units": { "dn": "DN" }
                }
              ]
            }
            """);

        var norms = JsonNormsProvider.Load(normsDir.Root);

        norms.Find("test.clause.inherited").ShouldNotBeNull().Clause.ShouldBe("табл. Б.2");
        norms.Find("test.clause.own").ShouldNotBeNull().Clause.ShouldBe("п. Б.1.2");
    }

    [Fact]
    public void ByClause_returns_only_records_with_exact_clause()
    {
        using var normsDir = new TemporaryNormsDirectory();
        normsDir.WriteRaw(
            "appendix_B_clauses.json",
            """
            {
              "schemaVersion": 1,
              "doc": "СП 485.1311500.2020",
              "edition": "ред. 2020",
              "records": [
                {
                  "id": "test.clause.a",
                  "clause": "табл. Б.2",
                  "values": { "dn": 15 },
                  "units": { "dn": "DN" }
                },
                {
                  "id": "test.clause.b",
                  "clause": "табл. Б.2",
                  "values": { "dn": 20 },
                  "units": { "dn": "DN" }
                },
                {
                  "id": "test.clause.prefix",
                  "clause": "табл. Б.20",
                  "values": { "dn": 25 },
                  "units": { "dn": "DN" }
                },
                {
                  "id": "test.clause.case",
                  "clause": "Табл. Б.2",
                  "values": { "dn": 32 },
                  "units": { "dn": "DN" }
                }
              ]
            }
            """);

        var ids = JsonNormsProvider.Load(normsDir.Root)
            .ByClause("табл. Б.2")
            .Select(record => record.Id)
            .Order(StringComparer.Ordinal);

        ids.ShouldBe(ExactByClauseIds);
    }

    [Fact]
    public void Load_reads_repository_norms_and_finds_known_record()
    {
        var norms = JsonNormsProvider.Load(RepoPaths.Norms);

        norms.Find("sp485.tB_2.gost10704.dn15.18x2").ShouldNotBeNull();
    }
}

/// <summary>Временная папка с файлами норм минимального формата data/schemas/norms.schema.json.</summary>
internal sealed class TemporaryNormsDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WaterAuto.NormsTests." + Guid.NewGuid().ToString("N"));

    public TemporaryNormsDirectory() => Directory.CreateDirectory(Root);

    /// <summary>Файл с одной записью табл. Б.2 — для тестов редакций и повторов id.</summary>
    public void Write(string fileName, string edition, string id, int dn) =>
        WriteRaw(
            fileName,
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

    public void WriteRaw(string fileName, string json) =>
        File.WriteAllText(Path.Combine(Root, fileName), json);

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
