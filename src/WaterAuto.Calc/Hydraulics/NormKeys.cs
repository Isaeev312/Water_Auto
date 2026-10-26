namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Ключи JSON норм, которые читает Calc. Список должен совпадать с таблицей требований tools/ValidateData (T4):
/// переименование ключа в data/norms ловится тестом согласованности FormulaTests.
/// </summary>
internal static class NormKeys
{
    public const string ClauseTableB2 = "табл. Б.2";
    public const string ClauseTableB3 = "табл. Б.3";

    public const string Dn = "dn";
    public const string DOut = "D_out";
    public const string Wall = "wall";
    public const string Kt = "Kt";
    public const string Psi = "psi";
    public const string Theta = "theta";
    public const string DnMin = "dnMin";
    public const string DnMax = "dnMax";

    public const string AttrStandard = "standard";
    public const string AttrQuantity = "quantity";
    public const string AttrOutdoorOnly = "outdoorOnly";
}
