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

The editor handles one enabled tag group at a time. It can edit tag order,
addresses, simulation mappings, and the active mode. Realtime OPC acquisition
is not implemented in the worker yet; selecting that mode currently causes the
worker to report an unsupported mode error.

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

For elapsed-time simulation, open a Simulation XML in the editor, select a
process, and press **Start simulation**. One row is written and flushed on each
configured tick (one second in the supplied settings). The file rolls at the
configured shift boundary. **Stop** flushes and closes it. Live output is
stored under `Output/Live/<xml name>/<equipment>/<shift>.txt` in the
repository. Set `SGP_OUTPUT_PATH` to use another output directory. The web
server must remain running for live output to continue.

Each process has an equipment name (for example, `PCP001KPI` for `PCP001`).
Live output folders, and the worker's per-process output folders, use the
equipment name instead of the process ID. To rename equipment, select its
process, edit **Equipment name**, and press **Save name**. A running process
cannot be renamed. The name is saved as `EquipmentName` in
`src/SgpSimulator.Worker/appsettings.json` during repository development, or in
the configurator's `simulator-settings.json` when deployed. Set
`SGP_SETTINGS_PATH` to save it to another settings file. Restart the worker to
pick up a new name. A process without a name uses its process ID.
