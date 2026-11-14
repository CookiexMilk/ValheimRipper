using System.Text.Json;

namespace ValheimRipper.Core
{
	sealed class Settings
	{
		public string Project { get; set; } = "";
		public Dictionary<string, string> Paths { get; set; } = new();
		public Dictionary<string, bool> Options { get; set; } = new();

		static string FilePath => Path.Combine(Storage.Folder, "settings.json");

		public static Settings Load()
		{
			try
			{
				return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings() : new Settings();
			}
			catch (JsonException)
			{
				return new Settings();
			}
		}

		public void Save()
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		}
	}
}
