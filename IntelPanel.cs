using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Media;
using EVEAA.Mod.Intel;

namespace EVEAA.Mod;

/// <summary>인텔 알림 메인 패널 — 로그인 / 대시보드 1~7</summary>
internal sealed class IntelPanel : NativeChildForm
{
	private readonly ModSettings _settings;
	private readonly IntelEngine _engine = new();
	private readonly IntelReporter _reporter;
	private readonly Panel _loginPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White };
	private readonly Panel _dashPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };

	private PictureBox _portrait = null!;
	private Label _nameLabel = null!;
	private Label _locLabel = null!;
	private readonly Label[] _jumpButtons = new Label[6];
	private int _jumpSelected = 4;
	private TextBox _pathBox = null!;
	private TextBox _channelBox = null!;
	private Label _activeLogLabel = null!;
	private IntelLogView _logList = null!;
	private int _logClickIndex = -1;
	private Point _logClickPoint;
	private AlertSoundPlayer? _alertSound;
	private Button _btnSoundToggle = null!;
	private Label _soundToggleStatus = null!;
	private TrackBar _volBar = null!;
	private Label _volLabel = null!;
	private Label _soundPathLabel = null!;
	private bool _soundEnabled;
	private bool _wantVisible;
	private RegionMapOverlayForm? _mapOverlay;
	private NumericUpDown _mapZkbSec = null!;
	private NumericUpDown _mapIntelSec = null!;
	private NumericUpDown _mapFreshSec = null!;
	private Button _btnIntelReportToggle = null!;
	private Label _intelReportToggleStatus = null!;
	private bool _intelReportEnabled;
	private Panel _charListPanel = null!;

	/// <summary>점프거리 표시 대상으로 지정된 캐릭터 — 없으면 목록의 첫 캐릭터로 대체.</summary>
	private TrackedCharacter? MainCharacter =>
		_settings.IntelCharacters.FirstOrDefault(c => c.CharacterId == _settings.IntelMainCharacterId)
		?? _settings.IntelCharacters.FirstOrDefault();

	public IntelPanel(ModSettings settings)
	{
		_settings = settings;
		_reporter = new IntelReporter(_settings);
		BackColor = Color.White;
		ClientSize = new Size(640, 400);
		BuildLogin();
		BuildDashboard();
		Controls.Add(_dashPanel);
		Controls.Add(_loginPanel);

		try
		{
			_engine.LoadData();
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex);
		}

		_engine.LocationUpdated += OnLocationUpdated;
		_engine.ThreatDetected += OnThreat;
		_engine.ThreatDetected += _reporter.ReportThreat;
		_engine.Status += msg =>
		{
			if (IsDisposed || !IsHandleCreated) { Debug.WriteLine(msg); return; }
			try { BeginInvoke(() => Debug.WriteLine(msg)); } catch { }
		};
		_engine.ActiveLogChanged += path =>
		{
			if (IsDisposed) return;
			if (!IsHandleCreated) { UpdateActiveLogLabel(path); return; }
			try { BeginInvoke(() => UpdateActiveLogLabel(path)); } catch { }
		};

		LoadAlertSound();
		RestoreSession();
	}



	public IntelEngine Engine => _engine;

	public void Attach(IntPtr parent) => AttachAsChild(parent);

	public void LayoutInParent(int x, int y, int w, int h, bool force = false)
	{
		// 경보기 탭일 때 Layout이 다시 Show 하지 않도록 _wantVisible 준수
		bool changed = force || !IsHandleCreated || Bounds.X != x || Bounds.Y != y || Bounds.Width != w || Bounds.Height != h;
		PlaceInParent(x, y, w, h, bringToFront: _wantVisible, visible: _wantVisible, force: force);
		if (!_wantVisible) return;
		if (!changed && !force) return;
		_loginPanel.Bounds = ClientRectangle;
		_dashPanel.Bounds = ClientRectangle;
		PerformLayout();
		if (force)
			try { _logList?.ForceRepaint(); } catch { }
	}

	public void SetVisible(bool visible)
	{
		_wantVisible = visible;
		if (!IsHandleCreated) return;
		if (visible)
		{
			// 이미 정상 부착·표시 중이면 재부착 생략 — SetParent/SWP_FRAMECHANGED를 불필요하게
			// 반복하면 원본 창의 형제 컨트롤 리페인트가 깨질 수 있다.
			if (!IsShownInHost)
				ReparentToHost();
			if (MainCharacter is not null)
				_engine.Start();
			try { _logList?.ForceRepaint(); } catch { }
		}
		else
		{
			// 엔진/ZKB는 하단에서 계속 — 인텔 패널은 HWND_MESSAGE로 분리해 완전 제거
			HideInParent();
		}
	}

	private void RestoreSession()
	{
		if (string.IsNullOrWhiteSpace(_settings.ChatlogsDir))
			_settings.ChatlogsDir = ChatlogWatcher.DefaultChatlogsDir();
		_pathBox.Text = _settings.ChatlogsDir;
		_engine.ChatlogsDir = _settings.ChatlogsDir;
		_engine.JumpRange = _settings.JumpRange > 0 ? _settings.JumpRange : 4;
		SelectJump(_engine.JumpRange);
		_channelBox.Text = _settings.IntelChannel ?? "";
		_engine.ChannelName = _channelBox.Text.Trim();
		_soundEnabled = _settings.AlertSoundEnabled;
		if (_btnSoundToggle is not null)
			ApplySoundToggleVisual();
		ApplyVolumeUi();
		UpdateSoundPathLabel();
		{
			string? hit = string.IsNullOrWhiteSpace(_engine.ChannelName)
				? null
				: ChatlogWatcher.FindClosestLogToNow(_engine.ChatlogsDir, _engine.ChannelName);
			UpdateActiveLogLabel(hit);
			_engine.PinChatLog(hit);
		}

		var main = MainCharacter;
		if (main is not null)
		{
			main.ExpiresAt = DateTimeOffset.UtcNow; // force refresh
			_engine.SetCharacter(main);
			ShowDashboard();
			RebuildCharacterList();
			MaybeAskIntelReportConsent();
			_ = BootstrapAsync();
		}
		else
		{
			ShowLogin();
		}
	}

	private async Task BootstrapAsync()
	{
		// 위치 실패해도 채팅 감시는 반드시 시작 (이전엔 여기서 막혀 알림 전체가 죽었어)
		try
		{
			await _engine.RefreshLocationAsync();
			UpdateDashboardUi();
		}
		catch (Exception ex)
		{
			_locLabel.Text = "위치: (갱신 실패 — 재로그인)";
			Debug.WriteLine(ex);
		}
		_engine.Start();
		string? hit = string.IsNullOrWhiteSpace(_engine.ChannelName)
			? null
			: ChatlogWatcher.FindClosestLogToNow(_engine.ChatlogsDir, _engine.ChannelName);
		UpdateActiveLogLabel(hit);
		_engine.PinChatLog(hit);
	}

	private void ShowLogin()
	{
		_loginPanel.Visible = true;
		_loginPanel.BringToFront();
		_dashPanel.Visible = false;
		_engine.Stop();
	}

	private void ShowDashboard()
	{
		_dashPanel.Visible = true;
		_dashPanel.BringToFront();
		_loginPanel.Visible = false;
		UpdateDashboardUi();
	}

	private void BuildLogin()
	{
		var left = new Panel
		{
			Dock = DockStyle.Left,
			Width = 280,
			BackColor = Color.FromArgb(248, 248, 248),
			Padding = new Padding(16)
		};
		var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };

		var title = new Label
		{
			Text = "EVE 로그인",
			Font = new Font("맑은 고딕", 12f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(16, 24)
		};
		var btn = new Button
		{
			Text = "EVE SSO로 로그인",
			Size = new Size(220, 40),
			Location = new Point(16, 70),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.FromArgb(60, 120, 200),
			ForeColor = Color.White,
			Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
			Cursor = Cursors.Hand
		};
		btn.FlatAppearance.BorderSize = 0;
		btn.Click += async (_, _) => await DoLoginAsync();
		left.Controls.Add(title);
		left.Controls.Add(btn);

		var hint = new Label
		{
			Dock = DockStyle.Fill,
			Font = new Font("맑은 고딕", 10f),
			ForeColor = Color.FromArgb(50, 50, 50),
			Text =
				"감시 대상이 될 캐릭터로 로그인해 주세요.\n\n" +
				"로그인 시 캐릭터의 실시간 위치 정보를 읽기 위한 권한\n" +
				"(esi-location)을 허용해야 합니다.\n\n" +
				"위치는 약 2초마다, 인텔 채널 로그는 약 1.5초마다\n" +
				"갱신되며, 설정한 점프 범위 내 위협을 알려줍니다."
		};
		right.Controls.Add(hint);
		_loginPanel.Controls.Add(right);
		_loginPanel.Controls.Add(left);
	}

	private void BuildDashboard()
	{
		// left column: portrait / name+loc / logout — each in black rectangle
		var leftCol = new Panel { Dock = DockStyle.Left, Width = 148, Padding = new Padding(6), BackColor = Color.White };

		static Panel BoxFrame(int x, int y, int w, int h, out Panel inner)
		{
			var outer = new Panel
			{
				Location = new Point(x, y),
				Size = new Size(w, h),
				BackColor = Color.Black,
				Padding = new Padding(2)
			};
			inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
			outer.Controls.Add(inner);
			return outer;
		}

		// 인텔 상단 높이(~ZKB 제외) 안에 들어가도록 좌측을 압축
		var portraitFrame = BoxFrame(8, 6, 132, 100, out var portraitInner);
		_portrait = new PictureBox
		{
			Dock = DockStyle.Fill,
			SizeMode = PictureBoxSizeMode.Zoom,
			BorderStyle = BorderStyle.None,
			BackColor = Color.Gainsboro
		};
		portraitInner.Controls.Add(_portrait);

		var nameFrame = BoxFrame(8, 110, 132, 52, out var nameInner);
		_nameLabel = new Label
		{
			Dock = DockStyle.Top,
			Height = 22,
			Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
			TextAlign = ContentAlignment.MiddleCenter,
			Text = "-"
		};
		_locLabel = new Label
		{
			Dock = DockStyle.Fill,
			Font = new Font("맑은 고딕", 8f),
			TextAlign = ContentAlignment.TopCenter,
			ForeColor = Color.FromArgb(40, 40, 40),
			Padding = new Padding(2, 0, 2, 0),
			Text = "위치: -"
		};
		nameInner.Controls.Add(_locLabel);
		nameInner.Controls.Add(_nameLabel);

		// 폭 충분한 단일 버튼 — 좁은 칸에 넣으면 "로그아"처럼 잘림
		var logoutFrame = BoxFrame(8, 166, 132, 30, out var logoutInner);
		var logout = new Button
		{
			Text = "로그아웃",
			Dock = DockStyle.Fill,
			FlatStyle = FlatStyle.Flat,
			Margin = Padding.Empty,
			Font = new Font("맑은 고딕", 8.5f),
			TabStop = false
		};
		logout.FlatAppearance.BorderSize = 0;
		logout.Click += (_, _) =>
		{
			var main = MainCharacter;
			if (main is not null) RemoveCharacter(main);
		};
		logoutInner.Padding = new Padding(4, 2, 4, 2);
		logoutInner.Controls.Add(logout);

		var mapOpenFrame = BoxFrame(8, 200, 132, 30, out var mapOpenInner);
		var mapOpenBtn = new Button
		{
			Text = "리전 지도",
			Dock = DockStyle.Fill,
			FlatStyle = FlatStyle.Flat,
			Margin = Padding.Empty,
			Cursor = Cursors.Hand,
			Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
			BackColor = Color.FromArgb(235, 245, 255),
			TabStop = false
		};
		mapOpenBtn.FlatAppearance.BorderSize = 0;
		mapOpenBtn.MouseDown += (_, e) =>
		{
			if (e.Button == MouseButtons.Left)
				OpenMapOverlay();
		};
		mapOpenInner.Padding = new Padding(4, 2, 4, 2);
		mapOpenInner.Controls.Add(mapOpenBtn);

		var mapSetFrame = BoxFrame(8, 234, 132, 30, out var mapSetInner);
		var mapSetBtn = new Button
		{
			Text = "지도 설정…",
			Dock = DockStyle.Fill,
			FlatStyle = FlatStyle.Flat,
			Margin = Padding.Empty,
			Cursor = Cursors.Hand,
			Font = new Font("맑은 고딕", 8.5f),
			TabStop = false
		};
		mapSetBtn.FlatAppearance.BorderSize = 0;
		mapSetBtn.MouseDown += (_, e) =>
		{
			if (e.Button == MouseButtons.Left)
				OpenMapSettingsDialog();
		};
		mapSetInner.Padding = new Padding(4, 2, 4, 2);
		mapSetInner.Controls.Add(mapSetBtn);

		leftCol.Controls.Add(portraitFrame);
		leftCol.Controls.Add(nameFrame);
		leftCol.Controls.Add(logoutFrame);
		leftCol.Controls.Add(mapOpenFrame);
		leftCol.Controls.Add(mapSetFrame);

		// right column: 5 jump, 6 path/channel, 7 log
		var rightCol = new Panel
		{
			Dock = DockStyle.Right,
			Width = 250,
			Padding = new Padding(4),
			AutoScroll = true // 창이 낮아도 지도초/테스트가 잘리지 않게
		};
		rightCol.MouseDown += (_, _) => Focus();

		var lblJump = new Label { Text = "점프 범위", Location = new Point(4, 6), AutoSize = true };
		int jumpY = 26;
		for (int i = 0; i < 6; i++)
		{
			int value = i + 1;
			var cell = new Label
			{
				Text = value.ToString(),
				Location = new Point(4 + i * 36, jumpY),
				Size = new Size(34, 26),
				TextAlign = ContentAlignment.MiddleCenter,
				BorderStyle = BorderStyle.FixedSingle,
				Cursor = Cursors.Hand,
				Tag = value,
				TabStop = false
			};
			// Button 눌림 잔상 대신 Label + MouseDown (1클릭 즉시 반영)
			cell.MouseDown += (_, e) =>
			{
				if (e.Button == MouseButtons.Left)
					SelectJump(value);
			};
			_jumpButtons[i] = cell;
			rightCol.Controls.Add(cell);
		}
		SelectJump(_settings.JumpRange > 0 ? _settings.JumpRange : 4);

		var lblPath = new Label { Text = "Chatlogs 폴더", Location = new Point(4, 58), AutoSize = true };
		_pathBox = new TextBox
		{
			Location = new Point(4, 78),
			Width = 180,
			ReadOnly = true,
			TabStop = false
		};
		var browse = new Button { Text = "...", Location = new Point(188, 76), Size = new Size(32, 24), TabStop = false };
		browse.Click += (_, _) =>
		{
			using var dlg = new FolderBrowserDialog
			{
				Description = "EVE Chatlogs 폴더 선택",
				SelectedPath = _pathBox.Text
			};
			if (dlg.ShowDialog(this) == DialogResult.OK)
			{
				_pathBox.Text = dlg.SelectedPath;
				_settings.ChatlogsDir = dlg.SelectedPath;
				_engine.ChatlogsDir = dlg.SelectedPath;
				_settings.Save();
			}
		};

		var lblChan = new Label { Text = "감시 채널 이름", Location = new Point(4, 110), AutoSize = true };
		_channelBox = new TextBox
		{
			Location = new Point(4, 130),
			Width = 174,
			ReadOnly = true,
			TabStop = false,
			Cursor = Cursors.Hand,
			BackColor = Color.White
		};
		var editChan = new Button
		{
			Text = "입력",
			Location = new Point(186, 128),
			Size = new Size(48, 24),
			FlatStyle = FlatStyle.Flat,
			TabStop = false
		};
		void ApplyChannelText(string ch)
		{
			ch = (ch ?? "").Trim();
			_channelBox.Text = ch;
			_settings.IntelChannel = ch;
			_engine.ChannelName = ch;
			_settings.Save();
			string? hit = string.IsNullOrEmpty(ch)
				? null
				: ChatlogWatcher.FindClosestLogToNow(_engine.ChatlogsDir, ch);
			UpdateActiveLogLabel(hit);
			_engine.PinChatLog(hit);
		}
		void OpenChannelEditor()
		{
			using var dlg = new Form
			{
				Text = "감시 채널 이름",
				FormBorderStyle = FormBorderStyle.FixedDialog,
				StartPosition = FormStartPosition.CenterScreen,
				ClientSize = new Size(360, 110),
				MaximizeBox = false,
				MinimizeBox = false,
				ShowInTaskbar = false,
				TopMost = true
			};
			var tip = new Label
			{
				Text = "채널 로그 파일명 앞부분 (예: delv.imperium)",
				Location = new Point(12, 10),
				AutoSize = true
			};
			var tb = new TextBox
			{
				Location = new Point(12, 36),
				Width = 336,
				Text = _channelBox.Text
			};
			var ok = new Button
			{
				Text = "확인",
				DialogResult = DialogResult.OK,
				Location = new Point(192, 70),
				Size = new Size(75, 28)
			};
			var cancel = new Button
			{
				Text = "취소",
				DialogResult = DialogResult.Cancel,
				Location = new Point(273, 70),
				Size = new Size(75, 28)
			};
			dlg.Controls.Add(tip);
			dlg.Controls.Add(tb);
			dlg.Controls.Add(ok);
			dlg.Controls.Add(cancel);
			dlg.AcceptButton = ok;
			dlg.CancelButton = cancel;
			dlg.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
			if (dlg.ShowDialog() == DialogResult.OK)
				ApplyChannelText(tb.Text);
		}
		_channelBox.Click += (_, _) => OpenChannelEditor();
		editChan.Click += (_, _) => OpenChannelEditor();

		_activeLogLabel = new Label
		{
			Text = "감시 파일: (채널 이름 입력)",
			Location = new Point(4, 156),
			Size = new Size(236, 28),
			ForeColor = Color.FromArgb(80, 80, 80),
			Font = new Font("맑은 고딕", 7.5f)
		};

		_soundEnabled = _settings.AlertSoundEnabled;
		var lblSound = new Label
		{
			Text = "알림음",
			Location = new Point(4, 186),
			Size = new Size(42, 22),
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(50, 50, 50),
			Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold)
		};
		_btnSoundToggle = new Button
		{
			Location = new Point(46, 184),
			Size = new Size(44, 26),
			Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
			Cursor = Cursors.Hand,
			TabStop = false,
			FlatStyle = FlatStyle.Flat,
			UseVisualStyleBackColor = false
		};
		_btnSoundToggle.FlatAppearance.BorderSize = 1;
		_btnSoundToggle.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
		_btnSoundToggle.MouseDown += (_, e) =>
		{
			if (e.Button != MouseButtons.Left) return;
			ToggleAlertSound();
		};
		_soundToggleStatus = new Label
		{
			Location = new Point(92, 186),
			Size = new Size(28, 22),
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(80, 80, 80)
		};
		var btnSound = new Button
		{
			Text = "음원",
			Location = new Point(120, 184),
			Size = new Size(40, 26),
			TabStop = false
		};
		btnSound.Click += (_, _) => BrowseAlertSound();
		var btnTestSound = new Button
		{
			Text = "테스트",
			Location = new Point(162, 184),
			Size = new Size(58, 26),
			TabStop = false
		};
		btnTestSound.Click += (_, _) => PlayAlertSound(force: true);
		ApplySoundToggleVisual();

		_volLabel = new Label
		{
			Text = "크기",
			Location = new Point(4, 214),
			Size = new Size(32, 20),
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(60, 60, 60),
			Font = new Font("맑은 고딕", 8f)
		};
		_volBar = new TrackBar
		{
			Location = new Point(36, 210),
			Size = new Size(140, 26),
			Minimum = 0,
			Maximum = 100,
			TickFrequency = 10,
			SmallChange = 5,
			LargeChange = 10,
			AutoSize = false,
			TickStyle = TickStyle.None,
			TabStop = false
		};
		var volValue = new Label
		{
			Name = "volValue",
			Location = new Point(178, 214),
			Size = new Size(40, 20),
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(80, 80, 80),
			Font = new Font("맑은 고딕", 8f)
		};
		_volBar.ValueChanged += (_, _) =>
		{
			int v = _volBar.Value;
			_settings.AlertSoundVolume = v;
			volValue.Text = v + "%";
		};
		_volBar.MouseUp += (_, _) =>
		{
			_settings.Save();
			_alertSound?.SetVolume(_volBar.Value);
		};
		_volBar.KeyUp += (_, _) =>
		{
			_settings.Save();
			_alertSound?.SetVolume(_volBar.Value);
		};

		_soundPathLabel = new Label
		{
			Location = new Point(4, 236),
			Size = new Size(230, 16),
			ForeColor = Color.FromArgb(90, 90, 90),
			Font = new Font("맑은 고딕", 7f)
		};
		UpdateSoundPathLabel();
		ApplyVolumeUi();
		volValue.Text = _volBar.Value + "%";

		// 한 줄: ZKB / 인텔 / 강조 (초) — 세로 공간 절약
		var lblMapSec = new Label
		{
			Text = "지도(초)",
			Location = new Point(4, 252),
			AutoSize = true,
			Font = new Font("맑은 고딕", 7.5f, FontStyle.Bold),
			ForeColor = Color.FromArgb(40, 40, 40)
		};
		var lblZ = new Label { Text = "Z", Location = new Point(4, 272), AutoSize = true, Font = new Font("맑은 고딕", 7f) };
		_mapZkbSec = MakeMapSecSpin(18, 268, _settings.MapZkbDisplaySec, 5, 3600, v =>
		{
			_settings.MapZkbDisplaySec = v;
			_settings.Save();
		});
		var lblI = new Label { Text = "인", Location = new Point(78, 272), AutoSize = true, Font = new Font("맑은 고딕", 7f) };
		_mapIntelSec = MakeMapSecSpin(94, 268, _settings.MapIntelDisplaySec, 5, 7200, v =>
		{
			_settings.MapIntelDisplaySec = v;
			_settings.Save();
		});
		var lblF = new Label { Text = "강", Location = new Point(154, 272), AutoSize = true, Font = new Font("맑은 고딕", 7f) };
		_mapFreshSec = MakeMapSecSpin(172, 268, _settings.MapIntelHighlightSec, 1, 3600, v =>
		{
			_settings.MapIntelHighlightSec = v;
			_settings.Save();
		});

		// 인텔 서버 전송 (Intel Surveillance Program 리포터) — 알림음 토글과 동일 패턴
		_intelReportEnabled = _settings.IntelReportEnabled;
		var lblIntelReport = new Label
		{
			Text = "인텔전송",
			Location = new Point(4, 300),
			Size = new Size(52, 22),
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(50, 50, 50),
			Font = new Font("맑은 고딕", 8f, FontStyle.Bold)
		};
		_btnIntelReportToggle = new Button
		{
			Location = new Point(56, 298),
			Size = new Size(44, 26),
			Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
			Cursor = Cursors.Hand,
			TabStop = false,
			FlatStyle = FlatStyle.Flat,
			UseVisualStyleBackColor = false
		};
		_btnIntelReportToggle.FlatAppearance.BorderSize = 1;
		_btnIntelReportToggle.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
		_btnIntelReportToggle.MouseDown += (_, e) =>
		{
			if (e.Button != MouseButtons.Left) return;
			ToggleIntelReport();
		};
		_intelReportToggleStatus = new Label
		{
			Location = new Point(102, 300),
			Size = new Size(120, 22),
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(80, 80, 80)
		};
		ApplyIntelReportToggleVisual();

		// 로그인된 캐릭터 목록 — 클릭한 캐릭터가 인텔 로그의 점프거리 표시 대상("메인")이 된다.
		var lblChars = new Label
		{
			Text = "캐릭터 (클릭: 점프거리 표시 대상 지정)",
			Location = new Point(4, 336),
			AutoSize = true,
			Font = new Font("맑은 고딕", 7.5f, FontStyle.Bold),
			ForeColor = Color.FromArgb(40, 40, 40)
		};
		_charListPanel = new Panel
		{
			Location = new Point(4, 356),
			Size = new Size(220, 1)
		};
		rightCol.Controls.Add(lblChars);
		rightCol.Controls.Add(_charListPanel);
		RebuildCharacterList();

		rightCol.Controls.Add(lblJump);
		rightCol.Controls.Add(lblPath);
		rightCol.Controls.Add(_pathBox);
		rightCol.Controls.Add(browse);
		rightCol.Controls.Add(lblChan);
		rightCol.Controls.Add(_channelBox);
		rightCol.Controls.Add(editChan);
		rightCol.Controls.Add(_activeLogLabel);
		rightCol.Controls.Add(lblSound);
		rightCol.Controls.Add(_soundToggleStatus);
		rightCol.Controls.Add(btnSound);
		rightCol.Controls.Add(btnTestSound);
		rightCol.Controls.Add(_volLabel);
		rightCol.Controls.Add(_volBar);
		rightCol.Controls.Add(volValue);
		rightCol.Controls.Add(_soundPathLabel);
		rightCol.Controls.Add(lblMapSec);
		rightCol.Controls.Add(_mapZkbSec);
		rightCol.Controls.Add(lblZ);
		rightCol.Controls.Add(_mapIntelSec);
		rightCol.Controls.Add(lblI);
		rightCol.Controls.Add(_mapFreshSec);
		rightCol.Controls.Add(lblF);
		rightCol.Controls.Add(lblIntelReport);
		rightCol.Controls.Add(_btnIntelReportToggle);
		rightCol.Controls.Add(_intelReportToggleStatus);
		rightCol.Controls.Add(_btnSoundToggle);
		_btnSoundToggle.BringToFront();
		_btnIntelReportToggle.BringToFront();

		// 중앙: 인텔 로그 (ZKB는 창 하단 별도 패널)
		var centerCol = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6) };
		var logFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(200, 200, 200), Padding = new Padding(1) };
		var logInner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6) };
		var lblLog = new Label
		{
			Text = "인텔 로그  (채널 전체 · 더블클릭: 킬/캐릭터 zKill)",
			Dock = DockStyle.Top,
			Height = 22,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(50, 50, 50)
		};
		_logList = new IntelLogView
		{
			Dock = DockStyle.Fill,
			BackColor = Color.White,
			Font = new Font("맑은 고딕", 9.5f)
		};
		_logList.ItemDoubleClicked += (idx, pt) =>
		{
			_logClickIndex = idx;
			_logClickPoint = pt;
			OpenZkillAt(idx);
		};
		logInner.Controls.Add(_logList);
		logInner.Controls.Add(lblLog);
		logFrame.Controls.Add(logInner);
		centerCol.Controls.Add(logFrame);

		_dashPanel.Controls.Add(centerCol);
		_dashPanel.Controls.Add(rightCol);
		_dashPanel.Controls.Add(leftCol);
	}

	private async Task DoLoginAsync()
	{
		try
		{
			await _engine.LoginAsync();
			var c = _engine.Character;
			if (c is null) return;

			_settings.IntelCharacters.RemoveAll(x => x.CharacterId == c.CharacterId);
			_settings.IntelCharacters.Add(c);
			bool isFirst = _settings.IntelCharacters.Count == 1;
			if (isFirst) _settings.IntelMainCharacterId = c.CharacterId;
			_settings.Save();

			ShowDashboard();
			MaybeAskIntelReportConsent();

			if (!isFirst)
			{
				// 방금 로그인한 캐릭터가 메인이 아니면, 엔진은 계속 기존 메인 캐릭터를 추적한다 —
				// "캐릭터 추가"는 로그인만 하는 것이지 자동으로 점프거리 표시 대상이 되는 게 아니다.
				var main = MainCharacter;
				if (main is not null) _engine.SetCharacter(main);
			}
			RebuildCharacterList();
			_engine.Start();
		}
		catch (Exception ex)
		{
			MessageBox.Show("로그인 실패: " + ex.Message, "인텔 알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	private void OnLocationUpdated(TrackedCharacter c)
	{
		if (IsDisposed || !IsHandleCreated) return;
		try
		{
			BeginInvoke(() =>
			{
				if (_nameLabel.Text != c.CharacterName)
					_nameLabel.Text = c.CharacterName;
				string loc = string.IsNullOrEmpty(c.LocationSystem) ? "-" : c.LocationSystem;
				var reg = _engine.Maps.GetRegionForSystem(c.LocationSystem ?? "");
				string locText = reg is null ? "위치: " + loc : $"위치: {loc} ({reg.Name.Replace('_', ' ')})";
				if (_locLabel.Text != locText)
					_locLabel.Text = locText;
				string url = $"https://images.evetech.net/characters/{c.CharacterId}/portrait?size=128";
				if (!string.Equals(_portrait.ImageLocation, url, StringComparison.Ordinal))
				{
					try { _portrait.LoadAsync(url); } catch { }
				}
			});
		}
		catch { }
	}

	private void OnThreat(IntelThreatEvent ev)
	{
		if (IsDisposed) return;
		void Apply()
		{
			if (IsDisposed || _logList is null) return;
			_logList.InsertTop(ev);
			if (ev.IsAlert)
				PlayAlertSound(force: false);
		}
		try
		{
			if (!IsHandleCreated)
			{
				// 핸들 전이라도 소리만은 울릴 수 있음 — 로그는 핸들 후 반영
				if (ev.IsAlert) PlayAlertSound(force: false);
				return;
			}
			if (InvokeRequired) BeginInvoke(Apply);
			else Apply();
		}
		catch { }
	}


	private void UpdateDashboardUi()
	{
		var c = _engine.Character;
		if (c is null) return;
		_nameLabel.Text = c.CharacterName;
		string loc = string.IsNullOrEmpty(c.LocationSystem) ? "-" : c.LocationSystem;
		var reg = _engine.Maps.GetRegionForSystem(c.LocationSystem ?? "");
		_locLabel.Text = reg is null ? "위치: " + loc : $"위치: {loc} ({reg.Name.Replace('_', ' ')})";
		try { _portrait.LoadAsync($"https://images.evetech.net/characters/{c.CharacterId}/portrait?size=128"); } catch { }
	}

	private static NumericUpDown MakeMapSecSpin(int x, int y, int value, int min, int max, Action<int> onChange)
	{
		var n = new NumericUpDown
		{
			Location = new Point(x, y),
			Size = new Size(52, 22),
			Minimum = min,
			Maximum = max,
			Value = Math.Clamp(value, min, max),
			TabStop = false,
			Font = new Font("맑은 고딕", 7.5f)
		};
		n.ValueChanged += (_, _) => onChange((int)n.Value);
		return n;
	}

	private void SyncMapSecSpinners()
	{
		void Set(NumericUpDown? n, int v)
		{
			if (n is null) return;
			int clamped = (int)Math.Clamp(v, n.Minimum, n.Maximum);
			if ((int)n.Value != clamped) n.Value = clamped;
		}
		Set(_mapZkbSec, _settings.MapZkbDisplaySec);
		Set(_mapIntelSec, _settings.MapIntelDisplaySec);
		Set(_mapFreshSec, _settings.MapIntelHighlightSec);
	}

	private void OpenMapSettingsDialog()
	{
		using var dlg = new Form
		{
			Text = "지도 설정",
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			ClientSize = new Size(380, 200),
			MaximizeBox = false,
			MinimizeBox = false,
			ShowInTaskbar = false,
			TopMost = true
		};
		static NumericUpDown Spin(int x, int y, int val, int min, int max)
		{
			return new NumericUpDown
			{
				Location = new Point(x, y),
				Size = new Size(70, 24),
				Minimum = min,
				Maximum = max,
				Value = Math.Clamp(val, min, max)
			};
		}
		var tip = new Label
		{
			Text = "오버레이 지도에 성계가 표시되는 시간",
			Location = new Point(16, 10),
			AutoSize = true,
			ForeColor = Color.FromArgb(70, 70, 70)
		};
		var zkb = Spin(16, 40, _settings.MapZkbDisplaySec, 5, 3600);
		var intel = Spin(16, 76, _settings.MapIntelDisplaySec, 5, 7200);
		var fresh = Spin(16, 112, _settings.MapIntelHighlightSec, 1, 3600);
		dlg.Controls.Add(tip);
		dlg.Controls.Add(zkb);
		dlg.Controls.Add(intel);
		dlg.Controls.Add(fresh);
		dlg.Controls.Add(new Label { Text = "초 — ZKB 킬 성계 표시 (기본 30)", Location = new Point(96, 42), AutoSize = true });
		dlg.Controls.Add(new Label { Text = "초 — 인텔 로그 성계 표시 (기본 120)", Location = new Point(96, 78), AutoSize = true });
		dlg.Controls.Add(new Label { Text = "초 — 새 인텔 강조 유지 (기본 30)", Location = new Point(96, 114), AutoSize = true });
		var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(200, 156), Size = new Size(75, 28) };
		var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(281, 156), Size = new Size(75, 28) };
		dlg.Controls.Add(ok);
		dlg.Controls.Add(cancel);
		dlg.AcceptButton = ok;
		dlg.CancelButton = cancel;
		if (dlg.ShowDialog() != DialogResult.OK) return;
		_settings.MapZkbDisplaySec = (int)zkb.Value;
		_settings.MapIntelDisplaySec = (int)intel.Value;
		_settings.MapIntelHighlightSec = (int)fresh.Value;
		_settings.Save();
		SyncMapSecSpinners();
	}

	private void OpenMapOverlay()
	{
		if (_engine.Character is null)
		{
			MessageBox.Show("먼저 감시 캐릭터로 로그인해 주세요.", "리전 지도",
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		try
		{
			if (!_engine.MapsReady)
			{
				try { _engine.LoadData(); }
				catch (Exception ex)
				{
					MessageBox.Show(
						"지도 데이터 로드 실패:\n" + ex.Message +
						"\n\nEVEAA_mod.exe 옆에 eveaa_mod_data 폴더(Systems.dat, MapLayout.dat)가 있는지 확인하세요.",
						"리전 지도", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
			}
			if (_mapOverlay is null || _mapOverlay.IsDisposed)
				_mapOverlay = new RegionMapOverlayForm(_settings, _engine, OpenMapSettingsDialog);
			_mapOverlay.ShowOverlay();
		}
		catch (Exception ex)
		{
			MessageBox.Show("지도 오버레이 오류: " + ex.Message, "리전 지도",
				MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	private void SelectJump(int value)
	{
		if (value < 1) value = 1;
		if (value > 6) value = 6;
		_jumpSelected = value;
		_engine.JumpRange = value;
		_settings.JumpRange = value;
		_settings.Save();
		for (int i = 0; i < _jumpButtons.Length; i++)
		{
			var b = _jumpButtons[i];
			if (b is null) continue;
			bool on = (i + 1) == value;
			b.SuspendLayout();
			b.BackColor = on ? Color.Black : Color.White;
			b.ForeColor = on ? Color.White : Color.Black;
			b.ResumeLayout(false);
			b.Invalidate();
			b.Update();
		}
	}

	private void UpdateActiveLogLabel(string? path)
	{
		if (_activeLogLabel is null) return;
		string next;
		if (string.IsNullOrWhiteSpace(_engine.ChannelName))
			next = "감시 파일: (채널 이름 입력)";
		else if (string.IsNullOrEmpty(path))
			next = "감시 파일: 일치하는 로그 없음";
		else
			next = "감시 파일: " + Path.GetFileName(path);
		if (_activeLogLabel.Text != next)
			_activeLogLabel.Text = next;
	}

	private void OpenZkillAt(int index)
	{
		if (index < 0 || index >= _logList.Count) return;
		var ev = _logList[index];
		if (ev is null) return;

		// 클릭한 정확한 이름 조각(예: "Bastilia, hubitus1"에서 실제로 클릭한 쪽)을 우선 사용 —
		// ev.Character를 통째로 쓰면 NormalizeCharacterName이 콤마 앞부분만 남겨서 항상
		// 첫 번째 캐릭터로만 연결되던 문제가 있었다.
		string? clickedCharacterChunk = _logList.HitTestCharacterText(index, _logClickPoint);
		bool onCharacter = clickedCharacterChunk != null;
		string charName = ZkillLinkHelper.NormalizeCharacterName(clickedCharacterChunk ?? ev.Character);
		if (string.IsNullOrWhiteSpace(charName) || charName == "-")
			charName = ZkillLinkHelper.NormalizeCharacterName(ev.Speaker);

		_ = OpenZkillAsync(ev, onCharacter, charName);
	}

	private async Task OpenZkillAsync(IntelThreatEvent ev, bool onCharacter, string charName)
	{
		try
		{
			string url;
			if (onCharacter && !string.IsNullOrWhiteSpace(charName))
			{
				// 캐릭터 이름 더블클릭 → 해당 캐릭터 zKill 페이지/검색
				url = await ZkillLinkHelper.ResolveCharacterUrlAsync(charName);
			}
			else if (ev.IsKillReport)
			{
				// 킬 채팅 더블클릭 → 관련 killmail URL
				string victim = string.IsNullOrWhiteSpace(charName) ? ev.Character : charName;
				url = await ZkillLinkHelper.ResolveKillRelatedUrlAsync(victim, ev.Ship);
			}
			else if (!string.IsNullOrWhiteSpace(charName))
			{
				url = await ZkillLinkHelper.ResolveCharacterUrlAsync(charName);
			}
			else
			{
				return;
			}

			if (IsDisposed) return;
			Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
		}
		catch { }
	}

	private static string DefaultAlertSoundPath()
	{
		string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
		string besideExe = Path.Combine(baseDir, "sound", "pop.wav");
		if (File.Exists(besideExe))
			return besideExe;
		// exe 하나만 배포된 경우: 첫 실행 때 내장 리소스에서 풀어놓은 사본 (BundledAssets 참고)
		return Path.Combine(BundledAssets.ExtractedRoot, "sound", "pop.wav");
	}

	private void ToggleAlertSound()
	{
		_soundEnabled = !_soundEnabled;
		_settings.AlertSoundEnabled = _soundEnabled;
		ApplySoundToggleVisual();
		_settings.Save();
	}

	private void ApplySoundToggleVisual()
	{
		if (_btnSoundToggle is null) return;
		if (_soundEnabled)
		{
			_btnSoundToggle.Text = "ON";
			_btnSoundToggle.BackColor = Color.FromArgb(40, 160, 70);
			_btnSoundToggle.ForeColor = Color.White;
			if (_soundToggleStatus is not null) _soundToggleStatus.Text = "켜짐";
		}
		else
		{
			_btnSoundToggle.Text = "OFF";
			_btnSoundToggle.BackColor = Color.FromArgb(160, 60, 60);
			_btnSoundToggle.ForeColor = Color.White;
			if (_soundToggleStatus is not null) _soundToggleStatus.Text = "꺼짐";
		}
		_btnSoundToggle.Invalidate();
	}

	/// <summary>인텔 로그인 성공 직후(최초 1회만) 서버 전송 동의를 물어본다. 이미 물어본 적
	/// 있으면(수락/거절 여부와 무관) 아무 것도 하지 않는다 — 이후엔 대시보드의 "인텔전송"
	/// 토글로 언제든 직접 켜고 끌 수 있다.</summary>
	/// <summary>클릭한 캐릭터를 점프거리 표시/위치 추적 대상("메인")으로 지정한다.
	/// 알림(경고음)은 이후 단계에서 캐릭터별로 각자 독립 처리할 예정 — 지금은 메인 캐릭터
	/// 기준으로 위치·점프거리만 갱신한다.</summary>
	private void SelectMainCharacter(TrackedCharacter c)
	{
		_settings.IntelMainCharacterId = c.CharacterId;
		_settings.Save();
		c.ExpiresAt = DateTimeOffset.UtcNow; // 즉시 새로 갱신되도록
		_engine.SetCharacter(c);
		UpdateDashboardUi();
		RebuildCharacterList();
		_ = BootstrapAsync();
	}

	/// <summary>목록에서 캐릭터를 제거(로그아웃)한다. 제거 대상이 메인이었다면 남은 캐릭터 중
	/// 하나로 메인을 자동 교체하고, 아무도 안 남으면 로그인 화면으로 돌아간다.</summary>
	private void RemoveCharacter(TrackedCharacter c)
	{
		_settings.IntelCharacters.RemoveAll(x => x.CharacterId == c.CharacterId);
		if (_settings.IntelMainCharacterId == c.CharacterId)
			_settings.IntelMainCharacterId = _settings.IntelCharacters.FirstOrDefault()?.CharacterId ?? 0;
		_settings.Save();

		var newMain = MainCharacter;
		if (newMain is null)
		{
			_engine.Stop();
			_engine.SetCharacter(null);
			ShowLogin();
		}
		else
		{
			SelectMainCharacter(newMain);
		}
	}

	/// <summary>우측 패널 하단의 캐릭터 목록을 현재 _settings.IntelCharacters로 다시 그린다 —
	/// 메인 캐릭터는 초록 테두리로, 그 외는 회색 테두리로 표시한다.</summary>
	private void RebuildCharacterList()
	{
		if (_charListPanel is null) return;
		_charListPanel.SuspendLayout();
		foreach (Control old in _charListPanel.Controls) old.Dispose();
		_charListPanel.Controls.Clear();

		int y = 0;
		int mainId = MainCharacter?.CharacterId ?? 0;
		foreach (var c in _settings.IntelCharacters)
		{
			var row = BuildCharacterRow(c, c.CharacterId == mainId);
			row.Location = new Point(0, y);
			_charListPanel.Controls.Add(row);
			y += row.Height + 4;
		}

		var addBtn = new Button
		{
			Text = "+ 캐릭터 추가",
			Location = new Point(0, y),
			Size = new Size(214, 26),
			FlatStyle = FlatStyle.Flat,
			Font = new Font("맑은 고딕", 8.5f),
			Cursor = Cursors.Hand,
			TabStop = false
		};
		addBtn.FlatAppearance.BorderSize = 1;
		addBtn.Click += async (_, _) => await DoLoginAsync();
		_charListPanel.Controls.Add(addBtn);
		y += addBtn.Height;

		_charListPanel.Size = new Size(220, Math.Max(1, y));
		_charListPanel.ResumeLayout();
	}

	private static readonly Color MainCharBorder = Color.FromArgb(60, 180, 90);
	private static readonly Color OtherCharBorder = Color.FromArgb(210, 210, 210);

	private Panel BuildCharacterRow(TrackedCharacter c, bool isMain)
	{
		var outer = new Panel
		{
			Size = new Size(214, 40),
			BackColor = isMain ? MainCharBorder : OtherCharBorder,
			Padding = new Padding(2),
			Cursor = Cursors.Hand,
			Tag = c
		};
		var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

		var pic = new PictureBox
		{
			Size = new Size(32, 32),
			Location = new Point(2, 2),
			SizeMode = PictureBoxSizeMode.Zoom,
			BackColor = Color.Gainsboro
		};
		try { pic.LoadAsync($"https://images.evetech.net/characters/{c.CharacterId}/portrait?size=64"); } catch { }

		var nameLbl = new Label
		{
			Text = c.CharacterName,
			Location = new Point(40, 3),
			Size = new Size(146, 18),
			Font = new Font("맑은 고딕", 8.5f, isMain ? FontStyle.Bold : FontStyle.Regular),
			TextAlign = ContentAlignment.MiddleLeft,
			AutoEllipsis = true
		};
		var subLbl = new Label
		{
			Text = isMain ? "★ 점프거리 표시 중" : "클릭: 점프거리 표시 대상으로 지정",
			Location = new Point(40, 21),
			Size = new Size(146, 14),
			Font = new Font("맑은 고딕", 7f),
			ForeColor = isMain ? MainCharBorder : Color.FromArgb(120, 120, 120),
			AutoEllipsis = true
		};
		var removeBtn = new Button
		{
			Text = "x",
			Location = new Point(190, 2),
			Size = new Size(20, 20),
			Font = new Font("맑은 고딕", 7.5f),
			FlatStyle = FlatStyle.Flat,
			Cursor = Cursors.Hand,
			TabStop = false,
			ForeColor = Color.FromArgb(150, 60, 60)
		};
		removeBtn.FlatAppearance.BorderSize = 0;
		removeBtn.Click += (_, _) => RemoveCharacter(c);

		void SelectHandler(object? s, EventArgs e) => SelectMainCharacter(c);
		inner.Click += SelectHandler;
		pic.Click += SelectHandler;
		nameLbl.Click += SelectHandler;
		subLbl.Click += SelectHandler;

		inner.Controls.Add(pic);
		inner.Controls.Add(nameLbl);
		inner.Controls.Add(subLbl);
		inner.Controls.Add(removeBtn);
		outer.Controls.Add(inner);
		return outer;
	}

	private void MaybeAskIntelReportConsent()
	{
		if (_settings.IntelReportConsentAsked) return;
		_settings.IntelReportConsentAsked = true;

		var result = MessageBox.Show(this,
			"인텔 로그(성계·캐릭터·함선 등 인텔 채널에서 인식된 정보)를 중앙 서버로 전송해\n" +
			"함대 추적 등에 활용하는 데 동의하시겠습니까?\n\n" +
			"동의하시면 인텔 로그만 서버로 전송됩니다. (ZKB Feed, 위치 등 다른 정보는 전송되지 않음)\n" +
			"거절하시면 아무 것도 전송되지 않습니다.\n\n" +
			"이 선택은 나중에 언제든 대시보드의 \"인텔전송\" 버튼으로 다시 바꿀 수 있습니다.",
			"인텔 로그 서버 전송 동의",
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question);

		_intelReportEnabled = result == DialogResult.Yes;
		_settings.IntelReportEnabled = _intelReportEnabled;
		ApplyIntelReportToggleVisual();
		_settings.Save();
	}

	private void ToggleIntelReport()
	{
		_intelReportEnabled = !_intelReportEnabled;
		_settings.IntelReportEnabled = _intelReportEnabled;
		ApplyIntelReportToggleVisual();
		_settings.Save();
	}

	private void ApplyIntelReportToggleVisual()
	{
		if (_btnIntelReportToggle is null) return;
		if (_intelReportEnabled)
		{
			_btnIntelReportToggle.Text = "ON";
			_btnIntelReportToggle.BackColor = Color.FromArgb(40, 160, 70);
			_btnIntelReportToggle.ForeColor = Color.White;
			if (_intelReportToggleStatus is not null) _intelReportToggleStatus.Text = "전송 중 (Vale Watch)";
		}
		else
		{
			_btnIntelReportToggle.Text = "OFF";
			_btnIntelReportToggle.BackColor = Color.FromArgb(160, 60, 60);
			_btnIntelReportToggle.ForeColor = Color.White;
			if (_intelReportToggleStatus is not null) _intelReportToggleStatus.Text = "꺼짐";
		}
		_btnIntelReportToggle.Invalidate();
	}

	private void ApplyVolumeUi()
	{
		int v = _settings.AlertSoundVolume;
		if (v < 0) v = 0;
		if (v > 100) v = 100;
		_settings.AlertSoundVolume = v;
		if (_volBar is not null && _volBar.Value != v)
			_volBar.Value = v;
	}

	private void LoadAlertSound()
	{
		string path = _settings.AlertSoundPath;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			path = DefaultAlertSoundPath();
		try { _alertSound?.Dispose(); } catch { }
		_alertSound = new AlertSoundPlayer();
		int vol = _settings.AlertSoundVolume;
		if (vol < 0 || vol > 100) vol = 80;
		_alertSound.Load(path, vol);
		UpdateSoundPathLabel();
	}

	private void PlayAlertSound(bool force)
	{
		if (!force && !_settings.AlertSoundEnabled) return;
		try
		{
			if (_alertSound is null) LoadAlertSound();
			else _alertSound.SetVolume(_settings.AlertSoundVolume);
			_alertSound?.Play();
		}
		catch
		{
			try { SystemSounds.Exclamation.Play(); } catch { }
		}
	}

	private void UpdateSoundPathLabel()
	{
		if (_soundPathLabel is null) return;
		string path = _settings.AlertSoundPath;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			path = DefaultAlertSoundPath();
		string name = File.Exists(path) ? Path.GetFileName(path) : "(없음)";
		_soundPathLabel.Text = "음원: " + name;
	}

	private void BrowseAlertSound()
	{
		using var dlg = new OpenFileDialog
		{
			Title = "알림음 선택",
			Filter = "오디오 (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a|WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|모든 파일|*.*",
			CheckFileExists = true
		};
		string cur = _settings.AlertSoundPath;
		if (string.IsNullOrWhiteSpace(cur) || !File.Exists(cur))
			cur = DefaultAlertSoundPath();
		if (File.Exists(cur))
		{
			dlg.InitialDirectory = Path.GetDirectoryName(cur);
			dlg.FileName = Path.GetFileName(cur);
		}
		if (dlg.ShowDialog(this) != DialogResult.OK) return;
		_settings.AlertSoundPath = dlg.FileName;
		_settings.Save();
		LoadAlertSound();
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		try
		{
			if (_mapOverlay is not null && !_mapOverlay.IsDisposed)
			{
				_mapOverlay.Close();
				_mapOverlay.Dispose();
			}
		}
		catch { }
		_mapOverlay = null;
		_engine.Dispose();
		try { _reporter.Dispose(); } catch { }
		try { _alertSound?.Dispose(); } catch { }
		_alertSound = null;
		base.OnFormClosed(e);
	}


	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

}
