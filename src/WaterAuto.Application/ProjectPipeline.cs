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
    public const string PumpRoomPlanFile = "pump_room_plan.dxf";
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

        // Секции — по точкам ввода, к которым присоединены помещения; подпись — имя точки ввода с плана.
        var sections = routes.Select(r => r.InletId).Distinct()
            .Select(id => plan.Inlets.Select((inlet, k) => (inlet, k)).FirstOrDefault(x => x.inlet.Id == id) is { inlet: not null } found
                ? $"точка ввода {found.inlet.Name ?? (found.k + 1).ToString(CultureInfo.InvariantCulture)}"
                : id)
            .ToList();
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
                    new Dictionary<string, int> { ["pumps"] = station.WorkingPumps + station.StandbyPumps, ["truckInlets"] = station.TruckInlets, ["sections"] = sections.Count, ["extraSections"] = sections.Count - 1, ["inlets"] = Math.Max(plan.PumpRooms.Count > 0 ? plan.PumpRooms[0].Inlets.Count : 0, (int)(norms.Find("sp485.p6_10_27.suctionLines")?.Values["minCount"] ?? 1)) })],
            };
        }

        // Подбор комплектной установки по каталогу (Q, H); насосы, прибор управления, обвязка с арматурой и КИП входят в неё —
        // эти строки шаблона заменяются строкой установки.
        var catalogSets = PumpCatalog.Sets(normsDir);
        var roomData = PumpCatalog.Room(normsDir);
        PumpSetChoice? setChoice = null;
        if (catalogSets.Count > 0 && stationTakeoff.Items.Count > 0)
        {
            setChoice = PumpSetSelection.Choose(catalogSets, worst.PumpFlow.LitersPerSecond * 3.6, worst.PumpHead.Meters);
            issues.AddRange(setChoice.Issues);
            if (setChoice.Chosen is { } set)
            {
                string[] covered = ["pump", "cabinet", "valveS", "valveD", "checkD", "insertS", "insertD", "vacuum", "gauge", "cock", "teeS", "teeD", "reducerS", "reducerD", "capS", "capD"];
                var row = new TakeoffItem(
                    TakeoffGroup.PumpStation,
                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Установка пожаротушения {set.Designation}: {set.MainPumps} основн. + {set.StandbyPumps} резервн. насос {set.Pump}, P2 = {set.PowerKw:0.##} кВт, прибор управления Control MX-II, обвязка с арматурой и КИП; H = {setChoice.HeadAtDuty:0.0} м при Q = {worst.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч"),
                    roomData?.SetNote ?? "по каталогу изготовителя",
                    1,
                    "компл.",
                    $"коллекторы DN{set.CollectorDn}; или аналог",
                    Supplier: set.Maker,
                    Role: "pumpSet");
                stationTakeoff = stationTakeoff with { Items = [row, .. stationTakeoff.Items.Where(i => i.Role is null || !covered.Contains(i.Role))] };
                issues.Add(new Issue("pump.setChosen", IssueLevel.Info, $"Подобрана установка {set.Designation}: напор по каталогу {setChoice.HeadAtDuty:0.0} м при требуемом {worst.PumpHead.Meters:0.0} м; потери в обвязке установки каталогом не учтены — сверить с изготовителем."));
            }
        }

        // Расстановка оборудования в помещении насосной (слой ПТ_НС) с проходами п. 6.10.16.
        PumpRoomPlan? roomPlan = null;
        if (plan.PumpRooms.Count > 0 && setChoice?.Chosen is { } chosenSet && roomData is { } rd)
        {
            roomPlan = PumpRoomLayout.Place(plan.PumpRooms[0], [
                new StationItem(StationItemKind.PumpSet, chosenSet.Designation, chosenSet.Hydraulic),
                new StationItem(StationItemKind.Cabinet, "Прибор управления Control MX-II", chosenSet.Cabinet),
                new StationItem(StationItemKind.ControlUnit, "Узел управления", rd.ControlUnit, Count: sections.Count),
                new StationItem(StationItemKind.Jockey, "Жокей-насос", rd.Jockey),
            ], norms);
            issues.AddRange(roomPlan.Issues);
        }

        // Обвязка насосной: ввод → коллектор установки; установка → узлы управления → выпуски. Трубы — по стенам на кронштейнах
        // (рекомендации литературы из data/catalog/pump_room.json), длины и отводы — в спецификацию ПТ2.СО.
        PipingResult? piping = null;
        if (roomPlan is { Items.Count: > 0 } && plan.PumpRooms[0] is { Inlet: { } stationInlet } pumpRoom && PumpCatalog.Piping(normsDir) is { } pr && setChoice?.Chosen is { } pipedSet)
        {
            var series = new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes);
            var set = roomPlan.Items.First(i => i.Kind == StationItemKind.PumpSet);
            var units = roomPlan.Items.Where(i => i.Kind == StationItemKind.ControlUnit).ToList();

            // Патрубки установки — середина стороны, ближайшей к вводу; напорный — на 300 мм в сторону.
            var sides = new[] { new PlanPoint(set.X0, (set.Y0 + set.Y1) / 2), new PlanPoint(set.X1, (set.Y0 + set.Y1) / 2), new PlanPoint((set.X0 + set.X1) / 2, set.Y0), new PlanPoint((set.X0 + set.X1) / 2, set.Y1) };
            var suctionAt = sides.MinBy(s => Math.Abs(s.XMm - stationInlet.XMm) + Math.Abs(s.YMm - stationInlet.YMm))!;
            var suctionAt2 = Math.Abs(suctionAt.YMm - set.Y0) < 1 || Math.Abs(suctionAt.YMm - set.Y1) < 1 ? new PlanPoint(suctionAt.XMm - 300, suctionAt.YMm) : new PlanPoint(suctionAt.XMm, suctionAt.YMm - 300);
            var alongX = Math.Abs(suctionAt.YMm - set.Y0) < 1 || Math.Abs(suctionAt.YMm - set.Y1) < 1;
            var dischargeAt = alongX ? new PlanPoint(suctionAt.XMm + 300, suctionAt.YMm) : new PlanPoint(suctionAt.XMm, suctionAt.YMm + 300);
            // Отметки от пола насосной, мм: ввод — заглубление от земли; напорные — на отметке трубопроводов АУП;
            // всасывающие — у пола на оси коллектора установки (подъём к насосу, п. 6.10.29).
            var pr0 = design.PumpRoom;
            var inletElev = (pr0.GroundElevation - pr0.InletDepth - pr0.FloorElevation) * 1000;
            var pipeElev = design.PipeElevation > 0 ? (design.PipeElevation - pr0.FloorElevation) * 1000 : pr.BracketHeight;
            var truckElev = Math.Min((pr0.GroundElevation - pr0.FloorElevation + (norms.Find("sp485.p6_10_18.truckInletPlacement")?.Values["height"] ?? 1.2)) * 1000, (pr0.Height * 1000) - 200);
            var dnS = station.Suction.Pipe.Dn;
            var dnD = design.SupplyDn;
            var outerS = series.Pick(dnS)?.OuterDiameterMm ?? dnS;
            var outerD = series.Pick(dnD)?.OuterDiameterMm ?? dnD;
            var requests = new List<PipeRunRequest>
            {
                new("Всасывающий 1", dnS, outerS, stationInlet, null, suctionAt, pipedSet.CollectorAxisMm, 0) { RunHeightMm = pipedSet.CollectorAxisMm, FromWallHeightMm = inletElev },
            };

            // Второй и следующие вводы (п. 6.10.27) — каждый к всасывающему коллектору, рядом с первым.
            foreach (var (extra, k) in pumpRoom.Inlets.Skip(1).Select((p, k) => (p, k)))
            {
                requests.Add(new($"Всасывающий {k + 2}", dnS, outerS, extra, null, suctionAt2, pipedSet.CollectorAxisMm, 0) { RunHeightMm = pipedSet.CollectorAxisMm, FromWallHeightMm = inletElev });
            }
            var outlets = pumpRoom.Outlets;
            for (var k = 0; k < units.Count; k++)
            {
                var u = units[k];
                var c = new PlanPoint((u.X0 + u.X1) / 2, (u.Y0 + u.Y1) / 2);
                requests.Add(new($"Напорный к УУ {k + 1}", dnD, outerD, dischargeAt, pipedSet.Hydraulic.HeightMm, c, 1000, 1) { RunHeightMm = pipeElev });
                if (k < outlets.Count)
                {
                    requests.Add(new($"Выпуск секции {k + 1}", dnD, outerD, c, 1500, outlets[k], null, 1) { RunHeightMm = pipeElev, ToWallHeightMm = pipeElev });
                }
                else
                {
                    issues.Add(new Issue("pumpRoom.noOutlet", IssueLevel.Warning, $"Для секции {k + 1} нет точки выпуска на слое ПТ_НС_ВЫПУСК — выпуск не трассирован.", pumpRoom.Id));
                }
            }

            // Жокей-насос: от всасывающего патрубка установки и в напорный — над проходом.
            if (roomPlan.Items.FirstOrDefault(i => i.Kind == StationItemKind.Jockey) is { } jockey)
            {
                var jc = new PlanPoint((jockey.X0 + jockey.X1) / 2, (jockey.Y0 + jockey.Y1) / 2);
                var outerJ = series.Pick(pr.JockeyDn)?.OuterDiameterMm ?? pr.JockeyDn;
                requests.Add(new("Жокей-насос, всасывающий", pr.JockeyDn, outerJ, suctionAt, pipedSet.CollectorAxisMm, jc, 300, 0) { RunHeightMm = pipedSet.CollectorAxisMm });
                requests.Add(new("Жокей-насос, напорный", pr.JockeyDn, outerJ, jc, 600, dischargeAt, pipedSet.Hydraulic.HeightMm, 1) { RunHeightMm = 1500 });
            }

            // Патрубки ГМ для мобильной техники: точки слоя ПТ_НС_ГМ; нет — на стене ввода по обе стороны от него через 800 мм.
            var truckPoints = pumpRoom.TruckInlets.Count > 0
                ? pumpRoom.TruckInlets
                : Enumerable.Range(0, station.TruckInlets).Select(k => AlongWall(pumpRoom.Outline, stationInlet, (k % 2 == 0 ? 1 : -1) * 800 * ((k / 2) + 1))).ToList();
            var outerT = series.Pick(station.TruckInletDn)?.OuterDiameterMm ?? station.TruckInletDn;
            foreach (var (tp, k) in truckPoints.Select((p, k) => (p, k)))
            {
                requests.Add(new($"Патрубок ГМ {k + 1}", station.TruckInletDn, outerT, tp, null, dischargeAt, pipedSet.Hydraulic.HeightMm, 2) { RunHeightMm = pipeElev, FromWallHeightMm = truckElev });
            }

            if (pumpRoom.TruckInlets.Count == 0)
            {
                issues.Add(new Issue("pumpRoom.truckInletsAuto", IssueLevel.Info, "Патрубки ГМ поставлены на стене ввода (слой ПТ_НС_ГМ пуст): стена должна быть наружной, с подъездом пожарной техники (п. 6.10.18) — проверить.", pumpRoom.Id));
            }

            piping = PumpRoomPiping.Route(pumpRoom.Outline, requests, new PipingRules(pr.WallOffset, pr.BracketHeight, pr.LevelGap));
            issues.AddRange(piping.Issues);
            var ruP = CultureInfo.GetCultureInfo("ru-RU");
            var elbowStandard = design.FittingStandards.TryGetValue("Отвод", out var es) ? es : null;
            var pipeRows = piping.Runs.GroupBy(r => r.Dn).OrderBy(g => g.Key).SelectMany(g =>
            {
                var sp = series.Pick(g.Key);
                var meters = g.Sum(r => r.HorizontalMm + r.VerticalMm) / 1000;
                return new[]
                {
                    new TakeoffItem(TakeoffGroup.PumpStationPipes,
                        sp is null ? $"Трубы стальные DN{g.Key}" : string.Create(ruP, $"Трубы {sp.PipeType} DN{g.Key}, Ø{sp.OuterDiameterMm:0.##}×{sp.WallMm:0.##} мм"),
                        sp?.Standard, Math.Round(meters, 1), "м", "по трассе на плане насосной, с опусками"),
                    new TakeoffItem(TakeoffGroup.PumpStationFittings, $"Отвод 90° DN{g.Key}", elbowStandard, g.Sum(r => r.Elbows), "шт.", "повороты трассы и опуски к оборудованию"),
                };
            });
            // Отводы патрубков ГМ теперь считаются по трассе — строка шаблона убирается.
            stationTakeoff = stationTakeoff with { Items = [.. stationTakeoff.Items.Where(i => i.Role != "elbowT"), .. pipeRows] };
        }

        // Номера позиций спецификации насосной — на схеме, по роли элемента (сквозная нумерация).
        var schemePositions = SpecificationXlsx.Positions(stationTakeoff, continuous: true)
            .Where(p => p.Item.Role is not null)
            .ToDictionary(p => p.Item.Role!, p => p.Position);

        // Промывочные устройства — п. 6.7.1.15 (узлы управления — в спецификации насосной, ПТ2.СО).
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

        // Число трубопроводов насосной (пп. 6.10.27, 6.10.28, 6.7.1.43) — по разметке помещения насосной.
        if (plan.PumpRooms.Count > 0 && plan.PumpRooms[0] is var checkedRoom)
        {
            issues.AddRange(StationLines.Check(norms, checkedRoom.Inlets.Count, checkedRoom.Outlets.Count, sections.Count, hydrantPoints.Count).Issues);
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
                schemePositions,
                sections),
            SheetStamp(project, "Принципиальная", "Принципиальная схема насосной станции пожаротушения"),
            project.Font));
        // План насосной: оборудование по расстановке, номера позиций ПТ2.СО, принятые проходы и допущения.
        if (roomPlan is { Items.Count: > 0 })
        {
            int? PosOf(string role) => schemePositions.TryGetValue(role, out var n) ? n : null;
            var board = (norms.Find("sp485.p6_10_16.passages")?.Values["switchboard"] ?? 0) * 1000;
            var equipment = roomPlan.Items.Select(i => new PumpRoomEquipment(
                i.Label, i.X0, i.Y0, i.X1, i.Y1,
                i.Kind switch
                {
                    StationItemKind.PumpSet or StationItemKind.Cabinet => PosOf("pumpSet"),
                    StationItemKind.ControlUnit => PosOf("cu"),
                    _ => PosOf("jockey"),
                },
                i.Front,
                i.Front is null ? 0 : board)).ToList();
            Write(outDir, PumpRoomPlanFile, files, p => PumpRoomDxfWriter.Write(
                p,
                new PumpRoomDrawing(roomPlan.Room.Outline, roomPlan.Room.Door, [.. equipment.OrderBy(e => e.Label == roomPlan.Items[0].Label ? 0 : 1)], [
                    "Экспликация: " + string.Join("; ", equipment.GroupBy(e => e.Position).Select(g => $"{g.Key?.ToString(CultureInfo.InvariantCulture) ?? "—"} — {string.Join(", ", g.Select(e => e.Label).Distinct())}")) + ".",
                    .. roomPlan.Rules.Select(r => $"Проход: {r} (п. 6.10.16 СП 485)."),
                    "Габариты установки и прибора управления — по каталогу изготовителя; узлов управления и жокей-насоса — приняты условно (data/catalog/pump_room.json), уточнить по выбранному оборудованию.",
                    "Свободная зона у двери 1 × 1 м — правило программы.",
                    "Номера позиций — по спецификации ПТ2.СО.",
                    .. piping is null ? Array.Empty<string>() : [$"Трубопроводы — на кронштейнах у стен на высоте {PumpCatalog.Piping(normsDir)!.Value.BracketHeight / 1000:0.0} м, отступ от стены и ярусы — по рекомендациям: {PumpCatalog.Piping(normsDir)!.Value.Source}"],
                ])
                {
                    Pipes = [.. (piping?.Runs ?? []).Select(r => new PumpRoomPipe($"DN{r.Dn}", r.Path))],
                    WallMarks = [
                        .. plan.PumpRooms[0].Inlets.Select((wi, k) => (wi, $"Ввод {k + 1} DN{station.Suction.Pipe.Dn}")),
                        .. plan.PumpRooms[0].Outlets.Select((o, k) => (o, $"Выпуск в сеть, секция {k + 1}")),
                        .. (piping?.Runs ?? []).Where(r => r.Name.StartsWith("Патрубок ГМ", StringComparison.Ordinal)).Select(r => (r.Path[0], $"ГМ-{station.TruckInletDn}")),
                    ],
                },
                SheetStamp(project, "План насосной", "План насосной станции пожаротушения"),
                project.Font));
        }

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
                station,
                setChoice,
                roomPlan,
                piping,
                PumpCatalog.Piping(normsDir)?.Source),
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

    // Точка на той же стене прямоугольного контура, сдвинутая вдоль стены на shift, мм (в пределах стены).
    private static PlanPoint AlongWall(Polygon room, PlanPoint p, double shift)
    {
        var v = room.Vertices;
        var (x0, y0, x1, y1) = (v.Min(q => q.XMm), v.Min(q => q.YMm), v.Max(q => q.XMm), v.Max(q => q.YMm));
        var onVertical = Math.Min(Math.Abs(p.XMm - x0), Math.Abs(p.XMm - x1)) < Math.Min(Math.Abs(p.YMm - y0), Math.Abs(p.YMm - y1));
        return onVertical ? new PlanPoint(p.XMm, Math.Clamp(p.YMm + shift, y0 + 300, y1 - 300)) : new PlanPoint(Math.Clamp(p.XMm + shift, x0 + 300, x1 - 300), p.YMm);
    }

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
