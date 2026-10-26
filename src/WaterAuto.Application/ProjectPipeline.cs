using System.Globalization;
using System.Text;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Export;
using WaterAuto.Import;
using WaterAuto.Norms;
using WaterAuto.Reports;

namespace WaterAuto.Application;

public sealed record PipelineResult(bool Success, IReadOnlyList<string> Files, IReadOnlyList<Issue> Issues, DesignResult? Design);

/// <summary>
/// Сквозной расчёт по проекту (README §4): импорт чертежа → раскладка → трасса → проектный расчёт → ведомость
/// → выпуск: план (DWG и DXF), аксонометрия (DXF), спецификация (xlsx), пояснительная записка (docx), замечания (txt).
/// Если помещений с трассой несколько, в записку идёт расчёт с наибольшим требуемым давлением насоса.
/// Ошибка импорта или исходных данных останавливает расчёт; замечания всегда пишутся в issues.txt.
/// </summary>
public static class ProjectPipeline
{
    public const string PlanFile = "plan.dwg";
    public const string PlanDxfFile = "plan.dxf";
    public const string AxonometryFile = "axonometry.dxf";
    public const string SpecificationFile = "specification.xlsx";
    public const string NoteFile = "note.docx";
    public const string GeneralDataFile = "general_data.docx";
    public const string SpecificationDocxFile = "specification.docx";
    public const string PumpSchemeFile = "pump_scheme.dxf";
    public const string PumpStationSpecFile = "specification_pns.docx";
    public const string PumpStationSpecXlsxFile = "specification_pns.xlsx";
    public const string IssuesFile = "issues.txt";

