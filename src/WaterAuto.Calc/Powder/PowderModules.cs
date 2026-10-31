using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Powder;

/// <summary>
/// Количество модулей порошкового пожаротушения по прил. И СП 485.1311500.2020: по площади N = Sу/Sн·k1·k2·k3·k4 (И.3),
/// по объёму N = Vп/Vн·k1·k2·k3·k4 (И.1), k4 по объёму = 1 + 10·f (И.2), по площади и локально — числа свода (И.3.1.2,
/// И.3.2.1, И.3.2.2). Sн, Vн, k2 — только из документации на модуль; k1 — из паспорта, по заданию или 1,2 на предельной
/// высоте; k3 — табл. И.1 или задание. Чего нет ни в паспорте, ни в своде — ошибка без подстановки (А-17).
/// </summary>
public static class PowderModules
{
    private const string Doc = "СП 485.1311500.2020";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static PowderResult Calculate(IReadOnlyList<PowderRoom> rooms, PowderModuleModel module, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var formulas = norms.Find("sp485.pI_formulas.modules");
        var k1Rule = norms.Find("sp485.pI_3_1_1.k1");
        var k4Rule = norms.Find("sp485.pI_3_k4.byMethod");
        var local = norms.Find("sp485.p10_2_7.localZone");
        var large = norms.Find("sp485.p10_2_9.largeRoom");
        var leak = norms.Find("sp485.p10_2_8.volumeLeakage");
        var reserveRule = norms.Find("sp485.p10_2_16.reserve");
        if (formulas is null || k1Rule is null || k4Rule is null || local is null || large is null || leak is null || reserveRule is null)
        {
            return new([], 0, [new Issue("powder.noNorm", IssueLevel.Error, "В данных норм нет записей разд. 10 / прил. И СП 485 (data/norms/sp485/clauses_10_powder.json).")]);
        }

        var results = new List<PowderRoomResult>();
        if (module.K2 is not { } k2)
        {
            issues.Add(new Issue("powder.k2.missing", IssueLevel.Error,
                $"У модуля {module.Name} в документации нет коэффициента k2 (запас на затенения) — по своду его определяет изготовитель по огневым испытаниям; расчёт количества модулей не выполнен (А-17).",
                Clause: Doc + ", прил. И, п. И.3.1.1"));
            return new([], 0, issues);
        }

        foreach (var room in rooms)
        {
            var byVolume = room.Method is PowderMethod.Volume or PowderMethod.LocalVolume;
            if (byVolume && room.VolumeM3 > large.Values["volumeLimit"]!.Value + 1e-9)
            {
                issues.Add(new Issue("powder.volume.tooLarge", IssueLevel.Error,
                    string.Create(Ru, $"«{room.Name}»: объём {room.VolumeM3:0} м³ больше {large.Values["volumeLimit"]:0} м³ — тушение по объёму только по СТО, согласованному с МЧС; принять тушение по площади."),
                    room.Id, Doc + ", п. 10.2.9"));
                continue;
            }

            if (room.MountHeightM < module.MinHeightM - 1e-9 || room.MountHeightM > module.MaxHeightM + 1e-9)
            {
                issues.Add(new Issue("powder.height", IssueLevel.Error,
                    string.Create(Ru, $"«{room.Name}»: высота установки {room.MountHeightM:0.##} м вне диапазона {module.Name} {module.MinHeightM:0.##}–{module.MaxHeightM:0.##} м по документации — ярусы или другой модуль (10.2.18)."),
                    room.Id, Doc + ", п. 10.2.18"));
                continue;
            }

            // Sн/Vн — по ближайшей строке диаграммы распыла не ниже высоты установки (высота между строками — следующая строка).
            var above = module.Coverage.Where(c => c.HeightM >= room.MountHeightM - 1e-9).ToList();
            var rows = above.Count == 0 ? [] : above.Where(c => Math.Abs(c.HeightM - above.Min(x => x.HeightM)) < 1e-9).ToList();
            double? Pick(Func<PowderCoverage, double?> f) => rows.Select(f).Where(v => v is > 0).Select(v => v!.Value).DefaultIfEmpty().Min() is var m && m > 0 ? m : null;
            var perModule = byVolume ? Pick(c => c.Volume) : Pick(c => room.FireClass == 'A' ? c.AreaA : c.AreaB);
            if (perModule is not { } per)
            {
                issues.Add(new Issue("powder.coverage.missing", IssueLevel.Error,
                    string.Create(Ru, $"«{room.Name}»: в документации {module.Name} нет {(byVolume ? "защищаемого объёма" : $"защищаемой площади по классу {room.FireClass}")} для высоты {room.MountHeightM:0.##} м."),
                    room.Id, Doc + ", прил. И"));
                continue;
            }

            // k1: паспорт → задание → 1,2 на предельной высоте; иначе правила выбора нет.
            double k1;
            if (module.K1 is { } mk1) { k1 = mk1; }
            else if (room.K1 is { } rk1) { k1 = rk1; }
            else if (Math.Abs(room.MountHeightM - module.MaxHeightM) < 1e-9) { k1 = k1Rule.Values["atMaxHeight"]!.Value; }
            else
            {
                issues.Add(new Issue("powder.k1.missing", IssueLevel.Error,
                    string.Create(Ru, $"«{room.Name}»: k1 (неравномерность распыла, {k1Rule.Values["min"]:0.0}…{k1Rule.Values["max"]:0.0}) не задан ни в документации модуля, ни в проекте, а высота ниже предельной — задайте k1 (А-17)."),
                    room.Id, Doc + ", прил. И, п. И.3.1.1"));
                continue;
            }

            if (k1 < k1Rule.Values["min"]!.Value - 1e-9 || k1 > k1Rule.Values["max"]!.Value + 1e-9)
            {
                issues.Add(new Issue("powder.k1.range", IssueLevel.Warning, string.Create(Ru, $"«{room.Name}»: k1 = {k1:0.##} вне диапазона {k1Rule.Values["min"]:0.0}…{k1Rule.Values["max"]:0.0} по И.3.1.1."), room.Id, Doc + ", прил. И, п. И.3.1.1"));
            }

            // k3: задание → табл. И.1 по виду порошка; значение в скобках — для ручного пуска и импульсных модулей.
            double? k3 = room.K3;
            if (k3 is null && room.SubstanceRow is { } rowId && norms.Find(rowId) is { } t)
            {
                var bracket = (room.ManualOnly || module.Impulse) && t.Values.GetValueOrDefault("abPowderManual") is not null;
                k3 = module.Powder == PowderKind.B ? t.Values.GetValueOrDefault("bPowder") : bracket ? t.Values["abPowderManual"] : t.Values.GetValueOrDefault("abPowder");
                if (k3 is null)
                {
                    issues.Add(new Issue("powder.k3.notApplicable", IssueLevel.Error,
                        $"«{room.Name}»: порошок {module.Name} ({(module.Powder == PowderKind.B ? "класса В" : "классов А, В")}) по табл. И.1 не применяется для «{t.Attributes.GetValueOrDefault("substance")}».", room.Id, Doc + ", прил. И, табл. И.1"));
                    continue;
                }
            }

            if (k3 is not { } k3v)
            {
                issues.Add(new Issue("powder.k3.missing", IssueLevel.Error,
                    $"«{room.Name}»: горючее вещество не из табл. И.1 и k3 не задан — по своду определяется экспериментально (А-17).", room.Id, Doc + ", прил. И, табл. И.1"));
                continue;
            }

            // k4: паспорт → по способу (по объёму 1 + 10·f, по площади и локально — числа свода).
            double k4;
            var coefficient = formulas.Values["k4Coefficient"]!.Value;
            if (module.K4 is { } mk4) { k4 = mk4; }
            else
            {
                k4 = room.Method switch
                {
                    PowderMethod.Volume => 1 + (coefficient * room.OpenFraction),
                    PowderMethod.Area => k4Rule.Values["areaWhole"]!.Value,
                    PowderMethod.LocalArea => k4Rule.Values["localArea"]!.Value,
                    _ => k4Rule.Values["localVolume"]!.Value,
                };
            }

            if (room.Method == PowderMethod.Volume && room.OpenFraction * 100 > leak.Values["maxLeakage"]!.Value + 1e-9)
            {
                issues.Add(new Issue("powder.leakage", IssueLevel.Warning,
                    string.Create(Ru, $"«{room.Name}»: доля открытых проёмов {room.OpenFraction * 100:0.##} % больше {leak.Values["maxLeakage"]:0.#} % — тушение по объёму дополнительно по СТО (10.2.8); степень негерметичности принята равной f (И.2)."),
                    room.Id, Doc + ", п. 10.2.8"));
            }

            var protectedValue = room.Method switch
            {
                PowderMethod.LocalArea => room.AreaM2 * (1 + (local.Values["areaIncrease"]!.Value / 100)),
                PowderMethod.LocalVolume => room.VolumeM3 * (1 + (local.Values["volumeIncrease"]!.Value / 100)),
                PowderMethod.Volume => room.VolumeM3,
                _ => room.AreaM2,
            };
            var exact = protectedValue / per * k1 * k2 * k3v * k4;
            var n = (int)Math.Ceiling(exact - 1e-9);
            var sym = byVolume ? "V" : "S";
            var formula = string.Create(Ru, $"N = {sym}/{sym}н·k1·k2·k3·k4 = {protectedValue:0.##}/{per:0.##}·{k1:0.##}·{k2:0.##}·{k3v:0.##}·{k4:0.###} = {exact:0.##} → {n}");
            results.Add(new PowderRoomResult(room.Id, n, protectedValue, per, k1, k2, k3v, k4, formula, Doc + (byVolume ? ", прил. И, формула И.1" : ", прил. И, формула И.3")));
        }

        // Запас 100 % модулей наибольшего помещения (10.2.16).
        var reserve = results.Count == 0 ? 0 : (int)Math.Ceiling(results.Max(r => r.Modules) * reserveRule.Values["reservePercent"]!.Value / 100 - 1e-9);
        if (reserve > 0)
        {
            issues.Add(new Issue("powder.reserve", IssueLevel.Info, $"Запас модулей {module.Name} — {reserve} шт. (100 % наибольшего помещения).", Clause: Doc + ", п. 10.2.16"));
        }

        return new(results, reserve, issues);
    }
}
