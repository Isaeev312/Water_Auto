using System.Globalization;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Техническое задание смежному разделу ОВ на автоматизацию вентиляции при пожаре (АППЗ): команды СПС, их триггеры и
/// задержки, число выходов. Собирается из <see cref="Alarm.HvacCommands"/>, ручного ввода нет.
/// </summary>
public static class HvacSpec
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static NoteDocument Build(NoteTitle title, HvacInputs inputs, IReadOnlyList<HvacCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(commands);
        var sections = new List<NoteSection>
        {
            new("1", "Общие положения",
            [
                new NoteParagraph("Задание выдаётся разработчику разделов «Отопление, вентиляция» и «Электроснабжение» для увязки систем вентиляции с системой пожарной сигнализации (СПС). Команды формирует прибор управления СПС при переходе в режим «Пожар» (СП 484.1311500.2020, разд. 7); требования к вентиляции при пожаре — СП 7.13130."),
                new NoteParagraph(string.Create(Ru, $"Исходные данные раздела ОВ: общеобменные системы — {(inputs.GeneralVentilation.Count > 0 ? string.Join(", ", inputs.GeneralVentilation) : "не заданы")}; дымовые зоны — {(inputs.SmokeZones.Count > 0 ? string.Join("; ", inputs.SmokeZones.Select(z => $"{z.Name}: вытяжная {z.ExhaustSystem}{(z.SupplySystem is null ? "" : $", подпор {z.SupplySystem}")}, клапанов дымоудаления {z.SmokeDampers}")) : "не заданы")}; противопожарных нормально открытых клапанов — {inputs.NormallyOpenDampers}.")),
            ]),
            new("2", "Команды системы пожарной сигнализации",
            [
                new NoteParagraph("Предусмотреть приём следующих команд (сухой контакт реле прибора управления, по одному выходу на команду) и выполнение их исполнительными устройствами систем вентиляции:"),
                new NoteTable("Команды СПС в системы вентиляции", ["Обозн.", "Команда", "Дымовая зона", "Системы / клапаны", "Кол.", "Сигнал запуска", "Задержка", "Основание"],
                    [.. commands.Select(c => (IReadOnlyList<string>)[c.Id, KindText(c.Kind), c.Zone ?? "весь объект", c.Targets.Count > 0 ? string.Join(", ", c.Targets) : "—", c.Count.ToString(Ru), c.Trigger, c.DelaySeconds is { } d ? string.Create(Ru, $"{d:0} с") : "—", c.Clause])]),
                new NoteParagraph("Исполнительные механизмы противопожарных клапанов сохраняют положение заслонки при отключении питания привода (СП 7.13130, п. 7.19). Электроприёмники противодымной вентиляции — I категория надёжности, устройства автоматического отключения в цепях их питания не допускаются (п. 7.22)."),
            ]),
            new("3", "Что должен определить раздел ОВ",
            [
                new NoteList(
                [
                    "возможность частичного отключения общеобменной вентиляции и закрытия клапанов по зонам; если не определена — отключение по всему объекту при «Пожаре» из любой зоны (СП 484, п. 7.7.4);",
                    "алгоритм совместной работы вытяжной и приточной противодымной вентиляции и сочетание одновременно работающих систем (СП 7, п. 7.20);",
                    "места установки устройств дистанционного пуска у эвакуационных выходов или в пожарных шкафах (СП 7, п. 7.20; СП 484, п. 7.7.5);",
                    "сигналы обратной связи в СПС: положение клапанов, состояние вентиляторов (для контроля исполнения команд).",
                ]),
                new NotePending("Положение шкафов управления вентиляторами и клапанов на плане — указать разделу ОВ; без них линии управления в кабельный журнал не включены."),
            ]),
        };
        return new NoteDocument(title with { Title = "Техническое задание на автоматизацию систем вентиляции при пожаре (АППЗ)" }, sections, []);
    }

    public static string KindText(HvacCommandKind kind) => kind switch
    {
        HvacCommandKind.StopGeneralVentilation => "Отключить общеобменную вентиляцию",
        HvacCommandKind.CloseNormallyOpenDampers => "Закрыть противопожарные НО клапаны",
        HvacCommandKind.OpenSmokeDampers => "Открыть клапаны дымоудаления",
        HvacCommandKind.StartSmokeExhaust => "Пуск вытяжной противодымной вентиляции",
        HvacCommandKind.StartSmokeSupply => "Пуск приточной противодымной вентиляции (подпор)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