    public static PipelineResult Run(string projectPath, string outDir, string normsDir, string? registryPath)
    {
        Directory.CreateDirectory(outDir);
        var issues = new List<Issue>();
        var files = new List<string>();
        PipelineResult Stop()
        {
            files.Add(WriteIssues(outDir, issues));
            return new PipelineResult(false, files, issues, null);
        }

        var project = ProjectFile.Load(projectPath);
        var design = project.Design;
        var catalogPath = SprinklerCatalog.PathFor(normsDir);
        if (design.SprinklerK <= 0 && !File.Exists(catalogPath))
        {
            issues.Add(new Issue("project.sprinklerK", IssueLevel.Error, "В проекте не задан K оросителя (design.sprinklerK), а каталога оросителей для автоподбора нет (data/catalog/sprinklers.json)."));
            return Stop();
        }

        var drawing = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, project.Drawing));
        var import = FloorPlanImporter.Import(drawing);
        issues.AddRange(import.Issues);
        if (import.Plan is not { } plan)
        {
            return Stop();
        }

        var norms = JsonNormsProvider.Load(normsDir);
        var resolved = LayoutRules.Resolve(norms, design.RoomGroup, design.ConstructionClass, design.MaxSpacing is { } m ? new Length(m) : null);
        issues.AddRange(resolved.Issues);
        if (resolved.Rules is not { } rules)
        {
            return Stop();
        }

        var fire = FireParameters.Resolve(norms, design.RoomGroup, design.RoomHeight is { } rh ? new Length(rh) : null, design.StorageHeight is { } sh ? new Length(sh) : null);
        issues.AddRange(fire.Issues);
        if (fire.Parameters is not { } parameters)
        {
            return Stop();
        }

        var layout = SprinklerLayouter.Layout(plan, rules, design.FloorElevation is { } floor ? new Length(floor) : null);
        issues.AddRange(layout.Issues);

        // Столбец табл. Б.4 — «до порога давления» (большие диаметры); окончательно диаметры подбирает Б.5 в расчёте.
        var routing = PipeRouter.Route(plan, layout, norms, rules, Pressure.Zero, design.Ring);
        issues.AddRange(routing.Issues);
        if (design.MaxSprinklersPerBranch is { } maxBranch)
        {
            issues.AddRange(routing.Routes.SelectMany(r => BranchLimit.Check(r, maxBranch)));
        }
        if (routing.Routes.Count == 0)
        {
            issues.Add(new Issue("project.noRoute", IssueLevel.Error, "Ни одно помещение не удалось соединить трубопроводом с вводом — расчёт не выполняется."));
            return Stop();
        }

        var pipeSizes = PipeSizes(normsDir);
        var settings = new DesignSettings(
            design.PipeSeriesStandard,
            new KFactor(design.SprinklerK),
            new Length(design.DictatingAboveAxis),
            new Length(design.SupplyLength),
            design.SupplyDn,
            new Pressure(design.InletPressure),
            design.DesignVelocity,
            design.RingValvesClosed,
            pipeSizes);
        HydrantInput? hydrants = null;
        if (design.Hydrants is { } h)
        {
            var hs = new HydrantSettings(
                h.FireResistance,
                h.Category,
                h.HazardClass,
                h.Volume,
                new Length(h.BuildingHeight),
                new Length(h.RoomHeight ?? design.RoomHeight ?? 0),
                h.ValveDn,
                h.Nozzle,
                h.HoseLength,
                new Length(h.AboveAxis),
                new Length(h.PipeLength),
                h.PipeDn,
                h.IsParking,
                h.Zh1Row,
                h.ExtraJet);
            var demand = HydrantDemand.Resolve(norms, hs);
            issues.AddRange(demand.Issues);
            hydrants = demand.Demand is { } dv ? new HydrantInput(hs, dv) : null;
        }

        List<DesignResult> designs;
        SprinklerSelectionResult? selection = null;
        var sprinklerK = design.SprinklerK;
        var sprinklerModel = design.SprinklerModel;
        string? sprinklerSupplier = null;
        IReadOnlyList<int>? sprinklerTemps = null;
        if (sprinklerK > 0)
        {
            designs = routing.Routes.Select(r => DesignCalculation.Run(r, norms, rules, settings, parameters, hydrants)).ToList();
        }
        else
        {
            // Автоподбор: модели каталога с нужным положением розетки, полный расчёт для каждой (SprinklerSelection).
            var catalog = SprinklerCatalog.ForWater(SprinklerCatalog.Load(catalogPath), design.SprinklerDeflector).ToList();
            selection = SprinklerSelection.Select(routing.Routes, norms, rules, settings, parameters, hydrants, catalog.Select(c => c.Candidate).ToList());
            issues.AddRange(selection.Issues);
            if (selection is not { Chosen: { } chosen, Designs: { } chosenDesigns })
            {
                return Stop();
            }

            var entry = catalog.First(c => c.Candidate.Id == chosen.Id);
            sprinklerK = chosen.K;
            sprinklerModel = entry.Designation.Length > 0 ? $"{chosen.Model} ({entry.Designation})" : chosen.Model;
            sprinklerSupplier = chosen.Maker;
            sprinklerTemps = entry.Temperatures;
            settings = settings with { SprinklerK = new KFactor(chosen.K) };
            designs = [.. chosenDesigns];
            issues.Add(new Issue(
                "sprinkler.chosen",
                IssueLevel.Info,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Подобран ороситель {chosen.Maker} {chosen.Model}, K = {chosen.K:0.###}: из {selection.Rows.Count} моделей подходят {selection.Rows.Count(r => r.Valid)}, выбрана с наименьшим Q·P насоса.{(entry.Verified ? "" : " Данные каталога не сверены с паспортом изделия.")}")));
        }

        var worst = designs.MaxBy(d => d.PumpPressure.Mpa)!;
        issues.AddRange(designs.SelectMany(d => d.Issues).Distinct());
        var routes = designs.Select(d => d.Route).ToList();

        // Номинальная температура срабатывания (п. 6.2.16, табл. 6.4) и цвет колбы (ГОСТ Р 51043, табл. 2).
        var temperature = SprinklerTemperature.Choose(norms, design.MaxAmbientTemperature, sprinklerTemps);
        issues.AddRange(temperature.Issues);
        var temperatureText = temperature.Choice is { } tc ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $", {tc.Nominal} °С (колба — {Feminine(tc.Color)})") : "";

        var materials = Materials(normsDir);
        var takeoff = MaterialTakeoffBuilder.Build(routes, layout.Sprinklers.Count, norms, design.PipeSeriesStandard, new SprinklerSpec(
            string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.SprinklerName}, K = {sprinklerK:0.###}{temperatureText}"),
            sprinklerModel,
            sprinklerSupplier,
            sprinklerSupplier is null ? null : materials.AnalogNote),
            design.FittingStandards.Count > 0 ? design.FittingStandards : null,
            materials.SteelDensity,
            pipeSizes,
            materials.Coating);

        // Насосная установка: агрегаты, обвязка по скорости, патрубки для пожарной техники (пп. 6.10.3, 6.7.1.37, 6.10.17).
        var station = PumpStation.Design(norms, worst.PumpFlow, worst.PumpHead, new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes), new Pressure(design.InletPressure));
        issues.AddRange(station.Issues);
        var stationSet = EquipmentSet.PathFor(normsDir, EquipmentSet.PumpStationFile);
        var stationTakeoff = new MaterialTakeoff([], []);
        if (File.Exists(stationSet))
        {
            // Отдельная спецификация насосной (комплект ПТ2): оборудование, арматура и приборы, фасонные части обвязки.
            var ruPs = CultureInfo.GetCultureInfo("ru-RU");
            stationTakeoff = stationTakeoff with
            {
                Items = [.. EquipmentSet.Items(
                    stationSet,
                    TakeoffGroup.PumpStation,
                    new Dictionary<string, string>
                    {
                        ["q"] = (worst.PumpFlow.LitersPerSecond * 3.6).ToString("0.0", ruPs),
                        ["h"] = worst.PumpHead.Meters.ToString("0.0", ruPs),
                        ["dnS"] = station.Suction.Pipe.Dn.ToString(CultureInfo.InvariantCulture),
                        ["dnD"] = station.Discharge.Pipe.Dn.ToString(CultureInfo.InvariantCulture),
                        ["dnC"] = design.SupplyDn.ToString(CultureInfo.InvariantCulture),
                        ["tank"] = worst.JockeyTankMinLiters.ToString("0", CultureInfo.InvariantCulture),
                        ["truckDn"] = station.TruckInletDn.ToString(CultureInfo.InvariantCulture),
                    },
                    new Dictionary<string, int> { ["pumps"] = station.WorkingPumps + station.StandbyPumps, ["truckInlets"] = station.TruckInlets })],
            };
        }

        // Номера позиций спецификации насосной — на схеме, по роли элемента (сквозная нумерация).
        var schemePositions = SpecificationXlsx.Positions(stationTakeoff, continuous: true)
            .Where(p => p.Item.Role is not null)
            .ToDictionary(p => p.Item.Role!, p => p.Position);

        // Узел управления — на каждую секцию (ввод), DN подводящего; промывочные устройства — п. 6.7.1.15.
        var controlUnit = EquipmentSet.PathFor(normsDir, EquipmentSet.ControlUnitFile);
        if (File.Exists(controlUnit))
        {
            takeoff = takeoff with { Items = [.. takeoff.Items, .. EquipmentSet.Items(controlUnit, TakeoffGroup.ControlUnit, routes.Select(r => r.InletId).Distinct().Select(_ => design.SupplyDn))] };
        }

        var flushingSet = EquipmentSet.PathFor(normsDir, EquipmentSet.FlushingFile);
        if (File.Exists(flushingSet) && FlushingPoints.Find(routes, norms) is { Count: > 0 } flushing)
        {
            takeoff = takeoff with { Items = [.. takeoff.Items, .. EquipmentSet.Items(flushingSet, TakeoffGroup.Valves, flushing.Select(f => f.Dn))] };
        }

        issues.AddRange(takeoff.Issues.Where(i => !issues.Contains(i)));
        // Расстановка ПК (п. 7.14): радиус — из проекта или длина рукава + радиус компактной струи (В-22).
        var hydrantPoints = new List<PlanPoint>();
        Length? hydrantReach = null;
        if (hydrants is not null && design.Hydrants is { Place: true } hp)
        {
            hydrantReach = new Length(hp.Reach ?? (hp.HoseLength + hydrants.Demand.TableJetHeight.Meters));
            foreach (var room in plan.Rooms)
            {
                var placed = HydrantPlacement.Place(plan, room, hydrantReach.Value, hydrants.Demand.Jets);
                hydrantPoints.AddRange(placed.Points);
                issues.AddRange(placed.Issues);
            }
        }

        var hydrantSet = HydrantEquipment.PathFor(normsDir);
        if (hydrants is not null && File.Exists(hydrantSet))
        {
            takeoff = takeoff with { Items = [.. takeoff.Items, .. HydrantEquipment.Items(hydrantSet, hydrants.Settings, Math.Max(1, hydrantPoints.Count), issues.Any(i => i.Code == "vpv.diaphragm"))] };
        }

        // Опоры трубопроводов (табл. 6.7.1, пп. 6.7.1.26, 6.7.1.31) и их крепёж по шаблону каталога.
        var supports = PipeSupports.Build(routes, norms, design.PipeSeriesStandard, pipeSizes);
        issues.AddRange(supports.Issues.Where(i => !issues.Contains(i)));
        var supportSet = SupportEquipment.PathFor(normsDir);
        if (File.Exists(supportSet))
        {
            takeoff = takeoff with { Items = [.. takeoff.Items, .. SupportEquipment.Items(supportSet, supports, design.HangerRodLength)] };
        }

        var outIssues = issues.ToList();
        var summary = Summary(worst);
        // Привязки: к стенам (нижний ряд, левый столбец), к осям по колоннам, шаги в рядках, трубопроводы, ввод и ПК.
        var dimensions = (IReadOnlyList<PlanDimensionChain>)[.. PlanDimensions.Build(plan, layout), .. PlanDimensions.Ties(plan, layout, routes, hydrantPoints)];
        var axes = PlanDimensions.Axes(plan);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var inletNotes = plan.Inlets.Select(i => new PlanInletNote(i.Location, [
            $"Ввод DN{design.SupplyDn}",
            "отм. " + design.PipeElevation.ToString("+0.000;−0.000;0.000", ru),
            design.InletReference,
        ])).ToList();

        // plan.dwg — лист: трубы поверх исходного чертежа этажа, без замечаний; plan.dxf — слои программы с замечаниями (для проверки и вставки).
        Write(outDir, PlanFile, files, p => PlanDxfWriter.Write(p, layout, routes, [], summary: summary, inlets: inletNotes, basePath: drawing, supports: supports.Points, dimensions: dimensions, hydrants: hydrantPoints, axes: axes, style: new PlanDxfStyle(Font: project.Font)));
        Write(outDir, PlanDxfFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues, summary: summary, inlets: inletNotes, supports: supports.Points, dimensions: dimensions, hydrants: hydrantPoints, axes: axes, style: new PlanDxfStyle(Font: project.Font)));
        Write(outDir, AxonometryFile, files, p => AxonometryDxfWriter.Write(
            p,
            routes,
            new Length(design.PipeElevation),
            new AxonometryStyle(Font: project.Font, SprinklersUp: !design.SprinklerDeflector.Equals("down", StringComparison.OrdinalIgnoreCase), InletNote: design.InletReference),
            SheetStamp(project, "Аксонометр", "Аксонометрическая схема трубопроводов"),
            new AxonometryCalc(worst.Route.RoomId, worst.Route.InletId, CalcPoints.Labels(worst), [.. worst.Path.Select(x => x.Id)], worst.ZoneSprinklers)));
        // Принципиальная схема насосной станции (лист основного комплекта).
        var fromTank = design.InletPressure < (norms.Find("sp485.p6_9_12.minNetworkPressure")?.Values["pressure"] ?? 0);
        Write(outDir, PumpSchemeFile, files, p => PumpSchemeDxfWriter.Write(
            p,
            new PumpSchemeData(
                station.WorkingPumps + station.StandbyPumps,
                station.Suction.Pipe.Dn,
                station.Discharge.Pipe.Dn,
                design.SupplyDn,
                station.TruckInlets,
                station.TruckInletDn,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Н1, Н2 — агрегаты пожарные: Q = {worst.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч, H = {worst.PumpHead.Meters:0.0} м (рабочий и резервный)"),
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"НЖ — жокей-насос, бак не менее {worst.JockeyTankMinLiters:0} л"),
                fromTank ? "Из пожарного резервуара" : "Из наружной сети",
                [
                    "Резервный агрегат включается автоматически при невыходе рабочего на режим или его отключении (п. 6.10.3 СП 485).",
                    "Марки агрегатов, жокей-насоса и шкафа управления — по каталогу производителя по требуемым Q и H; DN обвязки — по скорости воды (п. 6.7.1.37).",
                    "Затворы на линиях агрегатов и перед узлом управления — с контролем положения; приборы — через трёхходовые краны (пп. 6.8.7, 6.8.10).",
                    "Оборудование, кроме оросителей, приборов и трубопроводов, окрашивается в красный цвет (п. 6.8.11).",
                    "Номера позиций — по спецификации насосной станции ПТ2.СО; фланцы и крепёж — по месту (ГОСТ 21.601, п. 9.4).",
                ],
                schemePositions),
            SheetStamp(project, "Принципиальная", "Принципиальная схема насосной станции пожаротушения"),
            project.Font));
        if (stationTakeoff.Items.Count > 0)
        {
            Write(outDir, PumpStationSpecFile, files, p => SpecificationDocx.Write(p, stationTakeoff, Stamp(project, "ПТ2.СО", "Спецификация оборудования, изделий и материалов насосной станции"), project.Font, continuous: true));
            Write(outDir, PumpStationSpecXlsxFile, files, p => SpecificationXlsx.Write(p, stationTakeoff, new SpecificationHeader($"{project.Name}. Насосная станция. Спецификация оборудования, изделий и материалов"), project.Font, continuous: true));
        }

        Write(outDir, SpecificationFile, files, p => SpecificationXlsx.Write(p, takeoff, new SpecificationHeader($"{project.Name}. Спецификация оборудования, изделий и материалов"), project.Font));
        Write(outDir, SpecificationDocxFile, files, p => SpecificationDocx.Write(p, takeoff, Stamp(project, "ПТ.СО", "Спецификация оборудования, изделий и материалов"), project.Font));

        var literature = registryPath is not null && File.Exists(registryPath)
            ? LiteratureRegistry.Parse(new StringReader(File.ReadAllText(registryPath)))
            : [];
        var ids = project.Literature.Count > 0
            ? project.Literature
            : literature.Where(e => e.Status.StartsWith("действует", StringComparison.Ordinal)).Select(e => e.Id).ToList();
        var t = project.Title;
        var note = ExplanatoryNoteComposer.Compose(
            new NoteInputs(
                new NoteTitle(t.Organization, t.Subject, t.Document, t.Author, t.Supervisor, t.CityYear, project.Name),
                project.Name,
                design.RoomGroup,
                design.ConstructionClass,
                plan,
                rules,
                layout,
                routing with { Routes = routes },
                takeoff,
                worst,
                settings,
                literature,
                ids,
                selection,
                $"Соединения стальных трубопроводов — на сварке по {design.WeldStandard}; фасонные части — {string.Join(", ", design.FittingStandards.Select(f => $"{Plural(f.Key)} по {f.Value}"))}.",
                supports,
                temperature.Choice,
                design.MaxAmbientTemperature,
                hydrantPoints.Count,
                hydrantReach,
                station),
            norms);
        Write(outDir, NoteFile, files, p => NoteDocx.Write(p, note, Stamp(project, "ПЗ", "Пояснительная записка"), project.Font));

        // Лист «Общие данные» (ГОСТ Р 21.101): ведомости, основные показатели, общие указания.
        var general = GeneralDataComposer.Compose(
            new GeneralDataInputs(project.Name, project.Sheets, plan, layout, worst, takeoff, temperature.Choice, hydrantPoints.Count, design.PipeSeriesStandard, design.WeldStandard, design.FittingStandards, Stamp(project, "ПТ.СО", "").Designation, stationTakeoff.Items.Count > 0 ? Stamp(project, "ПТ2.СО", "").Designation : null),
            norms,
            GeneralDataComposer.DocumentsPath(normsDir));
        Write(outDir, GeneralDataFile, files, p => GeneralDataDocx.Write(p, general, Stamp(project, "ПТ", "Общие данные") with { SheetCount = project.Sheets.Count }, project.Font));
        files.Add(WriteIssues(outDir, issues));
        return new PipelineResult(true, files, issues, worst);
    }

    // «Отвод» → «отводы» для перечня стандартов фасонных частей в ПЗ.
    // Основная надпись (ГОСТ Р 21.101) из раздела title проекта: обозначение — шифр и марка документа.
    private static TitleBlock Stamp(ProjectFile project, string mark, string name)
    {
        var t = project.Title;
        IReadOnlyList<TitleSignature> signatures = t.Signatures.Count > 0
            ? [.. t.Signatures.Select(s => new TitleSignature(s.Role, s.Name))]
            : [new("Разраб.", t.Author), new("Руковод.", t.Supervisor), new("Н. контр.", "")];
        return new TitleBlock($"{t.Code}-{mark}", project.Name, t.Building, name, t.Stage, t.Organization, signatures);
    }

    // Основная надпись листа основного комплекта: номер и наименование — из ведомости чертежей (по началу наименования).
    private static TitleBlock SheetStamp(ProjectFile project, string prefix, string fallbackName)
    {
        var sheet = project.Sheets.FirstOrDefault(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return Stamp(project, "ПТ", sheet?.Name ?? fallbackName) with { Sheet = sheet?.Sheet };
    }

    private static string Plural(string noun) => GeneralDataComposer.Plural(noun);

    // Цвет колбы в данных — прилагательное мужского рода («красный»); в спецификации — «колба красная».
    private static string Feminine(string color) => color.EndsWith("ый", StringComparison.Ordinal) || color.EndsWith("ий", StringComparison.Ordinal) || color.EndsWith("ой", StringComparison.Ordinal)
        ? color[..^2] + (color.EndsWith("ий", StringComparison.Ordinal) && !color.EndsWith("кий", StringComparison.Ordinal) ? "яя" : "ая")
        : color;

    // Принятые размеры труб из data/catalog/pipe_sizes.json (наружный диаметр × стенка по DN).
    private static List<PipeSize> PipeSizes(string normsDir)
    {
        var path = Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "pipe_sizes.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("sizes").EnumerateArray()
            .Select(s => new PipeSize(s.GetProperty("standard").GetString()!, s.GetProperty("dn").GetInt32(), s.GetProperty("outerDiameter").GetDouble(), s.GetProperty("wall").GetDouble()))
            .ToList();
    }

    // Плотность стали из data/catalog/materials.json — для теоретической массы труб в спецификации.
    // data/catalog/materials.json: плотность стали (масса труб), примечание к марочным изделиям, покрытие труб.
    private static (double? SteelDensity, string? AnalogNote, CoatingSpec? Coating) Materials(string normsDir)
    {
        var path = Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "materials.json");
        if (!File.Exists(path))
        {
            return (null, null, null);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var coating = root.TryGetProperty("coating", out var c)
            ? new CoatingSpec(c.GetProperty("name").GetString()!, c.TryGetProperty("designation", out var d) ? d.GetString() : null)
            : null;
        return (root.GetProperty("steelDensity").GetDouble(), root.TryGetProperty("analogNote", out var a) ? a.GetString() : null, coating);
    }

    // Табличка расчёта и диктующая зона на плане (docs/foreign_drawings_ideas.md, приёмы 1 и 2).
    private static PlanCalcSummary Summary(DesignResult d)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        string F(double v, string f) => v.ToString(f, ru);
        var p = d.Parameters;
        return new PlanCalcSummary(
            "СВОДКА ГИДРАВЛИЧЕСКОГО РАСЧЁТА (СП 485, прил. Б)",
            [
                ("Группа помещений", $"{p.Group}; параметры — {p.Source}"),
                ("Интенсивность орошения i", $"{F(p.Intensity, "0.###")} л/(с·м²)"),
                ("Минимальная площадь S", $"{F(p.MinArea, "0.##")} м²; в зоне N = {d.ZoneSprinklers.Count} оросит."),
                ("Диктующий ороситель", $"q = {F(d.DictatingFlow.LitersPerSecond, "0.00")} л/с, p = {F(d.DictatingPressure.Mpa, "0.000")} МПа (Б.1)"),
                ("Расчётный расход Q_р", $"{F(d.DesignFlow.LitersPerSecond, "0.00")} л/с (Б.23)"),
                ("Нормативный расход Q_н", $"{F(d.NormativeFlow.LitersPerSecond, "0.##")} л/с"),
                ("Расход насоса Q_нас", $"{F(d.PumpFlow.LitersPerSecond, "0.00")} л/с, {F(d.PumpFlow.LitersPerSecond * 3.6, "0.0")} м³/ч ({d.PumpFlowClause})"),
                .. (d.Hydrants is { } h
                    ? [("ВПВ (СН КР 40-04:2025)", $"{h.Demand.Jets} × {F(h.JetFlowActual.LitersPerSecond, "0.00")} л/с, H_ПК = {F(h.Demand.Head.Meters, "0.#")} м (Ж.2, Ж.3)")]
                    : Array.Empty<(string, string)>()),
                ("Давление в точке подключения", $"{F(d.InletNodePressure.Mpa, "0.000")} МПа"),
                ("Давление на входе насоса P_вх", $"{F(d.InletNodePressure.Mpa + ((1 + d.LocalLossFactor) * d.SupplyLinearLoss.Mpa) + d.Elevation.Mpa - d.PumpPressure.Mpa, "0.000")} МПа (по ТУ)"),
                ("Требуемое давление насоса P_н", $"{F(d.PumpPressure.Mpa, "0.000")} МПа, H_н = {F(d.PumpHead.Meters, "0.0")} м (Б.30)"),
                ("Наибольшая скорость", $"{F(d.Path.Max(x => x.Velocity), "0.00")} м/с (не более {F(d.VelocityLimit, "0.#")} м/с, п. 6.7.1.37)"),
                ("Запас воды W", $"{F(d.WaterVolume, "0.0")} м³ за {F(d.Duration, "0")} мин"),
            ],
            d.ZoneSprinklers,
            $"ДИКТУЮЩАЯ ЗОНА: S = {F(p.MinArea, "0.##")} м², N = {d.ZoneSprinklers.Count}, Q_р = {F(d.DesignFlow.LitersPerSecond, "0.00")} л/с",
            Math.Max(d.RowSpacing.Millimeters, d.LineSpacing.Millimeters) / 2);
    }

    private static void Write(string dir, string name, List<string> files, Action<string> write)
    {
        var path = Path.Combine(dir, name);
        write(path);
        files.Add(path);
    }

    private static string WriteIssues(string dir, List<Issue> issues)
    {
        var path = Path.Combine(dir, IssuesFile);
        var text = new StringBuilder();
        text.AppendLine(issues.Count == 0 ? "Замечаний нет." : $"Замечаний: {issues.Count}");
        foreach (var i in issues.OrderByDescending(i => i.Level))
        {
            text.Append(CultureInfo.InvariantCulture, $"[{i.Level}] {i.Code}: {i.Message}");
            if (i.Clause is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" ({ExplanatoryNoteComposer.ClauseRef(i.Clause)})");
            }

            if (i.ElementId is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" [{i.ElementId}]");
            }

            if (i.Location is { } at)
            {
                text.Append(CultureInfo.InvariantCulture, $" — {at}");
            }

            text.AppendLine();
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }
}
