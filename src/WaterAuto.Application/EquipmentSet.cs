using System.Globalization;
using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Комплект изделий на одно место установки по шаблону каталога (data/catalog/control_unit.json — узел управления на секцию,
/// flushing_set.json — промывочное устройство): строки спецификации, сложенные по DN мест.
/// </summary>
public static class EquipmentSet
{
    public const string ControlUnitFile = "control_unit.json";
    public const string FlushingFile = "flushing_set.json";

    public static string PathFor(string normsDir, string file) => Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, file);

    /// <param name="places">DN каждого места установки.</param>
    public static IReadOnlyList<TakeoffItem> Items(string path, TakeoffGroup group, IEnumerable<int> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var templates = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var items = new List<TakeoffItem>();

        // Строки — по позициям шаблона, внутри — по возрастанию DN (ГОСТ 21.110, п. 4.5).
        var byDn = places.GroupBy(dn => dn).OrderBy(g => g.Key).ToList();
        foreach (var t in templates)
        {
            foreach (var g in byDn)
            {
                items.Add(new TakeoffItem(
                    group,
                    t.GetProperty("name").GetString()!.Replace("{dn}", g.Key.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    t.TryGetProperty("designation", out var d) && d.GetString() is { Length: > 0 } designation ? designation : null,
                    t.GetProperty("per").GetDouble() * g.Count(),
                    t.GetProperty("unit").GetString()!,
                    t.TryGetProperty("note", out var n) ? n.GetString() : null));
            }
        }

        // Одинаковые строки (шаблон без {dn}) складываются.
        return items
            .GroupBy(i => (i.Name, i.Designation, i.Unit, i.Note))
            .Select(g => g.First() with { Quantity = g.Sum(i => i.Quantity) })
            .ToList();
    }
}
