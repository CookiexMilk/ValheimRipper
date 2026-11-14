using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ValheimRipper.Shell
{
	sealed class Window : Form
	{
		const string Host = "ripper.local";

		readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(23, 17, 12) };
		Bridge? bridge;

		public Window()
		{
			Text = "Valheim Ripper";
			FormBorderStyle = FormBorderStyle.None;
			StartPosition = FormStartPosition.CenterScreen;
			var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
			Size = new Size(Math.Min(1600, area.Width), Math.Min(900, area.Height));
			MinimumSize = new Size(1000, 640);
			BackColor = Color.FromArgb(23, 17, 12);
			Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
			Controls.Add(view);
			Load += async (_, _) => await Start();
		}

		async Task Start()
		{
			var round = 2;
			DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
			var root = Core.Storage.Folder;
			var ui = Path.Combine(root, "ui");
			Unpack(ui);
			var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "WebView2"));
			await view.EnsureCoreWebView2Async(environment);
			var core = view.CoreWebView2;
			core.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
			core.Settings.AreDefaultContextMenusEnabled = false;
			core.Settings.IsZoomControlEnabled = false;
			core.Settings.IsStatusBarEnabled = false;
			core.SetVirtualHostNameToFolderMapping(Host, ui, CoreWebView2HostResourceAccessKind.Allow);
			bridge = new Bridge(this, core);
			core.WebMessageReceived += (_, e) => bridge.Receive(e.WebMessageAsJson);
			core.Navigate($"https://{Host}/index.html");
		}

		static void Unpack(string folder)
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, true);
			var assembly = Assembly.GetExecutingAssembly();
			foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("UI/")))
			{
				var path = Path.Combine(folder, name[3..].Replace('\\', '/'));
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using var source = assembly.GetManifestResourceStream(name)!;
				using var target = File.Create(path);
				source.CopyTo(target);
			}
		}

		public void Command(string action)
		{
			switch (action)
			{
				case "drag":
					ReleaseCapture();
					SendMessage(Handle, 0xA1, 2, 0);
					break;
				case "min":
					WindowState = FormWindowState.Minimized;
					break;
				case "max":
					WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
					break;
				case "close":
					Close();
					break;
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (bridge != null && bridge.Running && MessageBox.Show(this,
				"A rip is running. Stop it and close? Finished stages are kept.",
				"Valheim Ripper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				e.Cancel = true;
				return;
			}
			bridge?.Stop();
			base.OnFormClosing(e);
		}

		[DllImport("user32.dll")] static extern bool ReleaseCapture();
		[DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, int wParam, int lParam);
		[DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
	}
}
