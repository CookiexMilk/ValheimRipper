using System.Reflection;

namespace ValheimRipper.Core
{
	static class Kit
	{
		static readonly Assembly Assembly = Assembly.GetExecutingAssembly();

		static IEnumerable<(string Name, string Resource)> Files(string folder) =>
			Assembly.GetManifestResourceNames()
				.Select(r => (Name: r.Replace('\\', '/'), Resource: r))
				.Where(f => f.Name.StartsWith("Kit/" + folder + "/"))
				.Select(f => (f.Name[(folder.Length + 5)..], f.Resource));

		public static byte[] Bytes(string path)
		{
			var resource = Assembly.GetManifestResourceNames().First(r => r.Replace('\\', '/') == "Kit/" + path);
			using var stream = Assembly.GetManifestResourceStream(resource)!;
			using var memory = new MemoryStream();
			stream.CopyTo(memory);
			return memory.ToArray();
		}

		public static string Text(string path) => System.Text.Encoding.UTF8.GetString(Bytes(path)).TrimStart('﻿');

		public static void Copy(string path, string target)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllBytes(target, Bytes(path));
		}

		public static int CopyFolder(string folder, string target, Func<string, bool>? keep = null)
		{
			var count = 0;
			foreach (var (name, resource) in Files(folder))
			{
				if (keep != null && !keep(name))
					continue;
				var path = Path.Combine(target, name);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using var stream = Assembly.GetManifestResourceStream(resource)!;
				using var file = File.Create(path);
				stream.CopyTo(file);
				count++;
			}
			return count;
		}
	}
}
