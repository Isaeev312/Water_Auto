using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Модуль газового пожаротушения из каталога с паспортными данными для спецификации и пуска.</summary>
public sealed record GasModuleEntry(GasModuleModel Model, string Designation, string Manufacturer, double? Pressure, double? StartCurrent, string? Certificate);

/// <summary>Чтение каталога модулей газового пожаротушения (data/catalog/gas_modules.json); схему проверяет tools/ValidateData.</summary>
public static class GasCatalog
{
    public const string File = "gas_modules.json";

    public static IReadOnlyList<GasModuleEntry> Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var list = new List<GasModuleEntry>();
        foreach (var m in doc.RootElement.GetProperty("modules").EnumerateArray())
        {
            double? Opt(string name) => m.TryGetProperty(name, out var v) ? v.GetDouble() : null;
            var charges = m.GetProperty("charges").EnumerateObject().ToDictionary(
                c => c.Name, c => new GasModuleCharge(c.Value.GetProperty("maxFill").GetDouble(), c.Value.TryGetProperty("residue", out var r) ? r.GetDouble() : null), StringComparer.Ordinal);
            var model = new GasModuleModel(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!, m.GetProperty("volume").GetDouble(), charges, m.GetProperty("source").GetString()!);
            list.Add(new GasModuleEntry(model, m.GetProperty("designation").GetString()!, m.GetProperty("manufacturer").GetString()!, Opt("pressure"), Opt("startCurrent"),
                m.TryGetProperty("certificate", out var cert) ? cert.GetString() : null));
        }

        return list;
    }
}
