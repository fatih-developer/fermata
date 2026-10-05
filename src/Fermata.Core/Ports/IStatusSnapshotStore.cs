using Fermata.Core.Monitoring;

namespace Fermata.Core.Ports;

/// <summary>Last monitor view on disk (see <see cref="StatusSnapshot"/>).</summary>
public interface IStatusSnapshotStore
{
    /// <summary>Null when missing or unreadable.</summary>
    StatusSnapshot? Load();

    void Save(StatusSnapshot snapshot);
}
