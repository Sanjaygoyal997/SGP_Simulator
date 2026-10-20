using Npgsql;

namespace SgpSimulator.Configurator;

public sealed class MasterDataOptions
{
    public const string SectionName = "MasterData";

    /// <summary>PostgreSQL connection string; empty turns the master data off.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public int PlantId { get; set; }

    /// <summary>
    /// Fills unset values from SGP_DB_HOST, SGP_DB_PORT, SGP_DB_NAME, SGP_DB_USER, SGP_DB_PASSWORD and
    /// SGP_DB_PLANT_ID, which a .env file next to the repository can provide.
    /// </summary>
    public void ApplyEnvironment()
    {
        static string? Read(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value.Trim() : null;

        if (string.IsNullOrWhiteSpace(ConnectionString) && Read("SGP_DB_HOST") is { } host)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = int.TryParse(Read("SGP_DB_PORT"), out var port) ? port : 5432,
                Database = Read("SGP_DB_NAME"),
                Username = Read("SGP_DB_USER"),
                Password = Read("SGP_DB_PASSWORD"),
                Timeout = 5
            };
            ConnectionString = builder.ConnectionString;
        }
        if (PlantId <= 0 && int.TryParse(Read("SGP_DB_PLANT_ID"), out var plant)) PlantId = plant;
    }

    /// <summary>The .env file that was read, if any.</summary>
    public static string? DotEnvPath { get; private set; }

    /// <summary>Reads the first .env found in the given folders or their parent folders.</summary>
    public static void FindAndLoadDotEnv(params string[] folders)
    {
        foreach (var folder in folders)
            for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, ".env");
                if (!File.Exists(path)) continue;
                LoadDotEnv(path);
                return;
            }
    }

    /// <summary>Sets KEY=VALUE lines of a .env file as environment variables that are not set yet.</summary>
    public static void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
        DotEnvPath = Path.GetFullPath(path);
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var separator = text.IndexOf('=');
            if (separator <= 0) continue;
            var key = text[..separator].Trim();
            var value = text[(separator + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
        }
    }
}

public sealed record MasterEquipment(int Id, string Name, string DisplayName, string Area);

public sealed record MasterRecipe(string Code, string Name, int CycleSeconds);

/// <summary>One row of ml.cycle_metadata; FilePath is relative to the output folder.</summary>
public sealed record CycleMetadata(string FileName, string Recipe, DateTime Start, DateTime End, int SetSeconds,
    DateTime CreatedAt, int MachineId, string FilePath = "");

/// <summary>Reads equipment and their recipes (items) from the master tables.</summary>
public sealed class MasterDataService(MasterDataOptions options)
{
    private int _plantId = options.PlantId;

    /// <summary>Host and database for display, without the password.</summary>
    public string Target
    {
        get
        {
            if (!Configured) return string.Empty;
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
                return $"{builder.Database} on {builder.Host}:{builder.Port}";
            }
            catch (ArgumentException) { return "(invalid connection string)"; }
        }
    }

    public bool Configured => !string.IsNullOrWhiteSpace(options.ConnectionString);

    /// <summary>The configured plant; 0 until resolved when none is set.</summary>
    public int PlantId => _plantId;

    public async Task<IReadOnlyList<MasterEquipment>> GetEquipmentAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.local_equipment_id, e.name, e.display_name,
                   COALESCE((SELECT a.name FROM master.area_master a
                             WHERE a.local_area_id = e.local_area_id AND a.plant_id = e.plant_id
                             ORDER BY a.id LIMIT 1), '') AS area
            FROM master.equipment_master e
            WHERE e.plant_id = @plant AND e.status = 'ACTIVE'
            ORDER BY area, e.display_name
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("plant", await ResolvePlantAsync(connection, cancellationToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MasterEquipment>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MasterEquipment(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3)));
        return result;
    }

    public async Task<IReadOnlyList<MasterRecipe>> GetRecipesAsync(int equipmentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item_code, name, cycle_time
            FROM master.item_master
            WHERE local_equipment_id = @equipment AND plant_id = @plant AND COALESCE(is_deleted, 0) = 0
            ORDER BY item_code
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("equipment", equipmentId);
        command.Parameters.AddWithValue("plant", await ResolvePlantAsync(connection, cancellationToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MasterRecipe>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MasterRecipe(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    /// <summary>Adds cycle rows to ml.cycle_metadata in one COPY, with the configured plant.</summary>
    public async Task InsertCycleMetadataAsync(IReadOnlyList<CycleMetadata> cycles, CancellationToken cancellationToken)
    {
        if (cycles.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        var plant = await ResolvePlantAsync(connection, cancellationToken);
        var pathLength = await FilePathColumnLengthAsync(connection, cancellationToken);
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY ml.cycle_metadata (cycle_filename, recipe_name, start_time, end_time, set_cycle_time, " +
            "created_at, machine_id, plant_id" + (pathLength is null ? "" : ", cycle_filepath") +
            ") FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var cycle in cycles)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(Truncate(cycle.FileName, 255), NpgsqlTypes.NpgsqlDbType.Varchar, cancellationToken);
            await importer.WriteAsync(Truncate(cycle.Recipe, 100), NpgsqlTypes.NpgsqlDbType.Varchar, cancellationToken);
            await importer.WriteAsync(Millis(cycle.Start), NpgsqlTypes.NpgsqlDbType.Timestamp, cancellationToken);
            await importer.WriteAsync(Millis(cycle.End), NpgsqlTypes.NpgsqlDbType.Timestamp, cancellationToken);
            await importer.WriteAsync(cycle.SetSeconds, NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);
            await importer.WriteAsync(Millis(cycle.CreatedAt), NpgsqlTypes.NpgsqlDbType.Timestamp, cancellationToken);
            await importer.WriteAsync(cycle.MachineId, NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);
            await importer.WriteAsync(plant, NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);
            if (pathLength is int length)
                await importer.WriteAsync(Truncate(cycle.FilePath, length), NpgsqlTypes.NpgsqlDbType.Varchar,
                    cancellationToken);
        }
        await importer.CompleteAsync(cancellationToken);
    }

    // ml.cycle_metadata.cycle_filepath is optional: null when the column does not exist, else its length limit.
    private int? _filePathLength;
    private bool _filePathChecked;

    private async Task<int?> FilePathColumnLengthAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_filePathChecked) return _filePathLength;
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(character_maximum_length, 10485760) FROM information_schema.columns
            WHERE table_schema = 'ml' AND table_name = 'cycle_metadata' AND column_name = 'cycle_filepath'
            """, connection);
        _filePathLength = await command.ExecuteScalarAsync(cancellationToken) is int length ? length : null;
        _filePathChecked = true;
        return _filePathLength;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    // The columns are timestamp(3) without time zone: local times, to the millisecond.
    private static DateTime Millis(DateTime time) => DateTime.SpecifyKind(
        new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Unspecified);

    // Without a configured plant, a database holding a single plant uses that one.
    private async Task<int> ResolvePlantAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_plantId > 0) return _plantId;
        await using var command = new NpgsqlCommand("SELECT id FROM master.plant_master ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plants = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) plants.Add(reader.GetInt32(0));
        if (plants.Count != 1)
            throw new InvalidOperationException(plants.Count == 0
                ? "master.plant_master has no plants."
                : $"Several plants exist ({string.Join(", ", plants)}). Set SGP_DB_PLANT_ID to one of them.");
        return _plantId = plants[0];
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!Configured)
            throw new InvalidOperationException(
                "Master data is not configured. Set SGP_DB_HOST and the other SGP_DB_ values in .env.");
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
