using System.Text.Json;
using System.Text.Json.Serialization;
using Fermata.Core.Monitoring;
using Fermata.Core.Ports;

namespace Fermata.Platform;

/// <summary>status.json: atomic replace; unreadable or missing means "no snapshot".</summary>
public sealed class JsonStatusSnapshotStore : IStatusSnapshotStore
{
    private readonly string _path;

    public JsonStatusSnapshotStore(string path)
    {
        _path = path;
    }

    public StatusSnapshot? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), StatusJsonContext.Default.StatusSnapshot)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(StatusSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".status.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, StatusJsonContext.Default.StatusSnapshot));
        FilePermissions.RestrictToCurrentUser(temp, isDirectory: false);
        File.Move(temp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StatusSnapshot))]
internal sealed partial class StatusJsonContext : JsonSerializerContext
{
}
