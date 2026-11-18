namespace SharedWorlds.Core;

public enum VersionRelation
{
	Unknown,
	Same,
	/// <summary>Right-hand version descends from the left-hand version.</summary>
	RightIsNewer,
	/// <summary>Left-hand version descends from the right-hand version.</summary>
	LeftIsNewer,
	Unrelated
}

/// <summary>
/// Hash/parent graph used for fast-forward detection and fork detection.
/// Terraria worlds cannot be merged, so an unrelated head is always a user decision.
/// </summary>
public sealed class VersionGraph
{
	private readonly Dictionary<string, VersionRecord> _records = new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyCollection<VersionRecord> Records => _records.Values;
	public int Count => _records.Count;

	public void Add(VersionRecord? record)
	{
		if (record == null || string.IsNullOrWhiteSpace(record.Hash))
			return;

		if (_records.TryGetValue(record.Hash, out VersionRecord? existing)) {
			// Same content can be reported with slightly different metadata.
			// Keep the oldest parent/timestamp; fill in missing information only.
			if (string.IsNullOrEmpty(existing.ParentHash) && !string.IsNullOrEmpty(record.ParentHash))
				existing.ParentHash = record.ParentHash;

			if (existing.Version == 0 && record.Version != 0)
				existing.Version = record.Version;

			if (existing.HostSteamId == 0 && record.HostSteamId != 0)
				existing.HostSteamId = record.HostSteamId;

			if (existing.TimestampUtc == default && record.TimestampUtc != default)
				existing.TimestampUtc = record.TimestampUtc;

			if (string.IsNullOrEmpty(existing.WorldName) && !string.IsNullOrEmpty(record.WorldName))
				existing.WorldName = record.WorldName;

			return;
		}

		_records[record.Hash] = record;
	}

	public void AddRange(IEnumerable<VersionRecord>? records)
	{
		if (records == null)
			return;

		foreach (VersionRecord record in records)
			Add(record);
	}

	public bool Contains(string? hash)
		=> !string.IsNullOrWhiteSpace(hash) && _records.ContainsKey(hash);

	public VersionRecord? Get(string? hash)
		=> !string.IsNullOrWhiteSpace(hash) && _records.TryGetValue(hash, out VersionRecord? record) ? record : null;

	/// <summary>True when <paramref name="ancestorHash"/> is the same as, or an ancestor of, <paramref name="descendantHash"/>.</summary>
	public bool IsAncestor(string? ancestorHash, string? descendantHash)
	{
		if (string.IsNullOrWhiteSpace(ancestorHash) || string.IsNullOrWhiteSpace(descendantHash))
			return false;

		string? current = descendantHash;
		for (int guard = 0; guard < 10_000 && !string.IsNullOrEmpty(current); guard++) {
			if (string.Equals(current, ancestorHash, StringComparison.OrdinalIgnoreCase))
				return true;

			if (!_records.TryGetValue(current, out VersionRecord? record))
				return false;

			current = record.ParentHash;
		}

		return false;
	}

	public VersionRelation Compare(string? leftHash, string? rightHash)
	{
		if (string.IsNullOrWhiteSpace(leftHash) || string.IsNullOrWhiteSpace(rightHash))
			return VersionRelation.Unknown;

		if (string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase))
			return VersionRelation.Same;

		bool rightDescendsFromLeft = IsAncestor(leftHash, rightHash);
		bool leftDescendsFromRight = IsAncestor(rightHash, leftHash);

		if (rightDescendsFromLeft && !leftDescendsFromRight)
			return VersionRelation.RightIsNewer;

		if (leftDescendsFromRight && !rightDescendsFromLeft)
			return VersionRelation.LeftIsNewer;

		if (rightDescendsFromLeft)
			return VersionRelation.Same;

		return VersionRelation.Unrelated;
	}

	/// <summary>Returns the records that no other record in the set descends from.</summary>
	public List<VersionRecord> FindHeads(IEnumerable<VersionRecord> records)
	{
		var list = records
			.Where(r => !string.IsNullOrWhiteSpace(r.Hash))
			.GroupBy(r => r.Hash, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToList();

		var heads = new List<VersionRecord>();
		foreach (VersionRecord candidate in list) {
			bool hasDescendant = list.Any(other =>
				!string.Equals(other.Hash, candidate.Hash, StringComparison.OrdinalIgnoreCase) &&
				IsAncestor(candidate.Hash, other.Hash));

			if (!hasDescendant)
				heads.Add(candidate);
		}

		return heads
			.OrderByDescending(h => h.Version)
			.ThenByDescending(h => h.TimestampUtc)
			.ToList();
	}

	public static VersionRecord? PickBest(IEnumerable<VersionRecord> heads)
		=> heads
			.OrderByDescending(h => h.Version)
			.ThenByDescending(h => h.TimestampUtc)
			.FirstOrDefault();
}
