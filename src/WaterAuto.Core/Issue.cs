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
public sealed record Issue(string Code, IssueLevel Level, string Message, string? ElementId = null, string? Clause = null);
