using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Каталог комплектных насосных установок (data/catalog/pump_sets_*.json) и условные габариты оборудования насосной
/// (data/catalog/pump_room.json).
/// </summary>
public static class PumpCatalog
{
    public static string Dir(string normsDir) => Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!;

    public static IReadOnlyList<PumpSet> Sets(string normsDir)
    {
        var sets = new List<PumpSet>();
        foreach (var file in Directory.EnumerateFiles(Dir(normsDir), "pump_sets_*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var maker = doc.RootElement.GetProperty("maker").GetString()!;
            foreach (var s in doc.RootElement.GetProperty("sets").EnumerateArray())
            {
                var cab = s.GetProperty("cabinet");
                sets.Add(new PumpSet(
                    s.GetProperty("designation").GetString()!,
                    s.GetProperty("pump").GetString()!,
                    s.GetProperty("mainPumps").GetInt32(),
                    s.GetProperty("standbyPumps").GetInt32(),
                    s.GetProperty("powerKw").GetDouble(),
                    [.. s.GetProperty("pumpCurve").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble()))],
                    new Footprint(s.GetProperty("lengthMm").GetDouble(), s.GetProperty("widthMm").GetDouble(), s.GetProperty("heightMm").GetDouble()),
                    new Footprint(cab.GetProperty("lengthMm").GetDouble(), cab.GetProperty("widthMm").GetDouble(), cab.GetProperty("heightMm").GetDouble()),
                    s.GetProperty("collectorDn").GetInt32(),
                    maker,
                    s.GetProperty("source").GetString()!));
            }
        }

        return sets;
    }

    /// <summary>Условные габариты узла управления и жокей-насоса; обозначение ТУ установки (если задано).</summary>
    public static (Footprint ControlUnit, Footprint Jockey, string? SetNote)? Room(string normsDir)
    {
        var path = Path.Combine(Dir(normsDir), "pump_room.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Footprint F(string key)
        {
            var e = doc.RootElement.GetProperty(key);
            return new Footprint(e.GetProperty("lengthMm").GetDouble(), e.GetProperty("widthMm").GetDouble(), e.GetProperty("heightMm").GetDouble());
        }

        return (F("controlUnit"), F("jockey"), doc.RootElement.TryGetProperty("setNote", out var n) ? n.GetString() : null);
    }
}
