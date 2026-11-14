using System.Text.RegularExpressions;
using ValheimRipper.Core;

namespace ValheimRipper.Stages
{
	static class Editor
	{
		static readonly Regex Importing = new(@"^Start importing ");

		static string Unity(Job job, string log, bool batch, Action<string>? line, params string[] arguments)
		{
			if (Detect.UnityOpen(job.Project))
				throw new InvalidOperationException("The project is open in Unity. Close it there, then press Carry on.");
			var path = Path.Combine(job.Logs, log);
			Directory.CreateDirectory(job.Logs);
			File.Delete(path);
			var all = new List<string>();
			if (batch)
				all.Add("-batchmode");
			all.AddRange(new[] { "-projectPath", job.Project, "-logFile", path });
			all.AddRange(arguments);
			using var stop = new CancellationTokenSource();
			var follower = line == null ? Task.CompletedTask : Task.Run(() => Follow(path, line, stop.Token));
			int code;
			try
			{
				code = Core.Run.Process(job.Unity, all, job);
			}
			finally
			{
				stop.Cancel();
				follower.Wait();
			}
			var text = File.Exists(path) ? ReadShared(path) : "";
			if (code != 0)
			{
				var errors = Regex.Matches(text, @"^.*(error CS\d+|Aborting batchmode|Exception).*$", RegexOptions.Multiline).Select(m => m.Value.Trim()).Distinct().Take(8);
				throw new InvalidOperationException($"Unity stopped with exit code {code} ({log}):\n{string.Join("\n", errors)}");
			}
			return text;
		}

		static string ReadShared(string path)
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}

		static void Follow(string path, Action<string> line, CancellationToken stop)
		{
			var offset = 0L;
			while (true)
			{
				var last = stop.IsCancellationRequested;
				if (File.Exists(path))
					try
					{
						using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
						stream.Seek(offset, SeekOrigin.Begin);
						using var reader = new StreamReader(stream);
						string? text;
						while ((text = reader.ReadLine()) != null)
							line(text);
						offset = stream.Position;
					}
					catch (IOException)
					{
					}
				if (last)
					return;
				stop.WaitHandle.WaitOne(1000);
			}
		}

		public static void Check(Job job)
		{
			var folder = Path.Combine(job.Assets, "ValheimRipperCheck");
			Kit.CopyFolder("Check", Path.Combine(folder, "Editor"));
			string text;
			try
			{
				job.Progress(null, "Compiling every shader");
				text = Unity(job, "check_shaders.log", true, null, "-quit", "-executeMethod", "ValheimRipperCheck.Shaders.Run");
			}
			finally
			{
				if (Directory.Exists(folder))
					Directory.Delete(folder, true);
				File.Delete(folder + ".meta");
			}
			foreach (Match broken in Regex.Matches(text, @"\[ValheimRipper\] Shader error: [^\r\n]+"))
				job.Warn(broken.Value["[ValheimRipper] ".Length..]);
			var result = Regex.Match(text, @"All (\d+) shaders compile\.|(\d+) shaders have errors \((\d+) fine\)");
			if (!result.Success)
				throw new InvalidOperationException("The shader check printed no result; see Logs\\check_shaders.log");
			job.Progress(1, result.Value);
			if (result.Groups[1].Success)
			{
				job.Facts["shaders"] = result.Groups[1].Value;
				job.Good(result.Value);
			}
			else
			{
				job.Facts["shaders"] = result.Groups[3].Value;
				job.Warn(result.Value + " See Logs\\check_shaders.log");
			}
		}

		public static void Import(Job job)
		{
			var imported = 0;
			var expected = Math.Max(1, Directory.EnumerateFiles(job.Assets, "*.meta", SearchOption.AllDirectories).Count());
			var text = Unity(job, "first_import.log", true, line =>
			{
				if (Importing.IsMatch(line) && ++imported % 250 == 0)
					job.Progress(Math.Min(0.99, (double)imported / expected), $"Imported {imported:N0} of about {expected:N0} assets");
				if (line.Contains("error CS"))
					job.Warn(line.Trim());
			}, "-quit");
			var errors = Regex.Matches(text, @"^.*error CS\d+.*$", RegexOptions.Multiline).Select(m => m.Value.Trim()).Distinct().ToList();
			if (errors.Count > 0)
				throw new InvalidOperationException("The project has compile errors:\n" + string.Join("\n", errors.Take(10)));
			job.Good($"Unity imported the project ({imported:N0} assets), no compile errors");
		}
	}
}
