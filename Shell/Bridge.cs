using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ValheimRipper.Core;
using ValheimRipper.Stages;

namespace ValheimRipper.Shell
{
	sealed class Bridge
	{
		static readonly Dictionary<string, string> Links = new()
		{
			["guide"] = "https://github.com/Valheim-Modding/Wiki/wiki/Valheim-Unity-Project-Guide",
			["website"] = "https://valheim.cookiemilk.se/",
			["github"] = "https://github.com/CookiexMilk/ValheimRipper",
			["discord"] = "https://discord.com/invite/7GwrfWuWFn",
			["paypal"] = "https://www.paypal.com/paypalme/CookiexMilk",
			["coffee"] = "https://buymeacoffee.com/CookiexMilk",
		};
		static readonly JsonSerializerOptions JSON = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		static readonly string Version = typeof(Bridge).Assembly.GetName().Version!.ToString(3);
		static readonly Step[] Steps =
		{
			new("export", null, Export.Run),
			new("prepare", null, Prepare.Run),
			new("packages", null, Packages.Run),
			new("husks", null, Husks.Run),
			new("dedupe", "dedupe", Dedupe.Run),
			new("import", null, Editor.Import),
			new("check", null, Editor.Check),
			new("template", "template", Template.Run),
		};
		static readonly Step[] RepairSteps =
		{
			new("repair", null, Prepare.Repair),
			new("check", null, Editor.Check),
		};

		readonly Window window;
		readonly CoreWebView2 core;
		readonly Settings settings = Settings.Load();
		readonly Dictionary<string, Found> found = new();
		readonly string runLog = Path.Combine(Storage.Folder, "last-run.log");
		CancellationTokenSource? cancel;
		Dictionary<string, bool> options = new();

		public bool Running => cancel != null;

		public Bridge(Window window, CoreWebView2 core)
		{
			this.window = window;
			this.core = core;
		}

		void Post(object message)
		{
			var json = JsonSerializer.Serialize(message, JSON);
			if (window.InvokeRequired)
				window.BeginInvoke(() => core.PostWebMessageAsJson(json));
			else
				core.PostWebMessageAsJson(json);
		}

		public void Receive(string json)
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			switch (root.GetProperty("type").GetString())
			{
				case "ready":
					Post(new { type = "version", text = $"ValheimRipper {Version}" });
					Post(new { type = "saved", options = settings.Options });
					if (Export.Restore(Detect.Ripper(Chosen("ripper"))) is string restored)
						Log($"A rip was interrupted, so AssetRipper's own settings were put back: {restored}", "warn");
					var arguments = Environment.GetCommandLineArgs();
					var project = Array.IndexOf(arguments, "--project");
					if (project >= 0 && project + 1 < arguments.Length)
						settings.Project = Path.GetFullPath(arguments[project + 1]);
					Refresh();
					if (arguments.Contains("--start"))
						Post(new { type = "autostart" });
					else if (arguments.Contains("--repair"))
						Start(true);
					else if (Array.IndexOf(arguments, "--demo") is int demo and >= 0)
					{
						var mode = demo + 1 < arguments.Length && !arguments[demo + 1].StartsWith("--") ? arguments[demo + 1] : "run";
						Post(new { type = "demo", mode, speed = 6, log = arguments.Contains("--log") });
					}
					break;
				case "options":
					options = Read(root.GetProperty("options"));
					settings.Options = options;
					settings.Save();
					break;
				case "browse":
					Browse(root.GetProperty("id").GetString()!);
					break;
				case "start":
					options = Read(root.GetProperty("options"));
					Start(false);
					break;
				case "repair":
					Start(true);
					break;
				case "stop":
					Stop();
					break;
				case "open":
					Open(root.GetProperty("what").GetString()!);
					break;
				case "link":
					if (Links.TryGetValue(root.GetProperty("id").GetString()!, out var url))
						Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
					break;
				case "window":
					window.Command(root.GetProperty("action").GetString()!);
					break;
			}
		}

