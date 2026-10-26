namespace WaterAuto.Core;

/// <summary>Помещение: контур с ПТ_КОНТУР, имя — текст внутри контура.</summary>
public sealed record Room(string Id, string Name, Polygon Contour);

/// <summary>Колонна: сечение с ПТ_КОЛОННЫ.</summary>
public sealed record Column(string Id, Polygon Outline);

/// <summary>Балка: «Толщина» полилинии — высота балки, «Уровень» — отметка низа перекрытия (README §7).</summary>
public sealed record Beam(string Id, Polygon Outline, Length Height, Length SlabBottom);

/// <summary>Короб: «Уровень» полилинии — отметка низа короба.</summary>
public sealed record Duct(string Id, Polygon Outline, Length Bottom);

/// <summary>Зона без оросителей (ПТ_ЗАПРЕТ).</summary>
public sealed record NoSprinklerZone(string Id, Polygon Outline);

/// <summary>Точка подключения: стояк или узел управления (ПТ_ВВОД).</summary>
public sealed record Inlet(string Id, PlanPoint Location, string? Name);

/// <summary>
/// Помещение насосной станции: контур, точка двери; Inlet — ввод воды в насосную, Outlets — выпуски в сеть (по одному на секцию).
/// </summary>
public sealed record PumpRoom(string Id, Polygon Outline, PlanPoint? Door = null)
{
    public PlanPoint? Inlet { get; init; }

    public IReadOnlyList<PlanPoint> Outlets { get; init; } = [];

    /// <summary>Патрубки для мобильной пожарной техники на наружной стене (слой ПТ_НС_ГМ); пусто — у ввода.</summary>
    public IReadOnlyList<PlanPoint> TruckInlets { get; init; } = [];
}

/// <summary>План этажа после импорта (ARCHITECTURE §5). ID — дескрипторы объектов чертежа.</summary>
public sealed record FloorPlan(
    IReadOnlyList<Room> Rooms,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Beam> Beams,
    IReadOnlyList<Duct> Ducts,
    IReadOnlyList<NoSprinklerZone> NoSprinklerZones,
    IReadOnlyList<Inlet> Inlets)
{
    /// <summary>Насосные станции на этаже (слой ПТ_НС или зона ПТ_ЗАПРЕТ с подписью «ПНС»).</summary>
    public IReadOnlyList<PumpRoom> PumpRooms { get; init; } = [];
}
