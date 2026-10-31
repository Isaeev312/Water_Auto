namespace WaterAuto.Core;

/// <summary>
/// Контроллер адресной линии (ДПЛС) по паспорту: ток от источника питания без адресных устройств, при известной
/// нагрузке линии <paramref name="LoadOfLoaded"/>, максимальный; допустимая нагрузка линии и число адресов.
/// </summary>
public sealed record LoopController(string Name, Current Idle, Current Loaded, Current LoadOfLoaded, Current Max, Current MaxLoopLoad, int MaxAddresses, string Source);

/// <summary>Адресный модуль входов: питается от линии контроллера, ток — нагрузка линии.</summary>
public sealed record LoopModule(string Name, int Inputs, Current LoopCurrent, int MaxPerLoop, string Source);

/// <summary>Резервированный источник питания: собственный ток от АКБ при отсутствии сети (Iибэ).</summary>
public sealed record PowerSupply(string Name, Current OwnCurrent, string Source);

/// <summary>
/// АКБ по паспорту: номинальная ёмкость C20, ёмкость в конце срока службы S (% от начальной) и токи разряда
/// по таблице разряда — ключ — время разряда.
/// </summary>
public sealed record BatteryModel(string Name, Charge Nominal, double EndOfLifePercent, IReadOnlyDictionary<TimeSpan, Current> Discharge, string Source);

/// <summary>Набор приборов питания автоматики АУПТ в АПС из каталога; все токи — при напряжении <paramref name="Voltage"/>, В.</summary>
public sealed record AlarmPowerSet(Voltage Voltage, LoopController Controller, LoopModule Module, PowerSupply Supply, BatteryModel Battery);

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
