namespace WaterAuto.Core;

/// <summary>Оформление выпускаемых документов: один шрифт для записки, спецификации, листов и надписей на чертежах.</summary>
public static class DocumentStyle
{
    /// <summary>Шрифт по умолчанию — GOST Common (ГОСТ 2.304, тип А); если его нет на компьютере, Word, Excel и AutoCAD подставят замену.</summary>
    public const string DefaultFont = "GOST Common";
}
