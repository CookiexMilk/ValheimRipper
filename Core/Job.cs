namespace ValheimRipper.Core
{
	sealed class Job
	{
		public required string Project { get; init; }
		public required string Game { get; init; }
		public required string Unity { get; init; }
		public required string Ripper { get; init; }
		public bool Dummy { get; init; }
		public required Dictionary<string, bool> Options { get; init; }
		public required CancellationToken Cancel { get; init; }
		public required Action<string, string?> Logger { get; init; }
		public Action<double?, string?> Reporter { get; set; } = (_, _) => { };
		public Dictionary<string, string> Facts { get; } = new();

		public string Assets => Path.Combine(Project, "Assets");
		public string Removed => Path.Combine(Project, "removed_plugins");
		public string Logs => Path.Combine(Project, "Logs");

		public string Managed =>
			Directory.GetFiles(Assets, "assembly_valheim.dll", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
			?? throw new InvalidOperationException("assembly_valheim.dll isn't in the project; the export didn't finish.");

		public bool On(string option) => Options.TryGetValue(option, out var on) && on;

		public void Log(string text) => Logger(text, null);
		public void Good(string text) => Logger(text, "good");
		public void Warn(string text) => Logger(text, "warn");
		public void Progress(double? fraction, string? detail = null) => Reporter(fraction, detail);
		public void Detail(string detail) => Reporter(-1, detail);
		public void Check() => Cancel.ThrowIfCancellationRequested();
	}
}
