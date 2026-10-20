using System.Collections.Concurrent;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record CycleMetadataStatus(bool Enabled, long Saved, int Pending, long NotLinked, string? LastError,
    DateTimeOffset? LastSavedAt);

/// <summary>
/// Queues finished cycles and writes them to ml.cycle_metadata in the background, so a slow or unavailable
/// database never holds up the simulation. Failed writes stay queued and are retried.
/// </summary>
public sealed class CycleMetadataSink : IAsyncDisposable
{
    private readonly MasterDataService _master;
    private readonly ILogger<CycleMetadataSink> _logger;
    private readonly ConcurrentQueue<CycleMetadata> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
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
    public void Add(CompletedCycle cycle, int machineId)
    {
        if (!Enabled) return;
        if (machineId <= 0)
        {
            Interlocked.Increment(ref _notLinked);
            return;
        }
        // The file is complete when the cycle ends, in the simulated timeline (past for historical data).
        _queue.Enqueue(new CycleMetadata(Path.GetFileName(cycle.Path), cycle.Recipe, cycle.Start, cycle.End,
            cycle.SetSeconds, cycle.End, machineId, Path.GetRelativePath(_outputRoot, cycle.Path)));
    }

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
                while (batch.Count < 5000 && _queue.TryPeek(out var next))
                {
                    batch.Add(next);
                    _queue.TryDequeue(out _);
                }
                Volatile.Write(ref _inBatch, batch.Count);
                if (batch.Count > 0)
                {
                    await _master.InsertCycleMetadataAsync(batch, _stopping.Token);
                    Interlocked.Add(ref _saved, batch.Count);
                    Volatile.Write(ref _inBatch, 0);
                    _lastSavedAt = DateTimeOffset.Now;
                    _lastError = null;
                    batch.Clear();
                    continue;
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Keep the batch and retry; the page shows the error meanwhile.
                if (_lastError != error.Message) _logger.LogError(error, "Saving cycle metadata failed");
                _lastError = error.Message;
                await Delay(TimeSpan.FromSeconds(10));
                continue;
            }
            await Delay(TimeSpan.FromSeconds(1));
        }
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
