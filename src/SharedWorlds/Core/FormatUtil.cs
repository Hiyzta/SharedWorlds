using System.Globalization;

namespace SharedWorlds.Core;

/// <summary>Human-readable timestamps. Versions stay internal; players see when a world last moved.</summary>
public static class FormatUtil
{
	public static string Timestamp(DateTime utc)
	{
		if (utc == default)
			return "never";

		return utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
	}

	public static string ShortTimestamp(DateTime utc)
	{
		if (utc == default)
			return "never";

		DateTime local = utc.ToLocalTime();
		return local.Date == DateTime.Today
			? local.ToString("HH:mm", CultureInfo.InvariantCulture)
			: local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
	}

	public static string Relative(DateTime utc)
	{
		if (utc == default)
			return "never";

		TimeSpan age = DateTime.UtcNow - utc;
		if (age < TimeSpan.Zero)
			age = TimeSpan.Zero;

		if (age.TotalSeconds < 45)
			return "just now";
		if (age.TotalMinutes < 60)
			return $"{(int)age.TotalMinutes}m ago";
		if (age.TotalHours < 24)
			return $"{(int)age.TotalHours}h ago";
		if (age.TotalDays < 7)
			return $"{(int)age.TotalDays}d ago";

		return Timestamp(utc);
	}

	public static string LastSync(DateTime utc)
		=> utc == default ? "Never synced" : $"Synced {Relative(utc)}";

	public static string PlayerCount(int count)
		=> count <= 0 ? "No players online" : count == 1 ? "1 player online" : $"{count} players online";
}
