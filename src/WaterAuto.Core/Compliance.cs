namespace WaterAuto.Core;

/// <summary>Вывод строки таблицы соответствия: соответствует, не соответствует, проверить (решение — за проектировщиком или по ТД).</summary>
public enum ComplianceVerdict
{
    Complies,
    Violates,
    Check,
}

/// <summary>
/// Строка таблицы соответствия требованиям норм (для экспертизы): требование, документ и пункт, нормативное значение,
/// принятое в проекте значение и вывод. Числа — из данных норм и результатов расчёта, не из кода.
/// </summary>
public sealed record ComplianceRow(string Requirement, string Clause, string Required, string Adopted, ComplianceVerdict Verdict);
