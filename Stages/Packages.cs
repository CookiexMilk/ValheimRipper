using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Packages
	{
		const string Collections = "com.unity.collections";
		const string CollectionsVersion = "2.6.8";
		const string Browser = "com.unity.assetbundlebrowser";
		const string UI = "com.unity.ugui";
		const string BrowserSource = "https://github.com/Unity-Technologies/AssetBundles-Browser.git#c7923746fa5e95916bf1a4087eceadb72305d5b5";

		static readonly Dictionary<string, string?> Swap = new()
		{
			["Unity.TextMeshPro"] = UI, ["UnityEngine.UI"] = UI,
			["Unity.InputSystem"] = "com.unity.inputsystem", ["Unity.InputSystem.ForUI"] = "com.unity.inputsystem",
			["Unity.Timeline"] = "com.unity.timeline", ["Unity.AI.Navigation"] = "com.unity.ai.navigation",
			["Unity.Formats.Fbx.Runtime"] = "com.unity.formats.fbx", ["Autodesk.Fbx"] = "com.autodesk.fbx",
			["Unity.Profiling.Core"] = "com.unity.profiling.core", ["Unity.ScriptableBuildPipeline"] = "com.unity.scriptablebuildpipeline",
			["Newtonsoft.Json"] = "com.unity.nuget.newtonsoft-json", ["Unity.Burst"] = "com.unity.burst", ["Unity.Burst.Unsafe"] = "com.unity.burst",
			["Unity.Mathematics"] = "com.unity.mathematics", ["Unity.Collections"] = Collections, ["Unity.Collections.LowLevel.ILSupport"] = Collections,
			["Unity.MemoryProfiler"] = "com.unity.memoryprofiler",
			["Autodesk.Fbx.BuildTestAssets"] = null, ["UnityEngine.SpatialTracking"] = null, ["UnityEngine.XR.LegacyInputHelpers"] = null,
		};
		static readonly string[] CollectionsStrip = { "DocCodeSamples.Tests", "Unity.Collections.CodeGen", "Unity.Collections.PerformanceTests", "Unity.Collections.Tests", "Unity.Collections.Tests.Playmode" };

		public static bool IsUnityPackage(string dll) => Swap.ContainsKey(Path.GetFileNameWithoutExtension(dll));

		public static void Run(Job job)
		{
			var all = job.On("packages");
			var packages = Path.Combine(job.Project, "Packages");
			var managed = job.Managed;
			var kept = Swap.Where(s => !all && s.Value != null && s.Value != UI && File.Exists(Path.Combine(managed, s.Key + ".dll")))
				.Select(s => s.Value!).Distinct().ToList();
			var node = JsonNode.Parse(Kit.Text("manifest.json"))!;
			foreach (var package in kept)
				node["dependencies"]!.AsObject().Remove(package);
			var manifest = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(Path.Combine(packages, "manifest.json"), WithBrowser(manifest, job), new UTF8Encoding(false));
			File.Delete(Path.Combine(packages, "packages-lock.json"));

			job.Progress(null, "Downloading the packages");
			var sources = Resolve(job, manifest);
			if (all)
				InstallCollections(job, packages, sources);

			job.Progress(0.6, "Updating references");
			var tables = new Dictionary<string, Dictionary<int, (string GUID, string Name)>>();
			var dlls = new List<string>();
			foreach (var (name, source) in Swap.Where(s => all || s.Value == UI))
			{
				var dll = Path.Combine(managed, name + ".dll");
				if (!File.Exists(dll))
					continue;
				dlls.Add(dll);
				if (source == null)
					continue;
				if (!sources.TryGetValue(source, out var folder))
					throw new InvalidOperationException($"{source} could not be downloaded. See {Path.Combine(Storage.Folder, "packages", "resolve.log")}");
				tables[Remap.GUIDOf(dll)] = Remap.Scripts(folder);
			}
			var (references, changed, used) = Remap.Apply(job.Project, tables, job);
			foreach (var (script, count) in used.OrderByDescending(u => u.Value).Take(6))
				job.Log($"{count,7:N0}  {script}");
			var parked = Path.Combine(job.Removed, "package_dlls");
			foreach (var dll in dlls)
				Prepare.Park(dll, parked);
			if (kept.Count > 0)
				job.Log($"Game DLLs kept; {string.Join(", ", kept)} left out of the package list so they don't clash. UI and TextMeshPro always come from Unity's package, which carries the editor's UI tools");
			job.Facts["references"] = references.ToString("N0");
			job.Good($"{references:N0} references in {changed:N0} files updated. {dlls.Count} DLLs moved to removed_plugins\\package_dlls");
		}

		static void InstallCollections(Job job, string packages, Dictionary<string, string> sources)
		{
			job.Progress(0.4, "Installing Collections");
			var collections = Path.Combine(packages, Collections);
			if (Directory.Exists(collections))
				Directory.Delete(collections, true);
			CopyFolder(sources[Collections], collections);
			foreach (var strip in CollectionsStrip)
			{
				if (Directory.Exists(Path.Combine(collections, strip)))
					Directory.Delete(Path.Combine(collections, strip), true);
				File.Delete(Path.Combine(collections, strip + ".meta"));
			}
			var packageJSON = Path.Combine(collections, "package.json");
			var package = JsonNode.Parse(File.ReadAllText(packageJSON))!.AsObject();
			package["dependencies"]!.AsObject().Remove("com.unity.nuget.mono-cecil");
			package.Remove("_fingerprint");
			File.WriteAllText(packageJSON, package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
			sources[Collections] = collections;
			job.Log($"Collections {CollectionsVersion} installed without its code generator");
		}

		static string WithBrowser(string manifest, Job job)
		{
			if (Detect.Git() == null)
			{
				job.Warn($"Git isn't installed, so the AssetBundle Browser wasn't added. With Git installed, add it in the Package Manager from git URL {BrowserSource}");
				return manifest;
			}
			var node = JsonNode.Parse(manifest)!;
			node["dependencies"]![Browser] = BrowserSource;
			job.Log("AssetBundle Browser added: Window > AssetBundle Browser");
			return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		}

		static Dictionary<string, string> Resolve(Job job, string manifest)
		{
			var temp = Path.Combine(Storage.Folder, "packages");
			var node = JsonNode.Parse(manifest)!;
			node["dependencies"]![Collections] = CollectionsVersion;
			var wanted = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			var manifestPath = Path.Combine(temp, "Packages", "manifest.json");
			var cache = Path.Combine(temp, "Library", "PackageCache");
			if (!Directory.Exists(cache) || !File.Exists(manifestPath) || File.ReadAllText(manifestPath) != wanted)
			{
				if (Directory.Exists(temp))
					Directory.Delete(temp, true);
				Directory.CreateDirectory(Path.Combine(temp, "Packages"));
				Directory.CreateDirectory(Path.Combine(temp, "Assets"));
				File.WriteAllText(manifestPath, wanted);
				var log = Path.Combine(temp, "resolve.log");
				var code = Core.Run.Process(job.Unity, new[] { "-batchmode", "-quit", "-projectPath", temp, "-logFile", log }, job);
				if (code != 0 || !Directory.Exists(cache))
					throw new InvalidOperationException($"Unity couldn't resolve the packages (exit {code}); see {log}");
			}
			return Directory.GetDirectories(cache).ToDictionary(d => Path.GetFileName(d).Split('@')[0], d => d);
		}

		static void CopyFolder(string from, string to)
		{
			foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
			{
				var target = Path.Combine(to, Path.GetRelativePath(from, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(file, target, true);
			}
		}
	}
}
