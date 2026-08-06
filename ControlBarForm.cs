using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EVEAA.Mod;

/// <summary>
/// EVEAA 크롬 호스트: 좌측 탭 + 우측 자동실행 + 인텔/ZKB (모두 같은 창 자식).
/// </summary>
internal sealed class ControlBarForm : Form
{
	private readonly ModSettings _settings;
	private readonly SideNavPanel _nav = new();
	private readonly AutoRunPanel _autoRun;
	private readonly IntelPanel _intel;
	private readonly ZkbFeedPanel _zkb;
	private readonly System.Windows.Forms.Timer _syncTimer;
	private readonly HashSet<IntPtr> _ourHandles = new();
	private readonly List<(IntPtr hwnd, int x, int y, int w, int h)> _origSnap = new();
	private CloseToTrayService? _trayService;
	private System.Media.SoundPlayer? _alarmSoundPlayer;

	private IntPtr _targetHwnd = IntPtr.Zero;
	private Process? _eveaaProcess;
	private int _logicalWidth;
	private int _logicalHeight;
	private AppView _view = AppView.Alarm;
	private bool _attached;
	private bool _childrenShifted;
	private bool _wasTargetVisible;
	private bool _wasForeground;
	private DateTime _lastForceRefreshUtc = DateTime.MinValue;
	private int _lastClientW;
	private int _lastClientH;
	private AppView _lastLaidOutView;

	/// <summary>이미 chrome이 확장된 EVEAA 창인지 표시하는 윈도우 프로퍼티 이름.
	/// 비정상 종료된 이전 Mod 세션이 남긴 창에 그대로 재부착하면 폭 확장/자식 이동이 중복 적용된다 — 재부착 전에 이 마커로 감지한다.</summary>
	private const string ChromeMarkerProp = "EveaaModChromeApplied";

	public static bool IsAlreadyChromed(IntPtr hwnd) =>
		hwnd != IntPtr.Zero && IsWindow(hwnd) && GetProp(hwnd, ChromeMarkerProp) != IntPtr.Zero;

	public ControlBarForm(ModSettings settings)
	{
		_settings = settings;
		_autoRun = new AutoRunPanel(settings);
		_intel = new IntelPanel(settings);
		_zkb = new ZkbFeedPanel(settings);

		Text = "EVEAA Mod Host";
		FormBorderStyle = FormBorderStyle.FixedToolWindow;
		ShowInTaskbar = false;
		Opacity = 0;
		ShowIcon = false;
		StartPosition = FormStartPosition.Manual;
		Size = new Size(0, 0);
		Location = new Point(-10000, -10000);

		_nav.ViewChanged += OnViewChanged;
		_zkb.AlarmSoundTestRequested += TestAlarmAlertSound;

		_syncTimer = new System.Windows.Forms.Timer { Interval = 200 };
		_syncTimer.Tick += (_, _) => Sync();
		_syncTimer.Start();
	}

