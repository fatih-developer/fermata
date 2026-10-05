using System.Text.Json;
using System.Text.Json.Serialization;
using Fermata.Core.Ports;
using Fermata.Core.Reset;

namespace Fermata.Platform;

/// <summary>
/// state.json with atomic replace (write temp file, then rename over the old one) and a last-known
/// good copy in state.json.bak, used when the main file is corrupt.
/// </summary>
public sealed class JsonResetStateStore : IResetStateStore
{
    private readonly string _path;

    public JsonResetStateStore(string path)
    {
        _path = path;
    }

    public string BackupPath => _path + ".bak";

    /// <summary>Set when the last <see cref="Load"/> had to fall back to the backup.</summary>
    public string? LastLoadWarning { get; private set; }

    public ResetState Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_path))
        {
            return new ResetState();
        }

        if (TryRead(_path, out var state))
        {
            return state;
        }

        // A corrupt state must not be silently replaced by an empty one: it may hold a pending
        // idempotency key. Resuming from the backup is safe because a replayed key is idempotent.
        if (File.Exists(BackupPath) && TryRead(BackupPath, out var backup))
        {
            LastLoadWarning = $"State file '{_path}' is corrupt; continued from the backup '{BackupPath}'.";
            return backup;
        }

        throw new InvalidDataException($"State file '{_path}' is corrupt and no usable backup exists. Inspect or delete it.");
    }

    private static bool TryRead(string path, out ResetState state)
    {
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllText(path), StateJsonContext.Default.ResetState)!;
            return state is not null;
        }
        catch (JsonException)
        {
            state = null!;
            return false;
        }
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

        // Keep the previous version only if it is valid, so a corrupt file never overwrites a good backup.
        if (File.Exists(_path) && TryRead(_path, out _))
        {
            File.Copy(_path, BackupPath, overwrite: true);
        }

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
