namespace EVEAA.Mod;

internal enum AppView
{
	Alarm,
	Intel
}

/// <summary>좌측 탭: 경보기 / 인텔 알림</summary>
internal sealed class SideNavPanel : NativeChildForm
{
	public const int WidthPx = 56;

	private readonly Button _btnAlarm;
	private readonly Button _btnIntel;
	private AppView _current = AppView.Alarm;

	public event Action<AppView>? ViewChanged;

	public SideNavPanel()
	{
		BackColor = SystemColors.Control;
		ClientSize = new Size(WidthPx, 300);

		_btnAlarm = MakeTabButton("경보기");
		_btnIntel = MakeTabButton("인텔\n알림");
		// Cross-process 자식창: 첫 Click은 활성화에 먹혀서 더블클릭처럼 동작함 → MouseDown 사용
		_btnAlarm.MouseDown += (_, e) =>
		{
			if (e.Button == MouseButtons.Left) Select(AppView.Alarm);
		};
		_btnIntel.MouseDown += (_, e) =>
		{
			if (e.Button == MouseButtons.Left) Select(AppView.Intel);
		};

		Controls.Add(_btnIntel);
		Controls.Add(_btnAlarm);

		Resize += (_, _) => LayoutTabs();
		ApplySelectedStyle();
	}

	public void Attach(IntPtr parent) => AttachAsChild(parent);

	public void LayoutInParent(int clientH, bool force = false)
	{
		PlaceInParent(0, 0, WidthPx, clientH, bringToFront: true, visible: true, force: force);
		LayoutTabs();
	}

	private void Select(AppView view)
	{
		bool same = _current == view;
		_current = view;
		ApplySelectedStyle();
		// 같은 탭 재클릭도 적용 (숨김 풀림 복구)
		ViewChanged?.Invoke(view);
		if (same) { /* refresh only */ }
	}

	private void LayoutTabs()
	{
		int h = Math.Max(ClientSize.Height, 2);
		int half = h / 2;
		_btnAlarm.SetBounds(0, 0, ClientSize.Width, half);
		_btnIntel.SetBounds(0, half, ClientSize.Width, h - half);
	}

	private void ApplySelectedStyle()
	{
		StyleTab(_btnAlarm, _current == AppView.Alarm);
		StyleTab(_btnIntel, _current == AppView.Intel);
	}

	private static Button MakeTabButton(string text)
	{
		Button b = new()
		{
			Text = text,
			FlatStyle = FlatStyle.Flat,
			Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
			TextAlign = ContentAlignment.MiddleCenter,
			Cursor = Cursors.Hand,
			TabStop = false,
			Margin = Padding.Empty,
			Padding = Padding.Empty
		};
		b.FlatAppearance.BorderSize = 1;
		return b;
	}

	private static void StyleTab(Button b, bool selected)
	{
		if (selected)
		{
			// 선택: 더 밝은 면 + 왼쪽 파란 강조선 (비선택과 확실히 구분)
			b.BackColor = Color.FromArgb(245, 245, 245);
			b.ForeColor = Color.FromArgb(20, 20, 20);
			b.FlatAppearance.BorderColor = Color.FromArgb(70, 130, 180);
			b.FlatAppearance.BorderSize = 2;
			b.Padding = new Padding(4, 0, 0, 0);
		}
		else
		{
			b.BackColor = Color.FromArgb(210, 210, 210);
			b.ForeColor = Color.FromArgb(90, 90, 90);
			b.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
			b.FlatAppearance.BorderSize = 1;
			b.Padding = Padding.Empty;
		}
	}
}
