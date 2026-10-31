using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Проверка способа оповещения по табл. Б.1 СП 3.13130.2026: строка таблицы (класс объекта) и показатель (машино-места,
/// площадь, число людей) — исходные данные; способ выбирает проектировщик (6.2.7), программа сверяет с порогом строки.
/// </summary>
public static class SoueMethod
{
    private const string Clause = "СП 3.13130.2026, прил. Б, табл. Б.1";

    /// <param name="rowId">ID строки табл. Б.1 в данных норм (data/norms/sp3/table_B_1_method.json).</param>
    /// <param name="indicator">Показатель строки: машино-места, м² или человек — в единицах строки.</param>
    public static IReadOnlyList<Issue> Check(WarningMethod method, string rowId, double indicator, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var row = norms.Find(rowId);
        if (row is null)
        {
            return [new Issue("soue.method.noRow", IssueLevel.Error, $"В данных норм нет строки табл. Б.1 «{rowId}» — способ оповещения не проверен.", Clause: Clause)];
        }

        var cls = row.Attributes.GetValueOrDefault("class") ?? rowId;
        var unit = row.Attributes.GetValueOrDefault("unit") ?? "";
        var soundAllowed = row.Values["sound"] == 1;
        var speechAllowed = row.Values["speech"] == 1;
        if (row.Values.GetValueOrDefault("threshold") is { } threshold)
        {
            if (indicator >= threshold && method == WarningMethod.Sound)
            {
                return [new Issue("soue.method.speechRequired", IssueLevel.Error,
                    string.Create(ru, $"{cls}: {indicator:0} {unit} — от {threshold:0} {unit} требуется речевой способ оповещения, выбран звуковой."), Clause: Clause)];
            }

            if (indicator < threshold && method == WarningMethod.Speech)
            {
                return [new Issue("soue.method.speechOptional", IssueLevel.Info,
                    string.Create(ru, $"{cls}: {indicator:0} {unit} — достаточно звукового способа; речевой принят по заданию (6.2.7)."), Clause: Clause)];
            }

            return [];
        }

        if (method == WarningMethod.Sound && !soundAllowed)
        {
            return [new Issue("soue.method.speechRequired", IssueLevel.Error, $"{cls}: звуковой способ не допускается, требуется речевой.", Clause: Clause)];
        }

        var replaceable = bool.TryParse(row.Attributes.GetValueOrDefault("replaceable"), out var r) && r;
        if (method == WarningMethod.Speech && !speechAllowed && !replaceable)
        {
            return [new Issue("soue.method.soundOnly", IssueLevel.Warning, $"{cls}: таблица задаёт звуковой способ; речевой принят по заданию.", Clause: Clause)];
        }

        return [];
    }
}
