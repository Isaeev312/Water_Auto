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
/// Труба. From — ближе к источнику. LossMultiplier — множитель к Q²·L/(100·Kт):
/// (1 + доля местных потерь) и, в тестах совместимости, поправки сторонних программ.
/// </summary>
public sealed record NetworkPipe(string Id, string From, string To, Length Length, PipeCharacteristic Characteristic, double LossMultiplier = 1.0);

/// <summary>Расчётная сеть: граф из узлов и труб, ровно один узел-источник.</summary>
public sealed record HydraulicNetwork(IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<NetworkPipe> Pipes);
