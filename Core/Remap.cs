using System.Text;
using System.Text.RegularExpressions;

namespace ValheimRipper.Core
{
	static class Remap
	{
		public static readonly string[] Yaml = { ".prefab", ".unity", ".asset", ".mat", ".controller", ".overridecontroller", ".anim", ".mask", ".playable", ".lighting", ".physicmaterial", ".mixer", ".spriteatlas" };
		static readonly Regex Namespace = new(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
		static readonly Regex GUID = new(@"^guid: (\w+)", RegexOptions.Multiline);

		public static string GUIDOf(string path) => GUID.Match(File.ReadAllText(path + ".meta")).Groups[1].Value;

		public static int FileId(string nameSpace, string name) => BitConverter.ToInt32(MD4(Encoding.UTF8.GetBytes("s\0\0\0" + nameSpace + name)), 0);

		public static Dictionary<int, (string GUID, string Name)> Scripts(string folder)
		{
			var table = new Dictionary<int, (string, string)>();
			foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
			{
				if (!File.Exists(file + ".meta"))
					continue;
				var match = Namespace.Match(File.ReadAllText(file));
				var space = match.Success ? match.Groups[1].Value : "";
				var name = Path.GetFileNameWithoutExtension(file);
				table[FileId(space, name)] = (GUIDOf(file), space.Length > 0 ? space + "." + name : name);
			}
			return table;
		}

		public static (int References, int Files, Dictionary<string, int> Used) Apply(string project, Dictionary<string, Dictionary<int, (string GUID, string Name)>> tables, Job? job = null)
		{
			var pattern = new Regex(@"\{fileID: (-?\d+), guid: (" + string.Join("|", tables.Keys) + @"), type: 3\}");
			var used = new Dictionary<string, int>();
			var references = 0;
			var files = 0;
			foreach (var path in Directory.EnumerateFiles(Path.Combine(project, "Assets"), "*", SearchOption.AllDirectories))
			{
				if (!Yaml.Contains(Path.GetExtension(path).ToLowerInvariant()))
					continue;
				job?.Check();
				var text = File.ReadAllText(path);
				if (!tables.Keys.Any(text.Contains))
					continue;
				var swapped = pattern.Replace(text, m =>
				{
					var table = tables[m.Groups[2].Value];
					if (!table.TryGetValue(int.Parse(m.Groups[1].Value), out var script))
						return m.Value;
					used[script.Name] = used.GetValueOrDefault(script.Name) + 1;
					references++;
					return "{fileID: 11500000, guid: " + script.GUID + ", type: 3}";
				});
				if (swapped != text)
				{
					File.WriteAllText(path, swapped, new UTF8Encoding(false));
					files++;
				}
			}
			return (references, files, used);
		}

		static byte[] MD4(byte[] data)
		{
			static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
			static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
			static uint H(uint x, uint y, uint z) => x ^ y ^ z;
			static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));
			var padding = (55 - data.Length) % 64;
			if (padding < 0) padding += 64;
			var message = new byte[data.Length + 1 + padding + 8];
			data.CopyTo(message, 0);
			message[data.Length] = 0x80;
			BitConverter.GetBytes((ulong)data.Length * 8).CopyTo(message, message.Length - 8);
			var a = 0x67452301u;
			var b = 0xEFCDAB89u;
			var c = 0x98BADCFEu;
			var d = 0x10325476u;
			var x = new uint[16];
			for (var block = 0; block < message.Length; block += 64)
			{
				for (var k = 0; k < 16; k++)
					x[k] = BitConverter.ToUInt32(message, block + k * 4);
				var aa = a;
				var bb = b;
				var cc = c;
				var dd = d;
				var round1 = new[] { 3, 7, 11, 19 };
				for (var j = 0; j < 16; j++)
				{
					var s = round1[j % 4];
					switch (j % 4)
					{
						case 0: a = Rol(a + F(b, c, d) + x[j], s); break;
						case 1: d = Rol(d + F(a, b, c) + x[j], s); break;
						case 2: c = Rol(c + F(d, a, b) + x[j], s); break;
						default: b = Rol(b + F(c, d, a) + x[j], s); break;
					}
				}
				var order2 = new[] { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
				var round2 = new[] { 3, 5, 9, 13 };
				for (var j = 0; j < 16; j++)
				{
					var k = order2[j];
					var s = round2[j % 4];
					switch (j % 4)
					{
						case 0: a = Rol(a + G(b, c, d) + x[k] + 0x5A827999, s); break;
						case 1: d = Rol(d + G(a, b, c) + x[k] + 0x5A827999, s); break;
						case 2: c = Rol(c + G(d, a, b) + x[k] + 0x5A827999, s); break;
						default: b = Rol(b + G(c, d, a) + x[k] + 0x5A827999, s); break;
					}
				}
				var order3 = new[] { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
				var round3 = new[] { 3, 9, 11, 15 };
				for (var j = 0; j < 16; j++)
				{
					var k = order3[j];
					var s = round3[j % 4];
					switch (j % 4)
					{
						case 0: a = Rol(a + H(b, c, d) + x[k] + 0x6ED9EBA1, s); break;
						case 1: d = Rol(d + H(a, b, c) + x[k] + 0x6ED9EBA1, s); break;
						case 2: c = Rol(c + H(d, a, b) + x[k] + 0x6ED9EBA1, s); break;
						default: b = Rol(b + H(c, d, a) + x[k] + 0x6ED9EBA1, s); break;
					}
				}
				a += aa; b += bb; c += cc; d += dd;
			}
			var result = new byte[16];
			BitConverter.GetBytes(a).CopyTo(result, 0);
			BitConverter.GetBytes(b).CopyTo(result, 4);
			BitConverter.GetBytes(c).CopyTo(result, 8);
			BitConverter.GetBytes(d).CopyTo(result, 12);
			return result;
		}
	}
}
