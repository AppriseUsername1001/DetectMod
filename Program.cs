using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EVEAA.Mod;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		try
		{
			RunMain(args);
		}
		catch (Exception ex)
		{
			try
			{
				string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
				File.WriteAllText(Path.Combine(dir, "eveaa_mod_crash.log"), ex.ToString());
			}
			catch { }
			try { MessageBox.Show(ex.ToString(), "EVEAA Mod 시작 실패"); } catch { }
		}
	}

	private static void RunMain(string[] args)
	{
		if (EveWatcher.IsWatchMode(args))
		{
			EveWatcher.RunWatchLoop();
			return;
		}

		ApplicationConfiguration.Initialize();
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		Application.ThreadException += (_, e) =>
		{
			try
			{
				string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
				File.WriteAllText(Path.Combine(dir, "eveaa_mod_crash.log"), e.Exception.ToString());
			}
			catch { }
			MessageBox.Show(e.Exception.ToString(), "EVEAA Mod UI Error");
		};

		// exe 하나만 배포해도 실행되도록: 원본 EVEAA exe/데이터가 옆에 없으면 내장 리소스에서 풀어놓는다.
		BundledAssets.EnsureExtracted();

		int fixedConfigs = WindowFix.SanitizeAllUserConfigs();
		ModSettings settings = ModSettings.Load();

		string? eveaaExe = EveaaLocator.FindOriginalExe(settings.EveaaExePath);
		if (string.IsNullOrEmpty(eveaaExe))
		{
			using OpenFileDialog dlg = new()
			{
				Title = "원본 EVEAA 실행 파일 선택",
				Filter = "EVEAA|EVEAA*.exe|실행 파일|*.exe",
				FileName = "EVEAA v2.26.exe"
			};
			if (dlg.ShowDialog() != DialogResult.OK)
			{
				MessageBox.Show(
					"원본 EVEAA exe를 찾지 못했습니다.\n이 프로그램과 같은 폴더에 'EVEAA v2.26.exe'를 두거나 경로를 선택하세요.",
					"EVEAA Mod",
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
				return;
			}

			eveaaExe = dlg.FileName;
			settings.EveaaExePath = eveaaExe;
			settings.Save();
		}
		else if (string.IsNullOrEmpty(settings.EveaaExePath))
		{
			settings.EveaaExePath = eveaaExe;
			settings.Save();
		}

		// 이미 떠 있으면 창에 붙이고, 없으면 실행
		IntPtr existing = FindEveaaWindow();

		// 비정상 종료된 이전 Mod 세션이 폭을 이미 확장해 놓은 창이면 그대로 재부착하지 않는다 —
		// ExpandChrome이 또 적용되어 좌/우 크롬 폭이 중복으로 늘어나며 배치가 어긋난다.
		// 원본 프로세스를 종료하고 깨끗한 상태로 새로 띄운다.
		if (existing != IntPtr.Zero && ControlBarForm.IsAlreadyChromed(existing))
		{
			GetWindowThreadProcessId(existing, out uint stalePid);
			try
			{
				Process stale = Process.GetProcessById((int)stalePid);
				if (!stale.HasExited)
				{
					stale.Kill(entireProcessTree: true);
					stale.WaitForExit(3000);
				}
			}
			catch { }
			existing = IntPtr.Zero;
		}

		Process? proc;
		if (existing != IntPtr.Zero)
		{
			GetWindowThreadProcessId(existing, out uint pid);
			proc = Process.GetProcessById((int)pid);
		}
		else
		{
			proc = Process.Start(new ProcessStartInfo
			{
				FileName = eveaaExe,
				WorkingDirectory = Path.GetDirectoryName(eveaaExe)!,
				UseShellExecute = true
			});
			if (proc == null)
			{
				MessageBox.Show("EVEAA 실행에 실패했습니다.", "EVEAA Mod", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}

			existing = WaitForEveaaWindow(proc, TimeSpan.FromSeconds(15));
			if (existing == IntPtr.Zero)
			{
				// 창이 안 보이면 좌표 버그일 수 있음 → 재살균 후 프로세스 종료/재실행
				WindowFix.SanitizeAllUserConfigs();
				try
				{
					if (!proc.HasExited)
					{
						proc.Kill(entireProcessTree: true);
						proc.WaitForExit(3000);
					}
				}
				catch
				{
				}

				proc = Process.Start(new ProcessStartInfo
				{
					FileName = eveaaExe,
					WorkingDirectory = Path.GetDirectoryName(eveaaExe)!,
					UseShellExecute = true
				});
				if (proc == null)
				{
					return;
				}

				existing = WaitForEveaaWindow(proc, TimeSpan.FromSeconds(15));
			}
		}

		if (existing == IntPtr.Zero || proc == null)
		{
			MessageBox.Show(
				"EVEAA 창을 찾지 못했습니다.\n설정이 깨져 있다면 다시 실행해 보세요." +
				(fixedConfigs > 0 ? $"\n(복구된 설정: {fixedConfigs}개)" : ""),
				"EVEAA Mod",
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning);
			return;
		}

		// 화면 밖이면 강제 중앙 복귀 (숨긴 상태로 정렬)
		ShowWindow(existing, SW_HIDE);
		EnsureWindowOnScreen(existing);

		ControlBarForm bar = new(settings);
		bar.AttachToEveaa(proc, existing);
		Application.Run(bar);
	}

	private static IntPtr WaitForEveaaWindow(Process proc, TimeSpan timeout)
	{
		DateTime until = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < until)
		{
			if (proc.HasExited)
				return IntPtr.Zero;

			// 보이는 창이 되기 전에 핸들을 잡아 즉시 숨김 → 크롬 없는 원본이 잠깐 보이는 시차 제거
			IntPtr hwnd = FindEveaaWindowForProcess(proc.Id, requireVisible: false);
			if (hwnd != IntPtr.Zero)
			{
				ShowWindow(hwnd, SW_HIDE);
				// 제목/크기가 잡힐 때까지 아주 짧게 대기
				for (int i = 0; i < 40; i++)
				{
					if (GetWindowRect(hwnd, out RECT rc) && rc.Right - rc.Left > 100)
						break;
					Thread.Sleep(25);
				}
				return hwnd;
			}

			Thread.Sleep(50);
		}

		return IntPtr.Zero;
	}

	private static IntPtr FindEveaaWindow() => FindEveaaWindowForProcess(0, requireVisible: true);

	private static IntPtr FindEveaaWindowForProcess(int processId, bool requireVisible)
	{
		IntPtr found = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			if (requireVisible && !IsWindowVisible(hwnd))
				return true;

			if (processId > 0)
			{
				GetWindowThreadProcessId(hwnd, out uint pid);
				if ((int)pid != processId)
					return true;
			}

			var sb = new StringBuilder(256);
			GetWindowText(hwnd, sb, sb.Capacity);
			string title = sb.ToString();
			if (title.StartsWith("EVEAA", StringComparison.OrdinalIgnoreCase) &&
			    !title.Contains("Mod", StringComparison.OrdinalIgnoreCase) &&
			    !title.Contains("Host", StringComparison.OrdinalIgnoreCase))
			{
				found = hwnd;
				return false;
			}

			return true;
		}, IntPtr.Zero);
		return found;
	}

	private static void EnsureWindowOnScreen(IntPtr hwnd)
	{
		if (!GetWindowRect(hwnd, out RECT rc))
		{
			return;
		}

		var bounds = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
		bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds));
		if (visible && rc.Left > -10000)
		{
			return;
		}

		Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
		int w = Math.Max(200, bounds.Width);
		int h = Math.Max(100, bounds.Height);
		int x = screen.WorkingArea.Left + (screen.WorkingArea.Width - w) / 2;
		int y = screen.WorkingArea.Top + (screen.WorkingArea.Height - h) / 2;
		SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
		ShowWindow(hwnd, SW_RESTORE);
	}

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_SHOWWINDOW = 0x0040;
	private const int SW_HIDE = 0;
	private const int SW_RESTORE = 9;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}
}
