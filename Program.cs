namespace ValheimRipper
{
	static class Program
	{
		[STAThread]
		static void Main()
		{
			using var mutex = new Mutex(true, "ValheimRipper", out var first);
			if (!first)
				return;

			ApplicationConfiguration.Initialize();
			try
			{
				Application.Run(new Shell.Window());
			}
			catch (Exception e)
			{
				MessageBox.Show(e.Message, "ValheimRipper", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}
	}
}
