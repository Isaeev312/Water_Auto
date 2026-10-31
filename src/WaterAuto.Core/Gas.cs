namespace WaterAuto.Core;

/// <summary>Расположение постоянно открытых проёмов по высоте помещения (прил. Д, п. Д.2.2) — для параметра П.</summary>
public enum GasOpenings
{
    /// <summary>Внизу (до 0,2H) и вверху (0,8–1,0H) примерно поровну.</summary>
    BothZones,
    TopOnly,
    BottomOnly,

    /// <summary>Равномерно по высоте и все остальные случаи.</summary>
    Other,
}

/// <summary>Подкласс пожара А1 для прил. Д, п. Д.3 (K4): нет, бумага в кипах, то же без доступа пожарных, прочие А1.</summary>
public enum GasClassA1
{
    None,
    PaperBales,
    PaperNoAccess,
    Other,
}

/// <summary>
/// Помещение АУГП — исходные данные проектировщика (СП 485, 9.5): объём и высота, ГОТВ (ID записи табл. Г), горючий материал
/// (ключ концентрации записи), площадь и расположение постоянно открытых проёмов, минимальная температура, высота над уровнем
/// моря, установка модульная или централизованная, подкласс А1, время подачи (null — нормативное по 9.7.4), остаток в модулях
/// и их число (ТД), остаток в трубопроводах по гидравлическому расчёту (null — не учтён, замечание), предельное избыточное
/// давление для проёма сброса (null — проём не считается), плотность воздуха.
/// </summary>
public sealed record GasRoom(
    string Id,
    string Name,
    double VolumeM3,
    double HeightM,
    string AgentId,
    string Material,
    double OpenAreaM2,
    GasOpenings Openings,
    double MinTemperatureC,
    double AltitudeM,
    bool Modular = true,
    GasClassA1 ClassA1 = GasClassA1.None,
    double? DischargeTimeS = null,
    double ModuleResidueKg = 0,
    int Modules = 0,
    double? PipeResidueKg = null,
    double? MaxOverpressureMPa = null,
    double AirDensity = 1.2);

/// <summary>Результат: концентрация, плотность ρ1, K2, K3, δ, массы Mр и Mг, площадь проёма сброса (null — не считалась, 0 — не нужен).</summary>
public sealed record GasMassResult(string RoomId, double Concentration, double Rho1, double K2, double K3, double Delta, double DischargeTimeS, double DesignMass, double TotalMass, double? ReliefAreaM2, IReadOnlyList<Issue> Issues);
