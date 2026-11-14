using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ValheimRipper.Core
{
	sealed record Found(string Id, string Label, string Tag, string Value, string State, bool Browse, bool Required, string? Path);

	static class Detect
	{
		static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		public static bool IsGame(string? folder) => folder != null && File.Exists(System.IO.Path.Combine(folder, "valheim_Data", "Managed", "assembly_valheim.dll"));

		public static string? Game(string? chosen)
		{
			if (IsGame(chosen))
				return chosen;
			var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
			var libraries = new List<string>();
			if (steam != null)
			{
				libraries.Add(steam);
				var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
				if (File.Exists(vdf))
					libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
			}
			return libraries.Select(l => System.IO.Path.Combine(l, "steamapps", "common", "Valheim")).Select(System.IO.Path.GetFullPath).FirstOrDefault(IsGame);
		}

		public static string? UnityVersion(string game)
		{
			var player = System.IO.Path.Combine(game, "UnityPlayer.dll");
			if (!File.Exists(player))
				return null;
			var match = Regex.Match(FileVersionInfo.GetVersionInfo(player).ProductVersion ?? "", @"^\d+\.\d+\.\d+[abfp]\d+");
			return match.Success ? match.Value : null;
		}

		public static string? EditorVersion(string unity)
		{
			if (!File.Exists(unity))
				return null;
			var match = Regex.Match(FileVersionInfo.GetVersionInfo(unity).ProductVersion ?? "", @"^\d+\.\d+\.\d+[abfp]\d+");
			return match.Success ? match.Value : null;
		}

		public static string? Unity(string version, string? chosen)
		{
			if (chosen != null && EditorVersion(chosen) == version)
				return chosen;
			var files = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			var roots = new List<string> { System.IO.Path.Combine(files, "Unity", "Hub", "Editor"), System.IO.Path.Combine(files, "Unity", "Editor") };
			var secondary = System.IO.Path.Combine(AppData, "UnityHub", "secondaryInstallPath.json");
			if (File.Exists(secondary) && JsonSerializer.Deserialize<string>(File.ReadAllText(secondary)) is { Length: > 0 } extra)
				roots.Insert(0, extra);
			var editors = System.IO.Path.Combine(AppData, "UnityHub", "editors-v2.json");
			var located = new List<string>();
			if (File.Exists(editors))
				located.AddRange(Regex.Matches(File.ReadAllText(editors), "\"location\"\\s*:\\s*\\[?\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
			var candidates = roots.Where(Directory.Exists)
				.SelectMany(r => Directory.GetDirectories(r).Prepend(r))
				.Select(d => System.IO.Path.Combine(d, "Editor", "Unity.exe"))
				.Concat(located);
			return candidates.FirstOrDefault(c => EditorVersion(c) == version);
		}

		public static string? Git() =>
			(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(folder => System.IO.Path.Combine(folder.Trim('"', ' '), "git.exe"))
				.FirstOrDefault(File.Exists);

		public static bool UnityOpen(string project)
		{
			var lockfile = System.IO.Path.Combine(project, "Temp", "UnityLockfile");
			if (!File.Exists(lockfile))
				return false;
			try
			{
				using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.None);
				return false;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				return true;
			}
		}

		public static string? Synced(string path)
		{
			var full = System.IO.Path.GetFullPath(path).TrimEnd('\\') + "\\";
			foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
				if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root && full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
					return "OneDrive";
			foreach (var (folder, service) in new[] { ("\\OneDrive\\", "OneDrive"), ("\\OneDrive - ", "OneDrive"), ("\\Dropbox\\", "Dropbox"), ("\\Google Drive\\", "Google Drive"), ("\\My Drive\\", "Google Drive"), ("\\iCloudDrive\\", "iCloud Drive") })
				if (full.Contains(folder, StringComparison.OrdinalIgnoreCase))
					return service;
			return null;
		}

		public static string? Ripper(string? chosen)
		{
			if (chosen != null && File.Exists(chosen))
				return chosen;
			var here = AppContext.BaseDirectory;
			var folders = new[] { here }.Concat(Directory.GetDirectories(here));
			var names = new[] { "AssetRipper.GUI.Free.exe", "AssetRipper.GUI.Premium.exe", "AssetRipper.GUI.exe", "AssetRipper.exe" };
			return folders.SelectMany(f => names.Select(n => System.IO.Path.Combine(f, n))).FirstOrDefault(File.Exists);
		}
	}
}
