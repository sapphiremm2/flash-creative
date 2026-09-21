using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdobeDownloader.Core;

public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("JSON file exceeds 32 MiB.");
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(file, Options, ct) ?? throw new InvalidDataException("JSON file was empty.");
    }

    public static async Task WriteAsync<T>(string path, T value, bool overwrite = true, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, value, Options, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
