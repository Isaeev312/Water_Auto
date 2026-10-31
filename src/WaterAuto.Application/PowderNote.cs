using System.Globalization;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Пояснительная записка установки порошкового пожаротушения модульного типа: исходные данные помещений, способ тушения,
/// расчёт количества модулей по прил. И СП 485 с подстановкой чисел, запас, требования к пуску и безопасности (разд. 10).
/// </summary>
public static class PowderNote
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static NoteDocument Build(NoteTitle title, IReadOnlyList<PowderRoom> rooms, PowderModuleEntry module, PowderResult result, IReadOnlyList<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(issues);
        var m = module.Model;
        var sections = new List<NoteSection>
        {
            new("1", "Исходные данные",
            [
                new NoteParagraph("Установка порошкового пожаротушения модульного типа (АУПП) проектируется по СП 485.1311500.2020, разд. 10 и прил. И. Защищаемые помещения, способ тушения, класс пожара и горючее вещество — решение проектировщика; применение АУПП в помещениях с постоянным пребыванием людей не допускается (п. 10.1.3)."),
                new NoteTable("Защищаемые помещения", ["Помещение", "Площадь, м²", "Объём, м³", "Высота установки, м", "Способ", "Класс пожара", "Доля проёмов f"],
                    [.. rooms.Select(r => (IReadOnlyList<string>)[r.Name, r.AreaM2.ToString("0.#", Ru), r.VolumeM3.ToString("0", Ru), r.MountHeightM.ToString("0.##", Ru), MethodText(r.Method), r.FireClass.ToString(), r.OpenFraction.ToString("0.###", Ru)])]),
                new NoteParagraph(string.Create(Ru, $"Модуль — {m.Name} ({module.Designation}), {module.Manufacturer}; порошок {(m.Powder == PowderKind.B ? "для класса В" : "для классов А, В")}{(m.Impulse ? ", импульсный" : "")}; высота установки {m.MinHeightM:0.##}–{m.MaxHeightM:0.##} м{(module.FireRank is { } fr ? $"; ранг модельного очага {fr}" : "")}. Источник: {m.Source}.")),
            ]),
            new("2", "Расчёт количества модулей",
            [
                new NoteFormula("*N* = *S*_{у}/*S*_{н}·*k*_{1}·*k*_{2}·*k*_{3}·*k*_{4} (по площади),   *N* = *V*_{п}/*V*_{н}·*k*_{1}·*k*_{2}·*k*_{3}·*k*_{4} (по объёму),", "2.1",
                [
                    "где *S*_{н}, *V*_{н} — площадь и объём, защищаемые одним модулем, по документации на модуль на высоте установки (формулы И.3, И.1);",
                    "*k*_{1} — неравномерность распыла 1…1,2 (1,2 — на предельной высоте); *k*_{2} — запас на затенения по документации изготовителя;",
                    "*k*_{3} — эффективность порошка по горючему веществу (табл. И.1); *k*_{4} — по площади 1,2, локально 1,3, по объёму 1 + 10·*f* (И.2);",
                    "локально расчётная площадь увеличивается на 10 %, объём — на 15 % (п. 10.2.7); дробное *N* округляется вверх.",
                ]),
                new NoteTable("Результат", ["Помещение", "Расчётная величина", "На модуль", "k1", "k2", "k3", "k4", "N, шт."],
                    [.. result.Rooms.Select(r => (IReadOnlyList<string>)[rooms.First(x => x.Id == r.RoomId).Name, r.Protected.ToString("0.##", Ru), r.PerModule.ToString("0.##", Ru), r.K1.ToString("0.##", Ru), r.K2.ToString("0.##", Ru), r.K3.ToString("0.##", Ru), r.K4.ToString("0.###", Ru), r.Modules.ToString(Ru)])]),
                .. result.Rooms.Select(r => (NoteBlock)new NoteParagraph($"{rooms.First(x => x.Id == r.RoomId).Name}: {r.Formula} ({r.Clause}).")),
                new NoteParagraph(string.Create(Ru, $"Всего модулей — {result.Rooms.Sum(r => r.Modules)} шт.; запас — {result.Reserve} шт. (100 % модулей наибольшего помещения, п. 10.2.16).")),
            ]),
            new("3", "Пуск и безопасность",
            [
                new NoteList(
                [
                    "все модули одного помещения срабатывают в интервале не более 3 с при автоматическом или дистанционном пуске (п. 10.2.4);",
                    "задержка выпуска порошка на эвакуацию людей, отключение общеобменной вентиляции и кондиционирования, закрытие противопожарных клапанов — до пуска (п. 10.4.3); команды — от прибора управления АПС;",
                    "в помещениях и у входов — световые табло «Порошок — уходи!» и «Порошок — не входи!», звуковая сигнализация (п. 10.3.1);",
                    "устройства ручного, дистанционного и местного пуска опломбированы, кроме устройств на пожарном посту (п. 10.4.2);",
                    "крепления модулей — на пятикратный вес (п. 10.2.14); вход после подачи порошка — только в изолирующих СИЗОД (п. 10.4.6).",
                ]),
                new NotePending(module.StartCurrent is { } sc
                    ? string.Create(Ru, $"Цепи пуска: ток пуска {sc:0.##} А{(module.StartDuration is { } sd ? $", импульс {sd:0.##} с" : "")} на модуль — выбор приборов пуска и расчёт их питания выполняются в разделе АПС (не автоматизировано).")
                    : "Цепи пуска: ток пуска модуля в каталоге не указан — приборы пуска и их питание подобрать по паспорту модуля в разделе АПС."),
            ]),
        };
        var problems = issues.Where(i => i.Code.StartsWith("powder.", StringComparison.Ordinal) && i.Level != IssueLevel.Info).ToList();
        if (problems.Count > 0)
        {
            sections.Add(new("4", "Замечания расчёта", [.. problems.Select(i => (NoteBlock)new NotePending($"{i.Level}: {i.Message}"))]));
        }

        return new NoteDocument(title with { Title = "Установка порошкового пожаротушения модульного типа. Пояснительная записка" }, sections, []);
    }

    public static string MethodText(PowderMethod method) => method switch
    {
        PowderMethod.Area => "по всей площади",
        PowderMethod.LocalArea => "локально по площади",
        PowderMethod.Volume => "по всему объёму",
        PowderMethod.LocalVolume => "локально по объёму",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };
}
