using System.Text.Json;
using WaterAuto.Calc.Hydraulics;

namespace WaterAuto.Application;

/// <summary>Модель каталога data/catalog/sprinklers.json с признаками для отбора.</summary>
public sealed record CatalogSprinkler(SprinklerCandidate Candidate, string Kind, string Purpose, string Medium, string Deflector, string Designation, bool Verified, string Source);

/// <summary>Чтение каталога оросителей (схема data/schemas/sprinklers.schema.json) и отбор кандидатов для автоподбора.</summary>
public static class SprinklerCatalog
{
    public static string PathFor(string normsDir) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(normsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, "catalog", "sprinklers.json");

    public static IReadOnlyList<CatalogSprinkler> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<CatalogSprinkler>();
        foreach (var s in doc.RootElement.GetProperty("sprinklers").EnumerateArray())
        {
            string Str(string name, string fallback = "") => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
            var intensity = s.TryGetProperty("intensity", out var arr)
                ? arr.EnumerateArray()
                    .Where(x => !x.TryGetProperty("medium", out var m) || m.GetString() == "water")
                    .Select(x => (x.GetProperty("P").GetDouble(), x.GetProperty("i").GetDouble()))
                    .ToList()
                : [];
            var candidate = new SprinklerCandidate(
                Str("id"),
                Str("maker"),
                Str("model"),
                s.GetProperty("K").GetDouble(),
                s.GetProperty("pMin").GetDouble(),
                s.GetProperty("pMax").GetDouble(),
                s.TryGetProperty("area_m2", out var a) ? a.GetDouble() : null,
                intensity);
            list.Add(new CatalogSprinkler(candidate, Str("kind"), Str("purpose", "general"), Str("medium", "water"), Str("deflector", "any"), Str("designation"), s.GetProperty("verified").GetBoolean(), Str("source")));
        }

        return list;
    }

    /// <summary>Спринклеры общего назначения для воды с заданным положением розетки (или любым).</summary>
    public static IEnumerable<CatalogSprinkler> ForWater(IEnumerable<CatalogSprinkler> all, string deflector) =>
        all.Where(s => s.Kind == "sprinkler" && s.Purpose == "general" && s.Medium is "water" or "universal" && (s.Deflector == deflector || s.Deflector == "any"));
}
