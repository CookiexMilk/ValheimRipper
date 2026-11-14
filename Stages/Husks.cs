using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Husks
	{
		static readonly Regex Document = new(@"(?m)^(?=--- !u!)");
		static readonly Regex Behaviour = new(@"^--- !u!114 &(-?\d+)");
		static readonly Regex Component = new(@"(?m)^  - component: \{fileID: (-?\d+)\}\r?\n");
		static readonly Regex AnyGUID = new(@"guid: (\w{32})");
		static readonly Regex BundleName = new(@"(?m)^(  assetBundleName: )\S[^\r\n]*");

		public static void Run(Job job)
		{
			var folder = Path.Combine(job.Assets, "StreamingAssets", "SoftRef", "Bundles");
			var total = 0;
			if (Directory.Exists(folder))
			{
				var bundles = Directory.GetFiles(folder).Where(f => !f.EndsWith(".meta")).OrderBy(f => f).ToArray();
				for (var i = 0; i < bundles.Length; i++)
				{
					job.Check();
					if (i % 20 == 0)
						job.Progress(i * 0.7 / bundles.Length, $"Checking bundle {i + 1:N0} of {bundles.Length:N0}");
					var removed = Bundle(bundles[i], Path.Combine(job.Removed, "original_bundles"), job);
					total += removed;
				}
			}
			job.Good($"{total} script-less objects removed from the bundles");
			job.Progress(0.75, "Cleaning project files");
			Project(job);
			BundleNames(job);
		}

		static void BundleNames(Job job)
		{
			job.Progress(0.9, "Clearing the game's asset bundle names");
			var cleared = 0;
			foreach (var meta in Directory.EnumerateFiles(job.Assets, "*.meta", SearchOption.AllDirectories))
			{
				job.Check();
				var text = File.ReadAllText(meta);
				if (!BundleName.IsMatch(text))
					continue;
				File.WriteAllText(meta, BundleName.Replace(text, "$1"), new UTF8Encoding(false));
				cleared++;
			}
			job.Good($"Asset bundle names cleared from {cleared:N0} assets, so building a mod's bundles builds only the mod's");
		}

		static int Bundle(string path, string backup, Job job)
		{
			var manager = new AssetsManager();
			try
			{
				var bundle = manager.LoadBundleFile(path, true);
				var index = Enumerable.Range(0, bundle.file.BlockAndDirInfo.DirectoryInfos.Count).FirstOrDefault(bundle.file.IsAssetsFile, -1);
				if (index < 0)
					return 0;
				var assets = manager.LoadAssetsFileFromBundle(bundle, index, false);
				var husks = new HashSet<long>();
				foreach (var info in assets.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
				{
					var script = manager.GetBaseField(assets, info)["m_Script"];
					if (script["m_FileID"].AsInt == 0 && script["m_PathID"].AsLong == 0)
						husks.Add(info.PathId);
				}
				if (husks.Count == 0)
					return 0;
				foreach (var info in assets.file.GetAssetsOfType(AssetClassID.GameObject))
				{
					var field = manager.GetBaseField(assets, info);
					var array = field["m_Component"]["Array"];
					if (array.Children.RemoveAll(c => c["component"]["m_FileID"].AsInt == 0 && husks.Contains(c["component"]["m_PathID"].AsLong)) > 0)
					{
						array.AsArray = new AssetTypeArrayInfo(array.Children.Count);
						info.SetNewData(field);
					}
				}
				var bundleInfo = assets.file.GetAssetsOfType(AssetClassID.AssetBundle)[0];
				var tree = manager.GetBaseField(assets, bundleInfo);
				var preload = tree["m_PreloadTable"]["Array"];
				var keep = preload.Children.Select(p => !(p["m_FileID"].AsInt == 0 && husks.Contains(p["m_PathID"].AsLong))).ToList();
				var before = new int[keep.Count + 1];
				for (var i = 0; i < keep.Count; i++)
					before[i + 1] = before[i] + (keep[i] ? 1 : 0);
				void Shift(AssetTypeValueField info)
				{
					var start = info["preloadIndex"].AsInt;
					var size = info["preloadSize"].AsInt;
					info["preloadIndex"].AsInt = before[start];
					info["preloadSize"].AsInt = before[start + size] - before[start];
				}
				var dropped = keep.Count(k => !k);
				preload.Children = preload.Children.Where((_, i) => keep[i]).ToList();
				preload.AsArray = new AssetTypeArrayInfo(preload.Children.Count);
				foreach (var pair in tree["m_Container"]["Array"].Children)
					Shift(pair["second"]);
				if (!tree["m_MainAsset"].IsDummy)
					Shift(tree["m_MainAsset"]);
				bundleInfo.SetNewData(tree);
				foreach (long id in husks)
					assets.file.GetAssetInfo(id).SetRemoved();
				bundle.file.BlockAndDirInfo.DirectoryInfos[index].SetNewData(assets.file);

				var loose = path + ".unpacked";
				var packed = path + ".patched";
				using (var writer = new AssetsFileWriter(loose))
					bundle.file.Write(writer);
				manager.UnloadAll(true);
				var reread = new AssetBundleFile();
				using (var reader = new AssetsFileReader(File.OpenRead(loose)))
				{
					reread.Read(reader);
					using var writer = new AssetsFileWriter(packed);
					reread.Pack(writer, AssetBundleCompressionType.LZ4);
				}
				reread.Close();
				File.Delete(loose);
				Verify(packed, husks);
				Directory.CreateDirectory(backup);
				var original = Path.Combine(backup, Path.GetFileName(path));
				if (!File.Exists(original))
					File.Copy(path, original);
				File.Move(packed, path, true);
				job.Log($"{Path.GetFileName(path)}: {husks.Count} husks, {dropped} preload entries, verified");
				return husks.Count;
			}
			finally
			{
				manager.UnloadAll(true);
			}
		}

		static void Verify(string path, HashSet<long> husks)
		{
			var manager = new AssetsManager();
			try
			{
				var bundle = manager.LoadBundleFile(path, true);
				var index = Enumerable.Range(0, bundle.file.BlockAndDirInfo.DirectoryInfos.Count).First(bundle.file.IsAssetsFile);
				var assets = manager.LoadAssetsFileFromBundle(bundle, index, false);
				var ids = assets.file.AssetInfos.Select(a => a.PathId).ToHashSet();
				if (husks.Any(ids.Contains))
					throw new InvalidOperationException($"{Path.GetFileName(path)}: a husk survived the patch");
				var tree = manager.GetBaseField(assets, assets.file.GetAssetsOfType(AssetClassID.AssetBundle)[0]);
				var preload = tree["m_PreloadTable"]["Array"].Children;
				if (preload.Any(p => p["m_FileID"].AsInt == 0 && !ids.Contains(p["m_PathID"].AsLong)))
					throw new InvalidOperationException($"{Path.GetFileName(path)}: the preload table points at a missing object");
				if (tree["m_Container"]["Array"].Children.Any(c => c["second"]["preloadIndex"].AsInt + c["second"]["preloadSize"].AsInt > preload.Count))
					throw new InvalidOperationException($"{Path.GetFileName(path)}: a container range is out of bounds");
			}
			finally
			{
				manager.UnloadAll(true);
			}
		}

		static void Project(Job job)
		{
			var loose = new Dictionary<string, string>();
			foreach (var path in Directory.EnumerateFiles(job.Assets, "*", SearchOption.AllDirectories))
			{
				var husk = false;
				if (path.EndsWith(".asset") && new FileInfo(path).Length < 2000)
				{
					var text = File.ReadAllText(path);
					husk = Regex.Matches(text, "--- !u!").Count == 1 && text.Contains("\n  m_Script: {fileID: 0}") && text.Contains("\n  m_GameObject: {fileID: 0}");
				}
				else if (path.EndsWith(".mat"))
					husk = File.ReadAllText(path).Contains("\n  m_Shader: {fileID: 0}");
				if (husk && File.Exists(path + ".meta"))
					loose[Remap.GUIDOf(path)] = path;
			}
			var components = 0;
			var scan = new[] { ".prefab", ".unity", ".mat", ".asset", ".controller", ".anim" };
			foreach (var path in Directory.EnumerateFiles(job.Assets, "*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(path);
				if (!scan.Contains(ext))
					continue;
				job.Check();
				var text = File.ReadAllText(path);
				if (loose.Count > 0)
					foreach (Match m in AnyGUID.Matches(text))
						loose.Remove(m.Groups[1].Value);
				if ((ext != ".prefab" && ext != ".unity") || !text.Contains("m_Script: {fileID: 0}"))
					continue;
				var documents = Document.Split(text);
				var husks = documents.Where(d => d.StartsWith("--- !u!114 &") && d.Contains("\n  m_Script: {fileID: 0}")).Select(d => Behaviour.Match(d).Groups[1].Value).ToHashSet();
				if (husks.Count == 0)
					continue;
				var kept = string.Concat(documents.Where(d => !(d.StartsWith("--- !u!114 &") && husks.Contains(Behaviour.Match(d).Groups[1].Value))));
				kept = Component.Replace(kept, m => husks.Contains(m.Groups[1].Value) ? "" : m.Value);
				File.WriteAllText(path, kept, new UTF8Encoding(false));
				components += husks.Count;
				job.Log($"{Path.GetFileName(path)}: {husks.Count} empty component(s) removed");
			}
			var parked = Path.Combine(job.Removed, "husks");
			foreach (var path in loose.Values)
				Prepare.Park(path, parked);
			job.Facts["leftovers"] = loose.Count.ToString();
			job.Good($"{components} empty components removed. {loose.Count} unused dev files moved to removed_plugins\\husks");
		}
	}
}
