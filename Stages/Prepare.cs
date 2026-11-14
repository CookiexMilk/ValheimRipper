using System.Text;
using System.Text.RegularExpressions;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Prepare
	{
		static readonly string[] EngineModules = { "UnityEngine.IdentifiersModule", "UnityEngine.InsightsModule", "UnityEngine.ProfilerModule", "UnityEngine.UnityConsentModule" };
		static readonly Regex GraphicsAPIs = new(@"(?m)^  m_BuildTargetGraphicsAPIs:.*\r?\n(?:  (?:- |  ).*\r?\n)*");
		const string APIs = "  m_BuildTargetGraphicsAPIs:\n"
			+ "  - m_BuildTarget: WindowsStandaloneSupport\n    m_APIs: 0200000015000000\n    m_Automatic: 0\n"
			+ "  - m_BuildTarget: MacStandaloneSupport\n    m_APIs: 10000000\n    m_Automatic: 0\n"
			+ "  - m_BuildTarget: LinuxStandaloneSupport\n    m_APIs: 1100000015000000\n    m_Automatic: 0\n";
		static readonly Regex Deferred = new(@"(?m)(^  m_Deferred:\r?\n    m_Mode: )\d+(\r?\n    m_Shader: )\{[^}]*\}");

		public static void Run(Job job)
		{
			Fix(job);
			Assemblies(job);
			Layout(job.Project, job.Unity);
			job.Progress(1, null);
		}

		public static void Fix(Job job)
		{
			if (job.Dummy)
			{
				BuiltInDeferred(job);
				var numbers = Shaders.FixDummy(job.Assets);
				if (numbers > 0)
					job.Log($"Dummy shaders: {numbers} had numbers in their properties that Unity can't read (like 1E-07), now written out in full");
			}
			else
			{
				job.Progress(0.05, "Fixing the game's shaders");
				var (files, fixedFiles) = Shaders.Fix(job.Assets);
				job.Good($"{files} YAML shaders, {fixedFiles} fixed");
				job.Facts["shaders"] = files.ToString();
			}
			SetGraphicsAPIs(job);

			foreach (var eye in Directory.GetFiles(job.Assets, "EyeHistogram.asset", SearchOption.AllDirectories))
			{
				File.Delete(eye);
				File.Delete(eye + ".meta");
				Kit.CopyFolder("PostFX", Path.GetDirectoryName(eye)!);
				job.Log("EyeHistogram replaced with Unity's post-processing v1 source");
			}
		}

		public static void Assemblies(Job job)
		{
			job.Progress(0.35, "Restoring the game DLLs");
			var managed = job.Managed;
			var shipped = Path.Combine(job.Game, "valheim_Data", "Managed");
			var restored = 0;
			foreach (var dll in Directory.GetFiles(managed, "*.dll"))
			{
				var original = Path.Combine(shipped, Path.GetFileName(dll));
				if (File.Exists(original) && CopyIfChanged(original, dll))
					restored++;
			}
			var hidden = job.On("packages");
			foreach (var meta in Directory.GetFiles(managed, "*.dll.meta"))
			{
				var text = File.ReadAllText(meta);
				var dll = meta[..^".meta".Length];
				var edited = Regex.Replace(text, @"(?m)(    Editor:\r?\n      enabled: )0", "${1}1");
				if (Path.GetFileName(dll) == "Assembly-CSharp.dll" || hidden && !Packages.IsUnityPackage(dll))
					edited = Regex.Replace(edited, @"(?m)^(  isExplicitlyReferenced: )\d", "${1}1");
				if (edited != text)
					File.WriteAllText(meta, edited, new UTF8Encoding(false));
			}
			job.Log(hidden
				? $"{restored} game DLLs restored and enabled in the editor, Auto Reference off so Unity's packages don't clash with them"
				: $"{restored} game DLLs restored and enabled in the editor, Auto Reference left as AssetRipper exported it except on Assembly-CSharp.dll");

			var assemblyCSharp = Path.Combine(managed, "Assembly-CSharp.dll");
			if (File.Exists(assemblyCSharp))
			{
				var scripts = Path.Combine(Path.GetDirectoryName(managed)!, "Scripts");
				Kit.CopyFolder("GameScripts", scripts);
				foreach (var script in Directory.GetFiles(scripts, "*.cs").Where(s => !File.Exists(s + ".meta")))
					File.WriteAllText(script + ".meta", $"fileFormatVersion: 2\nguid: {Guid.NewGuid():N}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
				var table = new Dictionary<string, Dictionary<int, (string, string)>> { [Remap.GUIDOf(assemblyCSharp)] = Remap.Scripts(scripts) };
				var (references, _, _) = Remap.Apply(job.Project, table, job);
				job.Log($"UILineRenderer and Line added as scripts, {references} references moved to them. Unity never uses classes from a DLL named Assembly-CSharp, so the game's copy stays only as a file.");
			}

			foreach (var module in EngineModules)
				foreach (var file in Directory.GetFiles(managed, module + ".dll*"))
					Park(file, job.Removed);
		}

		static void SetGraphicsAPIs(Job job)
		{
			var path = Path.Combine(job.Project, "ProjectSettings", "ProjectSettings.asset");
			var text = File.ReadAllText(path);
			if (!GraphicsAPIs.IsMatch(text))
			{
				job.Warn("No graphics API list in ProjectSettings.asset");
				return;
			}
			File.WriteAllText(path, GraphicsAPIs.Replace(text, APIs, 1), new UTF8Encoding(false));
			job.Log("Graphics APIs: Windows Direct3D11 + Vulkan, Mac Metal, Linux OpenGLCore + Vulkan, so asset bundles get Vulkan shaders");
		}

		static void BuiltInDeferred(Job job)
		{
			if (UseBuiltInDeferred(job.Project))
				job.Log("Dummy shaders: deferred lighting set to Unity's built-in shader, since the game's is a dummy");
			else
				job.Warn("Dummy shaders: no deferred shader setting in GraphicsSettings.asset");
		}

		public static bool UseBuiltInDeferred(string project)
		{
			var path = Path.Combine(project, "ProjectSettings", "GraphicsSettings.asset");
			var text = File.ReadAllText(path);
			if (!Deferred.IsMatch(text))
				return false;
			File.WriteAllText(path, Deferred.Replace(text, "${1}1${2}{fileID: 69, guid: 0000000000000000f000000000000000, type: 0}", 1), new UTF8Encoding(false));
			return true;
		}

		public static void Layout(string project, string unity)
		{
			var layout = Path.Combine(project, "UserSettings", "Layouts", "default-6000.dwlt");
			var standard = Path.Combine(Path.GetDirectoryName(unity)!, "Data", "Resources", "Layouts", "Default.wlt");
			if (File.Exists(layout) || !File.Exists(standard))
				return;
			Directory.CreateDirectory(Path.GetDirectoryName(layout)!);
			File.Copy(standard, layout);
		}

		public static void Repair(Job job)
		{
			if (job.Dummy)
			{
				var numbers = Shaders.FixDummy(job.Assets);
				job.Log(numbers == 0 ? "Dummy shaders: nothing to repair" : $"Dummy shaders: {numbers} repaired");
				return;
			}
			job.Progress(null, "Checking the YAML shaders");
			var (files, fixedFiles) = Shaders.Fix(job.Assets);
			job.Good(fixedFiles == 0 ? $"{files} YAML shaders, all intact" : $"{files} YAML shaders, {fixedFiles} repaired");
		}

		public static bool CopyIfChanged(string from, string to)
		{
			if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from)))
				return false;
			Directory.CreateDirectory(Path.GetDirectoryName(to)!);
			File.Copy(from, to, true);
			return true;
		}

		public static void Park(string file, string removed)
		{
			Directory.CreateDirectory(removed);
			foreach (var path in new[] { file, file + ".meta" }.Where(File.Exists))
				File.Move(path, Path.Combine(removed, Path.GetFileName(path)), true);
		}
	}
}
