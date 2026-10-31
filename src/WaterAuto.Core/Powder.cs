namespace WaterAuto.Core;

/// <summary>Способ порошкового пожаротушения (СП 485, 10.1.5): по всей площади, локально по площади, по всему объёму, локально по объёму.</summary>
public enum PowderMethod
{
    Area,
    LocalArea,
    Volume,
    LocalVolume,
}

/// <summary>Вид огнетушащего порошка для строки табл. И.1: для классов А, В или только для класса В.</summary>
public enum PowderKind
{
    AB,
    B,
}

/// <summary>Защищаемые одним модулем площадь, м², и объём, м³, по классам пожара А и В; чего нет в паспорте — null.</summary>
public sealed record PowderValues(double? AreaA, double? AreaB, double? VolumeA, double? VolumeB)
{
    public double? Pick(char fireClass, bool volume) => (fireClass, volume) switch
    {
        ('A', false) => AreaA,
        ('A', true) => VolumeA,
        (_, false) => AreaB,
        _ => VolumeB,
    };
}

/// <summary>
/// Отрезок высот установки по документации на модуль: значения на нижней и верхней границе; внутри — линейно (таблица с
/// интерполяцией или формула паспорта, посчитанная на концах). Диапазон «3,0 ± 0,5» с одним значением — одинаковые концы.
/// </summary>
public sealed record PowderSegment(double FromM, double ToM, PowderValues AtFrom, PowderValues AtTo);

/// <summary>
/// Модуль порошкового пожаротушения по паспорту: защищаемые площадь/объём по высоте установки (отрезки), вид порошка,
/// импульсный ли, и коэффициенты из документации: k1; k2 = 1 + K2Factor·Sз/Sу (если паспорт даёт формулу) с порогом
/// K2Limit для рекомендации доп. модулей; k4 для способов по площади и по объёму. Чего нет в паспорте — null.
/// </summary>
public sealed record PowderModuleModel(
    string Id,
    string Name,
    PowderKind Powder,
    bool Impulse,
    IReadOnlyList<PowderSegment> Coverage,
    double? K1,
    double? K2Factor,
    double? K2Limit,
    double? K4Area,
    double? K4Volume,
    string Source)
{
    public double MinHeightM => Coverage.Min(c => c.FromM);

    public double MaxHeightM => Coverage.Max(c => c.ToM);
}

/// <summary>
/// Защищаемое помещение (зона) — исходные данные проектировщика: площадь, объём, высота установки модулей, способ тушения,
/// класс пожара (A или B), строка табл. И.1 или k3 по заданию, доля открытых проёмов f, k1 по заданию, только ручной пуск,
/// затенённая площадь Sз, м² (для k2 по формуле паспорта; 0 — затенений нет, по решению проектировщика).
/// </summary>
public sealed record PowderRoom(
    string Id,
    string Name,
    double AreaM2,
    double VolumeM3,
    double MountHeightM,
    PowderMethod Method,
    char FireClass,
    string? SubstanceRow,
    double? K3,
    double OpenFraction,
    double? K1,
    bool ManualOnly = false,
    double? ShadedAreaM2 = null);

/// <summary>Результат по помещению: количество модулей, принятые Sн/Vн и коэффициенты, расчётная величина (площадь или объём).</summary>
public sealed record PowderRoomResult(string RoomId, int Modules, double Protected, double PerModule, double K1, double K2, double K3, double K4, string Formula, string Clause);

public sealed record PowderResult(IReadOnlyList<PowderRoomResult> Rooms, int Reserve, IReadOnlyList<Issue> Issues);

/// <summary>Блок пуска АУП по паспорту: число выходов, ток выхода и суммарный, ток контроля цепей, А.</summary>
public sealed record PowderStartUnit(string Name, int Outputs, double MaxOutputCurrent, double MaxTotalCurrent, double MonitorCurrent);

/// <summary>Цепи пуска: модулей на выход, выходов по помещениям, блоков пуска, замечания.</summary>
public sealed record PowderStartPlan(int ModulesPerOutput, IReadOnlyDictionary<string, int> OutputsByRoom, int Units, IReadOnlyList<Issue> Issues);