	public void AttachToEveaa(Process process, IntPtr hwnd)
	{
		_eveaaProcess = process;
		_targetHwnd = hwnd;

		// 원본 창을 숨긴 채 좌/우 크롬까지 전부 배치한 뒤 한 번에 표시
		// (ExpandChrome의 SWP_SHOWWINDOW / Sync 200ms 타이머 때문에 본문만 먼저 보이던 문제 방지)
		ShowWindow(hwnd, SW_HIDE);
		SendMessage(hwnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

		// 확장 전에 스냅샷 — WM_SIZE로 늘어난 크기를 찍으면 우측 제어가 AutoRun과 겹침
		SnapshotOriginalChildren();
		ExpandChrome();
		AdjustWindowHeightForView();
		ShiftSnapshotBy(SideNavPanel.WidthPx);
		ApplySnapshotPositions();
		_childrenShifted = true;

		_nav.Attach(hwnd);
		_autoRun.Attach(hwnd);
		_intel.Attach(hwnd);
		_intel.SetVisible(false);
		_zkb.Attach(hwnd);
		_zkb.Bind(_intel.Engine);

		_ourHandles.Clear();
		_ourHandles.Add(_nav.Handle);
		_ourHandles.Add(_autoRun.Handle);
		_ourHandles.Add(_intel.Handle);
		_ourHandles.Add(_zkb.Handle);

		_attached = true;
		_wasTargetVisible = true;
		LayoutChromePanels(force: true);

		SendMessage(hwnd, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
		ShowWindow(hwnd, SW_SHOW);
		EnsureChildShown(_nav.Handle);
		EnsureChildShown(_autoRun.Handle);
		EnsureChildShown(_zkb.Handle);
		try { if (_zkb.IsHandleCreated) { _zkb.Invalidate(true); _zkb.Update(); } } catch { }
		try { if (_nav.IsHandleCreated) { _nav.Invalidate(true); _nav.Update(); } } catch { }
		try { if (_autoRun.IsHandleCreated) { _autoRun.Invalidate(true); _autoRun.Update(); } } catch { }
		RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
			RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME | RDW_ERASE);
		SetForegroundWindow(hwnd);

		// 원본 EVEAA 2.26처럼 X/닫기는 트레이 숨김 없이 본문 창이 그대로 동작
		_trayService?.Dispose();
		_trayService = null;
	}

	/// <summary>좌·우(·인텔) 패널을 부모 클라이언트에 즉시 배치·표시. 부모 숨김 중에도 동작.</summary>
	private void LayoutChromePanels(bool force)
	{
		if (!_attached || _targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd))
			return;
		if (!GetClientRect(_targetHwnd, out RECT client))
			return;

		int clientW = client.Right - client.Left;
		int clientH = client.Bottom - client.Top;
		if (clientW < 10 || clientH < 10)
			return;

		bool sizeChanged = force ||
			clientW != _lastClientW || clientH != _lastClientH || _view != _lastLaidOutView;
		_lastClientW = clientW;
		_lastClientH = clientH;
		_lastLaidOutView = _view;
		if (!sizeChanged)
			return;

		_nav.LayoutInParent(clientH, force);
		_autoRun.LayoutInParent(clientW, clientH, force);

		int contentX = SideNavPanel.WidthPx;
		int contentW = Math.Max(10, clientW - SideNavPanel.WidthPx - AutoRunPanel.WidthPx);

		// ZKB는 뷰와 무관하게 항상 하단 고정 스트립 — 경보기 탭에서도 계속 보이도록
		int bottomH = ZkbPanelHeight(clientH);
		if (bottomH > clientH - 80) bottomH = Math.Max(80, clientH / 4);
		int topH = Math.Max(80, clientH - bottomH);

		if (_view == AppView.Intel)
		{
			HideOriginalChildren();
			_intel.SetVisible(true);
			_intel.LayoutInParent(contentX, 0, contentW, topH, force: true);
		}
		else
		{
			// 경보기: 인텔만 제거 + 원본을 상단 영역(ZKB 스트립 제외)으로 복구
			_intel.SetVisible(false);
			RestoreOriginalChildren(contentX, contentW, topH);
			ConstrainOriginalChildren(contentX, contentW, topH);
		}

		_zkb.SetVisible(true);
		_zkb.LayoutInParent(contentX, topH, contentW, bottomH, force: true);

		ApplyZOrder();
		if (_view == AppView.Alarm)
		{
			_intel.SetVisible(false);
			if (force)
				InvalidateOriginalChildren();
		}
	}

