using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace SgpSimulator.Core.OpcLogger;

public enum RuntimeMode
{
    Simulation,
    Realtime
}

public enum SimulationKind
{
    None,
    BatchRunning,
    RecipeName,
    StepValue,
    Drift,
    Pulse,
    Setpoint,
    Constant,
    DataFile
}

[XmlRoot("Projects")]
public sealed class OpcLoggerConfig
{
    [XmlElement("Project")]
    public List<OpcProject> Projects { get; set; } = [];

    /// <summary>Recipes that equipment schedules refer to by name.</summary>
    [XmlElement("Recipe")]
    public List<OpcRecipe> Recipes { get; set; } = [];

    /// <summary>Every TagGroup in document order; each one is a piece of equipment.</summary>
    public IReadOnlyList<OpcTagGroup> Equipment =>
        Projects.SelectMany(project => project.Groups).SelectMany(group => group.TagGroups).ToArray();

    public static void ValidateEquipmentNames(IEnumerable<OpcTagGroup> equipment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in equipment)
        {
            var name = group.Name;
            if (name.Length is 0 or > 64 || name.Contains("..", StringComparison.Ordinal) ||
                !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))
                throw new InvalidOperationException(
                    $"Equipment name '{name}' must use letters, numbers, dots, hyphens, or underscores (64 characters max).");
            if (!seen.Add(name))
                throw new InvalidOperationException($"Equipment name '{name}' is used more than once.");
        }
    }

    public static OpcLoggerConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        var serializer = new XmlSerializer(typeof(OpcLoggerConfig));
        return (OpcLoggerConfig)(serializer.Deserialize(stream)
            ?? throw new InvalidOperationException($"Could not deserialize OPC logger config '{path}'."));
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        Save(stream);
    }

    public void Save(Stream stream)
    {
        var serializer = new XmlSerializer(typeof(OpcLoggerConfig));
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, string.Empty);
        serializer.Serialize(stream, this, namespaces);
    }
}

public sealed class OpcProject
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Description { get; set; } = string.Empty;

    [XmlElement("OPCGroup")]
    public List<OpcGroup> Groups { get; set; } = [];
}

public sealed class OpcGroup
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Description { get; set; } = string.Empty;

    [XmlElement("TagGroup")]
    public List<OpcTagGroup> TagGroups { get; set; } = [];
}

public sealed class OpcTagGroup
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Description { get; set; } = string.Empty;

    [XmlAttribute]
    public string OPCServer { get; set; } = string.Empty;

    [XmlIgnore]
    public bool Enabled { get; set; } = true;

    [XmlAttribute("Enabled")]
    public string EnabledText
    {
        get => Enabled ? "True" : "False";
        set => Enabled = bool.Parse(value);
    }

    [XmlAttribute]
    public RuntimeMode RuntimeMode { get; set; } = RuntimeMode.Simulation;

    /// <summary>Simulator process (recipe, shift, tick) this equipment runs as; empty uses the first process.</summary>
    [XmlAttribute]
    public string ProcessId { get; set; } = string.Empty;

    public bool ShouldSerializeProcessId() => !string.IsNullOrWhiteSpace(ProcessId);

    /// <summary>master.equipment_master.local_equipment_id this equipment's recipes come from; 0 when not linked.</summary>
    [XmlAttribute]
    public int MasterEquipmentId { get; set; }

    public bool ShouldSerializeMasterEquipmentId() => MasterEquipmentId > 0;

    /// <summary>Spread of each cycle around its planned time, in percent.</summary>
    [XmlAttribute]
    public double CycleVariationPercent { get; set; } = 5;

    /// <summary>Percent of cycles that run 10-35% slower.</summary>
    [XmlAttribute]
    public double SlowCyclePercent { get; set; } = 10;

    /// <summary>Percent of stops that become breakdowns.</summary>
    [XmlAttribute]
    public double BreakdownPercent { get; set; } = 5;

    [XmlAttribute]
    public double BreakdownMinMinutes { get; set; } = 10;

    [XmlAttribute]
    public double BreakdownMaxMinutes { get; set; } = 30;

    /// <summary>Spread of the stopped periods between cycles, in percent.</summary>
    [XmlAttribute]
    public double GapVariationPercent { get; set; } = 30;

    /// <summary>Per-cycle shift of numeric process values, in percent.</summary>
    [XmlAttribute]
    public double ProcessVariationPercent { get; set; } = 2;

    [XmlElement("Tag")]
    public List<OpcTag> Tags { get; set; } = [];

    [XmlElement("Trigger")]
    public OpcTrigger? Trigger { get; set; }

    /// <summary>Recipe sequence this equipment follows; none means it runs continuously on its tags' data.</summary>
    [XmlElement("Schedule")]
    public OpcSchedule? Schedule { get; set; }

    public bool ShouldSerializeSchedule() => Schedule is { Steps.Count: > 0 };
}

