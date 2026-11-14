using System.Diagnostics;

namespace ValheimRipper.Core
{
	static class Run
	{
		public static int Process(string file, IEnumerable<string> arguments, Job job, Action<string>? line = null, string? folder = null)
		{
			var info = new ProcessStartInfo(file)
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = folder ?? Path.GetDirectoryName(file) ?? "",
			};
			foreach (var argument in arguments)
				info.ArgumentList.Add(argument);
			using var process = System.Diagnostics.Process.Start(info)!;
			process.OutputDataReceived += (_, e) => { if (e.Data != null) (line ?? job.Log)(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data != null) (line ?? job.Log)(e.Data); };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			using (job.Cancel.Register(() => Kill(process)))
				process.WaitForExit();
			job.Cancel.ThrowIfCancellationRequested();
			return process.ExitCode;
		}

		public static void Kill(System.Diagnostics.Process process)
		{
			try
			{
				if (!process.HasExited)
					process.Kill(true);
			}
			catch (InvalidOperationException)
			{
			}
		}
	}
}
