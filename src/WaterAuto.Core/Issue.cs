using System.Text.RegularExpressions;

namespace WaterAuto.Core;

public enum IssueLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Замечание расчёта или нарушение нормы (README §8): ошибки расчёта — объекты, а не исключения.
/// </summary>
/// <param name="Code">Постоянный код, например «topology.cycle».</param>
/// <param name="ElementId">ID узла или трубы, к которому относится замечание.</param>
/// <param name="Clause">Пункт нормы, если есть.</param>
/// <param name="Location">Место на плане, мм — для слоя ПТ_ЗАМЕЧАНИЯ и сообщений импорта.</param>
public sealed record Issue(string Code, IssueLevel Level, string Message, string? ElementId = null, string? Clause = null, PlanPoint? Location = null);

/// <summary>Название уровня замечания для документов и окон: в записках и на листах — по-русски.</summary>
public static partial class IssueLevels
{
    public static string Ru(this IssueLevel level) => level switch
    {
        IssueLevel.Error => "Ошибка",
        IssueLevel.Warning => "Предупреждение",
        _ => "Информация",
    };

    /// <summary>Текст замечания для документа: без ключей файла проекта в скобках («(design.maxAmbientTemperature)») — они подсказка проектировщику.</summary>
    public static string DocumentText(this Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return ProjectKey().Replace(issue.Message, "");
    }

    [GeneratedRegex(@" ?\((design|alarm|gas|powder)\.[A-Za-z.]+\)")]
    private static partial Regex ProjectKey();
}
