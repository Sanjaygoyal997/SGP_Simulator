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

Equipment can follow a recipe schedule. Press **Recipes** in the top bar to
define recipes, or press **Find recipes in data** to add the recipe names found
in the reference data. Recipes are saved in the XML as `<Recipe Name="..."/>`.
Then, under **Recipe schedule** for the selected equipment, add steps in order,
each a recipe and a number of cycles (for example, CT1482MLX3 for 5 cycles,
then CT2000ABC for 3 cycles). A cycle is one running period, `0 -> 1 -> 0` in
the first data column (or the generated running state).

Recipes can also come from the plant master data in PostgreSQL. Copy
`.env.example` at the repository root to `.env` and fill it in (`.env` is not
committed, so the password stays local):

```
SGP_DB_HOST=localhost
SGP_DB_PORT=5432
SGP_DB_NAME=smart_gate_plus
SGP_DB_USER=postgres
SGP_DB_PASSWORD=...
SGP_DB_PLANT_ID=1
```

The same names also work as real environment variables, which take precedence.
Leave `SGP_DB_PLANT_ID` empty when `master.plant_master` has a single plant.
For a deployed installation, put `.env` next to the configurator's files.
(`MasterData:ConnectionString` and `MasterData:PlantId` in the settings file
still work and win over `SGP_DB_` values.) Restart the configurator after
changing `.env`.

The configurator looks for `.env` in the folder it is started from, the
repository folder, and the folders above its program files. The line under
**Equipment** shows the master-data connection, or what is missing.

With master data available, equipment is driven by it:

- **+ Add equipment** lists the plant's active equipment from
  `master.equipment_master`. Selected equipment are added with the name from
  `display_name`, the OPC group from the area (`master.area_master`), the
  process whose ID equals `equipment_master.name` (if configured), and tags
  copied from an existing equipment. **Add manual equipment** still adds one
  that is not in the master data.
- Equipment whose name matches a master-data `display_name` or `name` are
  linked automatically when the XML opens. A linked equipment saves its
  `local_equipment_id` (`MasterEquipmentId`), and its name and OPC group follow
  the master data (choose **(not linked)** under **DB equipment** to edit them).
- A linked equipment's schedule steps offer its recipes from
  `master.item_master` (`item_code`, not deleted), and each step stores the
  item's `cycle_time` as `CycleSeconds`. While a step runs, running periods last
  about that many seconds, keeping the recorded variation. When the page
  opens, cycle times that changed in the database are updated; save the XML
to keep them. The worker only reads the XML, so it needs no database access.

One reference file can hold several recipes. The recipe column is the text
column (such as ch15) whose values name the recipes. While a step runs:

- data-pattern tags use only the rows recorded while that recipe ran, from the
  equipment's reference file, or else from another reference file with the
  same columns (such as another shift file). Choosing a **Data file** for a
  recipe restricts it to that file;
- a recipe with no recorded rows uses all of the data, and only the recipe
  name changes; the editor marks such recipes and steps;
- the recipe column, and columns repeating it (ch15 and ch16), and Recipe tags
  show the scheduled recipe;
- after the step's last cycle, the next recipe starts with a stopped
  (changeover) period.

After the last step the equipment stays idle: rows are still logged, with the
running state at 0. Tick **Repeat when finished** to start the schedule again
instead. Equipment without a schedule runs continuously on its tags' data. The
run table shows each equipment's current recipe, step, and cycle. The worker
follows the same schedules and logs each recipe change. The process still sets
the shift, tick, and generated-channel timing.

 open a Simulation XML in the editor and press
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
