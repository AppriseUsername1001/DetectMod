using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EVEAA.Mod;

/// <summary>
/// EVEAA(임베드된 메인 창)의 X / Alt+F4 → EVEAA_mod 트레이로 숨김.
/// 작업표시줄에 EVEAA만 남는 최소화가 아니라, 창을 숨기고 트레이 아이콘으로 유지.
/// </summary>
internal sealed class CloseToTrayService : IDisposable
{
	private readonly ControlBarForm _host;
	private NotifyIcon? _tray;
	private IntPtr _targetHwnd;
	private Process? _eveaaProcess;
	private bool _disposed;
	private bool _hiddenToTray;
	public bool IsHiddenToTray => _hiddenToTray;
	private int _savedExStyle;
	private bool _swallowNextUp;

	private IntPtr _mouseHook;
	private IntPtr _keyboardHook;
	private LowLevelMouseProc? _mouseProc;
	private LowLevelKeyboardProc? _keyboardProc;

	public CloseToTrayService(ControlBarForm host)
	{
		_host = host;
	}

	public void Attach(IntPtr eveaaHwnd, Process? eveaaProcess, string? iconPath)
	{
		_targetHwnd = eveaaHwnd;
		_eveaaProcess = eveaaProcess;
		EnsureTray(iconPath);
		InstallHooks();
	}

	private void EnsureTray(string? iconPath)
	{
		if (_tray is not null) return;

		Icon icon;
		try
		{
			// 트레이는 mod 프로세스 아이콘 우선 (EVEAA가 아닌 EVEAA_mod)
			string? modIcon = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(modIcon) && File.Exists(modIcon))
				icon = Icon.ExtractAssociatedIcon(modIcon) ?? SystemIcons.Application;
			else if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
				icon = Icon.ExtractAssociatedIcon(iconPath) ?? SystemIcons.Application;
			else
				icon = SystemIcons.Application;
		}
		catch
		{
			icon = SystemIcons.Application;
		}

		var menu = new ContextMenuStrip();
		menu.Items.Add("열기 (EVEAA Mod)", null, (_, _) => Restore());
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("종료", null, (_, _) => ExitFully());

