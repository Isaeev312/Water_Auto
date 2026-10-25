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

/// <summary>План этажа после импорта (ARCHITECTURE §5). ID — дескрипторы объектов чертежа.</summary>
public sealed record FloorPlan(
    IReadOnlyList<Room> Rooms,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Beam> Beams,
    IReadOnlyList<Duct> Ducts,
    IReadOnlyList<NoSprinklerZone> NoSprinklerZones,
    IReadOnlyList<Inlet> Inlets);
