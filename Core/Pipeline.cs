using System.Diagnostics;
using System.Text.Json;

namespace ValheimRipper.Core
{
	sealed record Step(string Id, string? Option, Action<Job> Action);

	static class Pipeline
	{
		public static string Marks(string project) => Path.Combine(project, ".rip");

		public static bool Done(string project, string id) => File.Exists(Path.Combine(Marks(project), id + ".done"));

		static string Chosen(string project) => Path.Combine(Marks(project), "options.json");

		public static Dictionary<string, bool>? Options(string project) =>
			File.Exists(Chosen(project)) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(Chosen(project))) : null;

		public static bool Run(IEnumerable<Step> steps, Job job, Action<string, string, double?, string?, string?> report)
		{
			foreach (var step in steps)
			{
				if (step.Option != null && !job.On(step.Option))
				{
					report(step.Id, "skipped", null, null, null);
					continue;
				}
				if (Done(job.Project, step.Id))
				{
					report(step.Id, "done", 1, "Done in an earlier run", null);
					continue;
				}
				var clock = Stopwatch.StartNew();
				report(step.Id, "running", 0, "", null);
				try
				{
					job.Logger($"== {step.Id}", null);
					job.Reporter = (fraction, detail) => report(step.Id, "running", fraction, detail, null);
					step.Action(job);
					Directory.CreateDirectory(Marks(job.Project));
					File.WriteAllText(Path.Combine(Marks(job.Project), step.Id + ".done"), DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
					if (!File.Exists(Chosen(job.Project)))
						File.WriteAllText(Chosen(job.Project), JsonSerializer.Serialize(job.Options));
					report(step.Id, "done", 1, null, Elapsed(clock.Elapsed));
				}
				catch (OperationCanceledException)
				{
					report(step.Id, "failed", null, "Stopped", Elapsed(clock.Elapsed));
					job.Logger("Stopped. Finished stages are kept.", "warn");
					return false;
				}
				catch (Exception e)
				{
					report(step.Id, "failed", null, e.Message.Split('\n')[0], Elapsed(clock.Elapsed));
					job.Logger(e.Message, "error");
					job.Logger(e.ToString(), null);
					return false;
				}
			}
			return true;
		}

		static string Elapsed(TimeSpan time) => time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min" : $"{Math.Max(1, (int)time.TotalSeconds)} s";
	}
}
