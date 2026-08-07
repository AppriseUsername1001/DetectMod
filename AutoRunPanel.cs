namespace EVEAA.Mod;

/// <summary>
/// EVE 자동실행 — EVEAA 창 안 우측 자식 패널 (테두리·이동이 메인과 하나).
/// </summary>
internal sealed class AutoRunPanel : NativeChildForm
{
	public const int WidthPx = 72;

	private readonly Label _titleLabel;
	private readonly Label _btnToggle;
	private readonly Label _statusLabel;
	private readonly ModSettings _settings;
	private bool _enabled;

	public AutoRunPanel(ModSettings settings)
	{
		_settings = settings;
		_enabled = settings.LaunchWithEve;

		BackColor = Color.FromArgb(245, 245, 245);
		ClientSize = new Size(WidthPx, 300);

		_titleLabel = new Label
		{
			AutoSize = false,
			Text = "EVE\n자동실행",
			Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
			ForeColor = Color.FromArgb(40, 40, 40),
			TextAlign = ContentAlignment.MiddleCenter,
			Size = new Size(WidthPx - 8, 40)
		};

		_btnToggle = new Label
		{
			Size = new Size(56, 32),
			Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
			Cursor = Cursors.Hand,
			TabStop = false,
			TextAlign = ContentAlignment.MiddleCenter,
			BorderStyle = BorderStyle.FixedSingle
		};
		_btnToggle.MouseDown += (_, e) =>
		{
			if (e.Button == MouseButtons.Left)
				OnToggleClick();
		};

		_statusLabel = new Label
		{
			AutoSize = false,
			Font = new Font("맑은 고딕", 7.5f),
			TextAlign = ContentAlignment.TopCenter,
			Size = new Size(WidthPx - 8, 36)
		};

		Controls.Add(_titleLabel);
		Controls.Add(_btnToggle);
		Controls.Add(_statusLabel);

		Paint += (_, e) =>
		{
			using Pen pen = new(Color.FromArgb(180, 180, 180));
			e.Graphics.DrawLine(pen, 0, 0, 0, Height);
		};

		Resize += (_, _) => LayoutControls();
		Load += (_, _) =>
		{
			ApplyToggleVisual();
			LayoutControls();
			if (_enabled)
				EveWatcher.Apply(true);
		};

		ToolTip tip = new() { ShowAlways = true };
		tip.SetToolTip(_btnToggle, "EVE 클라이언트가 켜지면 EVEAA를 자동으로 실행합니다");
		tip.SetToolTip(_titleLabel, "EVE 클라이언트가 켜지면 EVEAA를 자동으로 실행합니다");
	}

	public void Attach(IntPtr parent) => AttachAsChild(parent);

	public void LayoutInParent(int clientW, int clientH, bool force = false)
	{
		int x = Math.Max(0, clientW - WidthPx);
		PlaceInParent(x, 0, WidthPx, clientH, bringToFront: true, visible: true, force: force);
		LayoutControls();
	}

	private void OnToggleClick()
	{
		_enabled = !_enabled;
		_settings.LaunchWithEve = _enabled;
		_settings.Save();
		EveWatcher.Apply(_enabled);
		ApplyToggleVisual();
	}

	private void ApplyToggleVisual()
	{
		if (_enabled)
		{
			_btnToggle.Text = "ON";
			_btnToggle.BackColor = Color.FromArgb(46, 160, 67);
			_btnToggle.ForeColor = Color.White;
			_statusLabel.Text = "켜짐";
			_statusLabel.ForeColor = Color.FromArgb(35, 120, 50);
		}
		else
		{
			_btnToggle.Text = "OFF";
			_btnToggle.BackColor = Color.FromArgb(220, 220, 220);
			_btnToggle.ForeColor = Color.FromArgb(60, 60, 60);
			_statusLabel.Text = "꺼짐";
			_statusLabel.ForeColor = Color.DimGray;
		}
		_btnToggle.Invalidate();
		_statusLabel.Invalidate();
	}

	private void LayoutControls()
	{
		int cx = (ClientSize.Width - _btnToggle.Width) / 2;
		_titleLabel.Location = new Point(4, Math.Max(28, ClientSize.Height / 2 - 70));
		_titleLabel.Size = new Size(Math.Max(8, ClientSize.Width - 8), 40);
		_btnToggle.Location = new Point(cx, _titleLabel.Bottom + 8);
		_statusLabel.Location = new Point(4, _btnToggle.Bottom + 6);
		_statusLabel.Size = new Size(Math.Max(8, ClientSize.Width - 8), 36);
	}
}
