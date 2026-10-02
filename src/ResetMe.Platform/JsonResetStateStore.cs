using System.Text.Json;
using System.Text.Json.Serialization;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Platform;

/// <summary>state.json with atomic replace (write temp file, then rename over the old one).</summary>
public sealed class JsonResetStateStore : IResetStateStore
{
    private readonly string _path;

    public JsonResetStateStore(string path)
    {
        _path = path;
    }

    public ResetState Load()
    {
        if (!File.Exists(_path))
        {
            return new ResetState();
        }

        var json = File.ReadAllText(_path);
        // A corrupt state must not be silently replaced: it may hold a pending idempotency key.
        return JsonSerializer.Deserialize(json, StateJsonContext.Default.ResetState)
            ?? throw new InvalidDataException($"State file '{_path}' is empty or invalid.");
    }

    public void Save(ResetState state)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temp = Path.Combine(directory, $".state.{Environment.ProcessId}.tmp");
        var json = JsonSerializer.Serialize(state, StateJsonContext.Default.ResetState);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        FilePermissions.RestrictToCurrentUser(temp, isDirectory: false);
        File.Move(temp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResetState))]
internal sealed partial class StateJsonContext : JsonSerializerContext
{
}
