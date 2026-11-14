using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Dedupe
	{
		static readonly string[] Yaml = { ".mat", ".asset", ".anim", ".controller", ".mask", ".overrideController", ".prefab", ".physicMaterial" };
		static readonly string[] Binary = { ".png", ".jpg", ".tga", ".exr", ".ogg", ".wav", ".mp3", ".ttf", ".otf" };
		static readonly string[] Scan = Yaml.Concat(new[] { ".unity", ".playable", ".mixer", ".lighting", ".spriteatlas" }).ToArray();
		static readonly Regex Name = new(@"(?m)^  m_Name: (.*)$");
		static readonly Regex Reference = new(@"\{fileID: -?\d+(?:, guid: \w+, type: \d)?\}");
		static readonly Regex MetaId = new(@"(?m)^(guid|timeCreated): .*$");
		static readonly Regex Suffix = new(@"^(.*)_(\d+)$");
		static readonly Regex AnyGUID = new(@"guid: (\w{32})");

		public static void Run(Job job)
		{
			var merged = 0;
			var whites = 0;
			var rounds = 0;
			while (true)
			{
				job.Check();
				job.Progress(null, rounds == 0 ? "Finding duplicates" : "Second pass");
				var (merges, renames) = Plan(job);
				if (merges.Count == 0)
					break;
				whites += merges.Count(m => m.Key.EndsWith(".mat") && Count(File.ReadAllText(m.Key)) > Count(File.ReadAllText(m.Value)));
				Apply(job, merges, renames);
				merged += merges.Count;
				rounds++;
				job.Log($"Round {rounds}: {merges.Count:N0} copies merged, {renames.Count} renamed back");
			}
			job.Facts["duplicates"] = merged.ToString("N0");
			job.Good($"{merged:N0} duplicates merged, {whites} of them white materials");
		}

		static int Count(string text) => Regex.Matches(text, @"\{fileID: 0\}").Count;

		static string? Relation(string a, string b)
		{
			var la = a.Split('\n');
			var lb = b.Split('\n');
			if (la.Length != lb.Length)
				return null;
			var aMore = false;
			var bMore = false;
			for (var i = 0; i < la.Length; i++)
			{
				if (la[i] == lb[i])
					continue;
				if (Reference.Replace(la[i], "R") != Reference.Replace(lb[i], "R"))
					return null;
				if (lb[i].Contains("{fileID: 0}")) aMore = true;
				else if (la[i].Contains("{fileID: 0}")) bMore = true;
				else return null;
			}
			return aMore && bMore ? null : aMore ? "a" : bMore ? "b" : "same";
		}

		static string BinaryKey(string path)
		{
			var meta = MetaId.Replace(File.ReadAllText(path + ".meta"), "");
			return Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))) + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(meta)));
		}

		static (Dictionary<string, string> Merges, List<(string From, string To)> Renames) Plan(Job job)
		{
			var merges = new Dictionary<string, string>();
			var renames = new List<(string, string)>();
			var folders = new Stack<string>();
			folders.Push(job.Assets);
			while (folders.Count > 0)
			{
				var folder = folders.Pop();
				foreach (var child in Directory.GetDirectories(folder))
				{
					if (Path.GetFileName(child) is not ("Resources" or "Assemblies" or "StreamingAssets" or "Plugins" or "_ValheimEditor"))
						folders.Push(child);
				}
				var files = Directory.GetFiles(folder).Where(f => (Yaml.Contains(Path.GetExtension(f)) || Binary.Contains(Path.GetExtension(f))) && File.Exists(f + ".meta")).Select(Path.GetFileName).ToList();
				foreach (var group in files.GroupBy(f =>
				{
					var stem = Path.GetFileNameWithoutExtension(f!);
					var ext = Path.GetExtension(f!);
					var m = Suffix.Match(stem);
					return (m.Success ? m.Groups[1].Value : stem) + "|" + ext;
				}).Where(g => g.Count() > 1))
				{
					var key = group.Key.Split('|');
					var baseName = key[0] + key[1];
					var yaml = Yaml.Contains(key[1]);
					var paths = group.ToDictionary(f => f!, f => Path.Combine(folder, f!));
					var texts = yaml ? paths.ToDictionary(p => p.Key, p => File.ReadAllText(p.Value)) : null;
					var clusters = group.GroupBy(f => yaml ? Name.Match(texts![f!]) is { Success: true } m ? m.Groups[1].Value : "\0" : BinaryKey(paths[f!]));
					foreach (var cluster in clusters.Where(c => c.Count() > 1))
					{
						var order = cluster.OrderBy(f => texts != null ? Count(texts[f!]) : 0).ThenBy(f => f != baseName).ThenBy(f => f, StringComparer.Ordinal).ToList();
						var keepers = new List<string>();
						foreach (string? file in order)
						{
							var keeper = keepers.FirstOrDefault(k => texts == null || Relation(texts[k], texts[file!]) is "same" or "a");
							if (keeper != null)
								merges[paths[file!]] = paths[keeper];
							else
								keepers.Add(file!);
						}
						var target = Path.Combine(folder, baseName);
						if (keepers.Count == 1 && keepers[0] != baseName && (!File.Exists(target) || merges.ContainsKey(target)))
							renames.Add((paths[keepers[0]], target));
					}
				}
			}
			return (merges, renames);
		}

		static void Apply(Job job, Dictionary<string, string> merges, List<(string From, string To)> renames)
		{
			var swap = merges.ToDictionary(m => Remap.GUIDOf(m.Key), m => Remap.GUIDOf(m.Value));
			var changed = 0;
			foreach (var top in new[] { job.Assets, Path.Combine(job.Project, "ProjectSettings") })
				foreach (var path in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
				{
					if (top == job.Assets && !Scan.Contains(Path.GetExtension(path)) || merges.ContainsKey(path))
						continue;
					job.Check();
					var text = File.ReadAllText(path);
					var hit = false;
					foreach (Match m in AnyGUID.Matches(text))
						if (swap.ContainsKey(m.Groups[1].Value)) { hit = true; break; }
					if (!hit)
						continue;
					File.WriteAllText(path, AnyGUID.Replace(text, m => "guid: " + (swap.TryGetValue(m.Groups[1].Value, out var to) ? to : m.Groups[1].Value)), new UTF8Encoding(false));
					changed++;
				}
			var parked = Path.Combine(job.Removed, "duplicates");
			foreach (var file in merges.Keys)
			{
				var target = Path.Combine(parked, Path.GetRelativePath(job.Assets, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Move(file, target, true);
				File.Move(file + ".meta", target + ".meta", true);
			}
			foreach (var (from, to) in renames.Where(r => !File.Exists(r.To)))
			{
				File.Move(from, to);
				File.Move(from + ".meta", to + ".meta");
			}
			job.Log($"References repointed in {changed:N0} files");
		}
	}
}
