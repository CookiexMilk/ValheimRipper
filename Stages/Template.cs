using System.Text;
using System.Text.RegularExpressions;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Template
	{
		static readonly Regex Scenes = new(@"(?m)^  m_Scenes:\r?\n(?:  [- ] .*\r?\n)*");
		static readonly Regex Cursor = new(@"(?m)^(  defaultCursor: )\{[^}]*\}");
		static readonly Regex Preloaded = new(@"(?m)^  preloadedAssets:\r?\n(?:  - .*\r?\n)*");
		static readonly string[] GameScripts = { "Line.cs", "Line.cs.meta", "UILineRenderer.cs", "UILineRenderer.cs.meta" };

		public static string Folder(string project) => project.TrimEnd('\\', '/') + "Template";

		public static void Run(Job job)
		{
			var target = Folder(job.Project);
			if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
				throw new InvalidOperationException($"{target} already exists. Delete or rename it, then press Carry on.");
			var assemblies = Path.Combine(target, "Assets", "_Template", "Assemblies");
			var copies = new (string From, string To)[]
			{
				(Path.Combine(job.Project, "ProjectSettings"), Path.Combine(target, "ProjectSettings")),
				(Path.Combine(job.Project, "Packages"), Path.Combine(target, "Packages")),
				(job.Managed, assemblies),
			};
			var bytes = 0L;
			foreach (var (from, to) in copies)
			{
				job.Check();
				foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
				{
					var copy = Path.Combine(to, Path.GetRelativePath(from, file));
					Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
					File.Copy(file, copy, true);
					bytes += new FileInfo(file).Length;
				}
			}
			var scripts = Path.Combine(Path.GetDirectoryName(job.Managed)!, "Scripts");
			foreach (var file in GameScripts.Where(f => File.Exists(Path.Combine(scripts, f))))
			{
				Directory.CreateDirectory(Path.Combine(assemblies, "Scripts"));
				File.Copy(Path.Combine(scripts, file), Path.Combine(assemblies, "Scripts", file), true);
			}
			var settings = Path.Combine(target, "ProjectSettings");
			Edit(Path.Combine(settings, "EditorBuildSettings.asset"), Scenes, "  m_Scenes: []\n");
			Edit(Path.Combine(settings, "ProjectSettings.asset"), Cursor, "${1}{fileID: 0}");
			Edit(Path.Combine(settings, "ProjectSettings.asset"), Preloaded, "  preloadedAssets: []\n");
			Prepare.UseBuiltInDeferred(target);
			Prepare.Layout(target, job.Unity);
			job.Facts["template"] = target;
			job.Good($"Template project made: {target} ({Math.Max(1, bytes >> 20)} MB)");
		}

		static void Edit(string path, Regex pattern, string replacement)
		{
			var text = File.ReadAllText(path);
			File.WriteAllText(path, pattern.Replace(text, replacement, 1), new UTF8Encoding(false));
		}
	}
}
