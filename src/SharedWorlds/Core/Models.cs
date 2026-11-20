using System.Text.Json.Serialization;

namespace SharedWorlds.Core;

/// <summary>Persistent registration of one shared world on this machine.</summary>
public sealed class SharedWorldRecord
{
	/// <summary>Stable GUID for the shared world (not Terraria's UniqueId).</summary>
	public string WorldId { get; set; } = "";

	public string WorldName { get; set; } = "";

	/// <summary>File name inside <see cref="Terraria.Main.WorldPath"/>, e.g. "MyWorld.wld".</summary>
	public string WorldFileName { get; set; } = "";

	/// <summary>This account's Workshop item for this world. Zero until created.</summary>
	public ulong WorkshopItemId { get; set; }

	public int LocalVersion { get; set; }
	public string LocalHash { get; set; } = "";
	public string LastParentHash { get; set; } = "";

	public ulong LastHostSteamId { get; set; }
	public DateTime LastUpdatedUtc { get; set; }

	/// <summary>SteamID64 (string) -> that account's Workshop item ID for this world.</summary>
	public Dictionary<string, ulong> Participants { get; set; } = new();

	/// <summary>Known version records, oldest first. Capped when saved.</summary>
	public List<VersionRecord> History { get; set; } = new();

	/// <summary>Mods present when this local version was saved, for diagnostics.</summary>
	public List<ModEntry> Mods { get; set; } = new();

	public bool WorkshopStorageEnabled { get; set; } = true;

	/// <summary>Set when a snapshot was built but could not be uploaded yet.</summary>
	public bool PendingUpload { get; set; }
	public string? PendingSnapshotDir { get; set; }
	public int PendingVersion { get; set; }
	public string? PendingHash { get; set; }

	/// <summary>Last time the pending fields changed; used to merge clear/queue transitions across processes.</summary>
	public DateTime PendingUpdatedUtc { get; set; }

	/// <summary>Steam uploaded the item but is waiting for the one-time Workshop Legal Agreement.</summary>
	public bool PendingNeedsEula { get; set; }

	[JsonIgnore]
	public bool HasLocalFile => !string.IsNullOrWhiteSpace(WorldFileName);

	[JsonIgnore]
	public int HighestKnownVersion => Math.Max(LocalVersion, History.Count == 0 ? 0 : History.Max(v => v.Version));
}

public sealed class VersionRecord
{
	public int Version { get; set; }
	public string Hash { get; set; } = "";
	public string ParentHash { get; set; } = "";
	public ulong HostSteamId { get; set; }
	public DateTime TimestampUtc { get; set; }
	public string WorldName { get; set; } = "";

	/// <summary>Workshop item this record was observed on, if known. Not part of the identity.</summary>
	public string? SourceItemId { get; set; }

	[JsonIgnore]
	public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;
}

public sealed class ModEntry
{
	public string Name { get; set; } = "";
	public string Version { get; set; } = "";
}

public sealed class SnapshotManifest
{
	public int Schema { get; set; } = 1;
	public string WorldId { get; set; } = "";
	public string WorldName { get; set; } = "";
	public int Version { get; set; }
	public string ContentHash { get; set; } = "";
	public string ParentHash { get; set; } = "";
	public ulong LastHostSteamId { get; set; }
	public DateTime TimestampUtc { get; set; }

	public List<ModEntry> Mods { get; set; } = new();
	public Dictionary<string, ulong> Participants { get; set; } = new();
	public List<VersionRecord> History { get; set; } = new();

	[JsonIgnore]
	public VersionRecord AsVersionRecord => new() {
		Version = Version,
		Hash = ContentHash,
		ParentHash = ParentHash,
		HostSteamId = LastHostSteamId,
		TimestampUtc = TimestampUtc,
		WorldName = WorldName
	};
}

/// <summary>
/// Compact header stored in Steam Workshop item metadata (hard limit: 5000 bytes).
/// Content-side manifest.json is authoritative; this is only a query hint.
/// </summary>
public sealed class WorkshopMetadata
{
	[JsonPropertyName("s")] public int Schema { get; set; } = 1;
	[JsonPropertyName("id")] public string WorldId { get; set; } = "";
	[JsonPropertyName("n")] public string WorldName { get; set; } = "";
	[JsonPropertyName("v")] public int Version { get; set; }
	[JsonPropertyName("h")] public string ContentHash { get; set; } = "";
	[JsonPropertyName("p")] public string ParentHash { get; set; } = "";
	[JsonPropertyName("host")] public ulong LastHostSteamId { get; set; }
	[JsonPropertyName("ts")] public long TimestampUnix { get; set; }

	/// <summary>Optional participant map (SteamID64 -> item ID), trimmed to fit.</summary>
	[JsonPropertyName("items")] public Dictionary<string, ulong>? Participants { get; set; }
}

public sealed class SharedWorldRegistry
{
	public int Schema { get; set; } = 1;
	public List<SharedWorldRecord> Worlds { get; set; } = new();
}

public sealed class WorkshopItemInfo
{
	public ulong ItemId { get; set; }
	public ulong OwnerSteamId { get; set; }
	public string Title { get; set; } = "";
	public string Description { get; set; } = "";
	public string? MetadataJson { get; set; }
	public WorkshopMetadata? Metadata { get; set; }
	public DateTime UpdatedUtc { get; set; }
	public long FileSize { get; set; }
	public bool Banned { get; set; }
	public bool Installed { get; set; }
	public bool NeedsUpdate { get; set; }
}

public enum SyncStatus
{
	Unknown,
	LocalOnly,
	UpToDate,
	RemoteNewer,
	LocalNewer,
	Fork,
	PendingUpload,
	WorkshopUnavailable,
	Downloading,
	Uploading,
	Error
}

public sealed class SyncReport
{
	public SyncStatus Status { get; set; } = SyncStatus.Unknown;
	public string Message { get; set; } = "";
	public SharedWorldRecord? Record { get; set; }
	public VersionRecord? BestRemote { get; set; }
	public List<WorkshopItemInfo> Items { get; set; } = new();
	public List<VersionRecord> RemoteHeads { get; set; } = new();
	public List<VersionRecord> ForkHeads { get; set; } = new();
	public Exception? Exception { get; set; }

	[JsonIgnore]
	public bool IsOk => Status is SyncStatus.UpToDate or SyncStatus.LocalNewer or SyncStatus.RemoteNewer or SyncStatus.LocalOnly or SyncStatus.PendingUpload;
}

public sealed class UploadResult
{
	public bool Success { get; set; }
	public bool NeedsEula { get; set; }
	public bool Pending { get; set; }
	public string Message { get; set; } = "";
	public ulong ItemId { get; set; }
	public Exception? Exception { get; set; }
}

public sealed class DownloadResult
{
	public bool Success { get; set; }
	public string Message { get; set; } = "";
	public string? InstallFolder { get; set; }
	public Exception? Exception { get; set; }
}
