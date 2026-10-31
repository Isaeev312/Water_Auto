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
    public const string PumpStationFile = "pump_station.json";

    /// <summary>
    /// Роли строк шаблона насосной, которые входят в комплектную установку: при её подборе строки заменяются одной
    /// строкой установки, а сигналы этих позиций не выдаются.
    /// </summary>
    public static readonly IReadOnlySet<string> PumpSetRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "pump", "cabinet", "valveS", "valveD", "checkD", "insertS", "insertD", "vacuum", "gauge", "cock", "teeS", "teeD", "reducerS", "reducerD", "capS", "capD",
    };

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

        return Merge(items);
    }

    /// <summary>
    /// Комплект одного места с подстановками в наименование (<paramref name="tokens"/>: «q» → «{q}») и множителями количества
    /// по полю count шаблона (<paramref name="counts"/>: «pumps», «truckInlets»).
    /// </summary>
    /// <remarks>Раздел строки — поле group шаблона (equipment, valves, fittings — разделы насосной), без него — <paramref name="group"/>.</remarks>
    public static IReadOnlyList<TakeoffItem> Items(string path, TakeoffGroup group, IReadOnlyDictionary<string, string> tokens, IReadOnlyDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(counts);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = new List<TakeoffItem>();
        foreach (var t in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var name = tokens.Aggregate(t.GetProperty("name").GetString()!, (s, kv) => s.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal));
            var times = t.TryGetProperty("count", out var c) ? counts.GetValueOrDefault(c.GetString()!) : 1;
            if (times == 0)
            {
                continue;
            }

            var section = t.TryGetProperty("group", out var g) ? g.GetString() switch
            {
                "valves" => TakeoffGroup.PumpStationValves,
                "fittings" => TakeoffGroup.PumpStationFittings,
                _ => group,
            } : group;
            items.Add(new TakeoffItem(
                section,
                name,
                t.TryGetProperty("designation", out var d) && d.GetString() is { Length: > 0 } designation ? designation : null,
                t.GetProperty("per").GetDouble() * times,
                t.GetProperty("unit").GetString()!,
                t.TryGetProperty("note", out var n) ? n.GetString() : null,
                Role: t.TryGetProperty("role", out var r) ? r.GetString() : null));
        }

        return Merge(items);
    }

    /// <summary>
    /// Сигналы в АПС по полю signals шаблона (ADR 0012): по одному источнику на сигнал позиции, количество — как у строки
    /// спецификации (per × множитель count). Подстановки и множители — как в <see cref="Items(string, TakeoffGroup, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, int})"/>.
    /// Позиции с ролью из <paramref name="excludedRoles"/> пропускаются — так сигналы совпадают со спецификацией,
    /// когда строки заменены комплектной установкой (<see cref="PumpSetRoles"/>).
    /// </summary>
    public static IReadOnlyList<SignalSource> Signals(string path, IReadOnlyDictionary<string, string> tokens, IReadOnlyDictionary<string, int> counts, IReadOnlySet<string>? excludedRoles = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(counts);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var signals = new List<SignalSource>();
        foreach (var t in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var role = t.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (!t.TryGetProperty("signals", out var list) || (role is not null && excludedRoles?.Contains(role) == true))
            {
                continue;
            }

            var times = t.TryGetProperty("count", out var c) ? counts.GetValueOrDefault(c.GetString()!) : 1;
            if (times == 0)
            {
                continue;
            }

            var name = tokens.Aggregate(t.GetProperty("name").GetString()!, (s, kv) => s.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal));
            var count = (int)Math.Round(t.GetProperty("per").GetDouble() * times);
            var mark = t.TryGetProperty("mark", out var m) ? m.GetString() : null;
            var perSection = c.ValueKind == JsonValueKind.String && c.GetString() == "sections" ? (int)Math.Round(t.GetProperty("per").GetDouble()) : 0;
            foreach (var s in list.EnumerateArray())
            {
                signals.Add(new SignalSource(
                    role,
                    name,
                    s.GetProperty("kind").GetString() switch
                    {
                        "valvePosition" => SignalKind.ValvePosition,
                        "pressureSwitch" => SignalKind.PressureSwitch,
                        "pumpStart" => SignalKind.PumpStart,
                        "pumpFault" => SignalKind.PumpFault,
                        "automaticsOff" => SignalKind.AutomaticsOff,
                        var kind => throw new InvalidDataException($"{path}: неизвестный вид сигнала «{kind}»."),
                    },
                    count,
                    s.TryGetProperty("clause", out var cl) ? cl.GetString() : null,
                    mark,
                    perSection));
            }
        }

        return signals;
    }

    /// <summary>Сигналы комплекта на каждое место установки (<paramref name="places"/> — DN мест), сложенные по DN.</summary>
    /// <remarks>
    /// Сквозной расчёт узлы управления берёт из шаблона насосной (роли cu, cuValve); control_unit.json вместе с ним
    /// не читается, иначе сигналы узлов посчитались бы дважды.
    /// </remarks>
    public static IReadOnlyList<SignalSource> Signals(string path, IEnumerable<int> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return places
            .GroupBy(dn => dn)
            .OrderBy(g => g.Key)
            .SelectMany(g => Signals(path, new Dictionary<string, string> { ["dn"] = g.Key.ToString(CultureInfo.InvariantCulture) }, new Dictionary<string, int>())
                .Select(s => s with { Count = s.Count * g.Count() }))
            .ToList();
    }

    private static List<TakeoffItem> Merge(List<TakeoffItem> items)
    {
        // Одинаковые строки (шаблон без {dn}) складываются.
        return items
            .GroupBy(i => (i.Name, i.Designation, i.Unit, i.Note))
            .Select(g => g.First() with { Quantity = g.Sum(i => i.Quantity) })
            .ToList();
    }
}
