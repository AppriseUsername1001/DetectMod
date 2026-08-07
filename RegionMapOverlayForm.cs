using EVEAA.Mod.Intel;

namespace EVEAA.Mod;

/// <summary>
/// Watch character region map overlay: move / pin / resize / close, remember bounds.
/// </summary>
internal sealed class RegionMapOverlayForm : Form
{
	private readonly ModSettings _settings;
	private readonly IntelEngine _engine;
	private readonly Action? _onSettings;
	private readonly RegionMapControl _map = new();
	private readonly Panel _titleBar;
	private readonly Label _titleLabel;
	private readonly Button _btnPin;
	private readonly Button _btnClose;
	private readonly Button _btnSettings;
	private readonly System.Windows.Forms.Timer _tick;
	private bool _locked;
	private bool _dragging;
	private Point _dragScreenOrigin;
	private Point _formOrigin;
	private bool _resizing;
	private Point _resizeStart;
	private Size _resizeStartSize;
	private const int TitleH = 28;
	private const int Grip = 18;

	public RegionMapOverlayForm(ModSettings settings, IntelEngine engine, Action? onSettings = null)
	{
		_settings = settings;
		_engine = engine;
		_onSettings = onSettings;

		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		TopMost = true;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.FromArgb(22, 22, 24);
		MinimumSize = new Size(280, 220);
		DoubleBuffered = true;
		KeyPreview = true;

		_titleBar = new Panel
		{
			Dock = DockStyle.Top,
			Height = TitleH,
			BackColor = Color.FromArgb(36, 36, 40)
		};
		_titleLabel = new Label
		{
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
			ForeColor = Color.White,
			Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
			Text = "-"
		};
		_btnPin = new Button
		{
			Text = "PIN",
			Dock = DockStyle.Left,
			Width = 40,
			FlatStyle = FlatStyle.Flat,
			ForeColor = Color.Gainsboro,
			BackColor = Color.FromArgb(36, 36, 40),
			Cursor = Cursors.Hand,
			Font = new Font("맑은 고딕", 7.5f, FontStyle.Bold),
			TabStop = false
		};
		_btnPin.FlatAppearance.BorderSize = 0;
		_btnClose = new Button
		{
			Text = "X",
			Dock = DockStyle.Right,
			Width = 32,
			FlatStyle = FlatStyle.Flat,
			ForeColor = Color.White,
			BackColor = Color.FromArgb(36, 36, 40),
			Cursor = Cursors.Hand,
			Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
			TabStop = false
		};
		_btnClose.FlatAppearance.BorderSize = 0;
		_btnClose.Click += (_, _) => Hide();
		_btnSettings = new Button
		{
			Text = "SET",
			Dock = DockStyle.Left,
			Width = 40,
			FlatStyle = FlatStyle.Flat,
			ForeColor = Color.Gainsboro,
			BackColor = Color.FromArgb(36, 36, 40),
			Cursor = Cursors.Hand,
			Font = new Font("맑은 고딕", 7.5f, FontStyle.Bold),
			TabStop = false
		};
		_btnSettings.FlatAppearance.BorderSize = 0;
		_btnSettings.Click += (_, _) => _onSettings?.Invoke();

		_btnPin.Click += (_, _) => ToggleLock();

		_titleBar.Controls.Add(_titleLabel);
		_titleBar.Controls.Add(_btnClose);
		_titleBar.Controls.Add(_btnPin);
		_titleBar.Controls.Add(_btnSettings);
		_titleLabel.MouseDown += TitleMouseDown;
		_titleLabel.MouseMove += TitleMouseMove;
		_titleLabel.MouseUp += (_, _) => EndDrag();
		_titleBar.MouseDown += TitleMouseDown;
		_titleBar.MouseMove += TitleMouseMove;
		_titleBar.MouseUp += (_, _) => EndDrag();

		_map.Dock = DockStyle.Fill;
		_map.DarkTheme = true;
		_map.MouseDown += MapMouseDown;
		_map.MouseMove += MapMouseMove;
		_map.MouseUp += MapMouseUp;

		Controls.Add(_map);
		Controls.Add(_titleBar);

		_tick = new System.Windows.Forms.Timer { Interval = 500 };
		_tick.Tick += (_, _) =>
		{
			_map.PruneExpired();
			RefreshFromEngine();
		};

		ApplyGeometryFromSettings();
		ApplyLockVisual();
		RefreshFromEngine();

		_engine.LocationUpdated += OnLocation;
		_engine.ThreatDetected += OnThreat;
		_engine.ZkbLoss += OnZkb;
	}

