using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;

namespace WaterAuto.Export;

/// <summary>
/// Шаблон чертежа Water_Auto (README §7): слои соглашения и выхода программы с цветами, единицы — мм,
/// блок точки подключения и памятка на слое 0. Работа с архитектурным проектом: архитектурный план
/// подкладывается внешней ссылкой (XREF) в 0,0, контуры помещений, колонны, балки и короба обводятся на слоях ПТ_.
/// Формат — по расширению: .dwt и .dwg — DWG, иначе DXF.
/// </summary>
public static class LayerTemplate
{
    public const string InletBlock = "WA_ВВОД";

    // Слои входа (что читает импорт) и выхода (что пишет программа); цвета — как в tools/autocad/wa.lsp.
    private static readonly (string Name, short Color)[] Layers =
    [
        ("ПТ_КОНТУР", 3), ("ПТ_КОЛОННЫ", 8), ("ПТ_БАЛКИ", 30), ("ПТ_КОРОБА", 40), ("ПТ_ЗАПРЕТ", 210), ("ПТ_ВВОД", 5),
        (PlanDxfWriter.SprinklerLayer, 1), (PlanDxfWriter.PipeLayer, 4), (PlanDxfWriter.SegmentLayer, 7), (PlanDxfWriter.IssueLayer, 6),
        (PlanDxfWriter.ZoneLayer, 2), (PlanDxfWriter.SummaryLayer, 7),
        (AxonometryDxfWriter.PipeLayer, 4), (AxonometryDxfWriter.SprinklerLayer, 1), (AxonometryDxfWriter.LabelLayer, 7),
    ];

    private const string Memo =
        "ШАБЛОН WATER_AUTO. Единицы — мм. Архитектурный план — внешней ссылкой в 0,0; обвести на слоях:\\P" +
        "ПТ_КОНТУР — замкнутая полилиния каждого помещения, внутри текст с названием;\\P" +
        "ПТ_КОЛОННЫ — сечения колонн (полилиния, круг или блок);\\P" +
        "ПТ_БАЛКИ — замкнутые полилинии, «Толщина» = высота балки, мм; «Уровень» = отметка низа перекрытия;\\P" +
        "ПТ_КОРОБА — воздуховоды, площадки, оборудование: замкнутые полилинии, «Уровень» = отметка низа;\\P" +
        "ПТ_ЗАПРЕТ — зоны без оросителей;\\P" +
        "ПТ_ВВОД — блок WA_ВВОД или точка там, где питающий трубопровод входит в помещение (в каждом защищаемом помещении).\\P" +
        "Перед расчётом — команда WA-CHECK (tools/autocad/wa.lsp). Слои ПТ_ОРОСИТЕЛИ, ПТ_ТРУБЫ, ПТ_УЧАСТКИ, ПТ_ЗАМЕЧАНИЯ, ПТ_ДИКТ_ЗОНА, ПТ_РАСЧЁТ, ПТ_АКСОН_* заполняет программа.";

    public static void Write(string path) => DxfDrawing.Save(path, Build());

    public static CadDocument Build()
    {
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        foreach (var (name, color) in Layers)
        {
            DxfDrawing.AddLayer(doc, name, color, name is PlanDxfWriter.PipeLayer or AxonometryDxfWriter.PipeLayer ? LineWeightType.W40 : LineWeightType.Default);
        }

        var inlet = new BlockRecord(InletBlock);
        inlet.Entities.Add(new Circle(XYZ.Zero, 150));
        inlet.Entities.Add(new Line(new XYZ(-250, 0, 0), new XYZ(250, 0, 0)));
        inlet.Entities.Add(new Line(new XYZ(0, -250, 0), new XYZ(0, 250, 0)));
        doc.BlockRecords.Add(inlet);

        doc.Entities.Add(new MText
        {
            Value = Memo,
            InsertPoint = new XYZ(0, -1000, 0),
            Height = 250,
            RectangleWidth = 30000,
            Layer = doc.Layers[Layer.DefaultName],
        });
        return doc;
    }
}
