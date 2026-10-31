using System.Globalization;
using System.Text;
using System.Text.Json;
using WaterAuto.Alarm;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Export;
using WaterAuto.Import;
using WaterAuto.Norms;
using WaterAuto.Reports;

namespace WaterAuto.Application;

public sealed record PipelineResult(
    bool Success,
    IReadOnlyList<string> Files,
    IReadOnlyList<Issue> Issues,
    DesignResult? Design,
    PumpSetSummary? Pump = null,
    PumpStationSummary? PumpRoom = null);

/// <summary>
/// Подобранная комплектная насосная установка (автоподбор или выбор вручную из каталога): обозначение, насос,
/// количество основных и резервных, мощность, напор по кривой каталога при расчётной подаче, требуемые подача и напор
/// и кривая Q–H одного насоса — для карточки и графика насосной.
/// </summary>
public sealed record PumpSetSummary(
    string Designation,
    string Pump,
    int MainPumps,
    int StandbyPumps,
    double PowerKw,
    double HeadAtDutyMeters,
    string Maker,
    double RequiredFlowM3h,
    double RequiredHeadMeters,
    IReadOnlyList<PumpCurvePoint> Curve);

/// <summary>
/// Помещение насосной станции из чертежа и габариты подобранной установки — для карточки главного окна:
/// контур помещения в плане (длина, ширина, площадь), разметка вводов/выпусков/патрубков для мобильной техники
/// и габариты гидравлической части, прибора управления и коллектора из каталога. Нормативных чисел здесь нет.
/// Если помещения насосной в чертеже нет, <see cref="RoomId"/> и размеры равны null, счётчики — 0;
/// если установка не подобрана, <see cref="SetDesignation"/> пуст, а габариты нулевые.
/// </summary>
public sealed record PumpStationSummary(
    string? RoomId,
    double? RoomLengthM,
    double? RoomWidthM,
    double? RoomAreaM2,
    int Inlets,
    int Outlets,
    int TruckInlets,
    string SetDesignation,
    int MainPumps,
    int StandbyPumps,
    Footprint SetHydraulic,
    Footprint SetCabinet,
    int CollectorDn,
    double CollectorAxisMm);

