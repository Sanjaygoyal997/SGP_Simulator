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

Equipment can follow a recipe schedule. The **Schedule** table lists rows for
all equipment in the XML. Press **+ Add row** and choose the **Equipment**, the
**Recipe**, and the number of **Batches**, or tick **Loop** to keep running that
recipe until the simulation stops. A batch is one running period, `0 -> 1 -> 0`
in the first data column (or the generated running state). Each equipment runs
its own rows from top to bottom (use the arrows to reorder them), and all
equipment run in parallel. After its last row an equipment stays idle: rows are
still logged, with the running state at 0. Rows after a Loop row of the same
equipment are never reached. Equipment without rows run continuously on their
tags' data. The run table shows each equipment's current recipe, row, and batch.

**Variation for KPIs** (per equipment) makes the generated data deviate from
the plan, so performance, availability, and OEE show realistic losses:

- **Cycle time ±%** (default 5): spread of each batch around its cycle time
  (`item_master.cycle_time`, or the recorded cycle lengths);
- **Slow cycles %** (default 10): share of batches that run 10-35% longer;
- **Gap between cycles ±%** (default 30): spread of each stopped (0) period
  between cycles around its recorded length;
- **Breakdowns % of stops** (default 5) and **Breakdown min/max (minutes)**
  (default 10-30): share of stops that become a breakdown of that length;
- **Process values ±%** (default 2): every numeric process value (not 0/1
  states, recipe names, or constant columns) shifts by up to this much per
  running or stopped period, a little beyond the recorded range if needed.
  Whole-number columns stay whole numbers.

The actual cycle time is the running period (`0 -> 1` to `1 -> 0`); when it is
longer than the set cycle time, the KPI system books the difference as
downtime. Set all percentages to 0 to follow the plan closely.

To check what the KPI system receives, the run table's **Last cycle vs set**
column shows each equipment's last finished cycle against its set cycle time
and how many cycles ran over it (for example `652 s / set 620 s (over by 32 s)
- 12 of 20 over set`). The worker logs every finished cycle the same way, and a
batch run prints the count per equipment. With the default variation, roughly
half of the cycles run over the set cycle time. A KPI chart that caps cycle time
at the set value (booking the excess as downtime) shows averages just below the
set value even then. Cycle times and breakdown lengths are in seconds, whatever
the tick interval. The settings are
saved on the equipment's `<TagGroup>` and apply to data-pattern tags.

Recipes for an equipment linked to the master data come from `item_master`
(see below). For other equipment, press **Recipes** in the top bar to define
recipes, or **Find recipes in data** to add the names found in the reference
data; they are saved in the XML as `<Recipe Name="..."/>`.

One reference file can hold several recipes. The recipe column is the text
column (such as ch15) whose values name the recipes. While a step runs:

- data-pattern tags use only the rows recorded while that recipe ran, from the
  equipment's reference file, or else from another reference file with the
  same columns (such as another shift file). Choosing a **Data file** for a
  recipe restricts it to that file;
- a recipe with no recorded rows uses all of the data, and only the recipe
  name changes; the editor marks such recipes and rows;
- the recipe column, and columns repeating it (ch15 and ch16), and Recipe tags
  show the scheduled recipe;
- after a row's last batch, the next recipe starts with a stopped
  (changeover) period.

The worker follows the same schedules and logs each recipe change. The process
still sets the shift, tick, and generated-channel timing.

For elapsed-time simulation, open a Simulation XML in the editor and press
**Start simulation**. Every enabled equipment in the XML runs in parallel, each
with its own simulator and files. One row per equipment is appended on each
configured tick (one second in the supplied settings), and the file is closed
again after every row so its size and modified time update immediately
(programs watching the folder, for example on Windows, see each new row). A row
waits for the next tick if another program holds the file locked. The file rolls
at the configured shift boundary. **Stop** stops all of them and closes their
files. The table under the buttons shows each equipment's rows, errors, and a
link to its current file. Live output is stored under
`Output/Live/<xml name>/<equipment>/<shift>.txt` in the repository. Each
cycle is also written to its own file in the equipment's `Cycles` folder,
named after the cycle start time and recipe, for example
`Output/Live/<xml name>/<equipment>/Cycles/20261019_071530_CT1482MLX3.txt`. A
cycle file holds the parameter values of that cycle, one tab-separated row per
tick from cycle start (`0 -> 1`) to cycle end (`1 -> 0`), without a timestamp
column; a run that begins mid-cycle starts with the next complete cycle. Press
**Cycle file columns** above the tag list to choose which tags are written and
the column header of each (saved as `CycleColumn` on the tag). Columns follow
the tag order. With no tag selected, every tag is written under its `ch1(Y)`,
`ch2(Y)`, ... header. The worker writes the same `Cycles` folder next
to its shift files. Set
`SGP_OUTPUT_PATH` to use another output directory. The web server must remain
running for live output to continue.

The worker also runs every enabled equipment in its XML in parallel and writes
to `<OutputPath>/<equipment>/<shift>.txt`, where `OutputPath` is the process's
own `OutputPath` or the global one. In batch mode, `--equipment <name>` limits
the run to one equipment. Processes are configured in
`src/SgpSimulator.Worker/appsettings.json`; restart the editor after changing
them.
