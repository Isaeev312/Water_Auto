namespace WaterAuto.Core;

public enum NodeKind
{
    Sprinkler,
    Tee,
    Hydrant,
    Source,
}

/// <summary>Узел сети. Эмиттер (ороситель, пожарный кран) задан коэффициентом K.</summary>
public sealed record NetworkNode(string Id, NodeKind Kind, Length Elevation, KFactor? Emitter = null);

/// <summary>
/// Сортамент стальной трубы: стандарт (как в attrs табл. Б.2, напр. «ГОСТ 3262»), DN, наружный диаметр и стенка.
/// Нужен для Kт по табл. Б.2 / Б.8, скорости (6.7.1.37), спецификации и DN в DXF.
/// </summary>
public sealed record PipeGeometry(string Standard, int Dn, Length OuterDiameter, Length Wall)
{
    public Length InnerDiameter => new(OuterDiameter.Meters - (2 * Wall.Meters));
}

/// <summary>
/// Труба. From — ближе к источнику. LossMultiplier — множитель к Q²·L/(100·Kт):
/// (1 + доля местных потерь) и, в тестах совместимости, поправки сторонних программ.
/// Geometry — сортамент; без него скорость не считается (пластмасса, Kт по данным производителя).
/// </summary>
public sealed record NetworkPipe(string Id, string From, string To, Length Length, PipeCharacteristic Characteristic, double LossMultiplier = 1.0, PipeGeometry? Geometry = null);

/// <summary>Расчётная сеть: граф из узлов и труб, ровно один узел-источник.</summary>
public sealed record HydraulicNetwork(IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<NetworkPipe> Pipes);
