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
                    s.GetProperty("source").GetString()!)
                {
                    CollectorAxisMm = s.TryGetProperty("collectorAxisMm", out var axis) ? axis.GetDouble() : 200,
                });
            }
        }

        return sets;
    }

    /// <summary>Обозначение арматуры изготовителя по виду и DN (data/catalog/valves_*.json); null — в каталоге нет.</summary>
    public static Func<string, int, string?> Valves(string normsDir)
    {
        var rows = new List<(string Kind, int[] Dn, string Designation)>();
        foreach (var file in Directory.EnumerateFiles(Dir(normsDir), "valves_*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var kind in doc.RootElement.GetProperty("kinds").EnumerateObject())
            {
                foreach (var r in kind.Value.EnumerateArray())
                {
                    rows.Add((kind.Name, [.. r.GetProperty("dn").EnumerateArray().Select(x => x.GetInt32())], r.GetProperty("designation").GetString()!));
                }
            }
        }

        return (kind, dn) => rows.FirstOrDefault(r => r.Kind == kind && r.Dn.Contains(dn)) is { Designation: { } d }
            ? d.Replace("{dn}", dn.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : null;
    }

    /// <summary>Муфты и фасонные части под муфту изготовителя (data/catalog/grooved_*.json); null — каталога нет.</summary>
    public static GroovedCatalog? Grooved(string normsDir)
    {
        var file = Directory.EnumerateFiles(Dir(normsDir), "grooved_*.json").Order(StringComparer.Ordinal).FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var items = new Dictionary<string, IReadOnlyList<GroovedItem>>(StringComparer.Ordinal);
        foreach (var kind in doc.RootElement.GetProperty("items").EnumerateObject())
        {
            items[kind.Name] = [.. kind.Value.EnumerateArray().Select(i => new GroovedItem(
                i.GetProperty("od").GetDouble(),
                i.TryGetProperty("od2", out var o2) ? o2.GetDouble() : null,
                i.GetProperty("designation").GetString()!))];
        }

        return new GroovedCatalog(doc.RootElement.GetProperty("toleranceMm").GetDouble(), items);
    }

    /// <summary>Правило назначения расхода и уставок жокей-насоса по практике (data/catalog/jockey_practice.json); null — нет файла.</summary>
    public static JockeyPractice? JockeyRule(string normsDir)
    {
        var path = Path.Combine(Dir(normsDir), "jockey_practice.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        return new JockeyPractice(r.GetProperty("flowShareOfDictating").GetDouble(), r.GetProperty("stopAboveStartMpa").GetDouble(), r.GetProperty("source").GetString()!);
    }

    /// <summary>Серия жокей-насосов изготовителя (data/catalog/jockey_pumps_*.json, первый по имени); null — каталога нет.</summary>
    public static JockeyPumpSeries? JockeyPumps(string normsDir)
    {
        var file = Directory.EnumerateFiles(Dir(normsDir), "jockey_pumps_*.json").Order(StringComparer.Ordinal).FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        return new JockeyPumpSeries(
            r.GetProperty("designation").GetString()!,
            r.GetProperty("document").GetString()!,
            new Flow(r.GetProperty("flowLpsMin").GetDouble()),
            new Flow(r.GetProperty("flowLpsMax").GetDouble()),
            new Pressure(r.GetProperty("pressureMpaMin").GetDouble()),
            new Pressure(r.GetProperty("pressureMpaMax").GetDouble()),
            r.GetProperty("includesTank").GetBoolean());
    }

    /// <summary>Мембранные баки для жокей-насоса (data/catalog/membrane_tanks_*.json).</summary>
    public static IReadOnlyList<MembraneTank> Tanks(string normsDir)
    {
        var tanks = new List<MembraneTank>();
        foreach (var file in Directory.EnumerateFiles(Dir(normsDir), "membrane_tanks_*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var t in doc.RootElement.GetProperty("tanks").EnumerateArray())
            {
                tanks.Add(new MembraneTank(
                    t.GetProperty("designation").GetString()!,
                    t.GetProperty("volumeL").GetDouble(),
                    t.GetProperty("pnBar").GetDouble(),
                    t.GetProperty("prechargeBar").GetDouble(),
                    t.GetProperty("document").GetString()!));
            }
        }

        return tanks;
    }

    /// <summary>Правила прокладки обвязки из data/catalog/pump_room.json (рекомендации литературы); null — нет данных.</summary>
    public static (double WallOffset, double BracketHeight, double LevelGap, string Source, int JockeyDn)? Piping(string normsDir)
    {
        var path = Path.Combine(Dir(normsDir), "pump_room.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("piping", out var p))
        {
            return null;
        }

        return (p.GetProperty("wallOffsetMm").GetDouble(), p.GetProperty("bracketHeightMm").GetDouble(), p.GetProperty("levelGapMm").GetDouble(), p.GetProperty("source").GetString()!, p.TryGetProperty("jockeyDn", out var j) ? j.GetInt32() : 32);
    }

    /// <summary>Запас гильзы по диаметру и отступ фундамента от рамы, мм (data/catalog/pump_room.json, piping).</summary>
    public static (double SleeveExtra, double FoundationMargin) PipingExtras(string normsDir)
    {
        var path = Path.Combine(Dir(normsDir), "pump_room.json");
        if (!File.Exists(path))
        {
            return (50, 100);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("piping", out var p)
            ? (p.TryGetProperty("sleeveExtraMm", out var s) ? s.GetDouble() : 50, p.TryGetProperty("foundationMarginMm", out var f) ? f.GetDouble() : 100)
            : (50, 100);
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
