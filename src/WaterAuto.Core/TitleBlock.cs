namespace WaterAuto.Core;

/// <summary>Подпись в основной надписи: должность (графа 10) и фамилия (графа 11).</summary>
public sealed record TitleSignature(string Role, string Name);

/// <summary>
/// Данные основной надписи (ГОСТ Р 21.101, приложение Ж): обозначение (графа 1), объект (2), здание (3),
/// наименование листа или документа (4/5), стадия (6), лист (7; пусто — поле PAGE в docx), общее число листов (8; пусто — поле NUMPAGES), организация (9), подписи (10–13).
/// </summary>
public sealed record TitleBlock(
    string Designation,
    string Complex,
    string Building,
    string Name,
    string Stage,
    string Organization,
    IReadOnlyList<TitleSignature> Signatures,
    int? SheetCount = null,
    string? Sheet = null);
