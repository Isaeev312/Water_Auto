using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Релейный блок управления из каталога: выходы, ток линии и питания, адреса.</summary>
public sealed record RelayModel(string Id, string Name, string Designation, string Manufacturer, int Outputs, PoweredDevice Device);

/// <summary>Оповещатель из каталога с паспортом питания.</summary>
public sealed record NotifierEntry(string Id, string Designation, string Manufacturer, NotifierModel Model, PoweredDevice Device);

/// <summary>Чтение каталога оповещателей и релейных блоков (data/catalog/alarm_notifiers.json); схему проверяет tools/ValidateData.</summary>
public static class AlarmNotifierCatalog
{
    public const string File = "alarm_notifiers.json";

    public static (IReadOnlyList<NotifierEntry> Notifiers, IReadOnlyList<RelayModel> Relays) Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = doc.RootElement;
        var notifiers = new List<NotifierEntry>();
        foreach (var n in root.GetProperty("notifiers").EnumerateArray())
        {
            var kind = n.GetProperty("kind").GetString() switch
            {
                "sound" => NotifierKind.Sound,
                "speech" => NotifierKind.Speech,
                "light" => NotifierKind.Light,
                var k => throw new InvalidDataException($"{path}: неизвестный вид оповещателя «{k}»."),
            };
            var device = Device(n, Text(n, "name"));
            notifiers.Add(new NotifierEntry(Text(n, "id"), Text(n, "designation"), Text(n, "manufacturer"),
                new NotifierModel(Text(n, "name"), kind, n.TryGetProperty("levelAt1m", out var l) ? l.GetDouble() : null, device.Alarm, Text(n, "source"), device.Certificate), device));
        }

        var relays = new List<RelayModel>();
        foreach (var r in root.GetProperty("relays").EnumerateArray())
        {
            relays.Add(new RelayModel(Text(r, "id"), Text(r, "name"), Text(r, "designation"), Text(r, "manufacturer"), r.GetProperty("outputs").GetInt32(), Device(r, Text(r, "name"))));
        }

        return (notifiers, relays);
    }

    private static PoweredDevice Device(JsonElement e, string name) => new(
        name,
        new Current(e.GetProperty("loopCurrent").GetDouble()),
        e.GetProperty("addresses").GetInt32(),
        new Current(e.GetProperty("standbyCurrent").GetDouble()),
        new Current(e.GetProperty("alarmCurrent").GetDouble()),
        Text(e, "source"),
        e.TryGetProperty("certificate", out var c) ? c.GetString() : null);

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;
}
