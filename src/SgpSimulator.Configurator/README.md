# SGP Configurator

The browser editor runs on Windows and Linux with .NET 8. From the repository root:

```sh
dotnet run --project src/SgpSimulator.Configurator
```

Open `http://127.0.0.1:5187`. The editor loads and saves
`SmartOPCLogger/Configuration/DataLoggerConfigFile.xml`. The worker resolves the
same file during repository development.

Use **New XML** to create another configuration in that folder. Enter a file
name, project name, and mode. The OPC group uses the project name, and the tag
group uses the filename without `.xml`. The new file opens immediately; add a
tag before saving or running the worker. Use the file selector to return to an
existing XML. New files never replace an existing file. To run the worker with
a new file, set `Simulator__OpcLoggerConfigPath` to its absolute path.

For a deployed installation, set `SGP_CONFIG_PATH` to the absolute XML path for
the editor, and set `Simulator__OpcLoggerConfigPath` to that same path for the
worker. Set `Urls` or `ASPNETCORE_URLS` to change the listening address.

One XML can hold several pieces of equipment. Each `<TagGroup>` is one
equipment: its `Name` is the equipment name (for example, `PCP001KPI`), its tags
are the logged columns, and its optional `ProcessId` links it to a process in
`Simulator.Processes` (recipe, shift, and tick). Without `ProcessId` the first
process is used. Only enabled equipment runs.

```xml
<OPCGroup Name="Curing">
  <TagGroup Name="PCP001KPI" ProcessId="PCP001" Enabled="True" RuntimeMode="Simulation" ...>
    <Tag ... />
  </TagGroup>
  <TagGroup Name="PCP002KPI" ProcessId="PCP002" Enabled="True" RuntimeMode="Simulation" ...>
    <Tag ... />
  </TagGroup>
</OPCGroup>
```

In the editor, the equipment row at the top lists every equipment in the XML.
Select one to edit its name, OPC group, process, mode, enabled state, and tags.
The **OPC group** is the `<OPCGroup>` that holds the equipment; typing a new name
moves the equipment into that group (created if needed), and a group left with
no equipment is removed on save. Use **+ Add equipment** and
**Remove equipment** to change the list, then press **Save XML**; a removal is
not written until you save.
Equipment names must be unique and use letters, numbers, dots, hyphens, or
underscores, because they become output folder names. Realtime OPC
acquisition is not implemented yet; an enabled equipment in that mode cannot
start.

Simulation tags can generate new values from a recorded data file. The editor
lists `.txt` files and `.txt` entries inside ZIP files in `SmartOPCLogger/Data`.
Select a file and one of its columns for each tag. To make the output match a
reference file, press **From reference** above the tag list, choose the file,
and keep **Replace all tags**: the editor creates one tag per reference column,
in the same order, so the generated shift files have the same columns as the
reference (for example, 34 columns for `PCP001KPI.zip/01Feb23A.txt`). Rename
the tags if needed, then save the XML. The worker learns the
observed running/stopped periods, keeps cycle time near completed running
periods (from `0 -> 1` to the following `1 -> 0`), varies stopped-period
lengths, and varies
continuous readings within the recorded range. Related tags from one file
share the same generated phase and source rows. Binary states and text values
remain valid. This is synthetic output, not a replay; batch `--seed` makes a
run reproducible. `SGP_DATA_PATH` can point to another data directory on
Windows or Linux. Batch runs also accept
`Simulator__OpcLoggerConfigPath` for the selected XML file.

For elapsed-time simulation, open a Simulation XML in the editor and press
**Start simulation**. Every enabled equipment in the XML runs in parallel, each
with its own simulator and files. One row per equipment is written and flushed
on each configured tick (one second in the supplied settings). The file rolls
at the configured shift boundary. **Stop** stops all of them and closes their
files. The table under the buttons shows each equipment's rows, errors, and a
link to its current file. Live output is stored under
`Output/Live/<xml name>/<equipment>/<shift>.txt` in the repository. Set
`SGP_OUTPUT_PATH` to use another output directory. The web server must remain
running for live output to continue.

The worker also runs every enabled equipment in its XML in parallel and writes
to `<OutputPath>/<equipment>/<shift>.txt`, where `OutputPath` is the process's
own `OutputPath` or the global one. In batch mode, `--equipment <name>` limits
the run to one equipment. Processes are configured in
`src/SgpSimulator.Worker/appsettings.json`; restart the editor after changing
them.
