namespace WaterAuto.Core;

/// <summary>Класс пожарной опасности перекрытия (покрытия) и стен — от него зависят пп. 6.2.10 и 6.2.21.</summary>
public enum ConstructionClass
{
    K0,
    K1,
    K2,
    K3,
    Unrated,
}

/// <summary>
/// Ороситель раскладки; ID — «ID помещения.S номер». UnderObstacleId — ID короба (площадки, оборудования),
/// под которым ороситель установлен дополнительно по п. 6.2.14; null — ороситель под перекрытием.
/// </summary>
public sealed record PlacedSprinkler(string Id, string RoomId, PlanPoint Location, string? UnderObstacleId = null);

/// <summary>
/// Прямоугольная зона раскладки: часть помещения со своей сеткой (ARCHITECTURE §6, этап 2).
/// RowAngleRad — направление рядов на плане: вдоль него ряды соседних зон продолжают друг друга.
/// </summary>
public sealed record LayoutZone(string RoomId, Polygon Outline, int SprinklerCount, double RowAngleRad);

/// <summary>Результат раскладки плана: оросители, зоны и замечания.</summary>
public sealed record SprinklerLayout(
    IReadOnlyList<PlacedSprinkler> Sprinklers,
    IReadOnlyList<LayoutZone> Zones,
    IReadOnlyList<Issue> Issues);
