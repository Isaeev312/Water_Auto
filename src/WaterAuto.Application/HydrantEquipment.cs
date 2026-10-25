using System.Globalization;
using System.Text.Json;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Оборудование пожарного крана для спецификации по шаблону data/catalog/hydrant_set.json: клапан, головка, рукав, ствол, шкаф —
/// пока на один кран (число кранов на этаже программа не знает).
/// </summary>
public static class HydrantEquipment
{
    public static string PathFor(string normsDir) => Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "hydrant_set.json");

    public static IReadOnlyList<TakeoffItem> Items(string path, HydrantSettings s, int hydrants = 1)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var dn = s.ValveDn.ToString(CultureInfo.InvariantCulture);
        var barrel = root.TryGetProperty("barrel", out var b) && b.TryGetProperty(dn, out var name) ? name.GetString()! : "";
        return root.GetProperty("items").EnumerateArray().Select(i => new TakeoffItem(
            TakeoffGroup.Hydrants,
            i.GetProperty("name").GetString()!
                .Replace("{dn}", dn, StringComparison.Ordinal)
                .Replace("{hose}", s.HoseLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{nozzle}", s.NozzleMm.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{barrel}", barrel, StringComparison.Ordinal),
            i.GetProperty("designation").GetString(),
            i.GetProperty("perHydrant").GetDouble() * hydrants,
            i.GetProperty("unit").GetString()!,
            hydrants == 1 ? "на один пожарный кран" : null)).ToList();
    }
}
