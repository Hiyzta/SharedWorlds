using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace SharedWorlds.Core;

public sealed class SharedWorldsConfig : ModConfig
{
	public static SharedWorldsConfig Current
	{
		get {
			try {
				return ModContent.GetInstance<SharedWorldsConfig>() ?? Default;
			}
			catch {
				return Default;
			}
		}
	}

	private static readonly SharedWorldsConfig Default = new();

	public override ConfigScope Mode => ConfigScope.ClientSide;

	[Header("Workshop")]
	[DefaultValue(true)]
	public bool EnableWorkshopStorage { get; set; } = true;

	[DefaultValue(true)]
	public bool AutoUploadOnWorldExit { get; set; } = true;

	[DefaultValue(180)]
	[Range(30, 600)]
	public int UploadTimeoutSeconds { get; set; } = 180;

	[DefaultValue(60)]
	[Range(10, 300)]
	public int QueryTimeoutSeconds { get; set; } = 60;

	[DefaultValue(180)]
	[Range(30, 600)]
	public int DownloadTimeoutSeconds { get; set; } = 180;

	[Header("LiveSession")]
	[DefaultValue(true)]
	public bool AutoUploadWhilePlaying { get; set; } = true;

	[DefaultValue(10)]
	[Range(5, 60)]
	public int AutoUploadIntervalMinutes { get; set; } = 10;

	[DefaultValue(true)]
	public bool EnableHostMigration { get; set; } = true;

	[Header("LocalSafety")]
	[DefaultValue(8)]
	[Range(0, 50)]
	public int MaxLocalBackups { get; set; } = 8;

	[DefaultValue(250)]
	[Range(10, 1000)]
	public int MaxHistoryEntries { get; set; } = 250;

	[Header("Interface")]
	[DefaultValue(true)]
	public bool ShowMainMenuButton { get; set; } = true;

	[DefaultValue(true)]
	public bool EnablePeerTransfer { get; set; } = true;

	[DefaultValue(false)]
	public bool VerboseLogging { get; set; } = false;
}
