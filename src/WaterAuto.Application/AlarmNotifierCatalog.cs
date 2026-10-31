using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Релейный блок управления из каталога: выходы, ток линии и питания, адреса.</summary>
public sealed record RelayModel(string Id, string Name, string Designation, string Manufacturer, int Outputs, PoweredDevice Device);

/// <summary>Оповещатель из каталога с паспортом питания; у речевых громкоговорителей линии 100 В — чувствительность и отводы.</summary>
public sealed record NotifierEntry(string Id, string Designation, string Manufacturer, NotifierModel Model, PoweredDevice Device, SpeechSpeaker? Speaker = null);

/// <summary>Усилитель речевого оповещения из каталога.</summary>
public sealed record AmplifierEntry(string Id, SpeechAmplifier Model);

/// <summary>Каталог оповещателей: оповещатели, релейные блоки, усилители речевого оповещения и модули контроля линий.</summary>
public sealed record NotifierCatalog(IReadOnlyList<NotifierEntry> Notifiers, IReadOnlyList<RelayModel> Relays, IReadOnlyList<AmplifierEntry> Amplifiers, IReadOnlyList<LineMonitor> LineMonitors);

/// <summary>Чтение каталога оповещателей и релейных блоков (data/catalog/alarm_notifiers.json); схему проверяет tools/ValidateData.</summary>
public static class AlarmNotifierCatalog
{
    public const string File = "alarm_notifiers.json";

    public static NotifierCatalog Load(string path)
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
            SpeechSpeaker? speaker = null;
            if (n.TryGetProperty("sensitivityDb", out var s) && n.TryGetProperty("taps", out var taps))
            {
                speaker = new SpeechSpeaker(s.GetDouble(), [.. taps.EnumerateArray().Select(t => new SpeakerTap(t.GetProperty("acousticW").GetDouble(), t.GetProperty("consumedW").GetDouble()))],
                    n.TryGetProperty("lineVoltage", out var lv) ? lv.GetDouble() : 100);
            }

            notifiers.Add(new NotifierEntry(Text(n, "id"), Text(n, "designation"), Text(n, "manufacturer"),
                new NotifierModel(Text(n, "name"), kind, n.TryGetProperty("levelAt1m", out var l) ? l.GetDouble() : null, device.Alarm, Text(n, "source"), device.Certificate), device, speaker));
        }

        var relays = new List<RelayModel>();
        foreach (var r in root.GetProperty("relays").EnumerateArray())
        {
            relays.Add(new RelayModel(Text(r, "id"), Text(r, "name"), Text(r, "designation"), Text(r, "manufacturer"), r.GetProperty("outputs").GetInt32(), Device(r, Text(r, "name"))));
        }

        var amplifiers = new List<AmplifierEntry>();
        if (root.TryGetProperty("amplifiers", out var amps))
        {
            foreach (var a in amps.EnumerateArray())
            {
                amplifiers.Add(new AmplifierEntry(Text(a, "id"), new SpeechAmplifier(Text(a, "name"), Text(a, "designation"), Text(a, "manufacturer"),
                    a.GetProperty("maxConsumedW").GetDouble(), a.GetProperty("maxAcousticW").GetDouble(), a.GetProperty("maxBranches").GetInt32(), a.GetProperty("maxSegmentM").GetDouble(), a.GetProperty("lineVoltage").GetDouble(),
                    Text(a, "batteryName"), a.GetProperty("batteries").GetInt32(), a.GetProperty("backupStandbyH").GetDouble(), a.GetProperty("backupAlarmH").GetDouble(), a.GetProperty("mainsW").GetDouble(),
                    Text(a, "source"), a.TryGetProperty("certificate", out var c) ? c.GetString() : null)));
            }
        }

        var monitors = new List<LineMonitor>();
        if (root.TryGetProperty("lineMonitors", out var mons))
        {
            foreach (var m in mons.EnumerateArray())
            {
                monitors.Add(new LineMonitor(Text(m, "name"), Text(m, "designation"), Text(m, "manufacturer"), new Current(m.GetProperty("lineCurrent").GetDouble()), Text(m, "source"), m.TryGetProperty("certificate", out var c) ? c.GetString() : null));
            }
        }

        return new NotifierCatalog(notifiers, relays, amplifiers, monitors);
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
