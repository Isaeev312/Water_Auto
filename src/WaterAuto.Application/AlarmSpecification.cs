using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Состав оборудования АПС для спецификации: приборы из расчёта питания (контроллеры, модули, РИП, АКБ), извещатели по
/// выбранным моделям из раскладки, ИПР и изоляторы из состава СПС. Ничего не считает заново — только собирает количества.
/// </summary>
public static class AlarmSpecification
{
    public static IReadOnlyList<AlarmSpecItem> Build(PowerBudget? budget, AlarmPowerSet? set, IReadOnlyList<DetectorZone> zones, DetectorLayout layout, IReadOnlyDictionary<string, DetectorModel> models, DetectorModel? manualModel, int manuals, int isolators,
        IReadOnlyList<(NotifierEntry Entry, int Count)>? notifiers = null, RelayModel? relay = null, int relays = 0, SpeechPlan? speech = null, SpeechAmplifier? amplifier = null, LineMonitor? lineMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(models);
        var items = new List<AlarmSpecItem>();
        if (budget is not null && set is not null)
        {
            var controllers = budget.Lines.FirstOrDefault(l => l.Name == set.Controller.Name)?.Count ?? 1;
            var modules = budget.Lines.FirstOrDefault(l => l.Name == set.Module.Name)?.Count ?? 0;
            items.Add(new("Контроллер двухпроводной линии связи", set.Controller.Name, Maker(set.Controller.Source), "шт.", controllers, set.Controller.Certificate, "адресная линия ИП и модулей; питание 12 В от РИП"));
            if (modules > 0)
            {
                items.Add(new("Адресный расширитель (модуль контроля входов) для сигналов АУПТ", set.Module.Name, Maker(set.Module.Source), "шт.", modules, set.Module.Certificate, $"{set.Module.Inputs} входов; питание от линии"));
            }
        }

        foreach (var g in zones.Where(z => z.ModelId is not null && models.ContainsKey(z.ModelId)).GroupBy(z => z.ModelId!))
        {
            var m = models[g.Key];
            var count = g.Sum(z => layout.CountByRoom.GetValueOrDefault(z.RoomId));
            if (count > 0)
            {
                items.Add(new($"Извещатель пожарный {(m.Kind == DetectorKind.Smoke ? "дымовой" : "тепловой")} адресный {m.Designation}", m.Name, m.Manufacturer, "шт.", count, m.Certificate,
                    m.Kind == DetectorKind.Heat && m.HeatClass is not null ? $"класс {m.HeatClass}" + (m.ResponseTemperature is { } t ? $", {t:0} °С" : "") : "с базой"));
            }
        }

        if (manuals > 0 && manualModel is not null)
        {
            items.Add(new($"Извещатель пожарный ручной адресный {manualModel.Designation}", manualModel.Name, manualModel.Manufacturer, "шт.", manuals, manualModel.Certificate, manualModel.Isolator ? "со встроенным изолятором КЗ" : null));
        }

        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        foreach (var (entry, count) in notifiers ?? [])
        {
            if (count > 0)
            {
                var kindText = entry.Model.Kind == NotifierKind.Light ? "световой" : entry.Model.Kind == NotifierKind.Speech ? "речевой" : "звуковой";
                var wiring = entry.Speaker is not null ? ", линия 100 В" : entry.Device.Addresses > 0 ? " адресный" : " неадресный";
                var note = entry.Speaker is not null && speech is not null
                    ? string.Create(ru, $"отвод {speech.Tap.AcousticW:0.#} Вт ({speech.Tap.ConsumedW:0.##} Вт от линии), {speech.LevelAt1m:0.#} дБ на 1 м по В.1; СОУЭ по СП 3.13130.2026")
                    : entry.Model.LevelAt1m is { } lv ? string.Create(ru, $"{lv:0} дБ на 1 м; СОУЭ по СП 3.13130.2026") : "СОУЭ по СП 3.13130.2026";
                items.Add(new($"Оповещатель пожарный {kindText}{wiring}", entry.Model.Name, entry.Manufacturer, "шт.", count, entry.Device.Certificate, note));
            }
        }

        if (speech is { Amplifiers: > 0 } && amplifier is not null)
        {
            items.Add(new("Блок речевого оповещения (усилитель линий 100 В)", amplifier.Name, amplifier.Manufacturer, "шт.", speech.Amplifiers, amplifier.Certificate,
                string.Create(ru, $"нагрузка {speech.TotalConsumedW:0.#} Вт из {amplifier.MaxConsumedW:0} Вт на блок; {speech.Lines.Count} линий; питание 220 В до {amplifier.MainsW:0} Вт")));
            if (amplifier.Batteries > 0)
            {
                items.Add(new("Аккумуляторная батарея блока речевого оповещения", amplifier.BatteryName, amplifier.Manufacturer, "шт.", speech.Amplifiers * amplifier.Batteries, null,
                    string.Create(ru, $"встроенный резерв блока: {amplifier.BackupStandbyH:0} ч дежурный, {amplifier.BackupAlarmH:0} ч оповещение по паспорту")));
            }

            if (lineMonitor is not null)
            {
                items.Add(new("Модуль контроля линии оповещения (оконечный)", lineMonitor.Name, lineMonitor.Manufacturer, "шт.", speech.Lines.Count, lineMonitor.Certificate, "по одному в конце каждой линии; питание от линии"));
            }
        }

        if (relay is not null && relays > 0)
        {
            items.Add(new("Блок сигнально-пусковой адресный (команды в системы вентиляции)", relay.Name, relay.Manufacturer, "шт.", relays, relay.Device.Certificate, $"{relay.Outputs} вых. в блоке; АППЗ по СП 484 разд. 7, СП 7"));
        }

        if (isolators > 0 && set?.Isolator is { } iso)
        {
            items.Add(new("Блок разветвительно-изолирующий (изолятор КЗ линии)", iso.Name, Maker(iso.Source), "шт.", isolators, iso.Certificate, "по п. 6.3.5 СП 484; размещение по линии — по топологии"));
        }

        if (budget is not null && set is not null)
        {
            items.Add(new("Источник питания резервированный", set.Supply.Name, Maker(set.Supply.Source), "шт.", budget.Supplies, set.Supply.Certificate, $"{budget.Voltage}; I категория надёжности (СП 6, п. 5.21){(budget.Supplies > 1 ? "; нагрузка поделена между источниками" : "")}"));
            if (set.Box is { } box)
            {
                items.Add(new("Бокс резервного питания для дополнительных АКБ", box, Maker(set.Supply.Source), "шт.", budget.Supplies, set.Supply.Certificate, "по РЭ РИП; сертификат — общий с РИП (прил. № 1)"));
            }

            items.Add(new("Аккумуляторная батарея", set.Battery.Name, Maker(set.Battery.Source), "шт.", budget.Supplies * set.Battery.Count, set.Battery.Certificate,
                budget.Required is { } r ? string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), $"требуется {r.AmpereHours:0.##} А·ч{(budget.Supplies > 1 ? " на источник" : "")}, принята {set.Battery.Label}, {set.Battery.Total.AmpereHours:0.##} А·ч на источник") : "ёмкость не определена"));
        }

        return items;
    }

    // Изготовитель приборов питания в каталоге не выделен отдельным полем: все паспорта — bolid.ru.
    private static string Maker(string source) => source.Contains("bolid.ru", StringComparison.OrdinalIgnoreCase) ? "НВП «Болид»" : "по паспорту";
}
