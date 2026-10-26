using System.Globalization;
using System.Text.Json;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Оборудование пожарного крана для спецификации по шаблону data/catalog/hydrant_set.json: клапан, головка, рукав, ствол, шкаф —
/// по числу расставленных кранов; при давлении у клапана выше предела — диафрагма (шаблон pressureReducer).
/// </summary>
public static class HydrantEquipment
{
    public static string PathFor(string normsDir) => Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "hydrant_set.json");

    public static IReadOnlyList<TakeoffItem> Items(string path, HydrantSettings s, int hydrants = 1, bool reducer = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var dn = s.ValveDn.ToString(CultureInfo.InvariantCulture);
        var barrel = root.TryGetProperty("barrel", out var b) && b.TryGetProperty(dn, out var name) ? name.GetString()! : "";
        var templates = root.GetProperty("items").EnumerateArray().ToList();
        if (reducer && root.TryGetProperty("pressureReducer", out var r))
        {
            templates.Add(r);
        }

        return templates.Select(i => new TakeoffItem(
            TakeoffGroup.Hydrants,
            i.GetProperty("name").GetString()!
                .Replace("{dn}", dn, StringComparison.Ordinal)
                .Replace("{hose}", s.HoseLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{nozzle}", s.NozzleMm.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{barrel}", barrel, StringComparison.Ordinal),
            i.GetProperty("designation").GetString() is { Length: > 0 } d ? d : null,
            i.GetProperty("perHydrant").GetDouble() * hydrants,
            i.GetProperty("unit").GetString()!,
            Note(i))).ToList();

        // Примечание строки: своё из шаблона и «на один пожарный кран», для диафрагмы — про отверстие.
        string? Note(JsonElement i)
        {
            if (i.GetProperty("name").GetString()!.StartsWith("Диафрагма", StringComparison.Ordinal))
            {
                return "диаметр отверстия — по расчёту";
            }

            var parts = new[] { hydrants == 1 ? "на один пожарный кран" : null, i.TryGetProperty("note", out var n) ? n.GetString() : null }.OfType<string>().ToList();
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }
    }
}
