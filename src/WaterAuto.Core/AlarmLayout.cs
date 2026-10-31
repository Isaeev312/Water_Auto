namespace WaterAuto.Core;

/// <summary>Тип точечного пожарного извещателя (СП 484, 6.6.15, 6.6.16).</summary>
public enum DetectorKind
{
    Smoke,
    Heat,
}

/// <summary>Алгоритм принятия решения о пожаре (СП 484, 6.4): от него зависит, сколькими ИП контролируется каждая точка (6.6.1, 6.6.2).</summary>
public enum DetectionAlgorithm
{
    A,
    B,
    C,
}

/// <summary>
/// Зона раскладки извещателей — помещение, для которого проектировщик задал тип ИП, алгоритм и адресность (решения
/// проекта, ADR 0012 п. 6; нужна ли здесь АПС — вопрос А-02, программа не решает). Высота — до перекрытия в зоне.
/// </summary>
public sealed record DetectorZone(string RoomId, string RoomName, Polygon Outline, Length CeilingHeight, DetectorKind Kind, DetectionAlgorithm Algorithm, bool Addressable);

/// <summary>Извещатель раскладки; ID — «ID помещения.D номер».</summary>
public sealed record PlacedDetector(string Id, string RoomId, PlanPoint Location, DetectorKind Kind);

/// <summary>Результат раскладки извещателей: ИП, число ИП по помещениям и замечания.</summary>
public sealed record DetectorLayout(IReadOnlyList<PlacedDetector> Detectors, IReadOnlyDictionary<string, int> CountByRoom, IReadOnlyList<Issue> Issues);
