using System.Globalization;
using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Чтение каталога кабелей АПС (data/catalog/alarm_cables.json); схему проверяет tools/ValidateData.</summary>
public static class AlarmCableCatalog
{
    public const string File = "alarm_cables.json";

    public static CableCatalog Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = doc.RootElement;
        var loop = root.GetProperty("loop");
        var table = new List<LoopCableSection>();
        foreach (var row in loop.GetProperty("maxLengthKm").EnumerateArray())
        {
            var byLoad = row.GetProperty("byLoadMa").EnumerateObject()
                .ToDictionary(p => double.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetDouble() * 1000);
            table.Add(new LoopCableSection(row.GetProperty("section").GetDouble(), byLoad));
        }

        var speech = root.TryGetProperty("speech", out var sp) ? sp : (JsonElement?)null;
        return new CableCatalog(
            root.GetProperty("reserve").GetDouble(),
            root.GetProperty("inCabinetM").GetDouble(),
            loop.GetProperty("name").GetString()!,
            table,
            loop.GetProperty("source").GetString()!,
            root.GetProperty("power").GetProperty("name").GetString()!,
            root.GetProperty("mains").GetProperty("name").GetString()!,
            root.GetProperty("signal").GetProperty("name").GetString()!,
            speech?.GetProperty("name").GetString(),
            speech is { } s1 ? [.. s1.GetProperty("sections").EnumerateArray().Select(x => x.GetDouble())] : null,
            speech?.GetProperty("resistivity").GetDouble(),
            speech?.GetProperty("source").GetString());
    }
}
