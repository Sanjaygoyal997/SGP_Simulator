using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.Output;

/// <summary>
/// Writes every cycle (the rows from 0 -> 1 to the following 1 -> 0) to its own file, named after the cycle
/// start time and recipe, for example <c>20261019_071530_CT1482MLX3.txt</c>. Same columns as the shift files.
/// </summary>
public sealed class CycleFileWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _header;
    private readonly bool _keepOpen;
    private string? _path;
    private StreamWriter? _writer;
    private bool _inCycle;
    private bool? _wasRunning;

    /// <param name="keepOpen">False closes the file after every row so live readers see each new row.</param>
    public CycleFileWriter(string directory, int channelCount, bool keepOpen = true)
    {
        _directory = directory;
        _header = ShiftFileWriter.Header(channelCount);
        _keepOpen = keepOpen;
        Directory.CreateDirectory(directory);
    }

    /// <summary>The file of the cycle in progress, or the last one written.</summary>
    public string? CurrentFile => _path;

    public void Write(SimulatedRow row, bool running, string? recipe)
    {
        var started = _wasRunning == false && running;
        _wasRunning = running;
        if (!running)
        {
            Close();
            return;
        }

        // A run that begins mid-cycle waits for the next 0 -> 1, so every file holds a whole cycle.
        if (started) Begin(row.Timestamp, recipe);
        if (!_inCycle) return;
        var line = ShiftFileWriter.FormatRow(row);
        if (_keepOpen)
        {
            _writer!.WriteLine(line);
            return;
        }
        try
        {
            using var stream = new FileStream(_path!, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Encoding.ASCII);
            writer.WriteLine(line);
        }
        catch (IOException)
        {
            // Another program holds the file; this row stays in the shift file only.
        }
    }

    private void Begin(DateTime start, string? recipe)
    {
        var name = start.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeRecipe = Regex.Replace(recipe ?? string.Empty, @"[^A-Za-z0-9._-]+", "_").Trim('_', '.');
        if (safeRecipe.Length > 0) name += "_" + safeRecipe;
        var path = Path.Combine(_directory, name + ".txt");
        for (var number = 2; File.Exists(path); number++)
            path = Path.Combine(_directory, $"{name}_{number}.txt");
        File.WriteAllText(path, _header + Environment.NewLine, Encoding.ASCII);
        _path = path;
        _writer = _keepOpen ? new StreamWriter(path, append: true, Encoding.ASCII) : null;
        _inCycle = true;
    }

    private void Close()
    {
        if (!_inCycle) return;
        _writer?.Dispose();
        _writer = null;
        _inCycle = false;
    }

    public void Dispose() => Close();
}
