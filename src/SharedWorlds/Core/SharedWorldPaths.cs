using Terraria;
namespace SharedWorlds.Core;

public static class SharedWorldPaths
{
	public const string FolderName = "SharedWorlds";

	public static string Root => Path.Combine(Main.SavePath, FolderName);
	public static string RegistryPath => Path.Combine(Root, "registry.json");
	public static string SnapshotsRoot => Path.Combine(Root, "snapshots");
	public static string BackupsRoot => Path.Combine(Root, "backups");
	public static string TempRoot => Path.Combine(Root, "temp");
	public static string PendingRoot => Path.Combine(Root, "pending");

	public static void EnsureCreated()
	{
		Directory.CreateDirectory(Root);
		Directory.CreateDirectory(SnapshotsRoot);
		Directory.CreateDirectory(BackupsRoot);
		Directory.CreateDirectory(TempRoot);
		Directory.CreateDirectory(PendingRoot);
	}

	public static bool IsInsideSavePath(string path)
	{
		try {
			string full = Path.GetFullPath(path);
			string save = Path.GetFullPath(Main.SavePath);

			if (full.Equals(save, StringComparison.OrdinalIgnoreCase))
				return true;

			return full.StartsWith(save.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch {
			return false;
		}
	}

	public static string WorldPath(SharedWorldRecord record) => Path.Combine(Main.WorldPath, record.WorldFileName);
	public static string TwldPath(SharedWorldRecord record) => Path.ChangeExtension(WorldPath(record), ".twld");

	public static string SnapshotDir(string worldId, int version, string hash)
		=> Path.Combine(SnapshotsRoot, worldId, $"v{version}_{HashUtil.Short(hash, 8)}");

	public static string BackupDir(string worldId, int version)
		=> Path.Combine(BackupsRoot, worldId, $"v{version}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

	public static string TempDir(string name)
	{
		string dir = Path.Combine(TempRoot, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>Makes a safe .wld file name, preserving an existing extension.</summary>
	public static string SafeWorldFileName(string name)
	{
		foreach (char c in Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"))
			name = name.Replace(c, '_');

		name = name.Trim().TrimEnd('.');
		if (name.Length == 0)
			name = "SharedWorld";

		if (name.Length > 80)
			name = name[..80].TrimEnd();

		if (!name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase))
			name += ".wld";

		return name;
	}

	public static string SafeFolderName(string name)
	{
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');

		name = name.Trim();
		return name.Length == 0 ? "world" : name;
	}
}
