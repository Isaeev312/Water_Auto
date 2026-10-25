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
        if (design.SprinklerK <= 0)
        {
            issues.Add(new Issue("project.sprinklerK", IssueLevel.Error, "В проекте не задан K оросителя (design.sprinklerK) — по технической документации изделия."));
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

        var layout = SprinklerLayouter.Layout(plan, rules, design.FloorElevation is { } floor ? new Length(floor) : null);
        issues.AddRange(layout.Issues);

        // Столбец табл. Б.4 — «до порога давления» (большие диаметры); окончательно диаметры подбирает Б.5 в расчёте.
        var routing = PipeRouter.Route(plan, layout, norms, rules, Pressure.Zero);
        issues.AddRange(routing.Issues);
        if (routing.Routes.Count == 0)
        {
            issues.Add(new Issue("project.noRoute", IssueLevel.Error, "Ни одно помещение не удалось соединить трубопроводом с вводом — расчёт не выполняется."));
            return Stop();
        }

        var settings = new DesignSettings(
            design.PipeStandard,
            new KFactor(design.SprinklerK),
            new Length(design.DictatingAboveAxis),
            new Length(design.SupplyLength),
            design.SupplyDn,
            new Pressure(design.InletPressure),
            design.DesignVelocity);
        var designs = routing.Routes.Select(r => DesignCalculation.Run(r, norms, rules, settings)).ToList();
        var worst = designs.MaxBy(d => d.PumpPressure.Mpa)!;
        issues.AddRange(designs.SelectMany(d => d.Issues).Distinct());
        var routes = designs.Select(d => d.Route).ToList();

        var takeoff = MaterialTakeoffBuilder.Build(routes, layout.Sprinklers.Count, norms, design.PipeStandard, new SprinklerSpec(
            string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{design.SprinklerName}, K = {design.SprinklerK:0.###}"),
            design.SprinklerModel));
        issues.AddRange(takeoff.Issues.Where(i => !issues.Contains(i)));

        var outIssues = issues.ToList();
        Write(outDir, PlanFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues));
        Write(outDir, PlanDxfFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues));
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
                ids),
            norms);
        Write(outDir, NoteFile, files, p => NoteDocx.Write(p, note));
        files.Add(WriteIssues(outDir, issues));
        return new PipelineResult(true, files, issues, worst);
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
