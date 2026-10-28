using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

public sealed class ProjectPipelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м: помещение с именем, колонна, ввод — по соглашению о слоях (README §7).
    private string Drawing(bool withInlet = true)
    {
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        Layer L(string name)
        {
            if (!doc.Layers.TryGetValue(name, out var layer))
            {
                layer = new Layer(name);
                doc.Layers.Add(layer);
            }

            return layer;
        }

        LwPolyline Rect(double x0, double y0, double x1, double y1) =>
            new([new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1)]) { IsClosed = true };
        var room = Rect(0, 0, 30000, 20000);
        room.Layer = L(FloorPlanImporter.ContourLayer);
        doc.Entities.Add(room);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(15000, 10000, 0), Height = 250, Layer = L(FloorPlanImporter.ContourLayer) });
        var column = Rect(7300, 7300, 7700, 7700);
        column.Layer = L(FloorPlanImporter.ColumnLayer);
        doc.Entities.Add(column);
        if (withInlet)
        {
            doc.Entities.Add(new Point(new XYZ(500, 500, 0)) { Layer = L(FloorPlanImporter.InletLayer) });
        }

        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    private string Project(double k = 0.47, ProjectHydrants? hydrants = null, bool ring = false) =>
        Write("project.waproj", ProjectFile.ToJson(new ProjectFile
        {
            Name = "Тестовая автостоянка",
            Drawing = "floor.dxf",
            Design = new ProjectDesign
            {
                RoomGroup = "2",
                SprinklerK = k,
                SprinklerModel = "по каталогу",
                PipeElevation = -1.5,
                DictatingAboveAxis = 3.5,
                SupplyLength = 30,
                SupplyDn = 125,
                InletPressure = 0.1,
                DesignVelocity = 5,
                Hydrants = hydrants,
                Ring = ring,
            },
        }));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private PipelineResult Run(string project) =>
        ProjectPipeline.Run(project, Path.Combine(_dir, "out"), RepoPaths.Norms, RepoPaths.Registry);

    [Fact]
    public void Drawing_project_produces_the_whole_package()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Files.Select(Path.GetFileName).ShouldBe([
            ProjectPipeline.PlanFile, ProjectPipeline.PlanDxfFile, ProjectPipeline.AxonometryFile, ProjectPipeline.PumpSchemeFile,
            ProjectPipeline.PumpStationSpecFile, ProjectPipeline.PumpStationSpecXlsxFile,
            ProjectPipeline.SpecificationFile, ProjectPipeline.SpecificationDocxFile, ProjectPipeline.NoteFile, ProjectPipeline.GeneralDataFile, ProjectPipeline.IssuesFile,
        ]);
        result.Files.ShouldAllBe(f => new FileInfo(f).Length > 0);
        result.Design!.PumpHead.Meters.ShouldBeGreaterThan(0);

        DwgReader.Read(result.Files[0]).Entities.Count(e => e.Layer.Name == "ПТ_ОРОСИТЕЛИ").ShouldBeGreaterThan(0);
        using var note = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.NoteFile, StringComparison.Ordinal)), false);
        var text = note.MainDocumentPart!.Document.Body!.InnerText;
        text.ShouldContain("Тестовая автостоянка");
        text.ShouldContain("Б.30");
    }

    [Fact]
    public void Hydrants_from_the_project_enter_the_pump_flow_and_the_note()
    {
        Drawing();
        var plain = Run(Project()).Design!;
        var result = Run(Project(hydrants: new ProjectHydrants
        {
            FireResistance = "II",
            Category = "В",
            HazardClass = "С0",
            Volume = 40,
            BuildingHeight = 12,
            RoomHeight = 3.3,
            AboveAxis = 1.35,
            PipeLength = 15,
            PipeDn = 65,
        }));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Design!.Hydrants.ShouldNotBeNull();
        result.Design.PumpFlow.LitersPerSecond.ShouldBeGreaterThan(plain.PumpFlow.LitersPerSecond + 5);
        using var note = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.NoteFile, StringComparison.Ordinal)), false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("СН КР 40-04:2025");
    }

    [Fact]
    public void Without_K_the_sprinkler_is_chosen_from_the_catalog()
    {
        Drawing();
        var result = Run(Project(k: 0));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Issues.ShouldContain(i => i.Code == "sprinkler.chosen");
        using var note = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.NoteFile, StringComparison.Ordinal)), false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Подбор оросителя");
    }

    [Fact]
    public void Import_errors_stop_with_locations_in_the_issue_file()
    {
        Drawing(withInlet: false);
        var result = Run(Project());
        result.Success.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "import.noInlet");
        File.ReadAllText(result.Files.Single()).ShouldContain("import.noInlet");
    }

    [Fact]
    public void Hydrant_set_for_one_hydrant_follows_the_catalog_template()
    {
        var s = new WaterAuto.Calc.Hydraulics.HydrantSettings("II", "В", "С0", 40, new Core.Length(12), new Core.Length(3.3), 65, 19, 20, new Core.Length(1.35), new Core.Length(15), 80);
        var items = HydrantEquipment.Items(HydrantEquipment.PathFor(RepoPaths.Norms), s);
        items.ShouldContain(i => i.Name == "Клапан пожарный запорный угловой DN65" && i.Quantity == 1);
        items.ShouldContain(i => i.Name.Contains("РС-70", StringComparison.Ordinal) && i.Name.Contains("19 мм", StringComparison.Ordinal));
        items.ShouldContain(i => i.Name.Contains("длина 20 м", StringComparison.Ordinal));
        items.ShouldAllBe(i => i.Group == Core.TakeoffGroup.Hydrants && i.Note!.StartsWith("на один пожарный кран", StringComparison.Ordinal));

        // Трубопроводы к кранам не трассируются — об этом сказано у клапана, чтобы монтажник не искал их в разделе труб.
        items[0].Note!.ShouldContain("не учтена");
    }

    [Fact]
    public void Diaphragm_is_added_when_the_valve_pressure_is_too_high()
    {
        var s = new WaterAuto.Calc.Hydraulics.HydrantSettings("II", "В", "С0", 40, new Core.Length(12), new Core.Length(3.3), 65, 19, 20, new Core.Length(1.35), new Core.Length(15), 80);
        HydrantEquipment.Items(HydrantEquipment.PathFor(RepoPaths.Norms), s, 3).ShouldNotContain(i => i.Name.StartsWith("Диафрагма", StringComparison.Ordinal));
        var reducer = HydrantEquipment.Items(HydrantEquipment.PathFor(RepoPaths.Norms), s, 3, reducer: true).Single(i => i.Name.StartsWith("Диафрагма", StringComparison.Ordinal));
        reducer.Name.ShouldBe("Диафрагма дроссельная DN65");
        reducer.Quantity.ShouldBe(3);
        reducer.Note.ShouldBe("диаметр отверстия — по расчёту");
    }

    [Fact]
    public void Control_unit_set_is_counted_per_section_and_summed()
    {
        var items = EquipmentSet.Items(EquipmentSet.PathFor(RepoPaths.Norms, EquipmentSet.ControlUnitFile), Core.TakeoffGroup.ControlUnit, [125, 125]);
        items.Single(i => i.Name == "Узел управления спринклерный водозаполненный DN125").Quantity.ShouldBe(2);
        items.Single(i => i.Name.StartsWith("Манометр", StringComparison.Ordinal)).Quantity.ShouldBe(4);
        items.ShouldAllBe(i => i.Group == Core.TakeoffGroup.ControlUnit);
    }

    [Fact]
    public void Specification_lists_flushing_coating_and_station_spec_carries_control_units()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        using var spec = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.SpecificationDocxFile, StringComparison.Ordinal)), false);
        var rows = spec.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>()
            .Select(r => r.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().ElementAtOrDefault(1)?.InnerText ?? "")
            .ToList();
        rows.ShouldContain("Промывочные устройства");
        rows.ShouldContain(r => r.StartsWith("Кран шаровой DN", StringComparison.Ordinal));
        rows.ShouldContain(r => r.StartsWith("Покрытие антикоррозионное", StringComparison.Ordinal));
        rows.ShouldNotContain(r => r.StartsWith("Узел управления спринклерный", StringComparison.Ordinal)); // в ПТ2.СО

        // Насосная — отдельной спецификацией ПТ2.СО со сквозными позициями и фасонными частями обвязки.
        rows.ShouldNotContain(r => r.StartsWith("Агрегат насосный", StringComparison.Ordinal));
        using var pns = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.PumpStationSpecFile, StringComparison.Ordinal)), false);
        var pnsRows = pns.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>()
            .Select(r => r.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Select(c => c.InnerText).ToList()).Where(c => c.Count == 9).ToList();
        pnsRows.ShouldContain(c => c[1] == "Фасонные части обвязки");
        pnsRows.ShouldContain(c => c[1] == "Узел управления спринклерный водозаполненный DN125" && c[6] == "1");
        // В проекте нет помещения насосной и нет вводов на слое ПТ_НС_ВВОД: задвижки «на каждом вводе» не выдумываются,
        // выпуск сопровождается предупреждением (аудит PS-6).
        pnsRows.ShouldNotContain(c => c[1].StartsWith("Задвижка DN", StringComparison.Ordinal) && c[8].Contains("на каждом вводе", StringComparison.Ordinal));
        result.Issues.ShouldContain(i => i.Code == "pumpRoom.missing");
        pnsRows.ShouldContain(c => c[1].StartsWith("Тройник DN", StringComparison.Ordinal));
        var numbers = pnsRows.Select(c => c[0]).Where(s => int.TryParse(s, out _)).Select(int.Parse).ToList();
        numbers.ShouldBe(Enumerable.Range(1, numbers.Count).ToList()); // сквозная нумерация

        // Крепления — по видам: все хомуты, затем шпильки (ГОСТ 21.110, п. 4.5).
        rows.FindLastIndex(r => r.StartsWith("Хомут", StringComparison.Ordinal)).ShouldBeLessThan(rows.FindIndex(r => r.StartsWith("Шпилька", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ring_project_reports_both_variants_in_the_note()
    {
        Drawing();
        var result = Run(Project(ring: true));
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        result.Design!.Ring.ShouldNotBeNull();
        using var note = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.NoteFile, StringComparison.Ordinal)), false);
        note.MainDocumentPart!.Document.Body!.InnerText.ShouldContain("Б.1.2.16.1");
    }

    [Fact]
    public void Dimension_chains_tie_the_lowest_row_and_left_column_to_the_walls()
    {
        var contour = new Core.Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new Core.FloorPlan([new Core.Room("R1", "Автостоянка", contour)], [], [], [], [], [new Core.Inlet("I1", new(500, 500), null)]);
        var norms = WaterAuto.Norms.JsonNormsProvider.Load(RepoPaths.Norms);
        var rules = WaterAuto.Calc.Layout.LayoutRules.Resolve(norms, "2", Core.ConstructionClass.K0).Rules!;
        var layout = WaterAuto.Calc.Layout.SprinklerLayouter.Layout(plan, rules);
        var chains = PlanDimensions.Build(plan, layout);
        chains.Count.ShouldBe(2);
        chains[0].Points[0].XMm.ShouldBe(0, 1e-6);
        chains[0].Points[^1].XMm.ShouldBe(30000, 1e-6);
        chains[1].Points[0].YMm.ShouldBe(0, 1e-6);
        chains[1].Points[^1].YMm.ShouldBe(20000, 1e-6);
        chains[0].Points.Zip(chains[0].Points.Skip(1), (a, b) => b.XMm - a.XMm).ShouldAllBe(d => d > 0);
    }

    [Fact]
    public void Axes_come_from_the_column_grid_and_carry_ties()
    {
        // Колонны 400 × 400 в узлах сетки X = 5000, 15000, 25000 и Y = 5000, 15000.
        var contour = new Core.Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        Core.Column Col(string id, double x, double y) => new(id, new Core.Polygon([new(x - 200, y - 200), new(x + 200, y - 200), new(x + 200, y + 200), new(x - 200, y + 200)]));
        var columns = new[] { 5000.0, 15000, 25000 }.SelectMany(x => new[] { 5000.0, 15000 }.Select(y => Col($"C{x}{y}", x, y))).ToList();
        var plan = new Core.FloorPlan([new Core.Room("R1", "Автостоянка", contour)], columns, [], [], [], [new Core.Inlet("I1", new(500, 500), null)]);
        var axes = PlanDimensions.Axes(plan);
        axes.Where(a => a.Vertical).Select(a => (a.Name, a.Position)).ShouldBe([("1", 5000), ("2", 15000), ("3", 25000)]);
        axes.Where(a => !a.Vertical).Select(a => (a.Name, a.Position)).ShouldBe([("А", 5000), ("Б", 15000)]);

        var norms = WaterAuto.Norms.JsonNormsProvider.Load(RepoPaths.Norms);
        var rules = WaterAuto.Calc.Layout.LayoutRules.Resolve(norms, "2", Core.ConstructionClass.K0).Rules!;
        var layout = WaterAuto.Calc.Layout.SprinklerLayouter.Layout(plan, rules);
        var chains = PlanDimensions.Ties(plan, layout, [], [new(10000, 3000)]);

        // Цепочка у левой стены содержит оси А и Б; у каждого рядка — своя цепочка шагов; ввод и ПК привязаны к осям.
        chains.ShouldContain(c => c.Points.All(p => p.XMm == 0) && c.Points.Any(p => p.YMm == 5000) && c.Points.Any(p => p.YMm == 15000));
        var rows = layout.Sprinklers.Select(s => Math.Round(s.Location.YMm)).Distinct().Count();
        chains.Count(c => c.Offset == -300).ShouldBe(rows - 1); // нижний — в цепочке до стен
        chains.ShouldContain(c => c.Points.Count == 2 && c.Points[0] == new Core.PlanPoint(10000, 3000) && c.Points[1].YMm == 5000);
        chains.ShouldContain(c => c.Points.Count == 2 && c.Points[0] == new Core.PlanPoint(500, 500) && c.Points[1].XMm == 5000);
    }

    [Fact]
    public void General_data_lists_the_spec_standards_with_names()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(string.Join("\n", result.Issues.Select(i => i.Message)));
        using var doc = WordprocessingDocument.Open(result.Files.Single(f => f.EndsWith(ProjectPipeline.GeneralDataFile, StringComparison.Ordinal)), false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        text.ShouldContain("Ведомость ссылочных и прилагаемых документов");
        text.ShouldContain("ГОСТ 3262-75");
        text.ShouldContain("Трубы стальные водогазопроводные. Технические условия");
        text.ShouldContain("Расход пожарного насоса");

        // Спецификация — в прилагаемых документах (ГОСТ 21.110, п. 4.13); составное обозначение покрытия — двумя строками.
        text.ShouldContain("Прилагаемые документы");
        text.ShouldContain("-ПТ.СОСпецификация оборудования, изделий и материалов");
        text.ShouldContain("ГОСТ 25129-2020Грунтовка ГФ-021");
        text.ShouldNotContain("ГОСТ 25129-2020, ГОСТ 6465-76");
    }
}
