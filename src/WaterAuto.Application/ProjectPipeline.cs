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
        var routing = PipeRouter.Route(plan, layout, norms, rules, Pressure.Zero);
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

        var settings = new DesignSettings(
            design.PipeSeriesStandard,
            new KFactor(design.SprinklerK),
            new Length(design.DictatingAboveAxis),
            new Length(design.SupplyLength),
            design.SupplyDn,
            new Pressure(design.InletPressure),
            design.DesignVelocity);
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
                h.IsParking);
            var demand = HydrantDemand.Resolve(norms, hs);
            issues.AddRange(demand.Issues);
            hydrants = demand.Demand is { } dv ? new HydrantInput(hs, dv) : null;
        }

        List<DesignResult> designs;
        SprinklerSelectionResult? selection = null;
        var sprinklerK = design.SprinklerK;
        var sprinklerModel = design.SprinklerModel;
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

        var takeoff = MaterialTakeoffBuilder.Build(routes, layout.Sprinklers.Count, norms, design.PipeSeriesStandard, new SprinklerSpec(
            string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.SprinklerName}, K = {sprinklerK:0.###}"),
            sprinklerModel));
        issues.AddRange(takeoff.Issues.Where(i => !issues.Contains(i)));

        var outIssues = issues.ToList();
        var summary = Summary(worst);
        Write(outDir, PlanFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues, summary: summary));
        Write(outDir, PlanDxfFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues, summary: summary));
        Write(outDir, AxonometryFile, files, p => AxonometryDxfWriter.Write(p, routes, new Length(design.PipeElevation)));
        Write(outDir, SpecificationFile, files, p => SpecificationXlsx.Write(p, takeoff, new SpecificationHeader($"{project.Name}. Спецификация оборудования, изделий и материалов")));

        var literature = registryPath is not null && File.Exists(registryPath)
            ? LiteratureRegistry.Parse(new StringReader(File.ReadAllText(registryPath)))
            : [];
        var ids = project.Literature.Count > 0
            ? project.Literature
            : literature.Where(e => e.Status.StartsWith("действует", StringComparison.Ordinal)).Select(e => e.Id).ToList();
        var t = project.Title;
        var note = ExplanatoryNoteComposer.Compose(
            new NoteInputs(
                new NoteTitle(t.Organization, t.Subject, t.Document, t.Author, t.Supervisor, t.CityYear),
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
                selection),
            norms);
        Write(outDir, NoteFile, files, p => NoteDocx.Write(p, note));
        files.Add(WriteIssues(outDir, issues));
        return new PipelineResult(true, files, issues, worst);
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
