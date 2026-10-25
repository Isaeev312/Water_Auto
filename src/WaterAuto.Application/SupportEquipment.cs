using System.Globalization;
using System.Text.Json;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Крепёж опор трубопроводов для спецификации по шаблону data/catalog/pipe_support.json: хомут, шпилька, анкер, гайки, шайбы —
/// резьба по DN трубы, длина шпильки — из проекта.
/// </summary>
public static class SupportEquipment
{
    public static string PathFor(string normsDir) => Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "pipe_support.json");

    public static IReadOnlyList<TakeoffItem> Items(string path, PipeSupportsResult supports, double rodLengthMm)
    {
        ArgumentNullException.ThrowIfNull(supports);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var threads = root.GetProperty("threads").EnumerateArray()
            .Select(t => (DnMax: t.GetProperty("dnMax").GetInt32(), Thread: t.GetProperty("thread").GetString()!))
            .OrderBy(t => t.DnMax)
            .ToList();
        string Thread(int dn) => threads.FirstOrDefault(t => dn <= t.DnMax).Thread ?? threads[^1].Thread;
        var rod = rodLengthMm.ToString("0", CultureInfo.InvariantCulture);
        var items = new Dictionary<(string Name, string? Designation, string Unit), double>();
        // Крепёж группируется по резьбе (М8, М10, М12), внутри — по DN хомута.
        foreach (var g in supports.Points.GroupBy(p => p.Dn).OrderBy(g => threads.FindIndex(t => g.Key <= t.DnMax) is var k and >= 0 ? k : threads.Count).ThenBy(g => g.Key))
        {
            var thread = Thread(g.Key);
            foreach (var i in root.GetProperty("items").EnumerateArray())
            {
                var name = i.GetProperty("name").GetString()!
                    .Replace("{dn}", g.Key.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{thread}", thread, StringComparison.Ordinal)
                    .Replace("{rod}", rod, StringComparison.Ordinal);
                var key = (name, i.TryGetProperty("designation", out var d) ? d.GetString() : null, i.GetProperty("unit").GetString()!);
                items[key] = items.GetValueOrDefault(key) + (i.GetProperty("perSupport").GetDouble() * g.Count());
            }
        }

        return items.Select(kv => new TakeoffItem(TakeoffGroup.Supports, kv.Key.Name, kv.Key.Designation, kv.Value, kv.Key.Unit)).ToList();
    }
}
