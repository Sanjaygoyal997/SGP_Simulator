using System.IO.Compression;

namespace SgpSimulator.Core.OpcLogger;

public sealed record SimulationDataColumn(string Name, string Sample);
public sealed record SimulationDataFile(string Id, IReadOnlyList<SimulationDataColumn> Columns);

public sealed class SimulationDataCatalog(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);

    public static string ResolveDirectory(string configPath)
    {
        var configured = Environment.GetEnvironmentVariable("SGP_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var sibling = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, "..", "Data"));
        if (Directory.Exists(sibling)) return sibling;

        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "SmartOPCLogger", "Data");
            if (Directory.Exists(candidate)) return candidate;
        }

        return sibling;
    }

    public IReadOnlyList<SimulationDataFile> List()
    {
        if (!Directory.Exists(_directory)) return [];
        var files = new List<SimulationDataFile>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.txt"))
        {
            using var reader = File.OpenText(path);
            files.Add(Describe(Path.GetFileName(path), reader));
        }
        foreach (var path in Directory.EnumerateFiles(_directory, "*.zip"))
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(entry.Open());
                files.Add(Describe($"{Path.GetFileName(path)}/{entry.FullName}", reader));
            }
        }
        return files.OrderBy(file => file.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string[]> LoadRows(string id)
    {
        using var reader = Open(id);
        var header = reader.ReadLine()?.Split('\t')
            ?? throw new InvalidOperationException($"Data file '{id}' is empty.");
        ValidateHeader(id, header);
        var rows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cells = line.Split('\t');
            if (cells.Length == header.Length) rows.Add(cells);
        }
        if (rows.Count == 0) throw new InvalidOperationException($"Data file '{id}' has no complete rows.");
        return rows;
    }

    private StreamReader Open(string id)
    {
        var file = List().FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException($"Data file '{id}' was not found in '{_directory}'.");
        var split = file.Id.IndexOf(".zip/", StringComparison.OrdinalIgnoreCase);
        if (split < 0) return File.OpenText(Path.Combine(_directory, file.Id));

        var archivePath = Path.Combine(_directory, file.Id[..(split + 4)]);
        var archive = ZipFile.OpenRead(archivePath);
        try
        {
            var entry = archive.GetEntry(file.Id[(split + 5)..])
                ?? throw new InvalidOperationException($"Data entry '{id}' was not found.");
            return new StreamReader(new ArchiveEntryStream(entry.Open(), archive));
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static SimulationDataFile Describe(string id, TextReader reader)
    {
        var header = reader.ReadLine()?.Split('\t')
            ?? throw new InvalidOperationException($"Data file '{id}' is empty.");
        ValidateHeader(id, header);
        var sample = reader.ReadLine()?.Split('\t') ?? [];
        return new SimulationDataFile(id, header.Skip(1)
            .Select((name, index) => new SimulationDataColumn(name, sample.Length > index + 1 ? sample[index + 1] : ""))
            .ToArray());
    }

    private static void ValidateHeader(string id, string[] header)
    {
        if (header.Length < 2 || header[0] != "(X)" ||
            header.Skip(1).Any(column => !column.EndsWith("(Y)", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Data file '{id}' needs an (X) timestamp and (Y) columns.");
    }

    private sealed class ArchiveEntryStream(Stream entry, ZipArchive archive) : Stream
    {
        public override bool CanRead => entry.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => entry.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { entry.Dispose(); archive.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
