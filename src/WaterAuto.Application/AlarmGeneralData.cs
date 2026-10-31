using System.Globalization;
using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Лист «Общие данные» тома АПС (ГОСТ Р 21.101): ведомость чертежей тома, ссылочные и прилагаемые документы, основные
/// показатели системы, общие указания — по составу как у тома АУПТ (<see cref="GeneralDataComposer"/>), числа — из расчёта.
/// </summary>
public static class AlarmGeneralData
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static GeneralData Build(string objectName, IReadOnlyList<ProjectSheet> sheets, IReadOnlyList<DetectorZone> zones, DetectorLayout layout, AlarmZoningResult? zoning, PowerBudget? budget, AlarmPowerSet? set, IReadOnlyList<ControlPoint> points, string codePrefix)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(points);
        var area = zones.Sum(z => z.Outline.AreaMm2) / 1e6;
        var models = zones.Where(z => z.ModelName is not null).Select(z => z.ModelName!).Distinct().ToList();
        var controllers = budget?.Lines.FirstOrDefault(l => set is not null && l.Name == set.Controller.Name)?.Count ?? 0;

        List<ReferenceRow> references =
        [
            new("СП 484.1311500.2020", "Системы пожарной сигнализации и автоматизация систем противопожарной защиты. Нормы и правила проектирования (с Изм. № 1)", ""),
            new("СП 6.13130.2025", "Система обеспечения пожарной безопасности объекта защиты. Электроустановки низковольтные. Требования пожарной безопасности", ""),
            new("СП 485.1311500.2020", "Установки пожаротушения автоматические. Нормы и правила проектирования (сигналы узлов управления, п. 6.8.7)", ""),
            new("ГОСТ Р 53325-2012", "Техника пожарная. Технические средства пожарной автоматики. Общие технические требования и методы испытаний", "сигналы прибора управления насосами"),
            new("ГОСТ Р 21.101-2020", "СПДС. Основные требования к проектной и рабочей документации", ""),
            new("РЭ НВП «Болид»", $"Руководства по эксплуатации приборов и извещателей ({string.Join(", ", new[] { set?.Controller.Name, set?.Module.Name, set?.Supply.Name, set?.Battery.Name }.Concat(models).Where(n => n is not null).Distinct())})", "паспортные данные каталога"),
        ];

        var indicators = new List<IndicatorRow>
        {
            new("Защищаемая площадь", "м²", area.ToString("0", Ru)),
            new("Извещатели пожарные автоматические", "шт.", layout.Detectors.Count.ToString(Ru)),
            new("Извещатели пожарные ручные", "шт.", (zoning?.Manuals.Count ?? 0).ToString(Ru)),
            new("Зон контроля пожарной сигнализации", "шт.", (zoning?.ZoneOf.Values.Distinct().Count() ?? 0).ToString(Ru)),
            new("Изоляторов короткого замыкания", "шт.", (zoning?.Isolators ?? 0).ToString(Ru)),
            new("Сигналов от установки пожаротушения", "шт.", points.Count.ToString(Ru)),
            new("Контроллеров адресной линии", "шт.", controllers.ToString(Ru)),
        };
        if (budget is not null)
        {
            indicators.Add(new("Ток потребления в режиме «Пожар»", "мА", (budget.Alarm.Amperes * 1000).ToString("0.#", Ru)));
            indicators.Add(new("Требуемая ёмкость АКБ", "А·ч", budget.Required?.AmpereHours.ToString("0.##", Ru) ?? "не определена"));
            indicators.Add(new("Принятая АКБ", "", $"{budget.Battery.Name}, {budget.Battery.Nominal.AmpereHours.ToString("0.##", Ru)} А·ч"));
        }

        var algorithms = string.Join(", ", zones.Select(z => z.Algorithm.ToString()).Distinct());
        List<string> instructions =
        [
            "Рабочие чертежи системы пожарной сигнализации разработаны по заданию на проектирование и архитектурно-строительным чертежам в соответствии с СП 484.1311500.2020 с Изм. № 1 и СП 6.13130.2025. Объём защиты (помещения, тип извещателей, алгоритм) задан проектировщиком.",
            $"Система адресная; извещатели — {(models.Count > 0 ? string.Join(", ", models) : "по спецификации")}; алгоритм принятия решения о пожаре — {algorithms}; каждая точка защищаемого помещения контролируется {(zones.Any(z => z.Algorithm == DetectionAlgorithm.C || !z.Addressable) ? "не менее чем двумя ИП (п. 6.6.2)" : "одним адресным ИП (п. 6.6.1)")}.",
            "Ручные извещатели установлены на стенах на высоте 1,5 м от пола; при уточнении путей эвакуации — у выходов, не далее 45 м друг от друга и 30 м от выхода (п. 6.6.27).",
            "Единичная неисправность линии не должна отключать более 24 автоматических ИП и одного ручного (п. 6.3.5): изоляторы короткого замыкания — по спецификации, места установки — по схеме соединений.",
            "Сигналы от установки пожаротушения (положение задвижек и затворов, сигнализаторы давления, шкаф насосов) принимаются адресными модулями в насосной; состав — по перечню сигналов и таблице адресов.",
            "Электропитание — I категория надёжности (СП 6, п. 5.21): от двух вводов с автоматическим переключением и от резервированного источника с АКБ на 24 ч дежурного режима и 1 ч «Пожар» (прил. Б СП 6).",
            "Электропроводки и линии связи — кабелями с медными жилами, не распространяющими горение, огнестойкими по области применения прил. Г СП 6 (п. 6.2); время работоспособности — п. 6.17.",
            "Применяемые технические средства должны иметь сертификаты соответствия, действующие на территории Кыргызской Республики (ТР ЕАЭС 043/2017).",
            "Размеры даны в миллиметрах, отметки — в метрах.",
        ];

        List<ReferenceRow> attached =
        [
            new($"{codePrefix}-АПС.ПЗ", "Пояснительная записка к расчёту", ""),
            new($"{codePrefix}-АПС.СО", "Спецификация оборудования (aps_spec.xlsx)", ""),
            new($"{codePrefix}-АПС.ТА", "Таблица адресов (aps_addresses.xlsx)", ""),
            new($"{codePrefix}-АПС.КЖ", "Кабельный журнал (aps_cables.xlsx)", ""),
            new($"{codePrefix}-АПС.ТЗ", "Техническое задание на взаимодействие с АУПТ", ""),
            new($"{codePrefix}-АПС.АКБ", "Расчёт ёмкости аккумуляторной батареи (aps_battery.xlsx)", ""),
        ];

        return new GeneralData(
            $"{objectName}. Система пожарной сигнализации. Общие данные",
            sheets.Select(s => new SheetRow(s.Sheet, s.Name, s.Note)).ToList(),
            references,
            indicators,
            instructions,
            attached);
    }
}
