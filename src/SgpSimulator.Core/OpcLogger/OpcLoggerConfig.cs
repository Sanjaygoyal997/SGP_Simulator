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

    [XmlElement("Tag")]
    public List<OpcTag> Tags { get; set; } = [];

    [XmlElement("Trigger")]
    public OpcTrigger? Trigger { get; set; }
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
