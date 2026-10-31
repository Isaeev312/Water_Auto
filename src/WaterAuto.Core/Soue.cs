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

/// <summary>Отвод трансформатора громкоговорителя линии 100 В: акустическая (номинальная) и потребляемая от линии мощность, Вт.</summary>
public sealed record SpeakerTap(double AcousticW, double ConsumedW);

/// <summary>
/// Речевой оповещатель (громкоговоритель) линии 100 В по паспорту: чувствительность S, дБ при 1 Вт на 1 м, отводы мощности и
/// напряжение линии. Уровень на 1 м при отводе P — по формуле В.1 СП 3: L1 = S + 10·lg P (коэффициент — из данных норм).
/// </summary>
public sealed record SpeechSpeaker(double SensitivityDb, IReadOnlyList<SpeakerTap> Taps, double LineVoltage);

/// <summary>
/// Блок речевого оповещения (усилитель линий 100 В) по паспорту: наибольшая потребляемая мощность оповещателей, наибольшая
/// акустическая мощность, число ответвлений линии, длина сегмента, собственный резерв питания (встроенные АКБ, время работы).
/// </summary>
public sealed record SpeechAmplifier(string Name, string Designation, string Manufacturer, double MaxConsumedW, double MaxAcousticW, int MaxBranches, double MaxSegmentM, double LineVoltage,
    string BatteryName, int Batteries, double BackupStandbyH, double BackupAlarmH, double MainsW, string Source, string? Certificate) : ICertified;

/// <summary>Оконечный модуль контроля линии оповещения (по одному на ответвление), питается от линии.</summary>
public sealed record LineMonitor(string Name, string Designation, string Manufacturer, Current LineCurrent, string Source, string? Certificate) : ICertified;

/// <summary>Линия речевого оповещения: группа (уровень), число громкоговорителей, мощности, длина (0 — не определена) и сечение.</summary>
public sealed record SpeechLine(int Number, string Group, int Speakers, double ConsumedW, double AcousticW, double LengthM, double? SectionMm2);

/// <summary>Состав речевой СОУЭ: усилители, линии, отвод громкоговорителя и уровень на 1 м по В.1.</summary>
public sealed record SpeechPlan(int Amplifiers, IReadOnlyList<SpeechLine> Lines, SpeakerTap Tap, double LevelAt1m, double TotalConsumedW, IReadOnlyList<Issue> Issues);
