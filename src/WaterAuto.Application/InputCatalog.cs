using System.Globalization;
using System.Text.Json;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Application;

/// <summary>
/// Списки допустимых значений для ввода исходных данных: подписи и величины берутся из data/ (нормы и каталоги),
/// а не из кода. Нужны приложению для выпадающих списков; расчёт эти значения не подставляет молча.
/// </summary>
public static class InputCatalog
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Группа помещений по приложению А СП 485 (строка табл. 6.1) с параметрами строки.</summary>
    public sealed record RoomGroupOption(string Id, string Label);

    /// <summary>Ороситель каталога: модель, K, положение розетки и номинальные температуры.</summary>
    public sealed record SprinklerOption(
        string Id,
        string Label,
        string Model,
        string Maker,
        double K,
        string Deflector,
        IReadOnlyList<int> Temperatures);

    /// <summary>Стандарт труб из перечня документов (data/catalog/documents.json).</summary>
    public sealed record PipeStandardOption(string Designation, string Name);

    /// <summary>Комплектная насосная установка каталога: количество основных и резервных насосов, мощность, пределы по Q и H.</summary>
    public sealed record PumpSetOption(
        string Designation,
        string Label,
        string Pump,
        int MainPumps,
        int StandbyPumps,
        double PowerKw,
        int CollectorDn,
        string Maker,
        double QMaxM3h,
        double HMaxM);

    /// <summary>Строка табл. Ж.1 СН КР 40-04:2025: описание здания и потребный расход на внутреннее пожаротушение.</summary>
    public sealed record HydrantRowOption(string Id, string Label, int Jets, double JetFlow);

    /// <summary>Модель извещателя для выбора в разделе alarm: ID каталога, подпись, вид (smoke/heat/manual).</summary>
    public sealed record DetectorOption(string Id, string Label, string Kind);

    /// <summary>Документ и редакция норм, по которым считается проект (для строки состояния).</summary>
    public sealed record NormsInfo(string Document, string Edition);

    /// <summary>Папка каталогов рядом с нормами: data/catalog.</summary>
    public static string CatalogDir(string normsDir) => Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!;

    /// <summary>Строки табл. 6.1: подпись собрана из величин норм (i, Q, S, шаг, время), поэтому не дублирует числа в коде.</summary>
    public static IReadOnlyList<RoomGroupOption> RoomGroups(string normsDir)
    {
        var norms = JsonNormsProvider.Load(normsDir);
        return norms.ByClause("табл. 6.1")
            .Where(r => r.Attributes.TryGetValue("group", out var g) && g.Length > 0)
            .OrderBy(r => GroupOrder(r.Attributes["group"]))
            .Select(r => new RoomGroupOption(r.Attributes["group"], RoomGroupLabel(r)))
            .ToList();
    }

    /// <summary>Оросители общего назначения для воды — те же, из которых выбирает автоподбор (SprinklerCatalog.ForWater).</summary>
    public static IReadOnlyList<SprinklerOption> Sprinklers(string normsDir)
    {
        var path = SprinklerCatalog.PathFor(normsDir);
        if (!File.Exists(path))
        {
            return [];
        }

        return
        [
            .. SprinklerCatalog.Water(SprinklerCatalog.Load(path))
                .Select(s => new SprinklerOption(
                    s.Candidate.Id,
                    SprinklerLabel(s),
                    s.Candidate.Model,
                    s.Candidate.Maker,
                    s.Candidate.K,
                    s.Deflector,
                    s.Temperatures ?? []))
                .OrderBy(s => s.Maker, StringComparer.Ordinal)
                .ThenBy(s => s.Model, StringComparer.Ordinal),
        ];
    }

    /// <summary>Стандарты стальных труб из ведомости документов (по наименованию «Трубы стальные…»).</summary>
    public static IReadOnlyList<PipeStandardOption> PipeStandards(string normsDir)
    {
        var path = Path.Combine(CatalogDir(normsDir), "documents.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("documents", out var documents))
        {
            return [];
        }

        return
        [
            .. documents.EnumerateArray()
                .Where(d => d.TryGetProperty("designation", out var designation) && designation.GetString() is { Length: > 0 }
                    && d.TryGetProperty("name", out var name) && name.GetString() is { } n
                    && n.StartsWith("Трубы стальные", StringComparison.Ordinal))
                .Select(d => new PipeStandardOption(d.GetProperty("designation").GetString()!, d.GetProperty("name").GetString()!))
                .DistinctBy(d => d.Designation, StringComparer.Ordinal)
                .OrderBy(d => d.Designation, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>DN труб из принятого сортамента data/catalog/pipe_sizes.json (по возрастанию).</summary>
    public static IReadOnlyList<int> PipeDns(string normsDir)
    {
        var path = Path.Combine(CatalogDir(normsDir), "pipe_sizes.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("sizes", out var sizes)
            ? [.. sizes.EnumerateArray().Select(s => s.GetProperty("dn").GetInt32()).Distinct().Order()]
            : [];
    }

    /// <summary>Комплектные насосные установки каталога с количеством основных и резервных насосов.</summary>
    public static IReadOnlyList<PumpSetOption> PumpSets(string normsDir)
    {
        var sets = PumpCatalog.Sets(normsDir);
        return
        [
            .. sets.Select(s =>
            {
                var curve = s.PumpCurve.OrderBy(p => p.QM3h).ToList();
                var qMax = curve.Count == 0 ? 0 : curve[^1].QM3h * s.MainPumps;
                var hMax = curve.Count == 0 ? 0 : curve[0].HM;
                var label = string.Create(
                    Ru,
                    $"{s.Designation} — {s.MainPumps} осн. + {s.StandbyPumps} рез., {s.PowerKw:0.##} кВт, Q до {qMax:0.#} м³/ч, H до {hMax:0.#} м");
                return new PumpSetOption(s.Designation, label, s.Pump, s.MainPumps, s.StandbyPumps, s.PowerKw, s.CollectorDn, s.Maker, qMax, hMax);
            }).OrderBy(s => s.Designation, StringComparer.Ordinal),
        ];
    }

    /// <summary>Строки табл. Ж.1 СН КР 40-04:2025 для выбора расхода ВПВ (подпись — описание здания из данных норм).</summary>
    public static IReadOnlyList<DetectorOption> Detectors(string normsDir)
    {
        var path = Path.Combine(CatalogDir(normsDir), AlarmDetectorCatalog.File);
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. AlarmDetectorCatalog.Load(path).Select(m => new DetectorOption(m.Id, $"{m.Name} ({m.Designation}), {m.Manufacturer}", m.Kind switch { Core.DetectorKind.Smoke => "smoke", Core.DetectorKind.Heat => "heat", _ => "manual" }))];
    }

    public static IReadOnlyList<HydrantRowOption> HydrantRows(string normsDir)
    {
        var norms = JsonNormsProvider.Load(normsDir);
        return
        [
            .. norms.ByClause("табл. Ж.1")
                .Where(r => r.Attributes.ContainsKey("description")
                    && r.Values.GetValueOrDefault("jets") is not null
                    && r.Values.GetValueOrDefault("jetFlow") is not null)
                .OrderBy(r => GroupOrder(r.Attributes.GetValueOrDefault("group") ?? ""))
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new HydrantRowOption(
                    r.Id,
                    string.Create(Ru, $"{r.Attributes["description"]} — {r.Values["jets"]!.Value:0} × {r.Values["jetFlow"]!.Value:0.#} л/с"),
                    (int)r.Values["jets"]!.Value,
                    r.Values["jetFlow"]!.Value))
                .ToList(),
        ];
    }

    /// <summary>Документ и редакция норм АУП (data/norms/sp485) — для строки состояния и подписей.</summary>
    public static NormsInfo? Norms(string normsDir)
    {
        var sp485 = Path.Combine(normsDir, "sp485");
        var files = Directory.Exists(sp485)
            ? Directory.EnumerateFiles(sp485, "*.json").Order(StringComparer.Ordinal)
            : Directory.EnumerateFiles(normsDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("doc", out var d) && doc.RootElement.TryGetProperty("edition", out var e))
            {
                return new NormsInfo(d.GetString() ?? "", e.GetString() ?? "");
            }
        }

        return null;
    }

    private static string RoomGroupLabel(NormRecord record)
    {
        var v = record.Values;
        string? F(string key, string unit, string format = "0.##") =>
            v.TryGetValue(key, out var value) && value is { } number ? string.Create(Ru, $"{number.ToString(format, Ru)} {unit}") : null;
        var intensity = F("i_water", "л/(с·м²)") ?? F("i_foam", "л/(с·м²)");
        var flow = F("Q_water", "л/с", "0.#") ?? F("Q_foam", "л/с", "0.#");
        var parts = new List<string>();
        if (intensity is not null)
        {
            parts.Add($"i = {intensity}");
        }

        if (flow is not null)
        {
            parts.Add($"Q = {flow}");
        }

        if (F("S", "м²", "0.#") is { } area)
        {
            parts.Add($"S = {area}");
        }

        if (F("maxSpacing", "м") is { } spacing)
        {
            parts.Add($"шаг ≤ {spacing}");
        }

        if (F("t_min", "мин", "0.#") is { } time)
        {
            parts.Add($"t = {time}");
        }

        return $"Группа {record.Attributes["group"]} — {string.Join(", ", parts)}";
    }

    private static string SprinklerLabel(CatalogSprinkler sprinkler)
    {
        var deflector = sprinkler.Deflector switch
        {
            "up" => "розетка вверх",
            "down" => "розетка вниз",
            "horizontal" => "розетка горизонтально",
            "concealed" => "скрытый",
            _ => "любое положение",
        };
        var temps = sprinkler.Temperatures is { Count: > 0 } t ? $", T {t.Min()}–{t.Max()} °С" : "";
        return string.Create(Ru, $"{sprinkler.Candidate.Model} — K {sprinkler.Candidate.K:0.##}, {deflector}{temps}");
    }

    // «4.1» и «4.2» — отдельные строки таблицы, нумерация не всегда целая.
    private static double GroupOrder(string group) =>
        double.TryParse(group, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.MaxValue;
}
