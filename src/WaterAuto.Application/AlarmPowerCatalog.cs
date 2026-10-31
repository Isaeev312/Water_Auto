using System.Globalization;
using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Каталог приборов питания автоматики АУПТ в АПС (data/catalog/alarm_power.json, ADR 0012, п. 5). Схему проверяет
/// tools/ValidateData; здесь — чтение и сверка напряжений: токи КДЛ и РИП при 12 и 24 В разные, смешение — ошибка данных.
/// </summary>
public static class AlarmPowerCatalog
{
    public const string File = "alarm_power.json";

    public static AlarmPowerSet Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = doc.RootElement;
        var voltage = new Voltage(root.GetProperty("voltage").GetDouble());
        var c = root.GetProperty("controller");
        var m = root.GetProperty("module");
        var s = root.GetProperty("supply");
        var b = root.GetProperty("battery");
        foreach (var (name, device) in new[] { ("controller", c), ("supply", s) })
        {
            var v = new Voltage(device.GetProperty("voltage").GetDouble());
            if (v != voltage)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"{path}: {name} задан при {v}, а набор — при {voltage}; токи одного набора — при одном напряжении."));
            }
        }

        var detectors = new Dictionary<string, LoopDevice>();
        if (root.TryGetProperty("detectors", out var list))
        {
            foreach (var d in list.EnumerateObject())
            {
                detectors[d.Name] = new LoopDevice(Text(d.Value, "name"), A(d.Value, "loopCurrent"), Text(d.Value, "source")) { Certificate = Cert(d.Value) };
            }
        }

        return new AlarmPowerSet(
            voltage,
            new LoopController(Text(c, "name"), A(c, "idle"), A(c, "loaded"), A(c, "loadOfLoaded"), A(c, "max"), A(c, "maxLoopLoad"),
                c.GetProperty("maxAddresses").GetInt32(), Text(c, "source")) { Certificate = Cert(c) },
            new LoopModule(Text(m, "name"), m.GetProperty("inputs").GetInt32(), A(m, "loopCurrent"), m.GetProperty("maxPerLoop").GetInt32(), Text(m, "source")) { Certificate = Cert(m) },
            new PowerSupply(Text(s, "name"), A(s, "ownCurrent"), Text(s, "source")) { Certificate = Cert(s) },
            new BatteryModel(Text(b, "name"), new Charge(b.GetProperty("nominal").GetDouble()), b.GetProperty("endOfLifePercent").GetDouble(),
                b.GetProperty("discharge").EnumerateObject().ToDictionary(p => TimeSpan.FromHours(double.Parse(p.Name, CultureInfo.InvariantCulture)), p => new Current(p.Value.GetDouble())),
                Text(b, "source")) { Certificate = Cert(b) })
        { Detectors = detectors };
    }

    private static Current A(JsonElement e, string name) => new(e.GetProperty(name).GetDouble());

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static string? Cert(JsonElement e) => e.TryGetProperty("certificate", out var c) ? c.GetString() : null;
}
