namespace WaterAuto.Core;

/// <summary>Раздел спецификации.</summary>
public enum TakeoffGroup
{
    Sprinklers,
    Pipes,
    Fittings,
    Hydrants,
}

/// <summary>
/// Строка ведомости материалов — основа спецификации (ГОСТ 21.110, форма 1).
/// Designation — тип, марка или обозначение документа (стандарт трубы, марка оросителя).
/// </summary>
public sealed record TakeoffItem(TakeoffGroup Group, string Name, string? Designation, double Quantity, string Unit, string? Note = null);

public sealed record MaterialTakeoff(IReadOnlyList<TakeoffItem> Items, IReadOnlyList<Issue> Issues);
