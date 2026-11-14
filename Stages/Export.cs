using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	sealed record SavedSettings(string Path, string? Text, string Ours);

	static class Export
	{
		static string Backup(string folder) => Path.Combine(folder, "AssetRipper.Settings.backup.json");
		static readonly Regex ProgressLine = new(@"ExportProgress : \((\d+)/(\d+)\)");

		public static void Run(Job job)
		{
			var staging = job.Project + "_export";
			var exported = Path.Combine(staging, "ExportedProject");
			if (!File.Exists(Path.Combine(exported, "Assets", "Plugins", "assembly_valheim.dll")))
			{
				if (Directory.Exists(staging))
					Directory.Delete(staging, true);
				Rip(job, staging);
			}
			if (Directory.Exists(job.Project))
				Directory.Delete(job.Project, true);
			Directory.Move(exported, job.Project);
			Directory.Delete(staging, true);
			job.Good($"Exported to {job.Project}");
		}

		static void Rip(Job job, string staging)
		{
			var folder = Path.GetDirectoryName(job.Ripper)!;
			var settings = Path.Combine(folder, "AssetRipper.Settings.json");
			var saved = File.Exists(settings) ? File.ReadAllText(settings) : null;
			var kit = JsonNode.Parse(Kit.Text("AssetRipper.Settings.json"))!;
			if (saved != null && JsonNode.Parse(saved) is JsonObject mine)
			{
				foreach (var (name, section) in mine)
					if (section is JsonObject values && kit[name] is JsonObject target)
						foreach (var (key, value) in values)
							target[key] = value?.DeepClone();
				job.Log("Using the settings saved in AssetRipper. Script and shader export are set by this app.");
			}
			kit["Import"]!["ScriptContentLevel"] = 2;
			kit["Export"]!["ScriptExportMode"] = 3;
			kit["Export"]!["ShaderExportMode"] = job.Dummy ? 0 : 1;
			var ours = kit.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(Backup(folder), JsonSerializer.Serialize(new SavedSettings(settings, saved, ours)));
			File.WriteAllText(settings, ours);
			var port = FreePort();
			var started = DateTime.Now.AddSeconds(-2);
			var info = new ProcessStartInfo(job.Ripper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
			info.ArgumentList.Add("--headless");
			info.ArgumentList.Add("--port");
			info.ArgumentList.Add(port.ToString());
			using var process = Process.Start(info)!;
			using var watch = new CancellationTokenSource();
			try
			{
				using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
				var root = $"http://127.0.0.1:{port}";
				Ready(http, root, job, process);
				var follower = Task.Run(() => Follow(folder, started, job, watch.Token));
				job.Progress(null, "Loading the game files");
				job.Log($"Loading {job.Game}");
				Post(http, root + "/LoadFolder", job.Game, job);
				job.Log($"Exporting to {staging}");
				Post(http, root + "/Export/UnityProject", staging, job);
				if (!File.Exists(Path.Combine(staging, "ExportedProject", "Assets", "Plugins", "assembly_valheim.dll")))
					throw new InvalidOperationException("AssetRipper finished, but the export has no assembly_valheim.dll. Its log is next to AssetRipper's exe.");
			}
			finally
			{
				watch.Cancel();
				Core.Run.Kill(process);
				if (saved != null)
					File.WriteAllText(settings, saved);
				else
					File.Delete(settings);
				File.Delete(Backup(folder));
			}
		}

		public static string? Restore(string? ripper)
		{
			if (ripper == null || !File.Exists(Backup(Path.GetDirectoryName(ripper)!)))
				return null;
			var path = Backup(Path.GetDirectoryName(ripper)!);
			var backup = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
			File.Delete(path);
			if (backup == null || !File.Exists(backup.Path) || File.ReadAllText(backup.Path) != backup.Ours)
				return null;
			if (backup.Text != null)
				File.WriteAllText(backup.Path, backup.Text);
			else
				File.Delete(backup.Path);
			return backup.Path;
		}

		static void Ready(HttpClient http, string root, Job job, Process process)
		{
			for (var i = 0; i < 120; i++)
			{
				job.Check();
				if (process.HasExited)
					throw new InvalidOperationException("AssetRipper closed as it started.");
				try
				{
					http.GetAsync(root + "/", job.Cancel).GetAwaiter().GetResult();
					return;
				}
				catch (HttpRequestException)
				{
					Thread.Sleep(500);
				}
			}
			throw new InvalidOperationException("AssetRipper didn't answer on its web port within a minute.");
		}

		static void Post(HttpClient http, string url, string path, Job job)
		{
			var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("Path", path) });
			using var response = http.PostAsync(url, body, job.Cancel).GetAwaiter().GetResult();
			if (response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.Found && !response.IsSuccessStatusCode)
				throw new InvalidOperationException($"AssetRipper answered {(int)response.StatusCode} to {url}.");
		}

		static void Follow(string folder, DateTime started, Job job, CancellationToken stop)
		{
			string? log = null;
			var offset = 0L;
			while (!stop.IsCancellationRequested)
			{
				log ??= Directory.GetFiles(folder, "AssetRipper_*.log").Where(f => File.GetCreationTime(f) >= started).OrderByDescending(File.GetCreationTime).FirstOrDefault();
				if (log != null)
				{
					try
					{
						using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
						stream.Seek(offset, SeekOrigin.Begin);
						using var reader = new StreamReader(stream);
						var text = reader.ReadToEnd();
						offset = stream.Position;
						var last = ProgressLine.Matches(text).LastOrDefault();
						if (last != null)
						{
							var done = double.Parse(last.Groups[1].Value);
							var total = double.Parse(last.Groups[2].Value);
							job.Progress(done / total, $"Exporting {done:N0} of {total:N0} assets");
						}
						foreach (var line in text.Split('\n').Where(l => l.Contains("[Error]") || l.StartsWith("Export : ")).Take(20))
							job.Log(line.TrimEnd());
					}
					catch (IOException)
					{
					}
				}
				stop.WaitHandle.WaitOne(1000);
			}
		}

		static int FreePort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			return port;
		}
	}
}
