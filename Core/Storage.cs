namespace ValheimRipper.Core
{
	static class Storage
	{
		public static readonly string Folder = Pick();

		static string Pick()
		{
			var beside = Path.Combine(AppContext.BaseDirectory, "ValheimRipper");
			try
			{
				Directory.CreateDirectory(beside);
				var probe = Path.Combine(beside, ".write");
				File.WriteAllText(probe, "");
				File.Delete(probe);
				return beside;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimRipper");
			}
		}
	}
}