	public void ShowOverlay()
	{
		ApplyGeometryFromSettings();
		RefreshFromEngine();
		if (!Visible)
			Show();
		else
			Activate();
		_tick.Start();
	}

	private void ToggleLock()
	{
		_locked = !_locked;
		_settings.MapOverlayLocked = _locked;
		_settings.Save();
		ApplyLockVisual();
	}

	private void ApplyLockVisual()
	{
		_locked = _settings.MapOverlayLocked;
		_btnPin.ForeColor = _locked ? Color.FromArgb(80, 180, 255) : Color.Gainsboro;
		_btnPin.Text = _locked ? "LOCK" : "PIN";
	}

	private void ApplyGeometryFromSettings()
	{
		int w = _settings.MapOverlayW > 100 ? _settings.MapOverlayW : 420;
		int h = _settings.MapOverlayH > 100 ? _settings.MapOverlayH : 360;
		int x = _settings.MapOverlayX;
		int y = _settings.MapOverlayY;
		if (x == int.MinValue || y == int.MinValue)
		{
			var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
			x = wa.Right - w - 24;
			y = wa.Top + 80;
		}
		else
		{
			var bounds = Screen.FromPoint(new Point(x, y)).WorkingArea;
			x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - 80));
			y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - 40));
		}
		Bounds = new Rectangle(x, y, w, h);
		_locked = _settings.MapOverlayLocked;
	}

	private void PersistGeometry()
	{
		_settings.MapOverlayX = Left;
		_settings.MapOverlayY = Top;
		_settings.MapOverlayW = Width;
		_settings.MapOverlayH = Height;
		_settings.MapOverlayLocked = _locked;
		_settings.Save();
	}

	private void OnLocation(TrackedCharacter c)
	{
		if (IsDisposed) return;
		try
		{
			if (InvokeRequired) BeginInvoke(() => ApplyLocation(c));
			else ApplyLocation(c);
		}
		catch { }
	}

	private void ApplyLocation(TrackedCharacter c)
	{
		string sys = c.LocationSystem ?? "";
		var region = ResolveRegion(sys);
		_map.SetRegion(region, _engine.Systems, sys);
		_titleLabel.Text = string.IsNullOrEmpty(sys)
			? (_map.RegionTitle.Length > 0 ? _map.RegionTitle : "-")
			: sys;
	}

	private void OnThreat(IntelThreatEvent ev)
	{
		if (IsDisposed || string.IsNullOrWhiteSpace(ev.System) || ev.IsClear) return;
		try
		{
			void Apply()
			{
				int showSec = Math.Max(5, _settings.MapIntelDisplaySec);
				int freshSec = Math.Max(1, _settings.MapIntelHighlightSec);
				_map.UpsertMarker(ev.System, MapMarkerKind.IntelFresh,
					TimeSpan.FromSeconds(showSec), TimeSpan.FromSeconds(freshSec), ev.FormatLogLine());
			}
			if (InvokeRequired) BeginInvoke(Apply);
			else Apply();
		}
		catch { }
	}

	private void OnZkb(ZkbLossEvent ev)
	{
		if (IsDisposed || string.IsNullOrWhiteSpace(ev.SystemName)) return;
		try
		{
			void Apply()
			{
				int showSec = Math.Max(5, _settings.MapZkbDisplaySec);
				string reason = $"[{ev.KillTimeUtc:HH:mm:ss}] 킬: {ev.ShipText} ({ev.AllianceText})";
				_map.UpsertMarker(ev.SystemName, MapMarkerKind.Zkb, TimeSpan.FromSeconds(showSec), reason: reason);
			}
			if (InvokeRequired) BeginInvoke(Apply);
			else Apply();
		}
		catch { }
	}

	private void RefreshFromEngine()
	{
		var c = _engine.Character;
		if (c is null) return;
		ApplyLocation(c);
	}

	private RegionMap? ResolveRegion(string system)
	{
		if (string.IsNullOrWhiteSpace(system)) return null;
		var map = _engine.Maps.GetRegionForSystem(system);
		if (map is not null) return map;
		var info = _engine.Systems.Get(system);
		return info is null ? null : _engine.Maps.GetRegion(info.Region);
	}

	private void TitleMouseDown(object? sender, MouseEventArgs e)
	{
		if (_locked || e.Button != MouseButtons.Left) return;
		_dragging = true;
		_dragScreenOrigin = Cursor.Position;
		_formOrigin = Location;
	}

	private void TitleMouseMove(object? sender, MouseEventArgs e)
	{
		if (!_dragging || _locked) return;
		var cur = Cursor.Position;
		Location = new Point(
			_formOrigin.X + (cur.X - _dragScreenOrigin.X),
			_formOrigin.Y + (cur.Y - _dragScreenOrigin.Y));
	}

	private void EndDrag()
	{
		if (!_dragging) return;
		_dragging = false;
		PersistGeometry();
	}

	private void MapMouseDown(object? sender, MouseEventArgs e)
	{
		if (_locked || e.Button != MouseButtons.Left) return;
		if (InResizeGrip(e.Location))
		{
			_resizing = true;
			_resizeStart = PointToScreen(e.Location);
			_resizeStartSize = Size;
			_map.Capture = true;
		}
	}

	private void MapMouseMove(object? sender, MouseEventArgs e)
	{
		if (_locked)
		{
			_map.Cursor = Cursors.Default;
			return;
		}
		_map.Cursor = InResizeGrip(e.Location) || _resizing ? Cursors.SizeNWSE : Cursors.Default;
		if (!_resizing) return;
		Point cur = PointToScreen(e.Location);
		int nw = Math.Max(MinimumSize.Width, _resizeStartSize.Width + (cur.X - _resizeStart.X));
		int nh = Math.Max(MinimumSize.Height, _resizeStartSize.Height + (cur.Y - _resizeStart.Y));
		Size = new Size(nw, nh);
	}

	private void MapMouseUp(object? sender, MouseEventArgs e)
	{
		if (!_resizing) return;
		_resizing = false;
		_map.Capture = false;
		PersistGeometry();
	}

	private bool InResizeGrip(Point p) =>
		p.X >= _map.Width - Grip && p.Y >= _map.Height - Grip;

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		Invalidate();
		_map.Invalidate();
	}

	protected override void OnVisibleChanged(EventArgs e)
	{
		base.OnVisibleChanged(e);
		if (Visible) _tick.Start();
		else
		{
			_tick.Stop();
			PersistGeometry();
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			Hide();
			return;
		}
		base.OnFormClosing(e);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		if (_locked) return;
		using var pen = new Pen(Color.FromArgb(130, 130, 130), 1.5f);
		int x = ClientSize.Width - 3;
		int y = ClientSize.Height - 3;
		for (int i = 0; i < 3; i++)
			e.Graphics.DrawLine(pen, x - 14 + i * 4, y, x, y - 14 + i * 4);
	}

	protected override void WndProc(ref Message m)
	{
		const int WM_NCHITTEST = 0x84;
		const int HTBOTTOMRIGHT = 17;
		if (!_locked && m.Msg == WM_NCHITTEST)
		{
			base.WndProc(ref m);
			Point p = PointToClient(Cursor.Position);
			if (p.X >= Width - Grip && p.Y >= Height - Grip)
				m.Result = (IntPtr)HTBOTTOMRIGHT;
			return;
		}
		base.WndProc(ref m);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_tick.Stop();
			_tick.Dispose();
			try { _engine.LocationUpdated -= OnLocation; } catch { }
			try { _engine.ThreatDetected -= OnThreat; } catch { }
			try { _engine.ZkbLoss -= OnZkb; } catch { }
			PersistGeometry();
		}
		base.Dispose(disposing);
	}
}