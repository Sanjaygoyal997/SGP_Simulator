using System.Collections.Concurrent;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record CycleMetadataStatus(bool Enabled, long Saved, int Pending, long NotLinked, string? LastError,
    DateTimeOffset? LastSavedAt);

/// <summary>
/// Writes cycles to ml.cycle_metadata in the background, so a slow or unavailable database never holds up the
/// simulation. Live cycles are added when they start and updated when they end; historical cycles are added
/// once, finished. Writes run in the order queued, and failed ones stay queued and are retried.
/// </summary>
public sealed class CycleMetadataSink : IAsyncDisposable
{
    private enum Kind { Finished, Start, End, Abandon }

    private sealed record Operation(Kind Kind, CycleMetadata Cycle);

    private readonly MasterDataService _master;
    private readonly ILogger<CycleMetadataSink> _logger;
    private readonly ConcurrentQueue<Operation> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    // Row ids of live cycles in progress, by cycle file path; used by the loop only.
    private readonly Dictionary<string, long> _open = new(StringComparer.Ordinal);
    private long _saved;
    private long _notLinked;
    private string? _lastError;
    private DateTimeOffset? _lastSavedAt;
    private int _inBatch;

    private readonly string _outputRoot;

    /// <param name="outputRoot">Folder the stored cycle file paths are relative to (Output/Live).</param>
    public CycleMetadataSink(MasterDataService master, bool enabled, string outputRoot, ILogger<CycleMetadataSink> logger)
    {
        _outputRoot = outputRoot;
        _master = master;
        _logger = logger;
        Enabled = enabled && master.Configured;
        _loop = Enabled ? Task.Run(LoopAsync) : Task.CompletedTask;
    }

    public bool Enabled { get; }

    public CycleMetadataStatus Status => new(Enabled, Interlocked.Read(ref _saved), _queue.Count + Volatile.Read(ref _inBatch),
        Interlocked.Read(ref _notLinked), _lastError, _lastSavedAt);

    /// <summary>Queues a finished cycle of an equipment linked to master.equipment_master (machine id).</summary>
    /// <param name="bootstrap">True for historical data, recorded as bootstrap_flag = 1.</param>
    public void Add(CompletedCycle cycle, int machineId, bool bootstrap = false)
    {
        if (!Linked(machineId)) return;
        // The file is complete when the cycle ends, in the simulated timeline (past for historical data).
        Enqueue(Kind.Finished, cycle, cycle.End, machineId, bootstrap);
    }

    /// <summary>Queues the row of a live cycle that has just started.</summary>
    public void Start(CompletedCycle cycle, int machineId)
    {
        if (Linked(machineId)) Enqueue(Kind.Start, cycle, cycle.Start, machineId);
    }

    /// <summary>Queues the end time of a live cycle queued by <see cref="Start"/>.</summary>
    public void End(CompletedCycle cycle, int machineId)
    {
        if (Enabled && machineId > 0) Enqueue(Kind.End, cycle, cycle.Start, machineId);
    }

    /// <summary>Queues the removal of a live cycle cut off before it ended (the run stopped).</summary>
    public void Abandon(string path, int machineId)
    {
        if (Enabled && machineId > 0)
            Enqueue(Kind.Abandon, new CompletedCycle(path, string.Empty, default, default, 0), default, machineId);
    }

    private bool Linked(int machineId)
    {
        if (!Enabled) return false;
        if (machineId > 0) return true;
        Interlocked.Increment(ref _notLinked);
        return false;
    }

    private void Enqueue(Kind kind, CompletedCycle cycle, DateTime createdAt, int machineId, bool bootstrap = false) =>
        _queue.Enqueue(new Operation(kind, new CycleMetadata(Path.GetFullPath(cycle.Path), cycle.Recipe, cycle.Start,
            cycle.End, cycle.SetSeconds, createdAt, machineId, Path.GetRelativePath(_outputRoot, cycle.Path), bootstrap)));

    /// <summary>Waits until everything queued so far is saved (or the wait times out).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Enabled && (!_queue.IsEmpty || Volatile.Read(ref _inBatch) > 0) && _lastError is null &&
               DateTime.UtcNow < until) await Task.Delay(200);
    }

    private async Task LoopAsync()
    {
        var batch = new List<CycleMetadata>();
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                // Finished cycles go in one COPY; a start, end or removal is written on its own, in order.
                while (batch.Count < 5000 && _queue.TryPeek(out var next) && next.Kind == Kind.Finished)
                {
                    batch.Add(next.Cycle);
                    _queue.TryDequeue(out _);
                }
                Volatile.Write(ref _inBatch, batch.Count);
                if (batch.Count > 0)
                {
                    await _master.InsertCycleMetadataAsync(batch, _stopping.Token);
                    Saved(batch.Count);
                    batch.Clear();
                    continue;
                }
                if (_queue.TryPeek(out var operation))
                {
                    await WriteAsync(operation);
                    _queue.TryDequeue(out _);
                    continue;
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Keep the write queued and retry; the page shows the error meanwhile.
                if (_lastError != error.Message) _logger.LogError(error, "Saving cycle metadata failed");
                _lastError = error.Message;
                await Delay(TimeSpan.FromSeconds(10));
                continue;
            }
            await Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task WriteAsync(Operation operation)
    {
        var cycle = operation.Cycle;
        switch (operation.Kind)
        {
            case Kind.Start:
                _open[cycle.FileName] = await _master.StartCycleMetadataAsync(cycle, _stopping.Token);
                Saved(1);
                break;
            // The id is forgotten only once the write succeeded, so a retry finds it again.
            case Kind.End when _open.TryGetValue(cycle.FileName, out var id):
                await _master.EndCycleMetadataAsync(id, cycle, _stopping.Token);
                _open.Remove(cycle.FileName);
                Saved(0);
                break;
            case Kind.Abandon when _open.TryGetValue(cycle.FileName, out var id):
                await _master.DeleteCycleMetadataAsync(id, _stopping.Token);
                _open.Remove(cycle.FileName);
                break;
        }
    }

    private void Saved(int rows)
    {
        Interlocked.Add(ref _saved, rows);
        Volatile.Write(ref _inBatch, 0);
        _lastSavedAt = DateTimeOffset.Now;
        _lastError = null;
    }

    private async Task Delay(TimeSpan delay)
    {
        try { await Task.Delay(delay, _stopping.Token); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(TimeSpan.FromSeconds(10));
        _stopping.Cancel();
        await _loop;
    }
}
