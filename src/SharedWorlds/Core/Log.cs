using Terraria.ModLoader;

namespace SharedWorlds.Core;

/// <summary>Small logging facade so core code does not depend on a loaded Mod instance.</summary>
public static class Log
{
	internal static Mod? ModInstance;

	public static void Info(string message) => Write(0, message, null);
	public static void Warn(string message, Exception? exception = null) => Write(1, message, exception);
	public static void Error(string message, Exception? exception = null) => Write(2, message, exception);
	public static void Debug(string message) => Write(3, message, null);

	private static void Write(int level, string message, Exception? exception)
	{
		string line = $"[SharedWorlds] {message}";

		if (ModInstance != null) {
			switch (level) {
				case 1: ModInstance.Logger.Warn(line); break;
				case 2: ModInstance.Logger.Error(line); break;
				case 3: ModInstance.Logger.Debug(line); break;
				default: ModInstance.Logger.Info(line); break;
			}

			if (exception != null)
				ModInstance.Logger.Error(exception);
			return;
		}

		Console.WriteLine(exception == null ? line : $"{line}\n{exception}");
	}
}
