using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Линия контроллера для журнала: устройства на ней (точки плана) и суммарный ток адресных устройств.
/// <paramref name="RiserMm"/> — дополнительная длина вертикальных участков, мм (задаётся вручную, когда уровни устройств не известны).
/// <paramref name="DeviceLevelsMm"/> — отметка уровня каждого устройства относительно поста, мм: кольцо обходит уровни
/// подряд, на каждом переходе прибавляется разность отметок.
/// </summary>
public sealed record LoopLine(int Number, IReadOnlyList<PlanPoint> Devices, Current Load, bool HasPumpModules, double RiserMm = 0, IReadOnlyList<string>? DeviceIds = null, IReadOnlyList<double>? DeviceLevelsMm = null);

public sealed record CableJournalResult(IReadOnlyList<CableLine> Lines, IReadOnlyList<Issue> Issues);

/// <summary>
/// Кабельный журнал АПС (ADR 0012): длины линий по плану — ДПЛС кольцом от пожарного поста через устройства линии и обратно
/// (ближайший сосед, расстояния по осям, как трассы по потолку), питание РИП — контроллер в пределах поста, сигналы АУПТ —
/// в насосной. Сечение ДПЛС — наименьшее, при котором паспортная длина для суммарного тока линии не меньше расчётной.
/// Марки кабелей и запас длины — из каталога (решение проектировщика), нормы — СП 6.13130.2025, пп. 6.2, 6.17.
/// </summary>
public static class CableJournal
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static CableJournalResult Build(PlanPoint panel, IReadOnlyList<LoopLine> loops, CableCatalog cables, Polygon? pumpRoom, int pumpSignals, string controllerName, string supplyName, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(cables);
        var lines = new List<CableLine>();
        var issues = new List<Issue>();
        var n = 0;
        foreach (var loop in loops)
        {
            var points = loop.Devices.ToList();
            var levels = loop.DeviceLevelsMm is { } dl && dl.Count == loop.Devices.Count ? dl.ToList() : null;
            if (loop.HasPumpModules && pumpRoom is not null)
            {
                points.Add(pumpRoom.Centroid);
                levels?.Add(0);
            }

            var segments = Segments(panel, points, levels);
            var risersMm = segments.Sum(x => x.RiserMm) + loop.RiserMm;
            var lengthM = (segments.Sum(x => x.Mm) + loop.RiserMm) / 1000 * cables.Reserve;
            var loadMa = loop.Load.Amperes * 1000;
            var section = cables.LoopSectionFor(lengthM, loadMa);
            var mark = cables.LoopName.Replace("{section}", (section ?? cables.MaxLoopSection).ToString("0.##", Ru), StringComparison.Ordinal);
            if (section is null)
            {
                issues.Add(new Issue("aps.cable.loopTooLong", IssueLevel.Warning,
                    string.Create(Ru, $"ДПЛС {controllerName} №{loop.Number}: длина {lengthM:0} м при токе {loadMa:0.#} мА больше паспортной для наибольшего сечения {cables.MaxLoopSection:0.##} мм² — разделить линию или добавить контроллер."),
                    Clause: cables.LoopSource));
            }

            lines.Add(new CableLine($"ДПЛС{loop.Number}", $"{controllerName} №{loop.Number}", $"{loop.Devices.Count} адресных устройств{(loop.HasPumpModules ? $", {moduleName} в насосной" : "")}", "адресная линия связи (кольцо)", mark, Math.Round(lengthM, 0),
                string.Create(Ru, $"ток линии {loadMa:0.#} мА; длина кольца по плану{(risersMm > 0 ? $" и стояков {risersMm / 1000:0.#} м" : "")} с запасом {cables.Reserve:0.##}; сечение по табл. длин ДПЛС"),
                Math.Round(lengthM / cables.Reserve, 0)));

            // Сегменты кольца от устройства к устройству (как в таблице прокладки): порядок — тот же обход ближайшего соседа.
            if (loop.DeviceIds is { } ids && ids.Count == loop.Devices.Count && ids.Count > 0)
            {
                var controller = $"{controllerName} №{loop.Number}";
                var k = 0;
                string Name(int index) => index < 0 ? controller : index < ids.Count ? ids[index] : $"{moduleName} (насосная)";
                foreach (var seg in segments)
                {
                    k++;
                    // Ручной стояк (без уровней устройств) — пополам на участки от поста и к посту.
                    var extra = seg.From < 0 || seg.To < 0 ? loop.RiserMm / 2 : 0;
                    var baseM = (seg.Mm + extra) / 1000;
                    lines.Add(new CableLine($"ДПЛС{loop.Number}.{k}", Name(seg.From), Name(seg.To), "", mark, Math.Round(baseM * cables.Reserve, 0),
                        seg.RiserMm > 0 || extra > 0 ? string.Create(Ru, $"со стояком {(seg.RiserMm + extra) / 1000:0.#} м") : "", Math.Round(baseM, 0)));
                }
            }

            n++;
        }

        for (var k = 1; k <= Math.Max(1, loops.Count); k++)
        {
            lines.Add(new CableLine($"П{k}", supplyName, $"{controllerName} №{k}", "питание 12 В", cables.PowerName, cables.InCabinetM, "в пределах пожарного поста, условно"));
        }

        lines.Add(new CableLine("С1", "ВРУ (ввод 1)", supplyName, "питание 220 В, I категория", cables.MainsName, 0, "длина — по месту ВРУ, не определена"));
        lines.Add(new CableLine("С2", "ВРУ (ввод 2)", supplyName, "питание 220 В, резерв", cables.MainsName, 0, "длина — по месту ВРУ, не определена"));
        issues.Add(new Issue("aps.cable.mains", IssueLevel.Info, "Длины линий 220 В от ВРУ к РИП не определены: положение ВРУ в проекте не задано (ТЗ на электроснабжение — А-11)."));

        if (pumpSignals > 0 && pumpRoom is not null)
        {
            var halfPerimeter = pumpRoom.Edges().Sum(e => Math.Abs(e.B.XMm - e.A.XMm) + Math.Abs(e.B.YMm - e.A.YMm)) / 2 / 1000;
            var each = Math.Round(halfPerimeter * cables.Reserve, 0);
            lines.Add(new CableLine("СИГ", $"{moduleName} (насосная)", $"{pumpSignals} сигналов АУПТ (задвижки, СДУ, шкаф)", "сухие контакты", cables.SignalName, each * pumpSignals,
                string.Create(Ru, $"{pumpSignals} линий по {each:0} м — половина периметра насосной с запасом, условно; уточнить по плану обвязки")));
            issues.Add(new Issue("aps.cable.signalsEstimate", IssueLevel.Info, "Длины сигнальных линий АУПТ приняты условно (половина периметра насосной на каждую): положение датчиков на плане обвязки в журнал не передаётся."));
        }

        return new(lines, issues);
    }

    /// <summary>Кольцо: от поста через все точки к ближайшей непосещённой и назад; расстояния по осям (трассы вдоль стен и балок).</summary>
    public static double Tour(PlanPoint start, IReadOnlyList<PlanPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var left = points.ToList();
        var at = start;
        var total = 0.0;
        while (left.Count > 0)
        {
            var next = left.MinBy(p => Manhattan(at, p));
            total += Manhattan(at, next);
            left.Remove(next);
            at = next;
        }

        return total + (points.Count > 0 ? Manhattan(at, start) : 0);
    }

    /// <summary>
    /// Участки обхода: индексы устройств (−1 — пост), длина участка по осям с вертикалью, мм, и вертикальная часть (стояк), мм;
    /// последний — возврат к посту. С уровнями устройств (<paramref name="levelsMm"/>, отметка относительно поста) кольцо
    /// обходит уровни подряд — от ближнего к посту к дальнему, внутри уровня — ближайший сосед; на переходе — разность отметок.
    /// </summary>
    public static IReadOnlyList<(int From, int To, double Mm, double RiserMm)> Segments(PlanPoint start, IReadOnlyList<PlanPoint> points, IReadOnlyList<double>? levelsMm = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        double Level(int i) => levelsMm is not null && i >= 0 && i < levelsMm.Count ? levelsMm[i] : 0;
        var groups = Enumerable.Range(0, points.Count).GroupBy(Level).OrderBy(g => Math.Abs(g.Key)).ThenByDescending(g => g.Key).ToList();
        var at = start;
        var atIndex = -1;
        var atLevel = 0.0;
        var result = new List<(int, int, double, double)>();
        foreach (var group in groups)
        {
            var left = group.ToList();
            while (left.Count > 0)
            {
                var next = left.MinBy(i => Manhattan(at, points[i]));
                var riser = Math.Abs(Level(next) - atLevel);
                result.Add((atIndex, next, Manhattan(at, points[next]) + riser, riser));
                left.Remove(next);
                at = points[next];
                atIndex = next;
                atLevel = Level(next);
            }
        }

        if (points.Count > 0)
        {
            var riser = Math.Abs(atLevel);
            result.Add((atIndex, -1, Manhattan(at, start) + riser, riser));
        }

        return result;
    }

    private static double Manhattan(PlanPoint a, PlanPoint b) => Math.Abs(a.XMm - b.XMm) + Math.Abs(a.YMm - b.YMm);
}