		_tray = new NotifyIcon
		{
			Icon = icon,
			Text = "EVEAA Mod",
			Visible = true,
			ContextMenuStrip = menu
		};
		_tray.DoubleClick += (_, _) => Restore();
	}

	private void InstallHooks()
	{
		_mouseProc = MouseHookCallback;
		_keyboardProc = KeyboardHookCallback;
		IntPtr mod = IntPtr.Zero;
		try
		{
			using var cur = Process.GetCurrentProcess();
			string? name = cur.MainModule?.ModuleName;
			if (!string.IsNullOrEmpty(name))
				mod = GetModuleHandle(name);
		}
		catch { }
		if (mod == IntPtr.Zero)
			mod = GetModuleHandle(null);
		_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
		_keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, mod, 0);
	}

	private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0 && _targetHwnd != IntPtr.Zero && !_disposed)
		{
			int msg = wParam.ToInt32();
			try
			{
				if (_swallowNextUp && (msg == WM_LBUTTONUP || msg == WM_NCLBUTTONUP))
				{
					_swallowNextUp = false;
					return (IntPtr)1;
				}

				if (msg == WM_LBUTTONDOWN || msg == WM_NCLBUTTONDOWN)
				{
					var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
					if (IsCloseButtonClick(info.Pt))
					{
						_swallowNextUp = true;
						// BeginInvoke 금지: 클릭이 EVEAA로 들어가 작업표시줄 최소화가 됨
						MinimizeToTray();
						return (IntPtr)1;
					}
				}
			}
			catch { }
		}
		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0 && _targetHwnd != IntPtr.Zero && !_disposed &&
		    (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
		{
			try
			{
				var info = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
				if (info.VkCode == VK_F4 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 &&
				    GetForegroundWindow() == _targetHwnd)
				{
					MinimizeToTray();
					return (IntPtr)1;
				}
			}
			catch { }
		}
		return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
	}

	private bool IsCloseButtonClick(PointScreen pt)
	{
		if (!IsWindow(_targetHwnd) || !IsWindowVisible(_targetHwnd))
			return false;

		IntPtr hitWnd = WindowFromPoint(pt);
		if (hitWnd == IntPtr.Zero) return false;
		IntPtr root = GetAncestor(hitWnd, GA_ROOT);
		if (root != _targetHwnd) return false;

		// 1) NCHITTEST
		IntPtr hit = SendMessage(_targetHwnd, WM_NCHITTEST, IntPtr.Zero, MakeLParam(pt.X, pt.Y));
		if (hit == (IntPtr)HTCLOSE)
			return true;

		// 2) DWM 캡션 버튼 영역(오른쪽 = 닫기) — Win11/DPI에서 HTCLOSE가 안 뜨는 경우
		if (TryGetCloseButtonScreenRect(out RECT closeRc))
		{
			if (pt.X >= closeRc.Left && pt.X < closeRc.Right &&
			    pt.Y >= closeRc.Top && pt.Y < closeRc.Bottom)
				return true;
		}

		return false;
	}

	private bool TryGetCloseButtonScreenRect(out RECT screenClose)
	{
		screenClose = default;
		if (!GetWindowRect(_targetHwnd, out RECT win))
			return false;

		// DWMWA_CAPTION_BUTTON_BOUNDS = 5 (window-relative)
		RECT buttons = default;
		int hr = DwmGetWindowAttribute(_targetHwnd, DWMWA_CAPTION_BUTTON_BOUNDS, ref buttons, Marshal.SizeOf<RECT>());
		if (hr != 0 || buttons.Right <= buttons.Left)
		{
			// fallback: 우측 상단 시스템 버튼 폭만큼
			int bw = GetSystemMetrics(SM_CXSIZE);
			int bh = GetSystemMetrics(SM_CYSIZE);
			screenClose = new RECT
			{
				Left = win.Right - bw - 4,
				Top = win.Top + 2,
				Right = win.Right - 2,
				Bottom = win.Top + bh + 6
			};
			return true;
		}

		// caption button bounds: minimize | maximize | close (좌→우). 닫기는 오른쪽 1/3.
		int w = buttons.Right - buttons.Left;
		int third = Math.Max(1, w / 3);
		screenClose = new RECT
		{
			Left = win.Left + buttons.Right - third,
			Top = win.Top + buttons.Top,
			Right = win.Left + buttons.Right,
			Bottom = win.Top + buttons.Bottom
		};
		return true;
	}

	public void MinimizeToTray()
	{
		if (_disposed || _targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) return;

		// 작업표시줄에 EVEAA가 "최소화"로 남지 않게 숨김 + 탭 제거
		_savedExStyle = GetWindowLong(_targetHwnd, GWL_EXSTYLE);
		int ex = _savedExStyle;
		ex |= WS_EX_TOOLWINDOW;
		ex &= ~WS_EX_APPWINDOW;
		SetWindowLong(_targetHwnd, GWL_EXSTYLE, ex);
		ShowWindow(_targetHwnd, SW_HIDE);
		SetWindowPos(_targetHwnd, IntPtr.Zero, 0, 0, 0, 0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_HIDEWINDOW);

		try
		{
			var tbl = (ITaskbarList)new CTaskbarList();
			tbl.HrInit();
			tbl.DeleteTab(_targetHwnd);
		}
		catch { }

		_hiddenToTray = true;

		if (_tray is not null)
		{
			_tray.Visible = true;
			_tray.Text = "EVEAA Mod (트레이 실행 중)";
			try
			{
				_tray.BalloonTipTitle = "EVEAA Mod";
				_tray.BalloonTipText = "트레이에서 실행 중입니다. 아이콘을 더블클릭하면 창이 열립니다.";
				_tray.ShowBalloonTip(2500);
			}
			catch { }
		}
	}

	public void Restore()
	{
		if (_disposed || _targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) return;

		if (_savedExStyle != 0)
			SetWindowLong(_targetHwnd, GWL_EXSTYLE, _savedExStyle);
		else
		{
			int ex = GetWindowLong(_targetHwnd, GWL_EXSTYLE);
			ex |= WS_EX_APPWINDOW;
			ex &= ~WS_EX_TOOLWINDOW;
			SetWindowLong(_targetHwnd, GWL_EXSTYLE, ex);
		}

		ShowWindow(_targetHwnd, SW_SHOW);
		ShowWindow(_targetHwnd, SW_RESTORE);
		SetWindowPos(_targetHwnd, IntPtr.Zero, 0, 0, 0, 0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

		try
		{
			var tbl = (ITaskbarList)new CTaskbarList();
			tbl.HrInit();
			tbl.AddTab(_targetHwnd);
		}
		catch { }

		SetForegroundWindow(_targetHwnd);
		_hiddenToTray = false;
		if (_tray is not null)
			_tray.Text = "EVEAA Mod";

		// 즉시 + 지연 리프레시 (DWM/캡처 영역이 한 박자 늦게 깨지는 경우 대비)
		void DoRefresh()
		{
			try
			{
				if (!_host.IsDisposed)
					_host.ForceChromeRefresh();
			}
			catch { }
		}
		try
		{
			if (!_host.IsDisposed)
			{
				if (_host.InvokeRequired)
					_host.BeginInvoke(DoRefresh);
				else
					DoRefresh();

				// 50ms / 300ms 후 한 번 더
				_host.BeginInvoke(async () =>
				{
					try
					{
						await Task.Delay(50);
						DoRefresh();
						await Task.Delay(250);
						DoRefresh();
					}
					catch { }
				});
			}
		}
		catch { }
	}

	public void ExitFully()
	{
		if (_disposed) return;
		UninstallHooks();
		if (_tray is not null)
		{
			_tray.Visible = false;
			_tray.Dispose();
			_tray = null;
		}

		try
		{
			if (_eveaaProcess is not null && !_eveaaProcess.HasExited)
			{
				_eveaaProcess.CloseMainWindow();
				if (!_eveaaProcess.WaitForExit(2500))
					_eveaaProcess.Kill(entireProcessTree: true);
			}
			else if (_targetHwnd != IntPtr.Zero && IsWindow(_targetHwnd))
			{
				PostMessage(_targetHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
			}
		}
		catch { }

		try
		{
			if (!_host.IsDisposed)
				_host.BeginInvoke(() => { try { _host.Close(); } catch { } });
		}
		catch
		{
			try { _host.Close(); } catch { }
		}
	}

	private void UninstallHooks()
	{
		if (_mouseHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_mouseHook);
			_mouseHook = IntPtr.Zero;
		}
		if (_keyboardHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_keyboardHook);
			_keyboardHook = IntPtr.Zero;
		}
		_mouseProc = null;
		_keyboardProc = null;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		UninstallHooks();
		if (_tray is not null)
		{
			_tray.Visible = false;
			_tray.Dispose();
			_tray = null;
		}
	}

	private static IntPtr MakeLParam(int lo, int hi) =>
		(IntPtr)((hi << 16) | (lo & 0xFFFF));

	private const int WH_MOUSE_LL = 14;
	private const int WH_KEYBOARD_LL = 13;
	private const int WM_LBUTTONDOWN = 0x0201;
	private const int WM_LBUTTONUP = 0x0202;
	private const int WM_NCLBUTTONDOWN = 0x00A1;
	private const int WM_NCLBUTTONUP = 0x00A2;
	private const int WM_KEYDOWN = 0x0100;
	private const int WM_SYSKEYDOWN = 0x0104;
	private const int WM_NCHITTEST = 0x0084;
	private const int WM_CLOSE = 0x0010;
	private const int HTCLOSE = 20;
	private const int VK_F4 = 0x73;
	private const int VK_MENU = 0x12;
	private const int GA_ROOT = 2;
	private const int SW_HIDE = 0;
	private const int SW_SHOW = 5;
	private const int SW_RESTORE = 9;
	private const int GWL_EXSTYLE = -20;
	private const int WS_EX_APPWINDOW = 0x00040000;
	private const int WS_EX_TOOLWINDOW = 0x00000080;
	private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
	private const int SM_CXSIZE = 30;
	private const int SM_CYSIZE = 31;
	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOZORDER = 0x0004;
	private const uint SWP_FRAMECHANGED = 0x0020;
	private const uint SWP_SHOWWINDOW = 0x0040;
	private const uint SWP_HIDEWINDOW = 0x0080;

	private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct PointScreen
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MsllHookStruct
	{
		public PointScreen Pt;
		public uint MouseData;
		public uint Flags;
		public uint Time;
		public IntPtr DwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KbdllHookStruct
	{
		public uint VkCode;
		public uint ScanCode;
		public uint Flags;
		public uint Time;
		public IntPtr DwExtraInfo;
	}

	[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
	private class CTaskbarList { }

	[ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList
	{
		void HrInit();
		void AddTab(IntPtr hwnd);
		void DeleteTab(IntPtr hwnd);
		void ActivateTab(IntPtr hwnd);
		void SetActiveAlt(IntPtr hwnd);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);
	[DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
	[DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);
	[DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointScreen pt);
	[DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);
	[DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
	[DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
	[DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
	[DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
	[DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
	[DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
	[DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref RECT pvAttribute, int cbAttribute);
}
