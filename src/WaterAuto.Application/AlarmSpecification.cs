using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Состав оборудования АПС для спецификации: приборы из расчёта питания (контроллеры, модули, РИП, АКБ), извещатели по
/// выбранным моделям из раскладки, ИПР и изоляторы из состава СПС. Ничего не считает заново — только собирает количества.
/// </summary>
public static class AlarmSpecification
{
    public static IReadOnlyList<AlarmSpecItem> Build(PowerBudget? budget, AlarmPowerSet? set, IReadOnlyList<DetectorZone> zones, DetectorLayout layout, IReadOnlyDictionary<string, DetectorModel> models, DetectorModel? manualModel, int manuals, int isolators)
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

        if (isolators > 0 && set?.Isolator is { } iso)
        {
            items.Add(new("Блок разветвительно-изолирующий (изолятор КЗ линии)", iso.Name, Maker(iso.Source), "шт.", isolators, iso.Certificate, "по п. 6.3.5 СП 484; размещение по линии — по топологии"));
        }

        if (budget is not null && set is not null)
        {
            items.Add(new("Источник питания резервированный", set.Supply.Name, Maker(set.Supply.Source), "шт.", 1, set.Supply.Certificate, $"{budget.Voltage}; I категория надёжности (СП 6, п. 5.21)"));
            items.Add(new("Аккумуляторная батарея", set.Battery.Name, Maker(set.Battery.Source), "шт.", 1, set.Battery.Certificate,
                budget.Required is { } r ? string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), $"требуется {r.AmpereHours:0.##} А·ч, принята {set.Battery.Nominal.AmpereHours:0.##} А·ч") : "ёмкость не определена"));
        }

        return items;
    }

    // Изготовитель приборов питания в каталоге не выделен отдельным полем: все паспорта — bolid.ru.
    private static string Maker(string source) => source.Contains("bolid.ru", StringComparison.OrdinalIgnoreCase) ? "НВП «Болид»" : "по паспорту";
}
