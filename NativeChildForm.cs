using System.Runtime.InteropServices;

namespace EVEAA.Mod;

/// <summary>EVEAA HWND에 자식으로 붙는 보더리스 패널 공통 베이스.</summary>
internal abstract class NativeChildForm : Form
{
	private bool _parented;
	private IntPtr _hostParent = IntPtr.Zero;

	protected NativeChildForm()
	{
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		MaximizeBox = false;
		MinimizeBox = false;
		ControlBox = false;
		TopMost = false;
		// 96 DPI(100%) 기준으로 설계된 자식 컨트롤(버튼/라벨/컬럼폭 등)을 실제 모니터 배율에 맞게
		// 자동으로 스케일링 — 사용자마다 디스플레이 배율이 달라 글자가 잘려 보이던 문제 수정.
		// 이 창 자체를 원본 EVEAA 좌표에 픽셀 단위로 배치하는 로직(PlaceInParent)은 외부에서
		// 별도로 제어되므로 이 설정과 충돌하지 않는다.
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScaleDimensions = new SizeF(96f, 96f);
		KeyPreview = true;
	}

	protected void AttachAsChild(IntPtr parentHwnd)
	{
		if (parentHwnd == IntPtr.Zero)
			return;

		bool justCreated = !IsHandleCreated;
		if (justCreated)
			CreateHandle();

		_hostParent = parentHwnd;
		ReparentToHost();

		// CreateHandle()을 직접 호출하는 경로는 Show()/CreateControl()이 도는 정상 경로를
		// 거치지 않아 AutoScaleMode의 DPI 스케일링이 자동으로 걸리지 않는다 — 명시적으로 트리거.
		// 반드시 ReparentToHost() 이후(실제 모니터에 얹힌 뒤)에 호출해야 한다 — CreateHandle()
		// 직후처럼 아직 어디에도 붙지 않은 시점에 호출하면 DeviceDpi가 0/미확정 값으로 계산되어
		// 배율이 0이 되면서 모든 자식 컨트롤(버튼/라벨/리스트)이 그대로 사라지는 심각한 회귀가
		// 있었다(1.14 — 43인치 TV처럼 특정 디스플레이 환경에서만 재현). DeviceDpi가 정상 범위가
		// 아니면 스케일링을 아예 건너뛴다 — 배율이 안 맞는 것보다 아예 안 보이는 게 훨씬 나쁘다.
		if (justCreated && DeviceDpi >= 96 && DeviceDpi <= 480)
			PerformAutoScale();

		// WinForms Visible과 HWND 표시 상태를 맞춤 (안 맞으면 이후 레이아웃이 숨김으로 남음)
		Visible = true;
	}

	/// <summary>호스트(EVEAA)에 다시 붙이고 WS_CHILD 스타일을 복구한다.</summary>
	protected void ReparentToHost()
	{
		if (!IsHandleCreated || _hostParent == IntPtr.Zero || !IsWindow(_hostParent))
			return;

		SetParent(Handle, _hostParent);
		_parented = true;

		int style = GetWindowLong(Handle, GWL_STYLE);
		style |= WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_VISIBLE;
		style &= ~WS_POPUP;
		SetWindowLong(Handle, GWL_STYLE, style);

		int ex = GetWindowLong(Handle, GWL_EXSTYLE);
		ex |= WS_EX_CONTROLPARENT;
		SetWindowLong(Handle, GWL_EXSTYLE, ex);

		SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0,
			SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
	}

	/// <summary>
	/// 부모 클라이언트 좌표로 배치. SetWindowPos와 WinForms Bounds를 함께 맞춰
	/// Dock 자식 컨트롤 레이아웃이 깨지지 않게 한다.
	/// </summary>
	protected void PlaceInParent(int x, int y, int w, int h, bool bringToFront = true, bool visible = true, bool force = false)
	{
		if (!IsHandleCreated)
			return;

		w = Math.Max(1, w);
		h = Math.Max(1, h);

		if (visible)
		{
			if (!_parented || GetParent(Handle) != _hostParent)
				ReparentToHost();
			if (!Visible)
				Visible = true;
		}

		bool wantShow = visible;
		bool isShown = IsWindowVisible(Handle);
		bool sameBounds = Location.X == x && Location.Y == y && Width == w && Height == h;
		// 크기·표시 동일하면 SetWindowPos 생략 — 인텔 로그 점멸 방지
		if (!force && sameBounds && wantShow == isShown && isShown)
			return;

		if (!visible)
		{
			HideInParent();
			return;
		}

		IntPtr z = bringToFront ? HWND_TOP : HWND_BOTTOM;
		uint flags = SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW;
		SetWindowPos(Handle, z, x, y, w, h, flags);

		SuspendLayout();
		try
		{
			// Borderless: Size만 맞춤. ClientSize 추가 설정은 ListView 헤더 이중 페인트 유발
			if (Location.X != x || Location.Y != y)
				Location = new Point(x, y);
			if (Size.Width != w || Size.Height != h)
				Size = new Size(w, h);
		}
		finally
		{
			ResumeLayout(true);
		}

		PerformLayout();
	}

