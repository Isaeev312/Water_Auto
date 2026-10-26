namespace WaterAuto.Core;

/// <summary>Раздел спецификации.</summary>
public enum TakeoffGroup
{
    PumpStation,
    PumpStationValves,
    PumpStationFittings,
    PumpStationPipes,
    ControlUnit,
    Sprinklers,
    Pipes,
    Fittings,
    Valves,
    Hydrants,
    Supports,
    Materials,
}

/// <summary>
/// Строка ведомости материалов — основа спецификации (ГОСТ 21.110, форма 1).
/// Designation — тип, марка или обозначение документа (стандарт трубы, марка оросителя).
/// </summary>
/// UnitMass — масса единицы, кг (для труб — 1 м); Supplier — поставщик (изготовитель);
/// Role — роль элемента на схеме (по ней схема ставит номер позиции).
public sealed record TakeoffItem(TakeoffGroup Group, string Name, string? Designation, double Quantity, string Unit, string? Note = null, double? UnitMass = null, string? Supplier = null, string? Role = null);

public sealed record MaterialTakeoff(IReadOnlyList<TakeoffItem> Items, IReadOnlyList<Issue> Issues);
