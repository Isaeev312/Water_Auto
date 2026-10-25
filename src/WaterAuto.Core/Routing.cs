namespace WaterAuto.Core;

public enum RouteNodeKind
{
    Sprinkler,
    Tee,
    Bend,
    Inlet,
}

/// <summary>Роль участка: рядок (распределительный), питающий или подводящий от ввода.</summary>
public enum RouteSegmentRole
{
    Row,
    Main,
    Supply,
}

/// <summary>Узел трассы. Для оросителя SprinklerId — ID из раскладки.</summary>
public sealed record RouteNode(string Id, RouteNodeKind Kind, PlanPoint Location, string? SprinklerId = null);

/// <summary>
/// Участок трассы. From — ближе к вводу. Dn — начальный диаметр (табл. Б.4), Fed — число оросителей за участком.
/// </summary>
public sealed record RouteSegment(string Id, string From, string To, RouteSegmentRole Role, Length Length, int Fed, int Dn);

/// <summary>Трасса одного помещения: дерево от ввода до оросителей.</summary>
public sealed record PipeRoute(string RoomId, string InletId, IReadOnlyList<RouteNode> Nodes, IReadOnlyList<RouteSegment> Segments);

public sealed record RoutingResult(IReadOnlyList<PipeRoute> Routes, IReadOnlyList<Issue> Issues);
