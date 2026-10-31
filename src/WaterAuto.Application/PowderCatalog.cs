using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Модуль из каталога с паспортными данными для спецификации и пуска (ток, импульс, масса, сертификат).</summary>
public sealed record PowderModuleEntry(PowderModuleModel Model, string Designation, string Manufacturer, double? StartCurrent, double? StartDuration, double? MonitorCurrent, double? PowderMass, double? Mass, string? FireRank, string? Certificate);

/// <summary>Чтение каталога модулей порошкового пожаротушения (data/catalog/powder_modules.json); схему проверяет tools/ValidateData.</summary>
public static class PowderCatalog
{
    public const string File = "powder_modules.json";

    public static IReadOnlyList<PowderModuleEntry> Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var list = new List<PowderModuleEntry>();
        foreach (var m in doc.RootElement.GetProperty("modules").EnumerateArray())
        {
            double? Opt(string name) => m.TryGetProperty(name, out var v) ? v.GetDouble() : null;
            string? Text(string name) => m.TryGetProperty(name, out var v) ? v.GetString() : null;
            var coverage = m.GetProperty("coverage").EnumerateArray().Select(c => new PowderCoverage(
                c.GetProperty("heightM").GetDouble(),
                c.TryGetProperty("areaA", out var a) ? a.GetDouble() : null,
                c.TryGetProperty("areaB", out var b) ? b.GetDouble() : null,
                c.TryGetProperty("volume", out var v) ? v.GetDouble() : null)).ToList();
            var model = new PowderModuleModel(
                m.GetProperty("id").GetString()!,
                m.GetProperty("name").GetString()!,
                m.GetProperty("powder").GetString() == "B" ? PowderKind.B : PowderKind.AB,
                m.GetProperty("impulse").GetBoolean(),
                m.GetProperty("minHeight").GetDouble(),
                m.GetProperty("maxHeight").GetDouble(),
                coverage,
                Opt("k1"),
                Opt("k2"),
                Opt("k4"),
                m.GetProperty("source").GetString()!);
            list.Add(new PowderModuleEntry(model, m.GetProperty("designation").GetString()!, m.GetProperty("manufacturer").GetString()!,
                Opt("startCurrent"), Opt("startDuration"), Opt("monitorCurrent"), Opt("powderMass"), Opt("mass"), Text("fireRank"), Text("certificate")));
        }

        return list;
    }
}