public sealed class OpcRecipe
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    /// <summary>Recorded data for this recipe; empty keeps each tag's own data file.</summary>
    [XmlAttribute]
    public string ReferenceFile { get; set; } = string.Empty;

    public bool ShouldSerializeReferenceFile() => !string.IsNullOrWhiteSpace(ReferenceFile);

    [XmlAttribute]
    public string Description { get; set; } = string.Empty;

    public bool ShouldSerializeDescription() => !string.IsNullOrWhiteSpace(Description);
}

public sealed class OpcSchedule
{
    [XmlIgnore]
    public bool Repeat { get; set; }

    [XmlAttribute("Repeat")]
    public string RepeatText
    {
        get => Repeat ? "True" : "False";
        set => Repeat = bool.Parse(value);
    }

    [XmlElement("Step")]
    public List<OpcScheduleStep> Steps { get; set; } = [];
}

public sealed class OpcScheduleStep
{
    [XmlAttribute]
    public string Recipe { get; set; } = string.Empty;

    /// <summary>Completed running periods (0 -> 1 -> 0) before the next step starts.</summary>
    [XmlAttribute]
    public int Cycles { get; set; } = 1;

    /// <summary>Target running-period length (item_master.cycle_time); 0 keeps the recorded lengths.</summary>
    [XmlAttribute]
    public int CycleSeconds { get; set; }

    public bool ShouldSerializeCycleSeconds() => CycleSeconds > 0;

    /// <summary>Keeps running this recipe until the simulation stops; Cycles is then ignored.</summary>
    [XmlIgnore]
    public bool Loop { get; set; }

    [XmlAttribute("Loop")]
    public string LoopText
    {
        get => Loop ? "True" : "False";
        set => Loop = bool.Parse(value);
    }

    public bool ShouldSerializeLoopText() => Loop;
}

public sealed class OpcTag
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Address { get; set; } = string.Empty;

    [XmlAttribute]
    public string Type { get; set; } = "Analog";

    [XmlAttribute]
    public string UnitText { get; set; } = string.Empty;

    [XmlAttribute]
    public string Format { get; set; } = "0.00";

    [XmlIgnore]
    public bool Alarm { get; set; }

    [XmlAttribute("Alarm")]
    public string AlarmText
    {
        get => Alarm ? "True" : "False";
        set => Alarm = bool.Parse(value);
    }

    [XmlAttribute]
    public SimulationKind SimulationKind { get; set; } = SimulationKind.None;

    public bool ShouldSerializeSimulationKind() => SimulationKind != SimulationKind.None;

    [XmlAttribute]
    public string SimulationChannel { get; set; } = string.Empty;

    public bool ShouldSerializeSimulationChannel() => !string.IsNullOrWhiteSpace(SimulationChannel);

    [XmlAttribute]
    public string SimulationFile { get; set; } = string.Empty;

    public bool ShouldSerializeSimulationFile() => !string.IsNullOrWhiteSpace(SimulationFile);

    [XmlAttribute]
    public string SimulationColumn { get; set; } = string.Empty;

    public bool ShouldSerializeSimulationColumn() => !string.IsNullOrWhiteSpace(SimulationColumn);

    [XmlAttribute]
    public double SimulationMin { get; set; }

    [XmlIgnore]
    public bool SimulationMinSpecified { get; set; }

    [XmlAttribute]
    public double SimulationMax { get; set; }

    [XmlIgnore]
    public bool SimulationMaxSpecified { get; set; }

    [XmlAttribute]
    public string SimulationValue { get; set; } = string.Empty;

    public bool ShouldSerializeSimulationValue() => !string.IsNullOrWhiteSpace(SimulationValue);

    /// <summary>Column header for this tag in the per-cycle files; empty leaves the tag out of them.</summary>
    [XmlAttribute]
    public string CycleColumn { get; set; } = string.Empty;

    public bool ShouldSerializeCycleColumn() => !string.IsNullOrWhiteSpace(CycleColumn);
}

public sealed class OpcTrigger
{
    [XmlAttribute]
    public string Type { get; set; } = "Recurring";

    [XmlAttribute]
    public int Frequency { get; set; } = 1;

    [XmlAttribute]
    public string Unit { get; set; } = "Second";
}
