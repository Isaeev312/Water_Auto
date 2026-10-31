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

/// <summary>Защищаемая модулем площадь и объём на высоте установки по диаграмме распыла из документации на модуль.</summary>
public sealed record PowderCoverage(double HeightM, double? AreaA, double? AreaB, double? Volume);

/// <summary>
/// Модуль порошкового пожаротушения по паспорту: защищаемая площадь/объём по высоте установки, пределы высоты, вид порошка,
/// импульсный ли, и коэффициенты k1, k2, k4, если их задаёт документация (прил. И СП 485). Чего нет в паспорте — null.
/// </summary>
public sealed record PowderModuleModel(
    string Id,
    string Name,
    PowderKind Powder,
    bool Impulse,
    double MinHeightM,
    double MaxHeightM,
    IReadOnlyList<PowderCoverage> Coverage,
    double? K1,
    double? K2,
    double? K4,
    string Source);

/// <summary>
/// Защищаемое помещение (зона) — исходные данные проектировщика: площадь, объём, высота установки модулей, способ тушения,
/// класс пожара (A или B — для выбора Sн), строка табл. И.1 (горючее вещество) или k3 по заданию, доля открытых проёмов f,
/// k1 по заданию, только ручной пуск.
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
    bool ManualOnly = false);

/// <summary>Результат по помещению: количество модулей, принятые Sн/Vн и коэффициенты, расчётная величина (площадь или объём).</summary>
public sealed record PowderRoomResult(string RoomId, int Modules, double Protected, double PerModule, double K1, double K2, double K3, double K4, string Formula, string Clause);

public sealed record PowderResult(IReadOnlyList<PowderRoomResult> Rooms, int Reserve, IReadOnlyList<Issue> Issues);
