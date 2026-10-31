namespace WaterAuto.Core;

/// <summary>Способ оповещения по СП 3.13130.2026 (6.2.1): звуковой или речевой; световой добавляется по условиям 6.8.2 и 6.16.</summary>
public enum WarningMethod
{
    Sound,
    Speech,
}

/// <summary>Вид оповещателя.</summary>
public enum NotifierKind
{
    Sound,
    Speech,
    Light,
}

/// <summary>
/// Модель оповещателя из каталога: уровень звука на 1 м (дБ; null у световых), ток потребления, паспорт и сертификат.
/// Для диапазона уровня в паспорте берётся наименьший (СП 3, В.3.5).
/// </summary>
public sealed record NotifierModel(string Name, NotifierKind Kind, double? LevelAt1m, Current Current, string Source, string? Certificate);

/// <summary>Зона оповещения — помещение с фоновым шумом (табл. В.1 или задание) и способом оповещения (решение проектировщика по табл. Б.1).</summary>
public sealed record SoueZone(string RoomId, string RoomName, Polygon Outline, double BackgroundDba, WarningMethod Method);

/// <summary>Результат по зоне: требуемый уровень на 1,5 м и радиус, в котором оповещатель его обеспечивает (В.2).</summary>
public sealed record SoueZoneResult(string RoomId, double RequiredLevel, Length SoundRadius, int Sounders, int Lights);

/// <summary>Оповещатель раскладки: «помещение.S номер» — звуковой или речевой на потолке, «помещение.L номер» — световой на стене.</summary>
public sealed record PlacedNotifier(string Id, string RoomId, PlanPoint Location, NotifierKind Kind);

public sealed record SoueLayout(IReadOnlyList<PlacedNotifier> Sounders, IReadOnlyList<PlacedNotifier> Lights, IReadOnlyList<SoueZoneResult> Zones, IReadOnlyList<Issue> Issues);
