namespace WaterAuto.Core;

/// <summary>
/// Контроллер адресной линии (ДПЛС) по паспорту: ток от источника питания без адресных устройств, при известной
/// нагрузке линии <paramref name="LoadOfLoaded"/>, максимальный; допустимая нагрузка линии и число адресов.
/// </summary>
public sealed record LoopController(string Name, Current Idle, Current Loaded, Current LoadOfLoaded, Current Max, Current MaxLoopLoad, int MaxAddresses, string Source) : ICertified
{
    public string? Certificate { get; init; }
}

/// <summary>
/// Изделие с сертификатом соответствия, действующим на объекте (в КР — вопрос А-12): номер, орган, срок. Null — в каталоге
/// не указан; расчёт об этом предупреждает, но не останавливается.
/// </summary>
public interface ICertified
{
    string Name { get; }

    string? Certificate { get; }
}

/// <summary>Адресный модуль входов: питается от линии контроллера, ток — нагрузка линии.</summary>
public sealed record LoopModule(string Name, int Inputs, Current LoopCurrent, int MaxPerLoop, string Source) : ICertified
{
    public string? Certificate { get; init; }
}

/// <summary>Адресное устройство линии контроллера без своего питания (извещатель): ток от линии, один адрес.</summary>
public sealed record LoopDevice(string Name, Current LoopCurrent, string Source) : ICertified
{
    public string? Certificate { get; init; }

    /// <summary>Адресов на линии занимает одно устройство; 0 — изолятор КЗ.</summary>
    public int Addresses { get; init; } = 1;
}

/// <summary>
/// Адресное устройство с собственным питанием от РИП (оповещатель, релейный блок): ток от линии контроллера и адреса —
/// в нагрузку линии; токи дежурного режима и «Пожара» — в расчёт АКБ.
/// </summary>
public sealed record PoweredDevice(string Name, Current LoopCurrent, int Addresses, Current Standby, Current Alarm, string Source, string? Certificate) : ICertified;

/// <summary>Резервированный источник питания: собственный ток от АКБ при отсутствии сети (Iибэ).</summary>
public sealed record PowerSupply(string Name, Current OwnCurrent, string Source) : ICertified
{
    public string? Certificate { get; init; }

    /// <summary>Номинальный ток нагрузки по паспорту; null — не задан (проверка не выполняется).</summary>
    public Current? MaxOutput { get; init; }
}

/// <summary>
/// АКБ по паспорту: номинальная ёмкость C20, ёмкость в конце срока службы S (% от начальной) и токи разряда
/// по таблице разряда — ключ — время разряда.
/// </summary>
public sealed record BatteryModel(string Name, Charge Nominal, double EndOfLifePercent, IReadOnlyDictionary<TimeSpan, Current> Discharge, string Source) : ICertified
{
    public string? Certificate { get; init; }
}

/// <summary>Набор приборов питания автоматики АУПТ в АПС из каталога; все токи — при напряжении <paramref name="Voltage"/>, В.</summary>
public sealed record AlarmPowerSet(Voltage Voltage, LoopController Controller, LoopModule Module, PowerSupply Supply, BatteryModel Battery)
{
    /// <summary>Извещатели набора по ключам каталога (smoke, heat, manual); их число берётся из раскладки.</summary>
    public IReadOnlyDictionary<string, LoopDevice> Detectors { get; init; } = new Dictionary<string, LoopDevice>();

    /// <summary>Изолятор КЗ линии (БРИЗ); null — в каталоге нет.</summary>
    public LoopDevice? Isolator { get; init; }
}

/// <summary>Строка расчёта питания: прибор, количество, токи на единицу (null — питается не от РИП), пояснение и источник.</summary>
public sealed record PowerLine(string Name, int Count, Current? Standby, Current? Alarm, string Note, string Source);

/// <summary>
/// Расчёт АКБ: суммарные токи ΣIд и ΣIп, Iибэ, коэффициенты Kн по паспорту АКБ и Kстр (Б.2), требуемая ёмкость (Б.1.1)
/// и время работы выбранной АКБ (Б.3). Null — величину посчитать нельзя, причина — в замечаниях.
/// </summary>
public sealed record PowerBudget(
    Voltage Voltage,
    IReadOnlyList<PowerLine> Lines,
    Current Standby,
    Current Alarm,
    Current SupplyOwn,
    BatteryModel Battery,
    double? StandbyLoadFactor,
    double? AlarmLoadFactor,
    Charge? Required,
    TimeSpan? Backup,
    double? AgingFactor = null);

/// <summary>Строка спецификации оборудования АПС.</summary>
public sealed record AlarmSpecItem(string Name, string Designation, string Manufacturer, string Unit, int Quantity, string? Certificate, string? Note);

/// <summary>Строка кабельного журнала: откуда, куда, назначение, марка, длина (0 — не определена) и примечание.</summary>
public sealed record CableLine(string Id, string From, string To, string Purpose, string Cable, double LengthM, string Note);

/// <summary>Сечение ДПЛС и наибольшая длина линии, м, по суммарному току адресных устройств, мА (паспорт контроллера).</summary>
public sealed record LoopCableSection(double SectionMm2, IReadOnlyDictionary<double, double> MaxLengthMByLoadMa);

/// <summary>Каталог кабелей АПС: запас длины, условная длина в посту, марки по умолчанию и таблица длин ДПЛС.</summary>
public sealed record CableCatalog(double Reserve, double InCabinetM, string LoopName, IReadOnlyList<LoopCableSection> LoopSections, string LoopSource, string PowerName, string MainsName, string SignalName)
{
    public double MaxLoopSection => LoopSections.Max(s => s.SectionMm2);

    /// <summary>Наименьшее сечение, при котором паспортная длина для ближайшего не меньшего столбца тока покрывает длину; null — ни одно.</summary>
    public double? LoopSectionFor(double lengthM, double loadMa)
    {
        foreach (var s in LoopSections.OrderBy(s => s.SectionMm2))
        {
            var column = s.MaxLengthMByLoadMa.Keys.Where(k => k >= loadMa - 1e-9).DefaultIfEmpty(s.MaxLengthMByLoadMa.Keys.Max()).Min();
            if (loadMa <= s.MaxLengthMByLoadMa.Keys.Max() + 1e-9 && s.MaxLengthMByLoadMa[column] >= lengthM)
            {
                return s.SectionMm2;
            }
        }

        return null;
    }
}