	private void SnapshotOriginalChildren()
	{
		_origSnap.Clear();
		if (_targetHwnd == IntPtr.Zero) return;
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) != _targetHwnd || _ourHandles.Contains(hwnd))
				return true;
			if (!GetWindowRect(hwnd, out RECT wr))
				return true;
			POINT pt = new() { X = wr.Left, Y = wr.Top };
			ScreenToClient(_targetHwnd, ref pt);
			int w = wr.Right - wr.Left;
			int h = wr.Bottom - wr.Top;
			if (w > 2 && h > 2)
				_origSnap.Add((hwnd, pt.X, pt.Y, w, h));
			return true;
		}, IntPtr.Zero);
	}

	private void ShiftSnapshotBy(int dx)
	{
		if (dx == 0 || _origSnap.Count == 0) return;
		for (int i = 0; i < _origSnap.Count; i++)
		{
			var o = _origSnap[i];
			_origSnap[i] = (o.hwnd, o.x + dx, o.y, o.w, o.h);
		}
	}

	private void ApplySnapshotPositions()
	{
		foreach (var o in _origSnap)
		{
			if (!IsWindow(o.hwnd)) continue;
			SetWindowPos(o.hwnd, IntPtr.Zero, o.x, o.y, o.w, o.h,
				SWP_NOZORDER | SWP_NOACTIVATE);
		}
	}

	/// <summary>
	/// 경보기: 첨부 직후 스냅샷 좌표 그대로 복구.
	/// 좌우로 밀거나 폭을 자르면 아이콘/제어가 서로 겹친다 — 가로 배치는 건드리지 않음.
	/// </summary>
	private void RestoreOriginalChildren(int contentX, int contentW, int topH)
	{
		if (_targetHwnd == IntPtr.Zero) return;
		if (_origSnap.Count == 0)
			SnapshotOriginalChildren();

		foreach (var o in _origSnap)
		{
			if (!IsWindow(o.hwnd) || _ourHandles.Contains(o.hwnd))
				continue;
			int nx = o.x;
			int ny = o.y;
			int nw = o.w;
			int nh = o.h;
			// 세로만 클램프 (ZKB/창 높이). 가로는 스냅샷 유지.
			if (ny < 0)
			{
				nh += ny;
				ny = 0;
			}
			if (ny + nh > topH)
				nh = Math.Max(10, topH - ny);
			if (nw < 10 || nh < 10) continue;
			ShowWindow(o.hwnd, SW_SHOW);
			SetWindowPos(o.hwnd, IntPtr.Zero, nx, ny, nw, nh,
				SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
		}
	}

	/// <summary>스냅샷으로만 재정렬 — 실시간 Fit는 레이아웃을 망가뜨림.</summary>
	private void ConstrainOriginalChildren(int contentX, int contentW, int topH)
	{
		RestoreOriginalChildren(contentX, contentW, topH);
	}

	/// <summary>경보기 모드: 원본 자식이 늘어난 창 높이 전체를 채우지 않도록 상단 절반으로 고정.</summary>
	private void ClampOriginalChildrenToTop(int topH)
	{
		if (_targetHwnd == IntPtr.Zero || topH <= 0) return;
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) != _targetHwnd || _ourHandles.Contains(hwnd))
				return true;
			if (!GetWindowRect(hwnd, out RECT wr))
				return true;
			POINT pt = new() { X = wr.Left, Y = wr.Top };
			ScreenToClient(_targetHwnd, ref pt);
			int w = wr.Right - wr.Left;
			int h = wr.Bottom - wr.Top;
			if (pt.Y >= topH)
				return true;
			int maxH = topH - pt.Y;
			if (h > maxH && maxH > 10)
				SetWindowPos(hwnd, IntPtr.Zero, pt.X, pt.Y, w, maxH, SWP_NOZORDER | SWP_NOACTIVATE);
			return true;
		}, IntPtr.Zero);
	}

	private void OnViewChanged(AppView view)
	{
		_view = view;
		if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd))
			return;

		SendMessage(_targetHwnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
		try
		{
			// 경보기=원본 높이, 인텔=ZKB 스트립 포함 — 빈 하단/겹침 방지
			AdjustWindowHeightForView();
			_lastClientW = 0;
			_lastClientH = 0;
			LayoutChromePanels(force: true);
			ApplyZOrder();
		}
		finally
		{
			SendMessage(_targetHwnd, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
			if (view == AppView.Alarm)
			{
				InvalidateOriginalChildren();
			}
			else if (_intel.IsHandleCreated)
			{
				RedrawWindow(_intel.Handle, IntPtr.Zero, IntPtr.Zero,
					RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
			}
			if (_zkb.IsHandleCreated)
			{
				RedrawWindow(_zkb.Handle, IntPtr.Zero, IntPtr.Zero,
					RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
				// 네이티브 RedrawWindow만으로는 탭 전환 후 알림음 테스트/색상 버튼이
				// 흰 배경에 덮여 그대로 비어 보이는 경우가 있어, WinForms 레벨에서도
				// 자식 컨트롤까지 재귀적으로 다시 그리게 강제한다.
				_zkb.Refresh();
			}
			if (_nav.IsHandleCreated)
				RedrawWindow(_nav.Handle, IntPtr.Zero, IntPtr.Zero,
					RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
			if (_autoRun.IsHandleCreated)
				RedrawWindow(_autoRun.Handle, IntPtr.Zero, IntPtr.Zero,
					RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
		}
	}

	/// <summary>경보기 모드에서 원본 EVEAA 자식을 인텔 패널 위로 올린다.</summary>
	private void RaiseOriginalChildren()
	{
		if (_targetHwnd == IntPtr.Zero) return;
		IntPtr intelHwnd = _intel.IsHandleCreated ? _intel.Handle : IntPtr.Zero;
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) != _targetHwnd || _ourHandles.Contains(hwnd) || hwnd == intelHwnd)
				return true;
			ShowWindow(hwnd, SW_SHOW);
			SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
				SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
			return true;
		}, IntPtr.Zero);
	}

	/// <summary>인텔 탭: 원본 경보기 자식을 전부 숨겨 레이어 섞임을 방지.</summary>
	private void HideOriginalChildren()
	{
		if (_targetHwnd == IntPtr.Zero) return;
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) != _targetHwnd || _ourHandles.Contains(hwnd))
				return true;
			ShowWindow(hwnd, SW_HIDE);
			return true;
		}, IntPtr.Zero);
	}

	/// <summary>
	/// 고정 z-order: 인텔탭은 원본 숨김 후 ZKB→인텔→좌/우, 경보기는 원본→좌/우(ZKB/인텔 숨김).
	/// </summary>
	private void ApplyZOrder()
	{
		if (_targetHwnd == IntPtr.Zero) return;
		RefreshOurHandles();

		if (_view == AppView.Intel)
		{
			HideOriginalChildren();
			_intel.SetVisible(true);
			if (_intel.IsHandleCreated)
			{
				ShowWindow(_intel.Handle, SW_SHOW);
				SetWindowPos(_intel.Handle, HWND_TOP, 0, 0, 0, 0,
					SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
			}
		}
		else
		{
			_intel.SetVisible(false);
			RaiseOriginalChildren();
		}

		// ZKB는 뷰와 무관하게 항상 표시 — 원본/인텔 위, 좌우 크롬 아래
		_zkb.SetVisible(true);
		if (_zkb.IsHandleCreated)
		{
			ShowWindow(_zkb.Handle, SW_SHOW);
			SetWindowPos(_zkb.Handle, HWND_TOP, 0, 0, 0, 0,
				SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
		}

		if (_nav.IsHandleCreated)
		{
			ShowWindow(_nav.Handle, SW_SHOW);
			SetWindowPos(_nav.Handle, HWND_TOP, 0, 0, 0, 0,
				SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
		}
		if (_autoRun.IsHandleCreated)
		{
			ShowWindow(_autoRun.Handle, SW_SHOW);
			SetWindowPos(_autoRun.Handle, HWND_TOP, 0, 0, 0, 0,
				SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
		}

		if (_view == AppView.Alarm)
		{
			_intel.SetVisible(false);
		}
	}

	private void RefreshOurHandles()
	{
		_ourHandles.Clear();
		if (_nav.IsHandleCreated) _ourHandles.Add(_nav.Handle);
		if (_autoRun.IsHandleCreated) _ourHandles.Add(_autoRun.Handle);
		if (_intel.IsHandleCreated) _ourHandles.Add(_intel.Handle);
		if (_zkb.IsHandleCreated) _ourHandles.Add(_zkb.Handle);
	}

	/// <summary>트레이/최소화/Alt-Tab 복원 후 좌·우·인텔·ZKB 크롬과 EVEAA 본문을 강제 재배치·다시 그림.</summary>
	public void ForceChromeRefresh()
	{
		if (!_attached || _targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd))
			return;
		// 짧은 간격 중복 강제 갱신 방지 (포커스 떨림/녹화 오버레이 등)
		var now = DateTime.UtcNow;
		if ((now - _lastForceRefreshUtc).TotalMilliseconds < 1500)
			return;
		_lastForceRefreshUtc = now;
		_wasTargetVisible = true;
		EnsureHostClipsChildren();
		// sizeChanged만으로 재배치 (force SetWindowPos 남발 금지)
		LayoutChromePanels(force: false);
		ApplyZOrder();
		try { if (_nav.IsHandleCreated) { _nav.Invalidate(false); _nav.Update(); } } catch { }
		try { if (_autoRun.IsHandleCreated) { _autoRun.Invalidate(false); _autoRun.Update(); } } catch { }
		try { if (_zkb.IsHandleCreated) { _zkb.Invalidate(false); _zkb.Update(); } } catch { }
		if (_view == AppView.Intel)
		{
			try { if (_intel.IsHandleCreated) { _intel.Invalidate(false); _intel.Update(); } } catch { }
		}
		else
			InvalidateOriginalChildren();
	}

	private void EnsureChildShown(IntPtr hwnd, bool bringToFront = false)
	{
		if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
		ShowWindow(hwnd, SW_SHOW);
		uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
		if (!bringToFront) flags |= SWP_NOZORDER;
		SetWindowPos(hwnd, bringToFront ? HWND_TOP : IntPtr.Zero, 0, 0, 0, 0, flags);
	}

	private void InvalidateOriginalChildren()
	{
		if (_targetHwnd == IntPtr.Zero) return;
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) == _targetHwnd && !_ourHandles.Contains(hwnd))
				RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
					RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_ERASE);
			return true;
		}, IntPtr.Zero);
	}

	/// <summary>원본 높이 + ZKB 하단 스트립. (이전 h*2는 과해서 약 절반 수준으로)</summary>
	private static int TargetChromeHeight(int logicalH)
	{
		int strip = Math.Clamp(logicalH / 5, 320, 400);
		return logicalH + strip;
	}

	private static int ZkbPanelHeight(int clientH)
	{
		return Math.Clamp(clientH / 5, 320, 400);
	}

	/// <summary>탭에 맞게 창 높이 조정. 경보기에 ZKB 빈 칸이 남지 않게.</summary>
	private void AdjustWindowHeightForView()
	{
		if (_targetHwnd == IntPtr.Zero || _logicalHeight < 120) return;
		if (!GetWindowRect(_targetHwnd, out RECT rc)) return;
		int curW = rc.Right - rc.Left;
		int curH = rc.Bottom - rc.Top;
		// ZKB 스트립이 뷰와 무관하게 항상 붙어있으므로 목표 높이도 항상 동일하게 유지
		int wantH = TargetChromeHeight(_logicalHeight);
		if (Math.Abs(curH - wantH) < 12) return;
		SetWindowPos(_targetHwnd, IntPtr.Zero, rc.Left, rc.Top, curW, wantH,
			SWP_NOZORDER | SWP_NOACTIVATE);
	}

	private void ExpandChrome()
	{
		if (_targetHwnd == IntPtr.Zero || !GetWindowRect(_targetHwnd, out RECT rc))
		{
			return;
		}

		int w = rc.Right - rc.Left;
		int h = rc.Bottom - rc.Top;
		_logicalWidth = w;
		_logicalHeight = h;

		// 가로: 좌탭 + 우측 자동실행. 세로: 경보기 높이 유지
		int newW = w + SideNavPanel.WidthPx + AutoRunPanel.WidthPx;
		int newH = h;
		// SWP_SHOWWINDOW 금지: 숨김 중 확장인데 다시 보이면 좌/우 패널보다 본문만 먼저 노출됨
		SetWindowPos(_targetHwnd, IntPtr.Zero, rc.Left, rc.Top, newW, newH, SWP_NOZORDER | SWP_NOACTIVATE);
		SetProp(_targetHwnd, ChromeMarkerProp, new IntPtr(1));
		EnsureHostClipsChildren();
	}

	/// <summary>
	/// 원본 EVEAA 창에 WS_CLIPCHILDREN이 없으면, 원본 자신의 배경 지우기/다시 그리기가
	/// 우리 자식 패널(ZKB 등) 영역 위로 그대로 덮어써서 흰 여백이 남는다 — 예: 클라이언트
	/// 목록에서 다른 행을 클릭할 때 원본이 자기 영역을 다시 그리며 ZKB 패널을 하얗게 지움.
	/// </summary>
	private void EnsureHostClipsChildren()
	{
		if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) return;
		int style = GetWindowLong(_targetHwnd, GWL_STYLE);
		if ((style & WS_CLIPCHILDREN) == 0)
			SetWindowLong(_targetHwnd, GWL_STYLE, style | WS_CLIPCHILDREN);
	}

	/// <summary>
	/// 경보기(원본) 화면의 "경고음 선택 :" 라벨과 같은 줄에 있는 Edit 컨트롤 텍스트(예: "BEEP")를 읽는다.
	/// hwnd는 실행마다 바뀌므로 라벨 텍스트로 매번 다시 찾는다. 못 찾으면 null.
	/// </summary>
	private string? FindAlertSoundName()
	{
		if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) return null;

		IntPtr labelHwnd = IntPtr.Zero;
		RECT labelRect = default;
		var editCandidates = new List<(IntPtr hwnd, RECT rect)>();

		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			var cls = new StringBuilder(64);
			GetClassName(hwnd, cls, 64);
			string className = cls.ToString();

			if (className.Contains("Static"))
			{
				var txt = new StringBuilder(64);
				GetWindowText(hwnd, txt, 64);
				if (txt.ToString().StartsWith("경고음"))
				{
					labelHwnd = hwnd;
					GetWindowRect(hwnd, out labelRect);
				}
			}
			else if (className.Contains("Edit"))
			{
				if (GetWindowRect(hwnd, out RECT r))
					editCandidates.Add((hwnd, r));
			}
			return true;
		}, IntPtr.Zero);

		if (labelHwnd == IntPtr.Zero) return null;

		foreach (var (hwnd, r) in editCandidates)
		{
			// 라벨과 세로 범위가 겹치는(같은 줄) Edit
			bool sameRow = r.Top < labelRect.Bottom && r.Bottom > labelRect.Top;
			if (!sameRow) continue;
			var sb = new StringBuilder(260);
			GetWindowText(hwnd, sb, 260);
			return sb.ToString();
		}
		return null;
	}

	/// <summary>ZKB 패널 "알림음 테스트" 버튼 — 경보기 쪽 현재 선택된 경고음을 1회 재생.</summary>
	private void TestAlarmAlertSound()
	{
		try
		{
			string? exeDir = null;
			if (!string.IsNullOrEmpty(_settings.EveaaExePath))
				exeDir = Path.GetDirectoryName(_settings.EveaaExePath);
			if (string.IsNullOrEmpty(exeDir) && _eveaaProcess is not null)
			{
				try { exeDir = Path.GetDirectoryName(_eveaaProcess.MainModule?.FileName); } catch { }
			}
			if (string.IsNullOrEmpty(exeDir))
			{
				MessageBox.Show("원본 EVEAA 위치를 찾을 수 없습니다.", "알림음 테스트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			// "경고음 선택" Edit는 사용자가 브라우즈로 커스텀 파일을 고를 때만 채워지고,
			// 기본값(BEEP)일 때는 비어 있는 것으로 보인다 — 비어 있으면 기본 BEEP로 테스트한다.
			string? name = FindAlertSoundName();
			string path;
			if (string.IsNullOrWhiteSpace(name))
			{
				path = Path.Combine(exeDir, "sound", "BEEP.wav");
			}
			else if (File.Exists(name))
			{
				// 브라우즈로 고른 커스텀 파일의 전체 경로인 경우
				path = name;
			}
			else
			{
				string trimmed = name.Trim();
				if (Path.GetExtension(trimmed).Length > 0)
					trimmed = Path.GetFileNameWithoutExtension(trimmed);
				path = Path.Combine(exeDir, "sound", trimmed + ".wav");
			}

			if (!File.Exists(path))
			{
				MessageBox.Show($"소리 파일을 찾을 수 없습니다:\n{path}", "알림음 테스트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			// AlertSoundPlayer의 동기 SoundPlayer.Load()(MemoryStream 경유)가 이 WAV에서 멈춰
			// UI 스레드 전체가 응답 없음 상태가 되는 경우가 있었다 — 파일 경로로 바로, Load()
			// 없이 Play()만 호출하는 표준적인 비동기 방식을 쓴다 (Play()가 필요하면 알아서
			// 백그라운드에서 로드한다). 볼륨 조절은 원본 WAV 샘플을 미리 스케일링해 임시
			// 파일로 저장한 뒤 그 파일을 재생하는 식으로 처리 — MemoryStream을 SoundPlayer에
			// 직접 물리지 않아야 위 멈춤 증상을 피할 수 있다.
			int volume = Math.Clamp(_settings.AlarmSoundTestVolume, 0, 100);
			string playPath = path;
			if (volume < 100)
			{
				try
				{
					byte[] raw = File.ReadAllBytes(path);
					byte[] scaled = AlertSoundPlayer.ScaleWavVolume(raw, volume / 100.0);
					string tmp = Path.Combine(Path.GetTempPath(), "eveaa_alarm_test_preview.wav");
					File.WriteAllBytes(tmp, scaled);
					playPath = tmp;
				}
				catch { playPath = path; }
			}

			// using으로 즉시 Dispose하면 비동기 재생 중 스트림/파일 핸들이 끊길 수 있어
			// 필드에 보관했다가 다음 테스트/창 종료 시점에 정리한다.
			try { _alarmSoundPlayer?.Dispose(); } catch { }
			_alarmSoundPlayer = new System.Media.SoundPlayer(playPath);
			_alarmSoundPlayer.Play();
		}
		catch (Exception ex)
		{
			MessageBox.Show("알림음 재생 오류: " + ex.Message, "알림음 테스트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	/// <summary>EVEAA 원본 직계 자식 컨트롤을 dx만큼 오른쪽으로 이동.</summary>
	private void ShiftOriginalChildren(int dx)
	{
		if (_targetHwnd == IntPtr.Zero || dx == 0)
		{
			return;
		}

		List<IntPtr> children = new();
		EnumChildWindows(_targetHwnd, (hwnd, _) =>
		{
			if (GetParent(hwnd) == _targetHwnd && !_ourHandles.Contains(hwnd))
			{
				children.Add(hwnd);
			}
			return true;
		}, IntPtr.Zero);

		foreach (IntPtr child in children)
		{
			if (!GetWindowRect(child, out RECT wr))
			{
				continue;
			}

			POINT pt = new() { X = wr.Left, Y = wr.Top };
			ScreenToClient(_targetHwnd, ref pt);
			int w = wr.Right - wr.Left;
			int h = wr.Bottom - wr.Top;
			SetWindowPos(child, IntPtr.Zero, pt.X + dx, pt.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
		}
	}

	private void Sync()
	{
		if (!_attached || _targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd))
		{
			if (_eveaaProcess != null && _eveaaProcess.HasExited)
			{
				WindowFix.SanitizeAllUserConfigs();
				Close();
			}
			return;
		}

		if (!GetWindowRect(_targetHwnd, out RECT rc))
		{
			return;
		}

		bool visible = IsWindowVisible(_targetHwnd);
		if (!visible)
		{
			_wasTargetVisible = false;
			return;
		}

		// 최소화/트레이 숨김 → 복원: 크기 같아도 자식 HWND 재배치·재표시
		if (!_wasTargetVisible)
		{
			_lastClientW = 0;
			_lastClientH = 0;
			_wasTargetVisible = true;
		}

		// Alt-Tab 등으로 포커스만 돌아와도 DWM/자식이 하얗게 남는 경우 → 강제 리프레시
		IntPtr fg = GetForegroundWindow();
		IntPtr fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOT);
		bool isFg = fg == _targetHwnd || fgRoot == _targetHwnd;
		bool focusGained = isFg && !_wasForeground;
		_wasForeground = isFg;
		if (focusGained)
		{
			ForceChromeRefresh();
			return;
		}

		int curW = rc.Right - rc.Left;
		int curH = rc.Bottom - rc.Top;
		int totalExtra = SideNavPanel.WidthPx + AutoRunPanel.WidthPx;

		if (_logicalWidth > 0 && curW > 80)
			_logicalWidth = Math.Max(200, curW - totalExtra);
		if (_logicalHeight > 0 && curH > 80)
			_logicalHeight = Math.Max(120, curH - ZkbPanelHeight(curH));

		RefreshOurHandles();
		if (!_childrenShifted)
		{
			SnapshotOriginalChildren();
			ShiftSnapshotBy(SideNavPanel.WidthPx);
			ApplySnapshotPositions();
			_childrenShifted = true;
		}

		LayoutChromePanels(force: false);
		if (_view == AppView.Intel)
		{
			HideOriginalChildren();
			if (!_intel.IsShownInHost || !_zkb.IsShownInHost)
			{
				_lastClientW = 0;
				_lastClientH = 0;
				LayoutChromePanels(force: true);
			}
		}
		else
		{
			// 매 틱 무조건 재확인 - 가시성 체크만으로는 놓치는 경우(레이스/DPI 변경 등)가 있어 경보기 뷰에서는 항상 강제로 인텔만 숨긴다 (HideInParent는 이미 숨은 상태에서도 안전). ZKB는 경보기에서도 계속 표시.
			if (_intel.IsHandleCreated)
				_intel.SetVisible(false);

			if (GetClientRect(_targetHwnd, out RECT client))
			{
				int cw = client.Right - client.Left;
				int ch = client.Bottom - client.Top;
				int contentX = SideNavPanel.WidthPx;
				int contentW = Math.Max(10, cw - SideNavPanel.WidthPx - AutoRunPanel.WidthPx);
				int bottomH = ZkbPanelHeight(ch);
				if (bottomH > ch - 80) bottomH = Math.Max(80, ch / 4);
				int topH = Math.Max(80, ch - bottomH);
				ConstrainOriginalChildren(contentX, contentW, topH);
				if (_zkb.IsHandleCreated)
				{
					_zkb.SetVisible(true);
					_zkb.LayoutInParent(contentX, topH, contentW, bottomH, force: false);
				}
			}
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_syncTimer.Stop();
		_syncTimer.Dispose();
		try { _trayService?.Dispose(); } catch { }
		_trayService = null;
		try { _alarmSoundPlayer?.Dispose(); } catch { }
		try { _nav.Close(); } catch { }
		try { _autoRun.Close(); } catch { }
		try { _intel.Close(); } catch { }
		try { _zkb.Close(); } catch { }

		// 임베드된 원본 EVEAA(2.26)가 혼자 남지 / 워처가 재실행하지 않게 종료
		try
		{
			if (_eveaaProcess is not null && !_eveaaProcess.HasExited)
				_eveaaProcess.Kill(entireProcessTree: true);
		}
		catch { }
		_eveaaProcess = null;
		_targetHwnd = IntPtr.Zero;

		base.OnFormClosed(e);
	}

	private const uint SWP_NOZORDER = 0x0004;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_SHOWWINDOW = 0x0040;
	private const int SW_HIDE = 0;
	private const int SW_SHOW = 5;
	private const int GA_ROOT = 2;
	private const int WM_SETREDRAW = 0x000B;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOSIZE = 0x0001;
	private const uint RDW_INVALIDATE = 0x0001;
	private const uint RDW_ERASE = 0x0004;
	private const uint RDW_UPDATENOW = 0x0100;
	private const uint RDW_ALLCHILDREN = 0x0080;
	private const uint RDW_FRAME = 0x0400;
	private const int GWL_STYLE = -16;
	private const int WS_CLIPCHILDREN = 0x02000000;
	private static readonly IntPtr HWND_TOP = IntPtr.Zero;

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
	[DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
	[DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
	[DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);
	[DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
	[DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
	[DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
	[DllImport("user32.dll")] private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);
	[DllImport("user32.dll")] private static extern IntPtr GetProp(IntPtr hWnd, string lpString);
	[DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
	[DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}
}