	/// <summary>
	/// 크로스 프로세스 자식에서 ShowWindow만으로는 다시 그려지는 경우가 있어
	/// HWND_MESSAGE로 떼어내 시각 트리에서 완전히 제거한다.
	/// </summary>
	protected void HideInParent()
	{
		if (!IsHandleCreated)
			return;
		// 이미 떼어낸 상태면 아무 것도 하지 않는다 — 경보기 뷰에서는 매 200ms틱마다
		// SetVisible(false)가 호출되는데, 매번 SetParent/SetWindowLong/SetWindowPos를
		// 무조건 다시 실행하면(1.12/1.16에서 고친 것과 같은 패턴) 다른 창에 포커스가
		// 가 있을 때 컴포지터 경합으로 ZKB feed 등 형제 창이 점멸하는 원인이 된다.
		if (!_parented)
			return;

		ShowWindow(Handle, SW_HIDE);

		int style = GetWindowLong(Handle, GWL_STYLE);
		style |= WS_CHILD;
		style &= ~WS_VISIBLE;
		style &= ~WS_POPUP;
		SetWindowLong(Handle, GWL_STYLE, style);

		SetWindowPos(Handle, HWND_BOTTOM, -32000, -32000, 1, 1,
			SWP_HIDEWINDOW | SWP_NOACTIVATE | SWP_FRAMECHANGED);

		// 메시지 전용 부모로 이동 → EVEAA 클라이언트에 더 이상 그려지지 않음
		SetParent(Handle, HWND_MESSAGE);
		_parented = false;

		if (Visible)
			Visible = false;
	}

	public bool IsShownInHost =>
		IsHandleCreated && _parented && IsWindowVisible(Handle);

	/// <summary>크로스 프로세스 자식 창에서 키보드 입력이 되도록 포커스를 강제한다.</summary>
	protected void FocusInput(Control? c)
	{
		if (c is null || !c.IsHandleCreated) return;
		try
		{
			IntPtr fg = GetForegroundWindow();
			uint fgTid = GetWindowThreadProcessId(fg, out _);
			uint ourTid = GetCurrentThreadId();
			bool attached = false;
			if (fg != IntPtr.Zero && fgTid != 0 && fgTid != ourTid)
				attached = AttachThreadInput(ourTid, fgTid, true);
			try
			{
				BringWindowToTop(Handle);
				Focus();
				c.Focus();
				SetFocus(c.Handle);
			}
			finally
			{
				if (attached)
					AttachThreadInput(ourTid, fgTid, false);
			}
		}
		catch
		{
		}
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		Focus();
	}

	protected override void WndProc(ref Message m)
	{
		const int WM_MOUSEACTIVATE = 0x0021;
		const int MA_ACTIVATE = 1;
		if (m.Msg == WM_MOUSEACTIVATE)
		{
			m.Result = (IntPtr)MA_ACTIVATE;
			return;
		}
		base.WndProc(ref m);
	}

	protected void DetachFromParent()
	{
		if (_parented && IsHandleCreated)
		{
			try { SetParent(Handle, IntPtr.Zero); } catch { }
			_parented = false;
		}
	}

	protected override CreateParams CreateParams
	{
		get
		{
			CreateParams cp = base.CreateParams;
			cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT;
			return cp;
		}
	}

	private const int GWL_STYLE = -16;
	private const int GWL_EXSTYLE = -20;
	private const int WS_CHILD = 0x40000000;
	private const int WS_CLIPCHILDREN = 0x02000000;
	private const int WS_CLIPSIBLINGS = 0x04000000;
	private const int WS_VISIBLE = 0x10000000;
	private const int WS_POPUP = unchecked((int)0x80000000);
	private const int WS_EX_TOOLWINDOW = 0x00000080;
	private const int WS_EX_CONTROLPARENT = 0x00010000;
	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_SHOWWINDOW = 0x0040;
	private const uint SWP_HIDEWINDOW = 0x0080;
	private const uint SWP_FRAMECHANGED = 0x0020;
	private const int SW_HIDE = 0;
	private static readonly IntPtr HWND_MESSAGE = new(-3);
	protected static readonly IntPtr HWND_TOP = new(0);
	protected static readonly IntPtr HWND_BOTTOM = new(1);

	[DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
	[DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
	[DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
	[DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
	[DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
	[DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
	[DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
	[DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
