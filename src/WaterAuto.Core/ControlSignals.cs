namespace WaterAuto.Core;

/// <summary>Вид сигнала оборудования АУПТ в систему пожарной сигнализации.</summary>
public enum SignalKind
{
    /// <summary>Положение запорного устройства (датчик положения).</summary>
    ValvePosition,

    /// <summary>Срабатывание сигнализатора давления узла управления.</summary>
    PressureSwitch,

    /// <summary>Обобщённый выход прибора управления насосами «Пуск».</summary>
    PumpStart,

    /// <summary>Обобщённый выход прибора управления насосами «Неисправность».</summary>
    PumpFault,

    /// <summary>Автоматический пуск насосов отключён (ручной режим или блокировка).</summary>
    AutomaticsOff,
}

/// <summary>Текст вида сигнала для перечней и таблиц.</summary>
public static class SignalKindText
{
    public static string Text(this SignalKind kind) => kind switch
    {
        SignalKind.ValvePosition => "Положение запорного устройства",
        SignalKind.PressureSwitch => "Срабатывание сигнализатора давления",
        SignalKind.PumpStart => "Пуск (обобщённый выход)",
        SignalKind.PumpFault => "Неисправность (обобщённый выход)",
        SignalKind.AutomaticsOff => "Автоматика отключена",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// Источник сигнала АУПТ для АПС (ADR 0012, п. 2): позиция каталога, вид сигнала и количество устройств.
/// Список берётся из поля signals шаблонов каталога, а не из кода.
/// </summary>
/// <param name="Role">Роль позиции на схеме насосной, если задана.</param>
/// <param name="Equipment">Наименование позиции с подставленными DN.</param>
/// <param name="Clause">Пункт нормы, по которому нужен контроль, если он указан в каталоге.</param>
/// <param name="Mark">Буквенная часть обозначения устройства из каталога (например «ЗД»), если задана.</param>
/// <param name="PerSection">Устройств на секцию, если количество позиции считается по секциям; 0 — позиция не привязана к секциям.</param>
public sealed record SignalSource(string? Role, string Equipment, SignalKind Kind, int Count, string? Clause = null, string? Mark = null, int PerSection = 0);

/// <summary>
/// Точка управления — одно устройство АУПТ, сигнал которого заводится в АПС: строка перечня сигналов и будущей таблицы адресов.
/// </summary>
/// <param name="Id">
/// Обозначение, производное от роли позиции АУПТ и номера устройства, например «cuValve.2»; если у позиции несколько
/// сигналов — с видом сигнала, например «cabinet.pumpFault.1».
/// </param>
/// <param name="Role">Роль позиции АУПТ — по ней берётся номер позиции спецификации, как на схеме насосной.</param>
/// <param name="Designation">Обозначение устройства для перечня и таблицы адресов, например «ЗД3»; без буквенной части в каталоге — <paramref name="Id"/>.</param>
/// <param name="Section">Номер секции в порядке схемы насосной («Секция N»); null — устройство насосной, не секции.</param>
public sealed record ControlPoint(string Id, string Equipment, SignalKind Kind, string? Clause, string? Role, string Designation, int? Section);

/// <summary>Строка таблицы адресов: контроллер, модуль, номер входа, обозначение и сигнал устройства АУПТ.</summary>
public sealed record AddressRow(string Controller, string Module, int Input, string Designation, string Equipment, string Signal, string Zone = "");
