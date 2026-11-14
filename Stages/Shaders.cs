using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimRipper.Stages
{
	static class Shaders
	{
		static readonly string[] Keys = { "m_PlayerSubPrograms:", "m_ParameterBlobIndices:" };
		static readonly string[] Empty = { "- []", "-" };
		static readonly Regex DevEditor = new(@"(?m)^(\s+m_CustomEditorName: )Needle\.\S+");
		static readonly Regex Property = new(@"(?m)^([ \t]*(?:\[[^\]\r\n]*\][ \t]*)*\w+[ \t]*\(""[^""\r\n]*"",[ \t]*)(\w+)([^\r\n]*)");
		static readonly Regex Exponent = new(@"(?<![\w.])-?\d+(?:\.\d+)?E[+-]?\d+(?![\w.])");

		public static (int Files, int Fixed) Fix(string root)
		{
			var files = 0;
			var changed = 0;
			foreach (var path in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
			{
				string head;
				using (var reader = new StreamReader(path))
				{
					var buffer = new char[200];
					head = new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
				}
				if (!head.Contains("--- !u!48 &"))
					continue;
				files++;
				var text = File.ReadAllText(path);
				var fixedText = Fill(text);
				if (fixedText != text)
				{
					File.WriteAllText(path, fixedText, new UTF8Encoding(false));
					changed++;
				}
			}
			return (files, changed);
		}

		public static int FixDummy(string root)
		{
			var changed = 0;
			foreach (var path in Directory.EnumerateFiles(root, "*.shader", SearchOption.AllDirectories))
			{
				var text = File.ReadAllText(path);
				var fixedText = Property.Replace(text, line => line.Groups[1].Value + line.Groups[2].Value
					+ Exponent.Replace(line.Groups[3].Value, number => Plain(number.Value, line.Groups[2].Value == "Int")));
				if (fixedText != text)
				{
					File.WriteAllText(path, fixedText, new UTF8Encoding(false));
					changed++;
				}
			}
			return changed;
		}

		static string Plain(string number, bool bits)
		{
			var value = float.Parse(number, CultureInfo.InvariantCulture);
			return bits
				? BitConverter.SingleToInt32Bits(value).ToString(CultureInfo.InvariantCulture)
				: ((decimal)value).ToString(CultureInfo.InvariantCulture);
		}

		static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

		static string Fill(string text)
		{
			var lines = text.Split('\n');
			var output = new List<string>(lines.Length + 1024);
			var i = 0;
			while (i < lines.Length)
			{
				var line = lines[i];
				output.Add(line);
				i++;
				if (!Keys.Contains(line.Trim()))
					continue;
				var depth = Indent(line);
				var prefix = new string(' ', depth) + "-";
				var items = new List<List<string>>();
				while (i < lines.Length && lines[i].StartsWith(prefix))
				{
					var j = i + 1;
					while (j < lines.Length && Indent(lines[j]) > depth)
						j++;
					items.Add(lines[i..j].ToList());
					i = j;
				}
				if (items.Count == 4 && items.Take(3).All(x => x.Count == 1 && Empty.Contains(x[0].Trim())) && !Empty.Contains(items[3][0].Trim()))
					items = new List<List<string>> { items[3], items[3], items[3], items[3] };
				foreach (var item in items)
					output.AddRange(item);
			}
			return DevEditor.Replace(string.Join("\n", output), "$1");
		}
	}
}
