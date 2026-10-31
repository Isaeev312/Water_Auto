namespace WaterAuto.Core;

/// <summary>Вид команды системы пожарной сигнализации в системы вентиляции (АППЗ).</summary>
public enum HvacCommandKind
{
    /// <summary>Отключение общеобменной вентиляции, кондиционирования и воздушного отопления.</summary>
    StopGeneralVentilation,

    /// <summary>Закрытие противопожарных нормально открытых клапанов.</summary>
    CloseNormallyOpenDampers,

    /// <summary>Открытие клапанов дымоудаления дымовой зоны.</summary>
    OpenSmokeDampers,

    /// <summary>Пуск вытяжной противодымной вентиляции дымовой зоны.</summary>
    StartSmokeExhaust,

    /// <summary>Пуск приточной противодымной вентиляции (подпор) с задержкой после вытяжной.</summary>
    StartSmokeSupply,
}

/// <summary>Дымовая зона из раздела ОВ: вытяжная система, приточная (подпор, может отсутствовать), число клапанов дымоудаления.</summary>
public sealed record SmokeZone(string Name, string ExhaustSystem, string? SupplySystem, int SmokeDampers);

/// <summary>Исходные данные АППЗ от раздела ОВ (решения проектировщика ОВ, не нормы).</summary>
public sealed record HvacInputs(IReadOnlyList<string> GeneralVentilation, IReadOnlyList<SmokeZone> SmokeZones, int NormallyOpenDampers);

/// <summary>
/// Команда ППУ в систему вентиляции: что, куда (системы или клапаны), по какому сигналу, с какой задержкой и по какому пункту.
/// Count — число управляемых единиц (клапанов, систем); Zone — дымовая зона или null для команд по всему объекту.
/// </summary>
public sealed record HvacCommand(string Id, HvacCommandKind Kind, string? Zone, IReadOnlyList<string> Targets, int Count, string Trigger, double? DelaySeconds, string Clause, string Note);
