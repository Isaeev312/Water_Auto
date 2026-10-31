namespace WaterAuto.Core;

/// <summary>Устройство на структурной схеме: обозначение с плана, подпись (модель/помещение) и вид для условного знака.</summary>
public sealed record SchemeDevice(string Id, string Label, SchemeDeviceKind Kind);

public enum SchemeDeviceKind
{
    SmokeDetector,
    HeatDetector,
    ManualCallPoint,
    Sounder,
    Light,
    Module,
    Relay,
    Isolator,
}

/// <summary>Адресная линия контроллера: устройства в порядке обхода кольца (как в кабельном журнале), модуль сигналов АУПТ в насосной.</summary>
public sealed record SchemeLoop(int Number, string Controller, IReadOnlyList<SchemeDevice> Devices, string? PumpModule, int Relays, string? RelayName);

/// <summary>Линия речевого оповещения на схеме: группа (уровень), число и модель громкоговорителей, мощность, оконечный модуль.</summary>
public sealed record SchemeSpeechLine(int Number, string Group, int Speakers, string Speaker, double ConsumedW, string? Monitor);

/// <summary>
/// Структурная схема АПС/СОУЭ (ADR 0012): источники питания, контроллеры с кольцевыми линиями и устройствами, релейные блоки,
/// усилитель речевого оповещения с линиями громкоговорителей. Только состав и порядок — без координат (их задаёт чертёж).
/// </summary>
public sealed record AlarmScheme(
    IReadOnlyList<SchemeLoop> Loops,
    string Supply,
    int Supplies,
    string Battery,
    string? Amplifier,
    int Amplifiers,
    IReadOnlyList<SchemeSpeechLine> SpeechLines,
    string? Note = null);