		static Dictionary<string, bool> Read(JsonElement element) => element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean());

		string? Chosen(string id) => settings.Paths.TryGetValue(id, out var path) ? path : null;

		void Refresh()
		{
			found.Clear();
			var game = Detect.Game(Chosen("game"));
			var version = game != null ? Detect.UnityVersion(game) : null;
			Add(new Found("game", "Valheim", version != null ? $"Unity {version}" : "", game ?? "Not found. Choose the Valheim folder.", game != null ? "ok" : "missing", true, true, game));
			var unity = version != null ? Detect.Unity(version, Chosen("unity")) : null;
			Add(new Found("unity", version != null ? $"Unity {version}" : "Unity", unity != null ? "same as the game" : "",
				unity ?? (version != null ? $"Not found. Install {version} with Unity Hub." : "Needs Valheim first."),
				unity != null ? "ok" : "missing", true, true, unity));
			var ripper = Detect.Ripper(Chosen("ripper"));
			var ripperVersion = ripper != null ? FileVersionInfo.GetVersionInfo(ripper).ProductVersion?.Split('+')[0] ?? "" : "";
			var untested = ripperVersion.Length > 0 && !ripperVersion.StartsWith("2.");
			if (untested)
				ripperVersion += " · this app is made for 2.x";
			else if (ripper != null && File.Exists(Path.Combine(Path.GetDirectoryName(ripper)!, "AssetRipper.Settings.json")))
				ripperVersion += " · your saved settings";
			Add(new Found("ripper", "AssetRipper", ripperVersion, ripper ?? "Not found. Put ValheimRipper next to AssetRipper, or choose it.", ripper == null ? "missing" : untested ? "warn" : "ok", true, true, ripper));
			var git = Detect.Git();
			Add(new Found("git", "Git", "for the AssetBundle Browser", git ?? "Not installed. The AssetBundle Browser is skipped.", git != null ? "ok" : "warn", false, false, git));
			Post(new { type = "found", items = new[] { "git", "unity", "ripper", "game" }.Select(id => found[id]) });
			if (settings.Project.Length == 0)
				settings.Project = Path.Combine(AppContext.BaseDirectory, "ValheimEditor");
			Project(settings.Project);
		}

		void Add(Found item) => found[item.Id] = item;

		void Project(string path)
		{
			settings.Project = path;
			settings.Save();
			string note;
			try
			{
				var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
				var free = drive.AvailableFreeSpace > 1L << 40 ? $"{drive.AvailableFreeSpace / (double)(1L << 40):0.0} TB" : $"{drive.AvailableFreeSpace >> 30} GB";
				var resume = Directory.Exists(Pipeline.Marks(path));
				var busy = !resume && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
				var finished = resume && Pipeline.Done(path, "check") && (!settings.Options.GetValueOrDefault("template") || Pipeline.Done(path, "template"));
				note = busy ? "Not empty. Choose an empty folder."
					: finished ? "This folder has a finished rip. Repair checks its shaders again; choose another folder for a new rip."
					: resume ? $"Unfinished rip found. Finished stages are skipped and its options stay as they were. {free} free."
					: $"{drive.Name.TrimEnd('\\')} has {free} free. The project needs about 20 GB."
					+ (drive.AvailableFreeSpace < 30L << 30 ? " That may not be enough." : "");
				if (!busy && !finished && Detect.Synced(path) is string service)
					note += $" {service} syncs this folder: the 20 GB project would upload, and files it locks break the import. Choose a folder outside it.";
				if (busy)
					path = "";
			}
			catch (Exception e) when (e is ArgumentException or IOException)
			{
				note = "This folder can't be used.";
				path = "";
			}
			Post(new { type = "project", path, note, resume = Directory.Exists(Pipeline.Marks(settings.Project)), ripOptions = Pipeline.Options(settings.Project) });
			Post(new { type = "progress", stages = Steps.Where(s => Pipeline.Done(settings.Project, s.Id)).Select(s => s.Id) });
		}

		void Browse(string id)
		{
			if (Running)
				return;
			if (id == "project")
			{
				using var dialog = new FolderBrowserDialog { Description = "An empty folder for the new Unity project", UseDescriptionForTitle = true, ShowNewFolderButton = true };
				if (dialog.ShowDialog(window) == DialogResult.OK)
					Project(dialog.SelectedPath);
				return;
			}
			string? picked = null;
			if (id is "unity" or "ripper")
			{
				using var dialog = new OpenFileDialog { Filter = id == "unity" ? "Unity editor|Unity.exe" : "AssetRipper|AssetRipper*.exe", Title = id == "unity" ? "Unity.exe of the version the game uses" : "AssetRipper's exe" };
				if (dialog.ShowDialog(window) == DialogResult.OK)
					picked = dialog.FileName;
			}
			else
			{
				using var dialog = new FolderBrowserDialog { Description = "The Valheim folder (with valheim.exe)", UseDescriptionForTitle = true };
				if (dialog.ShowDialog(window) == DialogResult.OK)
					picked = dialog.SelectedPath;
			}
			if (picked == null)
				return;
			settings.Paths[id] = picked;
			settings.Save();
			Refresh();
		}

		void Start(bool repair)
		{
			if (Running)
				return;
			Refresh();
			var missing = found.Values.Where(f => f.Required && f.Path == null).ToList();
			if (missing.Count > 0 || settings.Project.Length == 0)
				return;
			var project = settings.Project;
			if (Directory.Exists(project) && !Directory.Exists(Pipeline.Marks(project)) && Directory.EnumerateFileSystemEntries(project).Any())
				return;
			if (repair)
			{
				if (!Pipeline.Done(project, "check"))
					return;
				foreach (var step in RepairSteps)
					File.Delete(Path.Combine(Pipeline.Marks(project), step.Id + ".done"));
			}
			cancel = new CancellationTokenSource();
			Directory.CreateDirectory(Path.GetDirectoryName(runLog)!);
			File.WriteAllText(runLog, $"ValheimRipper {Version}, {DateTime.Now:yyyy-MM-dd HH:mm}\n");
			if (Pipeline.Options(project) is { } kept)
				options = new Dictionary<string, bool>(kept) { ["template"] = options.GetValueOrDefault("template") };
			var job = new Job
			{
				Project = project,
				Game = found["game"].Path!,
				Unity = found["unity"].Path!,
				Ripper = found["ripper"].Path!,
				Dummy = options.TryGetValue("yaml", out var yaml) && !yaml,
				Options = new Dictionary<string, bool>(options),
				Cancel = cancel.Token,
				Logger = Log,
			};
			Post(new { type = "running", value = true });
			var clock = Stopwatch.StartNew();
			Task.Run(() =>
			{
				var ok = Pipeline.Run(repair ? RepairSteps : Steps, job, (id, state, progress, detail, time) =>
				{
					if (state == "running")
						Post(new { type = "summary", big = Names(id), small = detail ?? "" });
					Post(new { type = "stage", id, state, progress = progress is < 0 ? null : progress, keep = progress is < 0, detail, time });
				});
				cancel = null;
				Post(new { type = "running", value = false });
				CopyLog(project);
				if (ok)
					Finish(job, clock.Elapsed, repair);
				else
					Post(new { type = "summary", big = "Stopped", small = "Carry on continues from here. Details are in the log." });
			});
		}

		static string Names(string id) => id switch
		{
			"export" => "Rips the game", "prepare" => "Fixes shaders and DLLs", "packages" => "Swaps in Unity's packages",
			"husks" => "Cleans up leftovers", "dedupe" => "Merges duplicates", "import" => "Imports into Unity", "check" => "Checks shaders",
			"template" => "Makes a template", "repair" => "Repairs shaders", _ => id,
		};

		void Finish(Job job, TimeSpan elapsed, bool repair)
		{
			var time = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min" : $"{Math.Max(1, (int)elapsed.TotalMinutes)} min";
			Post(new { type = "summary", big = repair ? $"Repaired in {time}" : $"Done in {time}", small = job.Project });
			var facts = new List<string[]>();
			void Fact(string key, string label) { if (job.Facts.TryGetValue(key, out var value)) facts.Add(new[] { value, label }); }
			Fact("shaders", "shaders compile");
			Fact("references", "references kept");
			Fact("duplicates", "duplicates merged");
			var open = Detect.UnityOpen(job.Project);
			var text = (repair ? "Shaders checked again."
				: job.Facts.TryGetValue("template", out var template) ? $"All stages finished. The template is in {Path.GetFileName(template)}."
				: "All stages finished.")
				+ (open ? " The project is already open in Unity." : " Unity is opening the project.");
			Post(new { type = "result", title = repair ? "Repaired" : "Done", text, facts });
			if (!open)
				Process.Start(new ProcessStartInfo(job.Unity) { ArgumentList = { "-projectPath", job.Project }, UseShellExecute = false });
		}

		void Log(string text, string? level)
		{
			Post(new { type = "log", text, level });
			try
			{
				File.AppendAllText(runLog, text + "\n");
			}
			catch (IOException)
			{
			}
		}

		void CopyLog(string project)
		{
			try
			{
				var logs = Path.Combine(project, "Logs");
				if (Directory.Exists(project))
				{
					Directory.CreateDirectory(logs);
					File.Copy(runLog, Path.Combine(logs, "ripper.log"), true);
				}
			}
			catch (IOException)
			{
			}
		}

		public void Stop() => cancel?.Cancel();

		void Open(string what)
		{
			var project = settings.Project;
			switch (what)
			{
				case "folder" when Directory.Exists(project):
					Process.Start("explorer.exe", project);
					break;
				case "log":
					var logs = Path.Combine(project, "Logs");
					Process.Start("explorer.exe", Directory.Exists(logs) ? logs : Path.GetDirectoryName(runLog)!);
					break;
			}
		}
	}
}
