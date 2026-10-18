using Npgsql;

namespace SgpSimulator.Configurator;

public sealed class MasterDataOptions
{
    public const string SectionName = "MasterData";

    /// <summary>PostgreSQL connection string; empty turns the master data off.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public int PlantId { get; set; }
}

public sealed record MasterEquipment(int Id, string Name, string DisplayName, string Area);

public sealed record MasterRecipe(string Code, string Name, int CycleSeconds);

/// <summary>Reads equipment and their recipes (items) from the master tables.</summary>
public sealed class MasterDataService(MasterDataOptions options)
{
    public bool Configured => !string.IsNullOrWhiteSpace(options.ConnectionString) && options.PlantId > 0;

    public int PlantId => options.PlantId;

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
        command.Parameters.AddWithValue("plant", options.PlantId);
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
        command.Parameters.AddWithValue("plant", options.PlantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MasterRecipe>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MasterRecipe(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!Configured)
            throw new InvalidOperationException(
                "Master data is not configured. Set MasterData:ConnectionString and MasterData:PlantId.");
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
