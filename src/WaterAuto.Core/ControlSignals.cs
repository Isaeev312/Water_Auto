namespace WaterAuto.Core;

/// <summary>Вид сигнала оборудования АУПТ в систему пожарной сигнализации.</summary>
public enum SignalKind
{
    /// <summary>Положение запорного устройства (датчик положения).</summary>
    ValvePosition,

    /// <summary>Срабатывание сигнализатора давления узла управления.</summary>
    PressureSwitch,
}

/// <summary>
/// Источник сигнала АУПТ для АПС (ADR 0012, п. 2): позиция каталога, вид сигнала и количество устройств.
/// Список берётся из поля signals шаблонов каталога, а не из кода.
/// </summary>
/// <param name="Role">Роль позиции на схеме насосной, если задана.</param>
/// <param name="Equipment">Наименование позиции с подставленными DN.</param>
/// <param name="Clause">Пункт нормы, по которому нужен контроль, если он указан в каталоге.</param>
public sealed record SignalSource(string? Role, string Equipment, SignalKind Kind, int Count, string? Clause = null);

/// <summary>
/// Точка управления — одно устройство АУПТ, сигнал которого заводится в АПС: строка перечня сигналов и будущей таблицы адресов.
/// </summary>
/// <param name="Id">Обозначение, производное от роли позиции АУПТ и номера устройства, например «cuValve.2».</param>
public sealed record ControlPoint(string Id, string Equipment, SignalKind Kind, string? Clause);