/// <summary>Точка паспортной кривой насоса Q–H, м³/ч и м.</summary>
public sealed record PumpCurvePoint(double QM3h, double HM);

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
    public const string PumpRoomPipingFile = "pump_room_piping.dxf";
    public const string PumpRoomSectionFile = "pump_room_section.dxf";
    public const string InletRouteFile = "inlet_route.dxf";
    public const string PumpRoomAxonometryFile = "pump_room_axonometry.dxf";
    public const string PumpStationSpecXlsxFile = "specification_pns.xlsx";
    public const string SignalListFile = "aps_signals.xlsx";
    public const string BatteryFile = "aps_battery.xlsx";
    public const string InteractionFile = "aps_interaction.docx";
    public const string AddressTableFile = "aps_addresses.xlsx";
    public const string AlarmPlanFile = "aps_plan.dxf";

    /// <summary>План АПС уровня n при нескольких уровнях (alarm.levels): aps_plan_1.dxf, aps_plan_2.dxf…</summary>
    public static string AlarmPlanLevelFile(int level) => string.Create(CultureInfo.InvariantCulture, $"aps_plan_{level}.dxf");
    public const string AlarmNoteFile = "aps_pz.docx";
    public const string AlarmSchemeFile = "aps_scheme.dxf";
    public const string AlarmSpecFile = "aps_spec.xlsx";
    public const string CableJournalFile = "aps_cables.xlsx";
    public const string CableJournalDocxFile = "aps_cables.docx";
    public const string AlarmGeneralDataFile = "aps_general_data.docx";
    public const string HvacSpecFile = "aps_hvac.docx";
    public const string SouePlanLayer = "СОУЭ_ОПОВЕЩАТЕЛИ";
    public const string IssuesFile = "issues.txt";

    /// <summary>Тома выпуска: каждый раздел — своей папкой с собственными обозначениями комплектов (ADR 0013).</summary>
    public const string AuptVolume = "АУПТ";
    public const string AlarmVolume = "АПС";

    public static PipelineResult Run(string projectPath, string outDir, string normsDir, string? registryPath)
    {
        Directory.CreateDirectory(outDir);
        var auptDir = Path.Combine(outDir, AuptVolume);
        var alarmDir = Path.Combine(outDir, AlarmVolume);
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
        if (project.Aupt && design.SprinklerK <= 0 && !File.Exists(catalogPath))
        {
            issues.Add(new Issue("project.sprinklerK", IssueLevel.Error, "В проекте не задан K оросителя (design.sprinklerK), а каталога оросителей для автоподбора нет (data/catalog/sprinklers.json)."));
            return Stop();
        }

        var drawing = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, project.Drawing));
        var import = FloorPlanImporter.Import(drawing, requireInlet: project.Aupt);
        issues.AddRange(import.Issues);
        if (import.Plan is not { } plan)
        {
            return Stop();
        }

        var norms = JsonNormsProvider.Load(normsDir);

        // АПС, этап 2 (ADR 0012): извещатели — только в помещениях и с параметрами, заданными проектировщиком.
        // Уровни (alarm.levels): зоны собираются по чертежу каждого уровня с уникальными ID помещений; раскладка, ЗКСПС, СОУЭ,
        // питание и спецификация считаются по всем зонам разом, лист плана — на каждый уровень, стояки — в кабельный журнал.
        var levels = AlarmLevels(project, plan, projectPath, issues);

        // Номер уровня устройства по его обозначению «помещение.D номер» (помещение — ID зоны уровня); не найдено — первый.
        int DeviceLevel(string deviceId)
        {
            var room = deviceId[..Math.Max(0, deviceId.LastIndexOf('.'))];
            return levels.FirstOrDefault(l => l.Zones.Any(z => z.RoomId == room))?.Index ?? 0;
        }
        var detectorZones = new List<DetectorZone>();
        foreach (var level in levels)
        {
            level.Zones.AddRange(AlarmZones(project, level, design, normsDir, issues, levels.Count > 1));
            detectorZones.AddRange(level.Zones);
        }

        var alarmSheets = AlarmSheets(project, levels);
        var detectorLayout = detectorZones.Count > 0 ? DetectorLayouter.Layout(detectorZones, norms) : null;
        AlarmZoningResult? zoning = null;
        SoueLayout? soue = null;
        var poweredDevices = new List<(PoweredDevice Device, int Count)>();
        RelayModel? relayModel = null;
        var soueRelayOutputs = 0;
        NotifierEntry? sounderEntry = null;
        NotifierEntry? lightEntry = null;
        SpeakerTap? speakerTap = null;
        SpeechAmplifier? amplifier = null;
        LineMonitor? lineMonitor = null;
        SpeechPlan? speechPlan = null;
        DetectorModel? manualModel = null;
        var detectorModels = new Dictionary<string, DetectorModel>();
        if (detectorLayout is not null)
        {
            issues.AddRange(detectorLayout.Issues);
            issues.AddRange(ZoneChecks.Check(detectorZones, detectorLayout, norms));
            // Состав СПС: ИПР, ЗКСПС, изоляторы. Модель ИПР — выбор проектировщика, иначе первая ручная в каталоге.
            var detectorCatalogPath = EquipmentSet.PathFor(normsDir, AlarmDetectorCatalog.File);
            if (File.Exists(detectorCatalogPath))
            {
                var models = AlarmDetectorCatalog.Load(detectorCatalogPath);
                detectorModels = models.ToDictionary(m => m.Id);
                manualModel = project.Alarm?.ManualModel is { } mm
                    ? models.FirstOrDefault(m => m.Id == mm && m.IsManual)
                    : models.FirstOrDefault(m => m.IsManual);
                if (project.Alarm?.ManualModel is { } wanted && manualModel is null)
                {
                    issues.Add(new Issue("aps.detector.unknown", IssueLevel.Error, $"АПС: ручного извещателя «{wanted}» нет в каталоге {AlarmDetectorCatalog.File}."));
                }
                else if (project.Alarm?.ManualModel is null && manualModel is not null)
                {
                    issues.Add(new Issue("aps.detector.default", IssueLevel.Info, $"АПС: модель ИПР не выбрана — принят {manualModel.Name}, первый ручной в каталоге; задайте alarm.manualModel, если нужен другой."));
                }
            }

            zoning = AlarmZoning.Build(detectorZones, detectorLayout, norms, manualModel?.Isolator ?? false);
            issues.AddRange(zoning.Issues);

            // СОУЭ (этап 3): способ по табл. Б.1 — решение проектировщика, фон по табл. В.1, модели — из каталога оповещателей.
            var notifierPath = EquipmentSet.PathFor(normsDir, AlarmNotifierCatalog.File);
            if (project.Alarm?.Warning is { } warning && File.Exists(notifierPath))
            {
                var notifierCatalog = AlarmNotifierCatalog.Load(notifierPath);
                var notifiers = notifierCatalog.Notifiers;
                var wantKind = warning.Method == WarningMethod.Speech ? NotifierKind.Speech : NotifierKind.Sound;
                sounderEntry = warning.Sounder is { } sid ? notifiers.FirstOrDefault(n => n.Id == sid) : notifiers.FirstOrDefault(n => n.Model.Kind == wantKind);
                lightEntry = warning.Light is { } lid ? notifiers.FirstOrDefault(n => n.Id == lid) : notifiers.FirstOrDefault(n => n.Model.Kind == NotifierKind.Light);
                if (sounderEntry is null || lightEntry is null)
                {
                    issues.Add(new Issue("soue.noModel", IssueLevel.Error, $"СОУЭ: в каталоге {AlarmNotifierCatalog.File} нет {(sounderEntry is null ? (warning.Method == WarningMethod.Speech ? "речевого" : "звукового") : "светового")} оповещателя (или задан неизвестный ID)."));
                }
                else
                {
                    // Громкоговоритель линии 100 В: уровень на 1 м — по В.1 от чувствительности и отвода (паспорт), усилитель — из каталога.
                    if (sounderEntry.Speaker is { } speaker)
                    {
                        speakerTap = SpeechLines.ChooseTap(speaker, warning.SpeakerTap);
                        if (speakerTap is null)
                        {
                            issues.Add(new Issue("soue.speech.tap", IssueLevel.Error, string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"СОУЭ: у {sounderEntry.Model.Name} нет отвода {warning.SpeakerTap:0.##} Вт; отводы по паспорту — {string.Join(", ", speaker.Taps.Select(t => t.AcousticW.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"))))} Вт (alarm.warning.speakerTap).")));
                            speakerTap = SpeechLines.ChooseTap(speaker, null);
                        }

                        var l1 = SpeechLines.LevelAt1m(speaker, speakerTap!, norms);
                        sounderEntry = sounderEntry with { Model = sounderEntry.Model with { LevelAt1m = double.IsNaN(l1) ? null : l1 } };
                        amplifier = warning.Amplifier is { } aid ? notifierCatalog.Amplifiers.FirstOrDefault(a => a.Id == aid)?.Model : (notifierCatalog.Amplifiers.Count > 0 ? notifierCatalog.Amplifiers[0].Model : null);
                        lineMonitor = notifierCatalog.LineMonitors.Count > 0 ? notifierCatalog.LineMonitors[0] : null;
                        if (amplifier is null)
                        {
                            issues.Add(new Issue("soue.speech.noAmplifier", IssueLevel.Error, $"СОУЭ: в каталоге {AlarmNotifierCatalog.File} нет усилителя речевого оповещения (amplifiers{(warning.Amplifier is { } wa ? $", задан «{wa}»" : "")}) — линии и усилители не рассчитаны."));
                        }
                    }
                    else if (warning.Method == WarningMethod.Speech)
                    {
                        issues.Add(new Issue("soue.speech.noSpeaker", IssueLevel.Warning, $"СОУЭ: у {sounderEntry.Model.Name} в каталоге нет чувствительности и отводов (sensitivityDb, taps) — уровень взят из levelAt1m, линии 100 В и усилители не рассчитаны."));
                    }

                    if (sounderEntry.Model.Kind != wantKind)
                    {
                        issues.Add(new Issue("soue.model.kind", IssueLevel.Warning, $"СОУЭ: оповещатель {sounderEntry.Model.Name} — {sounderEntry.Model.Kind}, а способ оповещения — {warning.Method}."));
                    }

                    if (warning.MethodRow is { } row)
                    {
                        issues.AddRange(SoueMethod.Check(warning.Method, row, warning.Indicator, norms));
                    }

                    var background = warning.BackgroundDba ?? norms.Find(warning.BackgroundRow ?? "sp3.tB_1.parking")?.Values["background"];
                    if (background is null)
                    {
                        issues.Add(new Issue("soue.noBackground", IssueLevel.Error, $"СОУЭ: фоновый шум не задан и строки «{warning.BackgroundRow}» нет в табл. В.1 СП 3."));
                    }
                    else
                    {
                        var soueZones = detectorZones.Select(z => new SoueZone(z.RoomId, z.RoomName, z.Outline, background.Value, warning.Method)).ToList();
                        soue = SoueLayouter.Layout(soueZones, sounderEntry.Model, lightEntry.Model, norms);
                        issues.AddRange(soue.Issues);
                        var speakersOnLine = sounderEntry.Speaker is not null;
                        if (soue.Sounders.Count > 0 && !speakersOnLine) { poweredDevices.Add((sounderEntry.Device, soue.Sounders.Count)); }
                        if (soue.Lights.Count > 0) { poweredDevices.Add((lightEntry.Device, soue.Lights.Count)); }
                        // Неадресные оповещатели включаются выходом реле: по выходу на линию звуковых и на линию световых в каждом помещении.
                        // Громкоговорители линии 100 В включает усилитель — реле им не нужны.
                        soueRelayOutputs = soueZones.Count * ((sounderEntry.Device.Addresses == 0 && !speakersOnLine && soue.Sounders.Count > 0 ? 1 : 0) + (lightEntry.Device.Addresses == 0 && soue.Lights.Count > 0 ? 1 : 0));
                        if (speakersOnLine && amplifier is { } amp && speakerTap is { } tap && sounderEntry.Speaker is { } spk && File.Exists(EquipmentSet.PathFor(normsDir, AlarmCableCatalog.File)))
                        {
                            // Линии речевого оповещения — по уровню: громкоговорители уровня, стояк от поста (первый уровень) до уровня.
                            var baseElev = levels[0].Elevation;
                            var groups = levels.Select(l =>
                            {
                                var rooms = l.Zones.Select(z => z.RoomId).ToHashSet(StringComparer.Ordinal);
                                var riser = l.Index > 0 && l.Elevation is { } e && baseElev is { } b ? Math.Abs(e - b) * 1000 : 0;
                                return (l.Name.Length > 0 ? l.Name : "линия", (IReadOnlyList<PlanPoint>)[.. soue.Sounders.Where(s => rooms.Contains(s.RoomId)).Select(s => s.Location)], riser);
                            }).ToList();
                            speechPlan = SpeechLines.Build(groups, spk, tap, amp, lineMonitor, AlarmCableCatalog.Load(EquipmentSet.PathFor(normsDir, AlarmCableCatalog.File)),
                                project.Alarm?.Panel is { } pp ? new PlanPoint(pp.X, pp.Y) : null, norms);
                            issues.AddRange(speechPlan.Issues);
                        }

                        if (soueRelayOutputs > 0)
                        {
                            issues.Add(new Issue("soue.relayOutputs", IssueLevel.Info, $"СОУЭ: оповещатели неадресные — {soueRelayOutputs} выходов реле на линии оповещения (по помещениям); учтены в релейных блоках и питании."));
                        }
                    }
                }
            }

            // Релейный блок для команд АППЗ — из того же каталога, независимо от СОУЭ.
            if ((project.Alarm?.Hvac is not null || soueRelayOutputs > 0) && File.Exists(notifierPath))
            {
                var relays = AlarmNotifierCatalog.Load(notifierPath).Relays;
                relayModel = project.Alarm?.Hvac?.Relay is { } rid ? relays.FirstOrDefault(r => r.Id == rid) : (relays.Count > 0 ? relays[0] : null);
            }

            if (soueRelayOutputs > 0 && relayModel is not null && project.Alarm?.Hvac is null)
            {
                poweredDevices.Add((relayModel.Device, (soueRelayOutputs + relayModel.Outputs - 1) / relayModel.Outputs));
            }
        }

        if (project.Alarm?.Hvac is { } hvac)
        {
            // АППЗ: команды СПС в вентиляцию — ТЗ разделу ОВ; состав систем — исходные данные проекта.
            var hvacInputs = new HvacInputs(hvac.GeneralVentilation, [.. hvac.SmokeZones.Select(z => new SmokeZone(z.Name, z.ExhaustSystem, z.SupplySystem, z.SmokeDampers))], hvac.NormallyOpenDampers);
            var hvacCommands = HvacCommands.Build(hvacInputs, norms);
            issues.AddRange(hvacCommands.Issues);
            if (hvacCommands.Outputs > 0)
            {
                if (relayModel is null)
                {
                    issues.Add(new Issue("aps.hvac.noRelay", IssueLevel.Warning, $"АППЗ: релейный блок не выбран (каталог {AlarmNotifierCatalog.File}) — {hvacCommands.Outputs} выходов не учтены в питании и спецификации."));
                }
                else
                {
                    poweredDevices.Add((relayModel.Device, (hvacCommands.Outputs + soueRelayOutputs + relayModel.Outputs - 1) / relayModel.Outputs));
                }
            }
            if (hvacCommands.Commands.Count > 0)
            {
                var th = project.Title;
                var hvacSpec = HvacSpec.Build(new NoteTitle(th.Organization, th.Subject, th.Document, th.Author, th.Supervisor, th.CityYear, project.Name), hvacInputs, hvacCommands.Commands);
                Write(alarmDir, HvacSpecFile, files, p => NoteDocx.Write(p, hvacSpec, Stamp(project, "АПС.ТЗ-ОВ", "Техническое задание на автоматизацию вентиляции при пожаре"), project.Font));
            }
        }


        IReadOnlyList<ControlPoint> controlPoints = [];
        PowerBudget? powerBudget = null;
        IReadOnlyList<AddressRow> addressRows = [];
        IReadOnlyList<AlarmSpecItem> alarmSpec = [];
        List<CableLine> cableLines = [];
        List<LoopLine> schemeLoops = [];
        AlarmPowerSet? alarmSetForDocs = null;

        // Блоки тома АПС — общие для проекта с АУПТ и без неё (aupt: false): питание, адреса, спецификация и журнал;
        // листы планов по уровням; документы тома. Питание считается при сигналах АУПТ или при раскладке АПС без АУПТ.
        void BuildAlarmPower()
        {
            var needPower = controlPoints.Count > 0 || (!project.Aupt && detectorLayout is not null);
            // АКБ приборов автоматики АУПТ (ADR 0012, этап 1): по одному входу модуля на точку управления.
            var powerSet = EquipmentSet.PathFor(normsDir, AlarmPowerCatalog.File);
            if (needPower && !File.Exists(powerSet))
            {
                issues.Add(new Issue("aps.power.noCatalog", IssueLevel.Warning,
                    $"Нет каталога приборов питания {Path.GetFileName(powerSet)}: расчёт АКБ и таблица адресов не выпускаются."));
            }
            else if (needPower)
            {
                // АПС — надстройка над АУПТ: ошибка её каталога не должна останавливать выпуск гидравлики и чертежей.
                AlarmPowerSet? alarmSet = null;
                try
                {
                    alarmSet = AlarmPowerCatalog.Load(powerSet);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    issues.Add(new Issue("aps.power.catalogError", IssueLevel.Error,
                        $"Каталог {Path.GetFileName(powerSet)} не прочитан: {ex.Message} Расчёт АКБ и таблица адресов не выпускаются."));
                }

                if (alarmSet is not null)
                {
                    // Извещатели — по выбранным моделям: ток, адреса и сертификат из каталога извещателей, количество — из раскладки и состава СПС.
                    Dictionary<string, int>? detectorCounts = null;
                    var addressDevices = new List<AddressTable.AddressDevice>();
                    if (detectorLayout is not null && zoning is not null)
                    {
                        detectorCounts = detectorZones.Where(z => z.ModelId is not null)
                            .GroupBy(z => z.ModelId!)
                            .ToDictionary(g => g.Key, g => g.Sum(z => detectorLayout.CountByRoom.GetValueOrDefault(z.RoomId)));
                        if (manualModel is not null && zoning.Manuals.Count > 0)
                        {
                            detectorCounts[manualModel.Id] = zoning.Manuals.Count;
                        }

                        alarmSet = alarmSet with { Detectors = detectorCounts.Keys.Where(detectorModels.ContainsKey).ToDictionary(id => id, id => detectorModels[id].AsLoopDevice()) };
                        var zoneById = detectorZones.ToDictionary(z => z.RoomId);
                        foreach (var d in detectorLayout.Detectors)
                        {
                            var z = zoneById[d.RoomId];
                            var model = z.ModelId is not null && detectorModels.TryGetValue(z.ModelId, out var dm) ? dm : null;
                            addressDevices.Add(new(d.Id, $"{(d.Kind == DetectorKind.Smoke ? "ИП дымовой" : "ИП тепловой")} {model?.Name ?? ""} — {z.RoomName}".Trim(), "Пожар", zoning.ZoneOf.GetValueOrDefault(d.Id, ""), model?.LoopCurrent ?? Current.Zero));
                        }

                        foreach (var manual in zoning.Manuals)
                        {
                            addressDevices.Add(new(manual.Id, $"ИПР {manualModel?.Name ?? ""} — {zoneById[manual.RoomId].RoomName}".Trim(), "Пожар (ручной)", "", manualModel?.LoopCurrent ?? Current.Zero));
                        }

                        // Громкоговорители линии 100 В — не адресные устройства: в таблицу адресов и ДПЛС не идут.
                        foreach (var s in sounderEntry is { Speaker: null, Device.Addresses: > 0 } ? soue?.Sounders ?? [] : [])
                        {
                            addressDevices.Add(new(s.Id, $"{sounderEntry?.Model.Name} — {zoneById[s.RoomId].RoomName}", "Оповещение (выход)", "", sounderEntry?.Device.LoopCurrent ?? Current.Zero));
                        }

                        foreach (var l in lightEntry is { Device.Addresses: > 0 } ? soue?.Lights ?? [] : [])
                        {
                            addressDevices.Add(new(l.Id, $"{lightEntry?.Model.Name} — {zoneById[l.RoomId].RoomName}", "Оповещение (табло)", "", lightEntry?.Device.LoopCurrent ?? Current.Zero));
                        }

                        // Порядок адресов — по уровням (стабильно): контроллеры берут устройства подряд, поэтому на линии уровни
                        // идут блоками; тот же ключ сортирует точки кольца ниже — таблица адресов, схема и журнал совпадают.
                        addressDevices = [.. addressDevices.OrderBy(d => DeviceLevel(d.Designation))];
                    }

                    var power = PowerBudgets.Build(controlPoints.Count, alarmSet, norms, detectorCounts, zoning?.Isolators ?? 0, poweredDevices);
                    powerBudget = power.Budget;
                    alarmSetForDocs = alarmSet;
                    issues.AddRange(power.Issues);
                    addressRows = AddressTable.Build(controlPoints, alarmSet.Controller, alarmSet.Module, addressDevices).Rows;
                    alarmSpec = AlarmSpecification.Build(powerBudget, alarmSet, detectorZones, detectorLayout ?? new DetectorLayout([], new Dictionary<string, int>(), []), detectorModels, manualModel, zoning?.Manuals.Count ?? 0, zoning?.Isolators ?? 0,
                        [.. soue is null ? [] : new[] { (sounderEntry!, soue.Sounders.Count), (lightEntry!, soue.Lights.Count) }], relayModel, poweredDevices.FirstOrDefault(x => relayModel is not null && x.Device == relayModel.Device).Count,
                        speechPlan, amplifier, lineMonitor);

                    // Кабельный журнал — только при заданной точке пожарного поста: без неё длин линий нет.
                    var cablesPath = EquipmentSet.PathFor(normsDir, AlarmCableCatalog.File);
                    {
                        var loopPlan = LoopLayout.Plan(controlPoints.Count, alarmSet.Controller, alarmSet.Module,
                            addressDevices.GroupBy(d => d.Current).Select(g => (g.Count(), g.Key, 1)).ToList());
                        // Устройства по уровням (тот же порядок, что в таблице адресов); отметка уровня относительно поста (первый
                        // уровень) — кольцо обходит уровни подряд, стояк — на каждом переходе (CableJournal.Segments).
                        var devicePoints = (detectorLayout?.Detectors.Select(d => (d.Location, d.RoomId, d.Id)) ?? []).Concat(zoning?.Manuals.Select(m => (m.Location, m.RoomId, m.Id)) ?? [])
                            .Concat(soue?.Sounders.Where(_ => sounderEntry is { Speaker: null, Device.Addresses: > 0 }).Select(s => (s.Location, s.RoomId, s.Id)) ?? []).Concat(soue?.Lights.Where(_ => lightEntry is { Device.Addresses: > 0 }).Select(l => (l.Location, l.RoomId, l.Id)) ?? [])
                            .OrderBy(d => DeviceLevel(d.Id)).ToList();
                        var loops = new List<LoopLine>();
                        var offset = 0;
                        var baseElevation = levels[0].Elevation;
                        double LevelMm(string id) => levels[DeviceLevel(id)].Elevation is { } e && baseElevation is { } b ? (e - b) * 1000 : 0;
                        for (var k = 0; k < Math.Max(1, loopPlan.Controllers); k++)
                        {
                            var take = k < loopPlan.DevicesByController.Count ? loopPlan.DevicesByController[k] : 0;
                            var load = k < loopPlan.LoadByController.Count ? loopPlan.LoadByController[k] : Current.Zero;
                            var onLoop = devicePoints.Skip(offset).Take(take).ToList();
                            loops.Add(new LoopLine(k + 1, onLoop.Select(d => d.Location).ToList(), load, k < loopPlan.ModulesByController.Count && loopPlan.ModulesByController[k] > 0, 0,
                                onLoop.Select(d => d.Id).ToList(), onLoop.Select(d => LevelMm(d.Id)).ToList()));
                            offset += take;
                        }

                        var mixed = loops.Count(l => l.DeviceIds is { } ids && ids.Select(DeviceLevel).Distinct().Count() > 1);
                        if (levels.Count > 1 && mixed > 0)
                        {
                            issues.Add(new Issue("aps.loop.levels", IssueLevel.Info,
                                $"Линии ДПЛС: устройства распределены по контроллерам подряд по уровням; {mixed} лини{(mixed == 1 ? "я проходит" : "и проходят")} через несколько уровней — кольцо обходит уровни блоками со стояком на каждом переходе. Отдельный контроллер на уровень не назначается (число контроллеров — по ёмкости линии); при другом решении — проектировщик."));
                        }

                        if (levels.Count > 1 && (baseElevation is null || levels.Any(l => l.Elevation is null)))
                        {
                            issues.Add(new Issue("aps.cable.noElevation", IssueLevel.Warning, "Кабельный журнал: у уровней не заданы отметки (alarm.levels[].elevation) — стояки ДПЛС между уровнями в длинах линий не учтены."));
                        }

                        schemeLoops = loops;
                        if (project.Alarm?.Panel is { } panelPoint && File.Exists(cablesPath))
                        {
                        var journal = CableJournal.Build(new PlanPoint(panelPoint.X, panelPoint.Y), loops, AlarmCableCatalog.Load(cablesPath),
                            plan.PumpRooms.Count > 0 ? plan.PumpRooms[0].Outline : null, controlPoints.Count, alarmSet.Controller.Name, alarmSet.Supply.Name, alarmSet.Module.Name);
                        issues.AddRange(journal.Issues);
                        var allLines = journal.Lines.ToList();

                        // Неадресные оповещатели (адресов 0) — не на ДПЛС: линия 12 В от выхода релейного блока на посту через
                        // оповещатели помещения цепочкой (по уровню — со стояком), отдельно звуковые и световые.
                        var relayName = relayModel?.Name ?? "релейный блок";
                        var notifierLine = 0;
                        foreach (var (entry, list, purpose) in new[] { (sounderEntry, soue?.Sounders, "линия звукового оповещения 12 В"), (lightEntry, soue?.Lights, "линия светового оповещения 12 В") })
                        {
                            if (entry is not { Speaker: null, Device.Addresses: 0 } || list is not { Count: > 0 })
                            {
                                continue;
                            }

                            foreach (var room in list.GroupBy(x => x.RoomId))
                            {
                                var pts = room.Select(x => x.Location).ToList();
                                var segs = CableJournal.Segments(new PlanPoint(panelPoint.X, panelPoint.Y), pts, [.. room.Select(x => LevelMm(x.Id))]);
                                var baseM = segs.Take(segs.Count - 1).Sum(x => x.Mm) / 1000;
                                notifierLine++;
                                var roomName = detectorZones.FirstOrDefault(z => z.RoomId == room.Key)?.RoomName ?? room.Key;
                                allLines.Add(new CableLine($"ЛОН{notifierLine}", $"{relayName} (выход)", $"{room.Count()} × {entry.Model.Name} — {roomName}", purpose, LoadCables(cablesPath).PowerName, Math.Round(baseM * LoadCables(cablesPath).Reserve, 0),
                                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"цепочка от поста через оповещатели помещения, ток {room.Count() * entry.Device.Alarm.Amperes * 1000:0} мА; сечение — по падению напряжения, проектировщик"), Math.Round(baseM, 0)));
                            }
                        }
                        if (speechPlan is { Lines.Count: > 0 } sp && amplifier is { } ampl)
                        {
                            // Линии речевого оповещения 100 В от усилителя (на посту) и его питание 220 В — в тот же журнал.
                            var speechCables = AlarmCableCatalog.Load(cablesPath);
                            foreach (var line in sp.Lines)
                            {
                                var mark = speechCables.SpeechName is { } sn ? sn.Replace("{section}", (line.SectionMm2 ?? speechCables.SpeechSections?.Max() ?? 0).ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")), StringComparison.Ordinal) : "по проекту";
                                allLines.Add(new CableLine($"ЛО{line.Number}", ampl.Name, $"{line.Speakers} громкоговорителей {sounderEntry?.Model.Name} ({line.Group}){(lineMonitor is not null ? $", {lineMonitor.Name} в конце" : "")}", "линия речевого оповещения 100 В (дерево)", mark, line.LengthM,
                                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{line.ConsumedW:0.#} Вт от линии, I = {line.ConsumedW / ampl.LineVoltage:0.###} А; сечение по формуле РЭ при ΔU ≤ 10 % (В.3.4); отдельно от ДПЛС")));
                            }

                            for (var k = 1; k <= sp.Amplifiers; k++)
                            {
                                allLines.Add(new CableLine($"С{k + 2}", "ВРУ (ввод 1)", $"{ampl.Name} №{k}", "питание 220 В, I категория", speechCables.MainsName, 0, string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"до {ampl.MainsW:0} Вт; встроенные АКБ {ampl.Batteries} × {ampl.BatteryName}; длина — по месту ВРУ")));
                            }
                        }

                        cableLines = allLines;
                        }
                    }

                    if (project.Alarm is not null && project.Alarm.Panel is null)
                    {
                        issues.Add(new Issue("aps.cable.noPanel", IssueLevel.Warning, "Кабельный журнал АПС не выпущен: не задана точка установки приборов (alarm.panel — координаты пожарного поста на плане, мм)."));
                    }

                    // Структурная схема: контроллеры с кольцами в порядке обхода, модуль насосной и реле, РИП, усилитель с линиями.
                    if (schemeLoops.Count > 0 && detectorLayout is not null)
                    {
                        var labelOf = addressDevices.GroupBy(d => d.Designation, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Equipment, StringComparer.Ordinal);
                        var relayCount = poweredDevices.Where(x => relayModel is not null && x.Device == relayModel.Device).Sum(x => x.Count);
                        var schemeLoopList = schemeLoops.Select((l, i) => new SchemeLoop(l.Number, alarmSet.Controller.Name,
                            [.. (l.DeviceIds ?? []).Select(id => new SchemeDevice(id, labelOf.GetValueOrDefault(id, ""), SchemeKind(id)))],
                            l.HasPumpModules ? alarmSet.Module.Name : null, i == 0 ? relayCount : 0, relayModel?.Name)).ToList();
                        var speechLines = speechPlan?.Lines.Select(l => new SchemeSpeechLine(l.Number, l.Group, l.Speakers, sounderEntry?.Model.Name ?? "", l.ConsumedW, lineMonitor?.Name)).ToList() ?? [];
                        var scheme = new AlarmScheme(schemeLoopList, alarmSet.Supply.Name, powerBudget?.Supplies ?? 1, alarmSet.Battery.Name, amplifier?.Name, speechPlan?.Amplifiers ?? 0, speechLines,
                            "Порядок устройств на линии — по обходу кольца из кабельного журнала; изоляторы КЗ (БРИЗ) расставляются по линии проектировщиком (п. 6.3.5 СП 484). Пульт и линия RS-485 — по проекту.");
                        var schemeSheet = alarmSheets.FirstOrDefault(sh => sh.Name.StartsWith("Структурная", StringComparison.OrdinalIgnoreCase));
                        Write(alarmDir, AlarmSchemeFile, files, p => AlarmSchemeDxfWriter.Write(p, scheme, Stamp(project, "АПС", schemeSheet?.Name ?? "Структурная схема") with { Sheet = schemeSheet?.Sheet }, project.Font));
                    }
                }
            }
        }

        void WriteAlarmPlans(IReadOnlyList<Issue> planIssues)
        {
            if (detectorLayout is not null)
            {
                // Лист плана на каждый уровень: извещатели, ИПР и оповещатели уровня — по ID его помещений; один уровень — aps_plan.dxf.
                var planSheets = alarmSheets.Where(s => s.Name.StartsWith("План", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var level in levels.Where(l => l.Zones.Count > 0))
                {
                    var rooms = level.Zones.Select(z => z.RoomId).ToHashSet(StringComparer.Ordinal);
                    var planSheet = level.Index < planSheets.Count ? planSheets[level.Index] : planSheets.LastOrDefault();
                    var planStamp = Stamp(project, "АПС", planSheet?.Name ?? "План расстановки извещателей") with { Sheet = planSheet?.Sheet };
                    var levelLayout = new DetectorLayout([.. detectorLayout.Detectors.Where(d => rooms.Contains(d.RoomId))], detectorLayout.CountByRoom.Where(kv => rooms.Contains(kv.Key)).ToDictionary(), []);
                    var levelIssues = planIssues.Where(i => i.ElementId is null || rooms.Contains(i.ElementId)).ToList();
                    var levelNotifiers = soue is null ? null : soue.Sounders.Concat(soue.Lights).Where(n => rooms.Contains(n.RoomId)).ToList();
                    Write(alarmDir, levels.Count > 1 ? AlarmPlanLevelFile(level.Index + 1) : AlarmPlanFile, files,
                        p => AlarmPlanDxfWriter.Write(p, levelLayout, level.Zones, levelIssues, project.Font, manuals: zoning?.Manuals.Where(m => rooms.Contains(m.RoomId)).ToList(), zoneOf: zoning?.ZoneOf, stamp: planStamp, notifiers: levelNotifiers));
                }
            }
        }

        void WriteAlarmDocs()
        {
            if (powerBudget is not null)
            {
                Write(alarmDir, BatteryFile, files, p => PowerBudgetXlsx.Write(p, powerBudget, $"{project.Name}. Расчёт ёмкости АКБ приборов автоматики АУПТ", project.Font));
            }

            if (addressRows.Count > 0)
            {
                Write(alarmDir, AddressTableFile, files, p => AddressTableXlsx.Write(p, addressRows, $"{project.Name}. Таблица адресов АПС", project.Font));
            }

            if (cableLines.Count > 0)
            {
                Write(alarmDir, CableJournalFile, files, p => CableJournalXlsx.Write(p, cableLines, $"{project.Name}. Кабельный журнал АПС", project.Font));
                Write(alarmDir, CableJournalDocxFile, files, p => CableJournalDocx.Write(p, cableLines, Stamp(project, "АПС.КЖ", "Кабельный журнал"), project.Font));
            }

            if (alarmSpec.Count > 0)
            {
                Write(alarmDir, AlarmSpecFile, files, p => AlarmSpecXlsx.Write(p, alarmSpec, $"{project.Name}. Спецификация оборудования АПС", project.Font));
            }

            if (controlPoints.Count > 0)
            {
                var ti = project.Title;
                var spec = InteractionSpec.Build(new NoteTitle(ti.Organization, ti.Subject, ti.Document, ti.Author, ti.Supervisor, ti.CityYear, project.Name), controlPoints, powerBudget);
                Write(alarmDir, InteractionFile, files, p => NoteDocx.Write(p, spec, Stamp(project, "АПС.ТЗ", "Техническое задание на взаимодействие с АПС"), project.Font));
            }

            if (detectorLayout is not null && project.Alarm is not null)
            {
                var alarmGeneral = AlarmGeneralData.Build(project.Name, alarmSheets, detectorZones, detectorLayout, zoning, powerBudget, alarmSetForDocs, controlPoints, project.Title.Code);
                Write(alarmDir, AlarmGeneralDataFile, files, p => GeneralDataDocx.Write(p, alarmGeneral, Stamp(project, "АПС", "Общие данные") with { SheetCount = alarmSheets.Count, Sheet = alarmSheets.Count > 0 ? alarmSheets[0].Sheet : null }, project.Font));
            }

            if (detectorLayout is not null || controlPoints.Count > 0)
            {
                // Записка АПС — объяснение хода расчёта для проектировщика: исходные данные, выбор ИП, размещение, ЗКСПС, сигналы, АКБ.
                var tn = project.Title;
                var alarmNote = AlarmNote.Build(new NoteTitle(tn.Organization, tn.Subject, tn.Document, tn.Author, tn.Supervisor, tn.CityYear, project.Name),
                    detectorZones, detectorLayout ?? new DetectorLayout([], new Dictionary<string, int>(), []), issues, controlPoints, powerBudget, norms, zoning, soue,
                    [.. files.Where(f => f.StartsWith(alarmDir, StringComparison.Ordinal)).Select(Path.GetFileName)!, AlarmNoteFile], speechPlan, amplifier);
                Write(alarmDir, AlarmNoteFile, files, p => NoteDocx.Write(p, alarmNote, Stamp(project, "АПС.ПЗ", "Пожарная сигнализация. Пояснительная записка"), project.Font));
            }
        }

        // Проект без водяной АУПТ: только том АПС (и смежные), раздел design не нужен.
        if (!project.Aupt)
        {
            if (detectorLayout is null)
            {
                issues.Add(new Issue("project.nothingToDo", IssueLevel.Error, "В проекте нет водяной АУПТ (aupt: false) и нет раздела alarm с помещениями — выпускать нечего."));
                return Stop();
            }

            BuildAlarmPower();
            WriteAlarmPlans([.. issues]);
            WriteAlarmDocs();
            files.Add(WriteIssues(outDir, issues));
            return new PipelineResult(!issues.Any(i => i.Level == IssueLevel.Error && i.Code.StartsWith("aps.", StringComparison.Ordinal)), files, issues, null);
        }

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
            design.DesignVelocity ?? DesignVelocityDefault(normsDir),
            design.RingValvesClosed,
            pipeSizes,
            // Диаметры по табл. Б.4, но участки со скоростью выше предела 6.7.1.37 увеличиваются автоматически:
            // иначе на реальном проекте напор доходил до 193 м и насос не подбирался.
            ResizeOverLimit: true);
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
        UserTank? userTank = design.Tank is { } tank
            ? new UserTank(new Length(tank.Length), new Length(tank.Width), new Length(tank.Height), new Length(tank.DeadLayer))
            : null;
        if (ReservoirCheck.Check(userTank, worst.WaterVolume, worst.TankMaxFill) is { } reservoirIssue)
        {
            issues.Add(reservoirIssue);
        }

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
        var station = PumpStation.Design(norms, worst.PumpFlow, worst.PumpHead, new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes, includeOutdoorOnly: true), new Pressure(design.InletPressure));
        issues.AddRange(station.Issues);
        var stationSet = EquipmentSet.PathFor(normsDir, EquipmentSet.PumpStationFile);

        // Подбор комплектной установки по каталогу (Q, H) — до спецификации ПТ2.СО: количество насосов в выпуске должно
        // совпадать с фактической установкой. Обозначение из проекта — выбор вручную; пусто — прежний автоподбор.
        // Насосы, прибор управления, обвязка с арматурой и КИП входят в установку — эти строки шаблона заменяются строкой установки.
        var catalogSets = PumpCatalog.Sets(normsDir);
        var roomData = PumpCatalog.Room(normsDir);
        if (catalogSets.Count == 0)
        {
            issues.Add(new Issue("pump.catalogMissing", IssueLevel.Warning,
                "Не найден каталог комплектных насосных установок (data/catalog/pump_sets_*.json): установка не подбирается, листы плана/разреза/аксонометрии насосной не выпускаются."));
        }

        if (roomData is null)
        {
            issues.Add(new Issue("pump.roomDataMissing", IssueLevel.Warning,
                "Не найден data/catalog/pump_room.json: расстановка оборудования и обвязка насосной не строятся."));
        }

        var setChoice = ChoosePumpSet(design.PumpStation.SetDesignation, catalogSets, worst, File.Exists(stationSet), issues);
        var pumpCount = setChoice?.Chosen is { } chosenForCount
            ? chosenForCount.MainPumps + chosenForCount.StandbyPumps
            : station.WorkingPumps + station.StandbyPumps;

        // Жокей-насос: расход и уставки — решение проектировщика (В-07), норма их не задаёт. Данные не влияют на расчёт
        // Q/H и спецификацию — только замечания и пояснительная записка; Error jockey.flow выпуск не останавливает.
        issues.AddRange(JockeyIssues(design.Jockey));
        var stationTakeoff = new MaterialTakeoff([], []);

        // Секции — по точкам ввода, к которым присоединены помещения; подпись — имя точки ввода с плана.
        var sections = routes.Select(r => r.InletId).Distinct()
            .Select(id => plan.Inlets.Select((inlet, k) => (inlet, k)).FirstOrDefault(x => x.inlet.Id == id) is { inlet: not null } found
                ? $"точка ввода {found.inlet.Name ?? (found.k + 1).ToString(CultureInfo.InvariantCulture)}"
                : id)
            .ToList();
        // Помещение насосной может быть задано зоной ПТ_ЗАПРЕТ с подписью «ПНС» (слой ПТ_НС не обязателен), но без точек
        // вводов/выпусков: без них не проверяются 6.10.27/6.10.28 и не трассируется обвязка. Ставим условные точки по стенам
        // с явным Info-замечанием — иначе пользователь получает только Error и неполный комплект.
        if (plan.PumpRooms.Count > 0 && plan.PumpRooms[0] is { } autoRoom
            && (autoRoom.Inlets.Count == 0 || autoRoom.Outlets.Count == 0))
        {
            var minSuction = (int)Need(norms, "sp485.p6_10_27.suctionLines").Values["minCount"]!.Value;
            var inlets = autoRoom.Inlets.Count > 0 ? autoRoom.Inlets : AutoWallPoints(autoRoom.Outline, Math.Max(1, minSuction), 0.1);
            var outlets = autoRoom.Outlets.Count > 0 ? autoRoom.Outlets : AutoWallPoints(autoRoom.Outline, Math.Max(1, sections.Count), 0.5);
            plan = plan with { PumpRooms = [autoRoom with { Inlets = inlets, Outlets = outlets }, .. plan.PumpRooms.Skip(1)] };
            if (autoRoom.Inlets.Count == 0)
            {
                issues.Add(new Issue("pumpRoom.inletsAuto", IssueLevel.Info,
                    $"В помещении насосной {autoRoom.Id} нет вводов на слое ПТ_НС_ВВОД — поставлено {inlets.Count} условных точек на стенах; проверьте привязку к сети (п. 6.10.27).", autoRoom.Id));
            }

            if (autoRoom.Outlets.Count == 0)
            {
                issues.Add(new Issue("pumpRoom.outletsAuto", IssueLevel.Info,
                    $"В помещении насосной {autoRoom.Id} нет выпусков на слое ПТ_НС_ВЫПУСК — поставлено {outlets.Count} условных точек на стенах; проверьте привязку к сети секций (п. 6.10.28).", autoRoom.Id));
            }
        }

        // Число вводов в насосную — одно для сигналов, спецификации и схемы.
        var inletCount = plan.PumpRooms.Count > 0 ? plan.PumpRooms[0].Inlets.Count : 0;
        if (File.Exists(stationSet))
        {
            // Отдельная спецификация насосной (комплект ПТ2): оборудование, арматура и приборы, фасонные части обвязки.
            var ruPs = CultureInfo.GetCultureInfo("ru-RU");
            var stationTokens = new Dictionary<string, string>
            {
                ["q"] = (worst.PumpFlow.LitersPerSecond * 3.6).ToString("0.0", ruPs),
                ["h"] = worst.PumpHead.Meters.ToString("0.0", ruPs),
                ["dnS"] = station.Suction.Pipe.Dn.ToString(CultureInfo.InvariantCulture),
                ["dnD"] = station.Discharge.Pipe.Dn.ToString(CultureInfo.InvariantCulture),
                ["dnC"] = design.SupplyDn.ToString(CultureInfo.InvariantCulture),
                ["tank"] = worst.JockeyTankMinLiters.ToString("0", CultureInfo.InvariantCulture),
                ["truckDn"] = station.TruckInletDn.ToString(CultureInfo.InvariantCulture),
            };
            var stationCounts = new Dictionary<string, int> { ["pumps"] = pumpCount, ["truckInlets"] = station.TruckInlets, ["sections"] = sections.Count, ["extraSections"] = sections.Count - 1, ["inlets"] = inletCount };
            stationTakeoff = stationTakeoff with { Items = [.. EquipmentSet.Items(stationSet, TakeoffGroup.PumpStation, stationTokens, stationCounts)] };

            // Сигналы АУПТ в АПС (ADR 0012) — по тем же строкам, что ПТ2.СО: строки комплектной установки сигналов не дают,
            // кроме шкафа: прибор управления есть и в установке, его выходы в АПС нужны так же (ГОСТ Р 53325-2012, п. 7.4.5).
            var signalSources = EquipmentSet.Signals(stationSet, stationTokens, stationCounts,
                setChoice?.Chosen is not null ? EquipmentSet.PumpSetRoles.Where(r => r != "cabinet").ToHashSet() : null);
            if (setChoice?.Chosen is { } setForSignals)
            {
                signalSources = [.. signalSources.Select(s => s.Role == "cabinet" ? s with { Equipment = $"Прибор управления установки {setForSignals.Designation}" } : s)];
            }

            var points = ControlPoints.Build(signalSources);
            controlPoints = points.Points;
            issues.AddRange(points.Issues);
            if (controlPoints.Count > 0)
            {
                issues.Add(new Issue("aps.signals.incomplete", IssueLevel.Info,
                    "Перечень сигналов АУПТ в АПС неполный: от шкафа насосов — только обобщённые «Пуск», «Неисправность» и «Автоматика отключена», остальные — по паспорту шкафа; сигналов жокей-насоса и обводной задвижки водомерного узла в каталоге нет (вопрос А-05)."));
            }

            BuildAlarmPower();
        }

        // Диагностика неполного выпуска: молча пропускать листы и данные нельзя (аудит PD-14, PS-6/PS-7).
        if (File.Exists(stationSet) && plan.PumpRooms.Count == 0)
        {
            issues.Add(new Issue("pumpRoom.missing", IssueLevel.Warning,
                "В чертеже нет помещения насосной (слой ПТ_НС): листы насосной не выпускаются, ПТ2.СО — без труб обвязки, креплений и покрытия."));
        }

        if (!File.Exists(stationSet))
        {
            issues.Add(new Issue("pump.templateMissing", IssueLevel.Warning,
                "Не найден шаблон спецификации насосной (data/catalog/pump_station.json): ПТ2.СО не выпускается."));
        }

        if (plan.PumpRooms.Count > 0 && plan.PumpRooms[0].Inlets.Count == 0)
        {
            issues.Add(new Issue("pumpRoom.noInlet", IssueLevel.Warning,
                $"В помещении насосной {plan.PumpRooms[0].Id} нет вводов на слое ПТ_НС_ВВОД: обвязка и трасса ввода не трассируются.", plan.PumpRooms[0].Id));
        }

        // Строка комплектной установки в ПТ2.СО: насосы, прибор управления и обвязка с арматурой входят в неё — строки
        // шаблона заменяются (при автоподборе и при выборе вручную из каталога одинаково).
        if (setChoice?.Chosen is { } chosenSetRow && stationTakeoff.Items.Count > 0)
        {
            var row = new TakeoffItem(
                TakeoffGroup.PumpStation,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Установка пожаротушения {chosenSetRow.Designation}: {chosenSetRow.MainPumps} основн. + {chosenSetRow.StandbyPumps} резервн. насос {chosenSetRow.Pump}, P2 = {chosenSetRow.PowerKw:0.##} кВт, прибор управления Control MX-II, обвязка с арматурой и КИП; H = {setChoice.HeadAtDuty:0.0} м при Q = {worst.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч"),
                roomData?.SetNote ?? "по каталогу изготовителя",
                1,
                "компл.",
                $"коллекторы DN{chosenSetRow.CollectorDn}; или аналог",
                Supplier: chosenSetRow.Maker,
                Role: "pumpSet");
            stationTakeoff = stationTakeoff with { Items = [row, .. stationTakeoff.Items.Where(i => i.Role is null || !EquipmentSet.PumpSetRoles.Contains(i.Role))] };
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
            var series = new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes, includeOutdoorOnly: true);
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
            var truckRule = Need(norms, "sp485.p6_10_18.truckInletPlacement");
            var truckHeight = truckRule.Values["height"]!.Value;
            var truckTolerance = truckRule.Values["heightTolerance"]!.Value;
            var truckElev = (pr0.GroundElevation - pr0.FloorElevation + truckHeight) * 1000;
            var truckCeiling = (pr0.Height * 1000) - 200;
            if (truckElev > truckCeiling)
            {
                // Молча урезать высоту нельзя: ось должна быть на нормативной высоте от отмостки (п. 6.10.18).
                issues.Add(new Issue("pumpRoom.truckElevation", IssueLevel.Error,
                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Патрубки ГМ не размещаются на нормативной высоте: ось {truckHeight:0.00} м над отмосткой (допуск ±{truckTolerance:0.00} м) — это {truckElev / 1000:0.00} м от пола насосной, а до низа перекрытия {pr0.Height:0.00} м (программа оставляет 0,2 м). Увеличьте высоту помещения или заглубление (п. 6.10.18)."),
                    pumpRoom.Id, "6.10.18"));
                truckElev = truckCeiling;
            }

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

            // Патрубки ГМ для мобильной техники: точки слоя ПТ_НС_ГМ; нет — на стене ввода за последним вводом через 800 мм.
            var truckPoints = pumpRoom.TruckInlets.Count > 0
                ? pumpRoom.TruckInlets
                : Enumerable.Range(0, station.TruckInlets).Select(k => AlongWall(pumpRoom.Outline, pumpRoom.Inlets.MaxBy(i => i.XMm + i.YMm)!, 1200 + (800 * k))).ToList();
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
            issues.AddRange(series.Issues);
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
            // Гильзы в стенах (закладные): на каждом проходе трубы через стену, Ø — наружный диаметр трубы + запас (Козицина, с. 26).
            var wallT = design.PumpRoom.WallThickness * 1000;
            var sleeveExtra = PumpCatalog.PipingExtras(normsDir).SleeveExtra;
            var sleeveRows = piping.Runs
                .SelectMany(r => new[] { IsWall(pumpRoom.Outline, r.Path[0]) ? r.Dn : 0, IsWall(pumpRoom.Outline, r.Path[^1]) ? r.Dn : 0 })
                .Where(dn => dn > 0)
                .GroupBy(dn => dn)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var outer = series.Pick(g.Key)?.OuterDiameterMm ?? g.Key;
                    return new TakeoffItem(TakeoffGroup.PumpStationFittings, string.Create(ruP, $"Гильза стальная для прохода трубы DN{g.Key} через стену: Ø не менее {outer + sleeveExtra:0} мм, L = {wallT:0} мм"), "по месту", g.Count(), "шт.", "закладная деталь; заделка — эластичным негорючим материалом", Role: "sleeve");
                });
            var extraRows = new List<TakeoffItem>(sleeveRows);
            if (design.PumpRoom.Underground)
            {
                var drains = (int)Need(norms, "sp485.p6_10_23.drainage").Values["drainPumpsUnderground"]!.Value;
                extraRows.Add(new TakeoffItem(TakeoffGroup.PumpStation, "Насос дренажный погружной с поплавковым выключателем", "марка — по каталогу", drains, "шт.", "в приямке; I категория электроснабжения (п. 6.10.23)", Role: "drainPump"));
            }

            extraRows.Add(new TakeoffItem(TakeoffGroup.PumpStationValves, "Воздухоотводчик автоматический DN15, PN 16", "по ТУ изготовителя", units.Count, "шт.", "в верхней точке напорной линии (п. 6.10.29); или аналог", Role: "airVent"));

            // Отводы патрубков ГМ теперь считаются по трассе — строка шаблона убирается.
            stationTakeoff = stationTakeoff with { Items = [.. stationTakeoff.Items.Where(i => i.Role != "elbowT"), .. pipeRows, .. extraRows] };
        }

        // Трассы от вводов в здание (ПТ_ВВОД_ЗД) до вводов в насосную (ПТ_НС_ВВОД): по порядку, в обход колонн.
        var inletRoutes = new List<InletRoute>();
        var (inletRouteZ, inletWallZ) = InletRouteLevels(design);
        if (plan.PumpRooms.Count > 0 && plan.BuildingInlets.Count > 0)
        {
            var pumpInlets = plan.PumpRooms[0].Inlets;
            var pairs = Math.Min(plan.BuildingInlets.Count, pumpInlets.Count);

            // Трассы прокладываются по очереди, каждая — в обход уже проложенных; из вариантов соединения вводов (прямой и
            // обратный порядок вводов насосной) берётся тот, где все трассы найдены, с наименьшей общей длиной.
            List<InletRoute> Attempt(IReadOnlyList<PlanPoint> targets)
            {
                var done = new List<InletRoute>();
                for (var k = 0; k < pairs; k++)
                {
                    done.Add(InletRouting.Route(plan, plan.BuildingInlets[k], targets[k], design.PumpRoom.InletRouteClearance * 1000, [.. done.Select(d => d.Path)]));
                }

                return done;
            }

            var best = new[] { pumpInlets.Take(pairs).ToList(), pumpInlets.Take(pairs).Reverse().ToList() }
                .Select(Attempt)
                .OrderBy(v => v.Sum(r => r.Issues.Count))
                .ThenBy(v => v.Sum(r => r.LengthMm))
                .First();
            issues.AddRange(best.SelectMany(r => r.Issues));
            inletRoutes.AddRange(best.Where(r => r.Issues.Count == 0));

            if (plan.BuildingInlets.Count != pumpInlets.Count)
            {
                issues.Add(new Issue("inlet.countMismatch", IssueLevel.Warning, $"Вводов в здание {plan.BuildingInlets.Count}, вводов в насосную {pumpInlets.Count}: трассы построены для {Math.Min(plan.BuildingInlets.Count, pumpInlets.Count)} пар по порядку."));
            }

            if (inletRoutes.Count > 0)
            {
                var seriesI = new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes, includeOutdoorOnly: true);

                // Отметка трассы ввода без коллизий с сетью АУП: задана в проекте — проверяется; не задана — ниже сети на
                // наибольшее нужное расхождение на пересечениях (полусумма наружных диаметров + 20 мм), с округлением вниз до 50 мм.
                var zNet = design.PipeElevation - design.PumpRoom.FloorElevation;
                var outerIn = seriesI.Pick(station.Suction.Pipe.Dn)?.OuterDiameterMm ?? station.Suction.Pipe.Dn;
                List<PipeSegment3> Segs(double z) => [.. inletRoutes.SelectMany(r => r.Path.Zip(r.Path.Skip(1)).Select(s => new PipeSegment3(s.First, s.Second, z, outerIn)))];
                var netSegs = routes.SelectMany(rt =>
                {
                    var nodes = rt.Nodes.ToDictionary(n => n.Id);
                    return rt.Segments.Concat(rt.Loops).Select(s => new PipeSegment3(nodes[s.From].Location, nodes[s.To].Location, zNet, seriesI.Pick(s.Dn)?.OuterDiameterMm ?? s.Dn));
                }).ToList();
                var need = PipeCollisions.MaxRequiredGap(Segs(0), netSegs, 20);
                var clear = Math.Floor((zNet - (need / 1000)) * 20) / 20;
                if (design.PumpRoom.InletRouteElevation is null && need > 0)
                {
                    inletRouteZ = clear;
                }

                var collisions = PipeCollisions.Check(Segs(inletRouteZ), netSegs, 20, "трасса ввода пересекает трубопровод АУП");
                issues.AddRange(collisions);
                if (collisions.Count > 0)
                {
                    issues.Add(new Issue("inlet.elevation", IssueLevel.Warning, string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Трасса ввода на {Elev(inletRouteZ)} пересекает сеть АУП ({collisions.Count} мест): без коллизий — не выше {Elev(clear)} (pumpRoom.inletRouteElevation).")));
                }


                var dnI = station.Suction.Pipe.Dn;
                var spI = seriesI.Pick(dnI);
                issues.AddRange(seriesI.Issues);
                var ruI = CultureInfo.GetCultureInfo("ru-RU");
                // Подъём у ввода в здание и опуск у насосной — вертикали и по два отвода на каждую.
                var rise = Math.Abs(inletRouteZ - inletWallZ);
                var meters = (inletRoutes.Sum(r => r.LengthMm) / 1000) + (inletRoutes.Count * 2 * rise);
                var riserElbows = rise > 0.001 ? inletRoutes.Count * 4 : 0;
                var outerI = spI?.OuterDiameterMm ?? dnI;
                stationTakeoff = stationTakeoff with
                {
                    Items = [
                        .. stationTakeoff.Items,
                        new TakeoffItem(TakeoffGroup.PumpStationPipes, spI is null ? $"Трубы стальные DN{dnI}" : string.Create(ruI, $"Трубы {spI.PipeType} DN{dnI}, Ø{spI.OuterDiameterMm:0.##}×{spI.WallMm:0.##} мм"), spI?.Standard, Math.Round(meters, 1), "м", "трасса от вводов в здание до насосной (план трассы ввода)", Role: "inletPipe"),
                        new TakeoffItem(TakeoffGroup.PumpStationFittings, $"Отвод 90° DN{dnI}", design.FittingStandards.TryGetValue("Отвод", out var esI) ? esI : null, inletRoutes.Sum(r => r.Elbows) + riserElbows, "шт.", "повороты трассы ввода, подъёмы и опуски", Role: "inletElbow"),
                        .. rise > 0.001 ? new[] { new TakeoffItem(TakeoffGroup.PumpStationValves, "Воздухоотводчик автоматический DN15, PN 16", "по ТУ изготовителя", inletRoutes.Count, "шт.", "в верхней точке трассы ввода перед опуском к насосной (п. 6.10.29); или аналог", Role: "inletVent") } : [],
                        new TakeoffItem(TakeoffGroup.PumpStationFittings, string.Create(ruI, $"Гильза стальная для прохода трубы DN{dnI} через наружную стену: Ø не менее {outerI + PumpCatalog.PipingExtras(normsDir).SleeveExtra:0} мм"), "по месту", inletRoutes.Count, "шт.", "ввод в здание; заделка — по узлу прохода (сальник), Козицина, с. 26", Role: "inletSleeve"),
                    ],
                };
            }
        }

        // Номера позиций спецификации насосной — на схеме, по роли элемента (сквозная нумерация).
        var schemePositions = SpecificationXlsx.Positions(stationTakeoff, continuous: true)
            .Where(p => p.Item.Role is not null)
            .GroupBy(p => p.Item.Role!)
            .ToDictionary(g => g.Key, g => g.First().Position); // у одной роли может быть несколько строк (гильзы разных DN)

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

        // Ошибка насосной (нет подходящей установки, нет сортамента труб, не хватает вводов/выпусков) останавливает выпуск:
        // иначе ПТ2.СО/схема уходят в работу без насоса или с фиктивным DN0 (аудит PC-5/PC-6).
        if (issues.Any(i => i.Level == IssueLevel.Error && i.Code.StartsWith("pump", StringComparison.Ordinal)))
        {
            return Stop();
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
        Write(auptDir, PlanFile, files, p => PlanDxfWriter.Write(p, layout, routes, [], summary: summary, inlets: inletNotes, basePath: drawing, supports: supports.Points, dimensions: dimensions, hydrants: hydrantPoints, axes: axes, style: new PlanDxfStyle(Font: project.Font)));
        WriteAlarmPlans(outIssues);

        Write(auptDir, PlanDxfFile, files, p => PlanDxfWriter.Write(p, layout, routes, outIssues, summary: summary, inlets: inletNotes, supports: supports.Points, dimensions: dimensions, hydrants: hydrantPoints, axes: axes, style: new PlanDxfStyle(Font: project.Font)));
        Write(auptDir, AxonometryFile, files, p => AxonometryDxfWriter.Write(
            p,
            routes,
            new Length(design.PipeElevation),
            new AxonometryStyle(Font: project.Font, SprinklersUp: !design.SprinklerDeflector.Equals("down", StringComparison.OrdinalIgnoreCase), InletNote: design.InletReference),
            SheetStamp(project, "Аксонометр", "Аксонометрическая схема трубопроводов"),
            new AxonometryCalc(worst.Route.RoomId, worst.Route.InletId, CalcPoints.Labels(worst), [.. worst.Path.Select(x => x.Id)], worst.ZoneSprinklers)));
        // Принципиальная схема насосной станции (лист основного комплекта).
        var fromTank = design.InletPressure < Need(norms, "sp485.p6_9_12.minNetworkPressure").Values["pressure"]!.Value;
        Write(auptDir, PumpSchemeFile, files, p => PumpSchemeDxfWriter.Write(
            p,
            new PumpSchemeData(
                setChoice?.Chosen is { } schemeSet ? schemeSet.MainPumps + schemeSet.StandbyPumps : station.WorkingPumps + station.StandbyPumps,
                station.Suction.Pipe.Dn,
                station.Discharge.Pipe.Dn,
                design.SupplyDn,
                station.TruckInlets,
                station.TruckInletDn,
                setChoice?.Chosen is { } ss
                    ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Н1…Н{ss.MainPumps + ss.StandbyPumps} — {ss.Designation}: основные Н1…Н{ss.MainPumps}, резервный Н{ss.MainPumps + ss.StandbyPumps}; Q = {worst.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч, H = {worst.PumpHead.Meters:0.0} м")
                    : string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Н1, Н2 — агрегаты пожарные: Q = {worst.PumpFlow.LitersPerSecond * 3.6:0.0} м³/ч, H = {worst.PumpHead.Meters:0.0} м (рабочий и резервный)"),
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"НЖ — жокей-насос, бак не менее {worst.JockeyTankMinLiters:0} л"),
                fromTank ? "Из пожарного резервуара" : "Из наружной сети",
                [
                    "Резервный агрегат включается автоматически при невыходе рабочего на режим или его отключении (п. 6.10.3 СП 485).",
                    "Марки агрегатов, жокей-насоса и шкафа управления — по каталогу производителя по требуемым Q и H; DN обвязки — по скорости воды (п. 6.7.1.37).",
                    setChoice?.Chosen is null
                        ? "Затворы на линиях агрегатов и перед узлом управления — с контролем положения; приборы — через трёхходовые краны (пп. 6.8.7, 6.8.10)."
                        : "Затворы перед узлом управления — с контролем положения (п. 6.8.7); арматура обвязки и её датчики — по паспорту комплектной установки (А-05); приборы — через трёхходовые краны (п. 6.8.10).",
                    "Оборудование, кроме оросителей, приборов и трубопроводов, окрашивается в красный цвет (п. 6.8.11).",
                    "Номера позиций — по спецификации насосной станции ПТ2.СО; фланцы и крепёж — по месту (ГОСТ 21.601, п. 9.4).",
                ],
                schemePositions,
                sections,
                // Обозначения — только у устройств с буквенной частью в каталоге; без неё на чертёж попал бы сырой id.
                Marks: controlPoints.Where(cp => cp.Role is not null && cp.Designation != cp.Id).GroupBy(cp => cp.Role!)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(cp => cp.Designation).Distinct()]),
                Inlets: Math.Max(1, inletCount),
                PumpLineSensors: setChoice?.Chosen is null),
            SheetStamp(project, "Принципиальная", "Принципиальная схема насосной станции пожаротушения"),
            project.Font));
        // План насосной: оборудование по расстановке, номера позиций ПТ2.СО, принятые проходы и допущения.
        if (roomPlan is { Items.Count: > 0 })
        {
            int? PosOf(string role) => schemePositions.TryGetValue(role, out var n) ? n : null;
            if (piping is null)
            {
                issues.Add(new Issue("pump.pipingMissing", IssueLevel.Warning, "Трассы обвязки не построены (нет ввода в насосную или правил прокладки в data/catalog/pump_room.json): лист «План обвязки насосной станции» не выпускается.", roomPlan.Room.Id));
            }
            var board = Need(norms, "sp485.p6_10_16.passages").Values["switchboard"]!.Value * 1000;
            var equipment = roomPlan.Items.Select(i => new PumpRoomEquipment(
                i.Label, i.X0, i.Y0, i.X1, i.Y1,
                i.Kind switch
                {
                    StationItemKind.PumpSet or StationItemKind.Cabinet => PosOf("pumpSet"),
                    StationItemKind.ControlUnit => PosOf("cu"),
                    _ => PosOf("jockey"),
                },
                i.Front,
                i.Front is null ? 0 : board)
            {
                // Марки насосов установки (основные и резервные) и жокей-насоса — как на принципиальной схеме.
                Pumps = i.Kind switch
                {
                    StationItemKind.PumpSet when setChoice?.Chosen is { } ps => [.. Enumerable.Range(1, ps.MainPumps + ps.StandbyPumps).Select(n => $"Н{n}")],
                    StationItemKind.Jockey => ["НЖ"],
                    _ => [],
                },
            }).ToList();
            var pr1 = design.PumpRoom;
            var features = PumpRoomFeatures.Build(roomPlan.Room.Outline, roomPlan.Items, roomPlan.Room.Door, pr1.PitSize * 1000, PumpCatalog.PipingExtras(normsDir).FoundationMargin);
            var sleeveExtraD = PumpCatalog.PipingExtras(normsDir).SleeveExtra;
            var seriesD = new PipeSeries(norms, design.PipeSeriesStandard, pipeSizes, includeOutdoorOnly: true);
            var sleeves = (piping?.Runs ?? [])
                .SelectMany(r => new[] { r.Path[0], r.Path[^1] }.Where(pt => IsWall(roomPlan.Room.Outline, pt)).Select(pt => (pt, (seriesD.Pick(r.Dn)?.OuterDiameterMm ?? r.Dn) + sleeveExtraD, $"гильза Ø{(seriesD.Pick(r.Dn)?.OuterDiameterMm ?? r.Dn) + sleeveExtraD:0}")))
                .ToList();
            issues.AddRange(seriesD.Issues);
            var valves = new List<(PlanPoint, double, string, int?)>();
            foreach (var r in piping?.Runs ?? [])
            {
                if (r.Name.StartsWith("Всасывающий", StringComparison.Ordinal))
                {
                    valves.Add(Along(r.Path, 700, false, "gate", PosOf("inletValve")));
                }
                else if (r.Name.StartsWith("Напорный к УУ", StringComparison.Ordinal))
                {
                    valves.Add(Along(r.Path, 700, true, "gate", PosOf("cuValve")));
                    valves.Add(Along(r.Path, 0, false, "vent", PosOf("airVent")));
                }
                else if (r.Name.StartsWith("Выпуск секции", StringComparison.Ordinal))
                {
                    valves.Add(Along(r.Path, 700, false, "gate", PosOf("cuValve")));
                }
                else if (r.Name.StartsWith("Патрубок ГМ", StringComparison.Ordinal))
                {
                    valves.Add(Along(r.Path, 700, false, "check", PosOf("checkT")));
                    valves.Add(Along(r.Path, 1400, false, "gate", PosOf("valveT")));
                }
            }

            var roomDrawing = new PumpRoomDrawing(roomPlan.Room.Outline, roomPlan.Room.Door, [.. equipment.OrderBy(e => e.Label == roomPlan.Items[0].Label ? 0 : 1)], [
                    "Экспликация: " + string.Join("; ", equipment.GroupBy(e => e.Position).Select(g => $"{g.Key?.ToString(CultureInfo.InvariantCulture) ?? "—"} — {string.Join(", ", g.Select(e => e.Label).Distinct())}")) + ".",
                    .. roomPlan.Rules.Select(r => $"Проход: {r} (п. 6.10.16 СП 485)."),
                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Отметка пола насосной {pr1.FloorElevation:+0.000;−0.000;0.000}; уровень земли {pr1.GroundElevation:+0.000;−0.000}; стены — {pr1.WallThickness * 1000:0} мм (условно, по архитектурному разделу)."),
                    string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Пол — с уклоном к приямку {pr1.PitSize:0.0} × {pr1.PitSize:0.0} м глубиной {pr1.PitDepth:0.0} м; дренажные насосы в приямке (п. 6.10.23)."),
                    "Фундамент установки — по паспорту изготовителя; при отсутствии сведений масса фундамента не менее 4 масс агрегатов (п. 6.10.26). Электродвигатели — не ниже 0,5 м от пола (п. 6.10.22).",
                    "Габариты установки и прибора управления — по каталогу изготовителя; узлов управления и жокей-насоса — приняты условно (data/catalog/pump_room.json), уточнить по выбранному оборудованию.",
                    "Свободная зона у двери 1 × 1 м — правило программы.",
                    "Номера позиций — по спецификации ПТ2.СО.",
                ])
            {
                WallThicknessMm = pr1.WallThickness * 1000,
                Explication = ("1", "Насосная станция пожаротушения", roomPlan.Room.Outline.AreaMm2 / 1e6, pr1.Height, "по СП 12.13130 (смежный раздел)"),
                Pit = features.Pit,
                PitLabel = string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Приямок {pr1.PitSize:0.0}×{pr1.PitSize:0.0}, h = {pr1.PitDepth:0.0} м; дренажные насосы — поз. {PosOf("drainPump")?.ToString(CultureInfo.InvariantCulture) ?? "—"}"),
                Foundation = features.Foundation,
                Sleeves = sleeves,
                WallMarks = [
                    .. plan.PumpRooms[0].Inlets.Select((wi, k) => (wi, string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Ввод {k + 1} DN{station.Suction.Pipe.Dn}, {Elev(pr1.GroundElevation - pr1.InletDepth)}"))),
                    .. plan.PumpRooms[0].Outlets.Select((o, k) => (o, string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Выпуск, секция {k + 1}, {Elev(design.PipeElevation)}"))),
                    .. (piping?.Runs ?? []).Where(r => r.Name.StartsWith("Патрубок ГМ", StringComparison.Ordinal)).Select(r => (r.Path[0], $"ГМ-{station.TruckInletDn}")),
                ],
            };
            List<string> pipingNotes =
            [
                "Трубопроводы — стальные на сварке; отметки — оси труб от пола насосной; подписи «DN, отметка».",
                "Всасывающие трубопроводы — с подъёмом к насосной установке не менее 1:200, переходы — эксцентрические, в верхних точках напорных линий — воздухоотводчики (п. 6.10.29, 6.7.1.22).",
                "Патрубки ГМ — с обратным клапаном и опломбированной открытой задвижкой, вывод на фасад на (1,20 ± 0,15) м от отмостки (пп. 6.10.17, 6.10.18).",
                .. piping is null ? Array.Empty<string>() : [$"Отступы от стен и прокладка на кронштейнах — по рекомендациям: {PumpCatalog.Piping(normsDir)!.Value.Source}"],
                "Номера позиций — по спецификации ПТ2.СО.",
            ];
            if (piping is not null)
            {
                Write(auptDir, PumpRoomPipingFile, files, p => PumpRoomDxfWriter.Write(
                    p,
                    roomDrawing with
                    {
                        Notes = pipingNotes,
                        Piping = true,
                        Pipes = [.. (piping?.Runs ?? []).Select(r => new PumpRoomPipe(string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"DN{r.Dn} {Elev((r.RunHeightMm / 1000) + pr1.FloorElevation)}"), r.Path)
                    {
                        Height = r.RunHeightMm / 1000,
                        // Стояки — где отметка конца отличается от отметки прокладки (подъём к патрубку, опуск у стены).
                        Risers = [
                            .. Math.Abs(r.FromHeightMm - r.RunHeightMm) > 1 ? new[] { (r.Path[0], (Math.Max(r.FromHeightMm, r.RunHeightMm) / 1000) + pr1.FloorElevation, (Math.Min(r.FromHeightMm, r.RunHeightMm) / 1000) + pr1.FloorElevation) } : [],
                            .. Math.Abs(r.ToHeightMm - r.RunHeightMm) > 1 ? new[] { (r.Path[^1], (Math.Max(r.ToHeightMm, r.RunHeightMm) / 1000) + pr1.FloorElevation, (Math.Min(r.ToHeightMm, r.RunHeightMm) / 1000) + pr1.FloorElevation) } : [],
                        ],
                    })],
                        Valves = valves,
                    },
                SheetStamp(project, "План обвязки", "План обвязки насосной станции") with { Name = "План обвязки насосной станции" },
                project.Font));
            }

            // Разрез 1-1 вдоль X: оборудование по высоте (установка — на фундаменте 150 мм), трубы на отметках с подъёмами и опусками.
            const double foundationH = 0.15;
            var rv = roomPlan.Room.Outline.Vertices;
            var blocks = new List<SectionBlock>();
            if (features.Foundation is { } fnd)
            {
                blocks.Add(new SectionBlock("фундамент", fnd.X0, fnd.X1, 0, foundationH));
            }

            foreach (var i in roomPlan.Items)
            {
                var (bottom, height, pumps, pos) = i.Kind switch
                {
                    StationItemKind.PumpSet => (foundationH, (setChoice?.Chosen?.Hydraulic.HeightMm ?? 1300) / 1000, setChoice?.Chosen is { } cs ? cs.MainPumps + cs.StandbyPumps : 0, PosOf("pumpSet")),
                    StationItemKind.Cabinet => (0.0, (setChoice?.Chosen?.Cabinet.HeightMm ?? 1460) / 1000, 0, PosOf("pumpSet")),
                    StationItemKind.ControlUnit => (0.0, (roomData?.ControlUnit.HeightMm ?? 1800) / 1000, 0, PosOf("cu")),
                    _ => (0.0, (roomData?.Jockey.HeightMm ?? 900) / 1000, 0, PosOf("jockey")),
                };
                blocks.Add(new SectionBlock(i.Label, i.X0, i.X1, bottom, bottom + height, pumps, pos));
            }

            var sectionPipes = (piping?.Runs ?? []).Select(r =>
            {
                var xs = r.Path.Select(pt => pt.XMm).ToList();
                var verticals = new List<(double, double, double)>();
                if (Math.Abs(r.FromHeightMm - r.RunHeightMm) > 1)
                {
                    verticals.Add((r.Path[0].XMm, r.FromHeightMm / 1000, r.RunHeightMm / 1000));
                }

                if (Math.Abs(r.ToHeightMm - r.RunHeightMm) > 1)
                {
                    verticals.Add((r.Path[^1].XMm, r.RunHeightMm / 1000, r.ToHeightMm / 1000));
                }

                return new SectionPipe($"DN{r.Dn}", xs.Min(), xs.Max(), r.RunHeightMm / 1000, verticals);
            }).ToList();
            // Аксонометрия обвязки: трубы в пространстве (опуск у стены, прокладка, подъём к патрубку), оборудование, арматура.
            var axoPipes = (piping?.Runs ?? []).Select(r => new AxonoPipe(
                $"DN{r.Dn}",
                [
                    new Point3(r.Path[0].XMm, r.Path[0].YMm, r.FromHeightMm / 1000),
                    .. r.Path.Select(q => new Point3(q.XMm, q.YMm, r.RunHeightMm / 1000)),
                    new Point3(r.Path[^1].XMm, r.Path[^1].YMm, r.ToHeightMm / 1000),
                ])).ToList();
            var axoBlocks = roomPlan.Items.Zip(blocks.Where(b => b.Label != "фундамент"), (i, b) => new AxonoBlock(b.Position is { } bp ? $"{bp}. {b.Label}" : b.Label, i.X0, i.Y0, i.X1, i.Y1, b.Bottom, b.Top)).ToList();
            var axoValves = new List<AxonoValve>();
            foreach (var r in piping?.Runs ?? [])
            {
                var z = r.RunHeightMm / 1000;
                foreach (var (pt, _, kind, pos) in valves.Where(v => r.Path.Zip(r.Path.Skip(1)).Any(s => OnSegment(v.Item1, s.First, s.Second))))
                {
                    axoValves.Add(new AxonoValve(new Point3(pt.XMm, pt.YMm, z), kind, pos));
                }
            }

            Write(auptDir, PumpRoomAxonometryFile, files, p => PumpRoomAxonometryDxfWriter.Write(
                p,
                new PumpRoomAxonometry(axoPipes, axoBlocks, axoValves, [
                    "Фронтальная изометрия (ГОСТ 2.317): ось Y — под 45°, без сокращения; отметки — оси труб от пола насосной.",
                    "Трубопроводы — стальные на сварке; обозначение — В2 (ГОСТ 21.205); номера позиций арматуры и оборудования — по ПТ2.СО.",
                    "Всасывающие — с подъёмом к насосной установке не менее 1:200, в верхних точках напорных линий — воздухоотводчики (п. 6.10.29).",
                    "Обвязка агрегатов внутри установки — по паспорту изготовителя (в комплекте установки).",
                ], pr1.FloorElevation),
                SheetStamp(project, "Аксонометрическая схема обвязки", "Аксонометрическая схема обвязки насосной станции") with { Name = "Аксонометрия обвязки насосной" },
                project.Font));

            Write(auptDir, PumpRoomSectionFile, files, p => PumpRoomSectionDxfWriter.Write(
                p,
                new PumpRoomSection(
                    rv.Min(q => q.XMm), rv.Max(q => q.XMm), pr1.WallThickness * 1000, pr1.FloorElevation, pr1.Height, pr1.GroundElevation - pr1.FloorElevation,
                    features.Pit is { } sp ? (sp.X0, sp.X1, pr1.PitDepth) : null,
                    blocks,
                    sectionPipes,
                    [
                        "Разрез — вид вдоль помещения насосной; все трубы спроецированы на плоскость разреза, отметки — оси труб.",
                        string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Ввод — на отметке {Elev(pr1.GroundElevation - pr1.InletDepth)} ({pr1.InletDepth:0.0} м от уровня земли {Elev(pr1.GroundElevation)}); трубопроводы АУП — на {Elev(design.PipeElevation)}."),
                        "Всасывающие трубопроводы — с подъёмом к насосной установке не менее 1:200 (п. 6.10.29); переходы — эксцентрические.",
                        "Электродвигатели насосов — не ниже 0,5 м от пола (п. 6.10.22); установка — на фундаменте по паспорту изготовителя (п. 6.10.26).",
                        "Высоты узла управления и жокей-насоса — условные (data/catalog/pump_room.json).",
                    ]),
                SheetStamp(project, "Разрез", "Разрез 1-1 насосной станции") with { Name = "Разрез 1-1 насосной станции" },
                project.Font));

            Write(auptDir, PumpRoomPlanFile, files, p => PumpRoomDxfWriter.Write(
                p,
                roomDrawing,
                SheetStamp(project, "План насосной", "План насосной станции пожаротушения"),
                project.Font));
        }

        // План трассы от ввода в здание до насосной.
        if (inletRoutes.Count > 0)
        {
            var prI = design.PumpRoom;
            var (elevI, inletElevI) = (inletRouteZ, inletWallZ);
            Write(auptDir, InletRouteFile, files, p => InletRouteDxfWriter.Write(
                p,
                new InletRouteDrawing(
                    [.. plan.Rooms.Select(r => r.Contour)],
                    [.. plan.Columns.Select(c => c.Outline)],
                    plan.PumpRooms[0].Outline,
                    [.. inletRoutes.Select(r => new InletRouteLine(string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"DN{station.Suction.Pipe.Dn}, {Elev(elevI)}"), r.Path, r.LengthMm)
                    {
                        Risers = Math.Abs(elevI - inletElevI) > 0.001 ? [(r.Path[0], Math.Max(elevI, inletElevI), Math.Min(elevI, inletElevI)), (r.Path[^1], Math.Max(elevI, inletElevI), Math.Min(elevI, inletElevI))] : [],
                        Vent = Math.Abs(elevI - inletElevI) > 0.001 && elevI > inletElevI ? r.Path[^2] : null,
                    })],
                    [
                        string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Вводов — {inletRoutes.Count}, каждый на полный расчётный расход (п. 6.10.27 СП 485); трубы DN{station.Suction.Pipe.Dn}, стальные на сварке."),
                        string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Трасса по стоянке — под перекрытием на отметке {Elev(elevI)} (от пола насосной); у ввода в здание — подъём с {Elev(inletElevI)}, у насосной — опуск до {Elev(inletElevI)}."),
                        "В верхней точке трассы перед опуском к насосной — автоматический воздухоотводчик (п. 6.10.29); горизонтальные участки — с уклоном не менее 1:200 в сторону подъёма к воздухоотводчику.",
                        string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Трасса — в обход колонн с отступом не менее {prI.InletRouteClearance:0.0} м; привязки поворотов — к ближайшим стенам."),
                        "Проход через наружную стену — в гильзе с сальниковым уплотнением; узел ввода и водомерный узел — по ТУ водоснабжающей организации.",
                        "Продолжение трасс в насосной — см. план обвязки насосной станции.",
                    ]),
                SheetStamp(project, "План трассы", "План трассы водопровода от ввода до насосной станции") with { Name = "План трассы от ввода до насосной" },
                project.Font));
        }

        if (stationTakeoff.Items.Count > 0)
        {
            Write(auptDir, PumpStationSpecFile, files, p => SpecificationDocx.Write(p, stationTakeoff, Stamp(project, "ПТ2.СО", "Спецификация оборудования, изделий и материалов насосной станции"), project.Font, continuous: true));
            Write(auptDir, PumpStationSpecXlsxFile, files, p => SpecificationXlsx.Write(p, stationTakeoff, new SpecificationHeader($"{project.Name}. Насосная станция. Спецификация оборудования, изделий и материалов"), project.Font, continuous: true));
        }

        if (controlPoints.Count > 0)
        {
            // Поз. — как на схеме насосной; шкаф комплектной установки — строка установки (роль pumpSet).
            var signalPositions = new Dictionary<string, int>(schemePositions);
            if (!signalPositions.ContainsKey("cabinet") && schemePositions.TryGetValue("pumpSet", out var setPosition))
            {
                signalPositions["cabinet"] = setPosition;
            }

            Write(alarmDir, SignalListFile, files, p => SignalListXlsx.Write(p, controlPoints, $"{project.Name}. Перечень сигналов АУПТ в АПС", project.Font, signalPositions));
        }

        WriteAlarmDocs();

        Write(auptDir, SpecificationFile, files, p => SpecificationXlsx.Write(p, takeoff, new SpecificationHeader($"{project.Name}. Спецификация оборудования, изделий и материалов"), project.Font));
        Write(auptDir, SpecificationDocxFile, files, p => SpecificationDocx.Write(p, takeoff, Stamp(project, "ПТ.СО", "Спецификация оборудования, изделий и материалов"), project.Font));

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
                PumpCatalog.Piping(normsDir)?.Source,
                userTank,
                design.Jockey),
            norms);
        Write(auptDir, NoteFile, files, p => NoteDocx.Write(p, note, Stamp(project, "ПЗ", "Пояснительная записка"), project.Font));

        // Лист «Общие данные» (ГОСТ Р 21.101): ведомости, основные показатели, общие указания.
        var general = GeneralDataComposer.Compose(
            new GeneralDataInputs(project.Name, project.Sheets, plan, layout, worst, takeoff, temperature.Choice, hydrantPoints.Count, design.PipeSeriesStandard, design.WeldStandard, design.FittingStandards, Stamp(project, "ПТ.СО", "").Designation, stationTakeoff.Items.Count > 0 ? Stamp(project, "ПТ2.СО", "").Designation : null),
            norms,
            GeneralDataComposer.DocumentsPath(normsDir));
        Write(auptDir, GeneralDataFile, files, p => GeneralDataDocx.Write(p, general, Stamp(project, "ПТ", "Общие данные") with { SheetCount = project.Sheets.Count }, project.Font));
        files.Add(WriteIssues(outDir, issues));
        var pumpSummary = setChoice?.Chosen is { } summarySet
            ? new PumpSetSummary(
                summarySet.Designation,
                summarySet.Pump,
                summarySet.MainPumps,
                summarySet.StandbyPumps,
                summarySet.PowerKw,
                setChoice.HeadAtDuty,
                summarySet.Maker,
                worst.PumpFlow.LitersPerSecond * 3.6,
                worst.PumpHead.Meters,
                [.. summarySet.PumpCurve.Select(p => new PumpCurvePoint(p.QM3h, p.HM))])
            : null;
        var pumpRoomSummary = PumpRoomSummary(plan, setChoice?.Chosen);
        return new PipelineResult(true, files, issues, worst, pumpSummary, pumpRoomSummary);
    }

    // Помещение насосной и габариты установки для карточки главного окна: контур в плане (bbox, м и м²),
    // разметка вводов/выпусков/патрубков ГМ и размеры выбранной установки из каталога. Никаких новых чисел:
    // всё либо прочитано из чертежа, либо взято из data/catalog/pump_sets_*.json.
    private static PumpStationSummary PumpRoomSummary(FloorPlan plan, PumpSet? set)
    {
        var room = plan.PumpRooms.Count > 0 ? plan.PumpRooms[0] : null;
        double? length = null;
        double? width = null;
        double? area = null;
        if (room is { Outline.Vertices.Count: > 0 } withRoom)
        {
            var vertices = withRoom.Outline.Vertices;
            var x0 = vertices.Min(p => p.XMm);
            var y0 = vertices.Min(p => p.YMm);
            var x1 = vertices.Max(p => p.XMm);
            var y1 = vertices.Max(p => p.YMm);
            length = Math.Round((x1 - x0) / 1000, 1);
            width = Math.Round((y1 - y0) / 1000, 1);
            area = Math.Round((x1 - x0) * (y1 - y0) / 1_000_000, 2);
        }

        return new PumpStationSummary(
            room?.Id,
            length,
            width,
            area,
            room?.Inlets.Count ?? 0,
            room?.Outlets.Count ?? 0,
            room?.TruckInlets.Count ?? 0,
            set?.Designation ?? "",
            set?.MainPumps ?? 0,
            set?.StandbyPumps ?? 0,
            set?.Hydraulic ?? new Footprint(0, 0, 0),
            set?.Cabinet ?? new Footprint(0, 0, 0),
            set?.CollectorDn ?? 0,
            set?.CollectorAxisMm ?? 0);
    }

    // Комплектная установка: обозначение из проекта (design.pumpStation.setDesignation) — выбор вручную из каталога с проверкой
    // напора по кривой; пустое — автоподбор по требуемым Q и H (PumpSetSelection). Неизвестное обозначение — Error и продолжение
    // автоподбором; недостаточный напор — Error (документы не выпускаются общим Stop по Error насосной). Исключений нет.
    private static PumpSetChoice? ChoosePumpSet(string designation, IReadOnlyList<PumpSet> sets, DesignResult worst, bool hasTemplate, List<Issue> issues)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var qM3h = worst.PumpFlow.LitersPerSecond * 3.6;
        if (designation.Length > 0)
        {
            if (sets.FirstOrDefault(s => string.Equals(s.Designation, designation, StringComparison.Ordinal)) is not { } set)
            {
                issues.Add(new Issue("pump.setNotFound", IssueLevel.Error, $"Установка «{designation}» не найдена в каталоге (data/catalog/pump_sets_*.json): выполнен автоподбор по требуемым Q и H. Проверьте обозначение в design.pumpStation.setDesignation."));
            }
            else
            {
                var headAtDuty = PumpSetSelection.HeadAt(set, qM3h);
                if (headAtDuty is not { } head || head < worst.PumpHead.Meters)
                {
                    var headText = headAtDuty is { } h
                        ? string.Create(ru, $"напор по кривой H = {h:0.0} м")
                        : "подача Q вне диапазона кривой установки (напор не определён)";
                    issues.Add(new Issue("pump.setInsufficient", IssueLevel.Error, string.Create(ru, $"Установка {set.Designation} не обеспечивает требуемый напор: {headText} при Q = {qM3h:0.0} м³/ч, требуется H = {worst.PumpHead.Meters:0.0} м. Выберите другую установку в design.pumpStation.setDesignation.")));
                    return null;
                }

                issues.Add(new Issue("pump.setChosen", IssueLevel.Info, string.Create(ru, $"Выбрана вручную из каталога установка {set.Designation}: напор по кривой {head:0.0} м при требуемом {worst.PumpHead.Meters:0.0} м; потери в обвязке установки каталогом не учтены — сверить с изготовителем.")));
                return new PumpSetChoice(set, head, []);
            }
        }

        if (sets.Count > 0 && hasTemplate)
        {
            var auto = PumpSetSelection.Choose(sets, qM3h, worst.PumpHead.Meters);
            issues.AddRange(auto.Issues);
            if (auto.Chosen is { } picked)
            {
                issues.Add(new Issue("pump.setChosen", IssueLevel.Info, string.Create(ru, $"Подобрана установка {picked.Designation}: напор по каталогу {auto.HeadAtDuty:0.0} м при требуемом {worst.PumpHead.Meters:0.0} м; потери в обвязке установки каталогом не учтены — сверить с изготовителем.")));
            }

            return auto;
        }

        return null;
    }

    // Жокей-насос — решение проектировщика (В-07): норма расход и уставки не задаёт. Проверяются только заданные данные:
    // отрицательный расход — Error; уставки включения/отключения несогласованы — Warning; валидные данные — Info.
    private static IEnumerable<Issue> JockeyIssues(ProjectJockey? jockey)
    {
        if (jockey is null)
        {
            yield break;
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var valid = true;
        if (jockey.FlowLitersPerSecond is { } flow && flow <= 0)
        {
            valid = false;
            yield return new Issue("jockey.flow", IssueLevel.Error,
                "Расход жокей-насоса должен быть больше 0. Расход/уставки норма не задаёт — В-07: уточните по опросному листу изготовителя.");
        }

        var start = jockey.StartPressureMpa;
        var stop = jockey.StopPressureMpa;
        if (start is { } startValue && stop is { } stopValue && startValue >= stopValue)
        {
            valid = false;
            yield return new Issue("jockey.settings", IssueLevel.Warning, string.Create(ru,
                $"Давление включения жокей-насоса {startValue:0.###} МПа не меньше давления отключения {stopValue:0.###} МПа: уточните уставки — норма их не задаёт (В-07)."));
        }
        else if (stop is not null && start is null)
        {
            valid = false;
            yield return new Issue("jockey.settings", IssueLevel.Warning,
                "Задано давление отключения жокей-насоса без давления включения: уточните уставки — норма их не задаёт (В-07).");
        }

        if (!valid)
        {
            yield break;
        }

        var parts = new List<string>();
        if (jockey.FlowLitersPerSecond is { } flowValue)
        {
            parts.Add(string.Create(ru, $"расход {flowValue:0.###} л/с"));
        }

        if (start is { } startText)
        {
            parts.Add(string.Create(ru, $"включение при P = {startText:0.###} МПа"));
        }

        if (stop is { } stopText)
        {
            parts.Add(string.Create(ru, $"отключение при P = {stopText:0.###} МПа"));
        }

        if (parts.Count > 0)
        {
            yield return new Issue("jockey.manual", IssueLevel.Info, string.Create(ru,
                $"Параметры жокей-насоса заданы решением проектировщика: {string.Join(", ", parts)}. Норма расход/уставки не задаёт (В-07)."));
        }
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

    // Условные точки на стенах помещения (вводы/выпуски), когда разметка ПТ_НС_ВВОД/ВЫПУСК не сделана: равномерно по
    // периметру прямоугольника помещения; start — доля периметра от левого нижнего угла.
    private static IReadOnlyList<PlanPoint> AutoWallPoints(Polygon room, int count, double start)
    {
        var v = room.Vertices;
        var (x0, y0, x1, y1) = (v.Min(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.XMm), v.Max(p => p.YMm));
        var (w, h) = (x1 - x0, y1 - y0);
        var perimeter = 2 * (w + h);
        PlanPoint At(double t)
        {
            var d = ((t % 1) + 1) % 1 * perimeter;
            if (d <= w)
            {
                return new PlanPoint(x0 + d, y0);
            }

            d -= w;
            if (d <= h)
            {
                return new PlanPoint(x1, y0 + d);
            }

            d -= h;
            if (d <= w)
            {
                return new PlanPoint(x1 - d, y1);
            }

            d -= w;
            return new PlanPoint(x0, y1 - d);
        }

        return [.. Enumerable.Range(0, count).Select(k => At(start + (k / (double)Math.Max(1, count))))];
    }

    // Отметка со знаком по правилам чертежей: +3,300 / −1,600 / ±0,000. Однозначная форма "+0.000" для
    // отрицательных чисел давала «-+1,600» (культура подставляет минус к литералу «+»).
    private static string Elev(double m) => m.ToString("+0.000;−0.000;0.000", CultureInfo.GetCultureInfo("ru-RU"));

    // Запись нормы, без которой расчёт насосной не выпускается: молчаливая подстановка (??) скрывает ошибку данных.
    private static NormRecord Need(JsonNormsProvider norms, string id) =>
        norms.Find(id) ?? throw new InvalidOperationException($"В данных норм нет записи {id}.");

    // Отметки трассы ввода, м от пола насосной: прокладка (под перекрытием — на отметке трубопроводов АУП) и вход в стену.
    private static (double Route, double Inlet) InletRouteLevels(ProjectDesign design)
    {
        var pr = design.PumpRoom;
        var inlet = pr.GroundElevation - pr.InletDepth - pr.FloorElevation;
        var route = pr.InletRouteElevation ?? (design.PipeElevation > 0 ? design.PipeElevation - pr.FloorElevation - 0.1 : pr.Height - 0.5);
        return (route, inlet);
    }

    // Точка на отрезке a–b (по осям, с допуском 1 мм).
    private static bool OnSegment(PlanPoint p, PlanPoint a, PlanPoint b) =>
        p.XMm >= Math.Min(a.XMm, b.XMm) - 1 && p.XMm <= Math.Max(a.XMm, b.XMm) + 1 && p.YMm >= Math.Min(a.YMm, b.YMm) - 1 && p.YMm <= Math.Max(a.YMm, b.YMm) + 1
        && Math.Abs(((b.XMm - a.XMm) * (p.YMm - a.YMm)) - ((b.YMm - a.YMm) * (p.XMm - a.XMm))) < 1 * Math.Max(1, Math.Abs(b.XMm - a.XMm) + Math.Abs(b.YMm - a.YMm));

    // Точка на стене контура (не дальше 1 мм).
    private static bool IsWall(Polygon room, PlanPoint p) => room.DistanceTo(p) < 1;

    // Точка на ломаной на расстоянии dist от начала (или от конца) и направление участка, рад.
    private static (PlanPoint, double, string, int?) Along(IReadOnlyList<PlanPoint> path, double dist, bool fromEnd, string kind, int? position)
    {
        var pts = fromEnd ? path.Reverse().ToList() : path.ToList();
        for (var i = 1; i < pts.Count; i++)
        {
            var (a, b) = (pts[i - 1], pts[i]);
            var len = Math.Sqrt(Math.Pow(b.XMm - a.XMm, 2) + Math.Pow(b.YMm - a.YMm, 2));
            if (dist <= len || i == pts.Count - 1)
            {
                var t = len > 0 ? Math.Min(dist, len) / len : 0;
                return (new PlanPoint(a.XMm + ((b.XMm - a.XMm) * t), a.YMm + ((b.YMm - a.YMm) * t)), Math.Atan2(b.YMm - a.YMm, b.XMm - a.XMm), kind, position);
            }

            dist -= len;
        }

        return (pts[0], 0, kind, position);
    }

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

    // Скорость подбора диаметров по Б.5, когда в проекте не задана (data/catalog/pipe_sizes.json, defaultDesignVelocity:
    // практика проектирования, не норма). null — диаметры по табл. Б.4.
    private static double? DesignVelocityDefault(string normsDir)
    {
        var path = Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "pipe_sizes.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("defaultDesignVelocity", out var v) ? v.GetDouble() : null;
    }

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
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        write(path);
        files.Add(path);
    }


    /// <summary>Зоны раскладки ИП из раздела alarm проекта: помещение по имени с чертежа («*» — все), высота — из зоны или design.roomHeight.</summary>
    /// <summary>Уровень АПС в прогоне: чертёж уровня, его зоны (ID помещений с префиксом уровня, кроме первого) и отметка.</summary>
    private sealed class AlarmLevel
    {
        public required int Index { get; init; }

        public required string Name { get; init; }

        public double? Elevation { get; init; }

        public required FloorPlan Plan { get; init; }

        public List<DetectorZone> Zones { get; } = [];
    }

    /// <summary>
    /// Уровни по alarm.levels: первый без своего чертежа — основной план проекта; остальные чертежи читаются тем же импортом.
    /// Без списка — один уровень на основном чертеже. Нечитаемый чертёж уровня — ошибка, уровень пропускается.
    /// </summary>
    private static List<AlarmLevel> AlarmLevels(ProjectFile project, FloorPlan plan, string projectPath, List<Issue> issues)
    {
        var declared = project.Alarm?.Levels ?? [];
        if (declared.Count == 0)
        {
            return [new AlarmLevel { Index = 0, Name = "", Plan = plan }];
        }

        var levels = new List<AlarmLevel>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        foreach (var (l, i) in declared.Select((l, i) => (l, i)))
        {
            FloorPlan? levelPlan = plan;
            if (l.Drawing is { Length: > 0 } file && !string.Equals(file, project.Drawing, StringComparison.OrdinalIgnoreCase))
            {
                var import = FloorPlanImporter.Import(Path.GetFullPath(Path.Combine(dir, file)), requireInlet: false);
                issues.AddRange(import.Issues.Select(x => x with { Message = $"Уровень «{l.Name}»: {x.Message}" }));
                levelPlan = import.Plan;
            }

            if (levelPlan is null)
            {
                issues.Add(new Issue("aps.level.noDrawing", IssueLevel.Error, $"АПС: чертёж уровня «{l.Name}» ({l.Drawing}) не прочитан — уровень пропущен."));
                continue;
            }

            levels.Add(new AlarmLevel { Index = levels.Count, Name = l.Name.Length > 0 ? l.Name : string.Create(CultureInfo.InvariantCulture, $"уровень {i + 1}"), Elevation = l.Elevation, Plan = levelPlan });
        }

        if (levels.Count == 0)
        {
            levels.Add(new AlarmLevel { Index = 0, Name = "", Plan = plan });
        }

        return levels;
    }

    /// <summary>
    /// Ведомость чертежей тома АПС: при нескольких уровнях лист «План…» повторяется на каждый уровень с его именем,
    /// номера следующих листов сдвигаются. Один уровень — ведомость проекта как есть.
    /// </summary>
    private static IReadOnlyList<ProjectSheet> AlarmSheets(ProjectFile project, List<AlarmLevel> levels)
    {
        var sheets = project.Alarm?.Sheets ?? [];
        if (levels.Count <= 1 || sheets.Count(s => s.Name.StartsWith("План", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return sheets;
        }

        var result = new List<ProjectSheet>();
        var shift = 0;
        foreach (var s in sheets)
        {
            var number = int.TryParse(s.Sheet, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
            if (s.Name.StartsWith("План", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var level in levels)
                {
                    result.Add(new ProjectSheet(number is { } p ? (p + level.Index).ToString(CultureInfo.InvariantCulture) : s.Sheet, $"{s.Name}. {level.Name}", s.Note));
                }

                shift = levels.Count - 1;
            }
            else
            {
                result.Add(number is { } p ? s with { Sheet = (p + shift).ToString(CultureInfo.InvariantCulture) } : s);
            }
        }

        return result;
    }

    private static List<DetectorZone> AlarmZones(ProjectFile project, AlarmLevel level, ProjectDesign design, string normsDir, List<Issue> issues, bool multiLevel)
    {
        var plan = level.Plan;
        var zones = new List<DetectorZone>();
        var catalogPath = EquipmentSet.PathFor(normsDir, AlarmDetectorCatalog.File);
        var catalog = (project.Alarm?.Zones.Count ?? 0) > 0 && File.Exists(catalogPath) ? AlarmDetectorCatalog.Load(catalogPath) : [];
        if ((project.Alarm?.Zones.Count ?? 0) > 0 && catalog.Count == 0 && level.Index == 0)
        {
            issues.Add(new Issue("aps.detector.noCatalog", IssueLevel.Warning, $"Нет каталога извещателей {AlarmDetectorCatalog.File}: модели ИП не выбраны, их ток в расчёте АКБ не учтён."));
        }

        var levelTag = multiLevel ? $" ({level.Name})" : "";
        foreach (var z in (project.Alarm?.Zones ?? []).Where(z => z.Level is null || string.Equals(z.Level, level.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var rooms = z.Room == "*" ? plan.Rooms : plan.Rooms.Where(r => string.Equals(r.Name, z.Room, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rooms.Count == 0)
            {
                issues.Add(new Issue("aps.layout.noRoom", IssueLevel.Warning, $"АПС: помещение «{z.Room}» на чертеже{levelTag} не найдено (слой ПТ_КОНТУР, имя — текст внутри контура)."));
                continue;
            }

            var height = z.CeilingHeight ?? design.RoomHeight;
            if (height is null)
            {
                issues.Add(new Issue("aps.layout.noHeight", IssueLevel.Error, $"АПС: для помещения «{z.Room}» не задана высота до перекрытия (alarm.zones[].ceilingHeight или design.roomHeight) — тип ИП и радиус зоны контроля зависят от неё."));
                continue;
            }

            foreach (var room in rooms)
            {
                // ID помещения уникален по всем уровням: на втором и дальше — с префиксом уровня (L2-R1), имя — с именем уровня.
                var roomId = level.Index == 0 ? room.Id : string.Create(CultureInfo.InvariantCulture, $"L{level.Index + 1}-{room.Id}");
                if (zones.Any(existing => existing.RoomId == roomId))
                {
                    issues.Add(new Issue("aps.layout.duplicateRoom", IssueLevel.Warning, $"АПС: помещение «{room.Name}»{levelTag} задано в разделе alarm дважды — взята первая зона.", roomId));
                    continue;
                }

                // Модель ИП — выбор проектировщика; без выбора — первая в каталоге, об этом Info. Паспорт модели идёт в расчёт АКБ.
                var model = catalog.Count > 0 ? AlarmDetectorCatalog.Choose(catalog, z.Detector, z.Model, room.Name + levelTag, issues) : null;
                var beams = plan.Beams.Where(b => b.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint).ToList();
                zones.Add(new DetectorZone(roomId, room.Name + levelTag, room.Contour, new Length(height.Value), z.Detector, z.Algorithm, z.Addressable)
                {
                    Beams = beams,
                    ModelId = model?.Id,
                    ModelName = model is null ? null : $"{model.Name} ({model.Designation})",
                });
            }
        }

        return zones;
    }

    /// <summary>Вид устройства для условного знака схемы — по суффиксу обозначения с плана (D — ИП, M — ИПР, S — оповещатель, L — табло).</summary>
    private static SchemeDeviceKind SchemeKind(string id)
    {
        var tail = id[(id.LastIndexOf('.') + 1)..];
        return tail.Length == 0 ? SchemeDeviceKind.Module : tail[0] switch
        {
            'D' => SchemeDeviceKind.SmokeDetector,
            'M' => SchemeDeviceKind.ManualCallPoint,
            'S' => SchemeDeviceKind.Sounder,
            'L' => SchemeDeviceKind.Light,
            _ => SchemeDeviceKind.Module,
        };
    }

    private static CableCatalog LoadCables(string path) => AlarmCableCatalog.Load(path);

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



