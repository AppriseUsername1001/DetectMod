using System.Diagnostics;
using System.Text;
using EVEAA.Mod.Intel;

namespace EVEAA.Mod;

/// <summary>창 하단 확장 영역에 붙는 ZKB Feed 패널.</summary>
internal sealed class ZkbFeedPanel : NativeChildForm
{
	private readonly ModSettings _settings;
	private readonly ListView _list;
	private readonly Label _status;
	private readonly Button _btnColors;
	private readonly Button _btnTestAlarmSound;
	private readonly TrackBar _volumeTrack;
	private readonly Label _volumePercentLabel;
	private readonly List<ZkbLossEvent> _items = new();
	private int _clickIndex = -1;
	private System.Windows.Forms.Timer? _redrawTimer;
	private IntelEngine? _engine;
	private bool _bound;
	private string _lastRenderedSignature = "";

	public ZkbFeedPanel(ModSettings settings)
	{
		_settings = settings;
		BackColor = Color.White;
		ClientSize = new Size(400, 200);

		var outer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(2) };
		var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(4) };
		// head 높이 ≥ 라벨+볼륨+상태 합 — 넘치면 ListView 헤더가 창 밖으로 이중 그려짐
		var head = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Color.White };
		var lbl = new Label
		{
			Text = "ZKB Feed  (로스메일 · 더블클릭: zKill)",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(50, 50, 50)
		};
		_btnColors = new Button
		{
			Text = "색상",
			Dock = DockStyle.Right,
			Width = 44,
			FlatStyle = FlatStyle.Flat,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(70, 70, 70),
			Cursor = Cursors.Hand,
			TabStop = false
		};
		_btnColors.FlatAppearance.BorderSize = 1;
		_btnColors.Click += (_, _) => OpenColorSettingsDialog();
		_btnTestAlarmSound = new Button
		{
			Text = "알림음 테스트",
			Dock = DockStyle.Right,
			Width = 84,
			FlatStyle = FlatStyle.Flat,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(70, 70, 70),
			Cursor = Cursors.Hand,
			TabStop = false
		};
		_btnTestAlarmSound.FlatAppearance.BorderSize = 1;
		_btnTestAlarmSound.Click += (_, _) => AlarmSoundTestRequested?.Invoke();
		var titleRow = new Panel { Dock = DockStyle.Top, Height = 20 };
		titleRow.Controls.Add(lbl);
		titleRow.Controls.Add(_btnTestAlarmSound);
		titleRow.Controls.Add(_btnColors);
		_status = new Label
		{
			Text = "ZKB: 대기",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(110, 110, 110),
			Font = new Font("맑은 고딕", 8f)
		};
		var sub = new Panel { Dock = DockStyle.Top, Height = 22 };
		sub.Controls.Add(_status);

		var volLabel = new Label
		{
			Text = "알림음 크기",
			Dock = DockStyle.Left,
			Width = 74,
			AutoEllipsis = false,
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(80, 80, 80)
		};
		_volumePercentLabel = new Label
		{
			Text = _settings.AlarmSoundTestVolume + "%",
			Dock = DockStyle.Right,
			Width = 34,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("맑은 고딕", 7.5f),
			ForeColor = Color.FromArgb(80, 80, 80)
		};
		_volumeTrack = new TrackBar
		{
			Dock = DockStyle.Fill,
			Minimum = 0,
			Maximum = 100,
			TickStyle = TickStyle.None,
			Value = Math.Clamp(_settings.AlarmSoundTestVolume, 0, 100)
		};
		_volumeTrack.ValueChanged += (_, _) =>
		{
			_settings.AlarmSoundTestVolume = _volumeTrack.Value;
			_settings.Save();
			_volumePercentLabel.Text = _volumeTrack.Value + "%";
		};
		var soundRow = new Panel { Dock = DockStyle.Top, Height = 24 };
		soundRow.Controls.Add(_volumeTrack);
		soundRow.Controls.Add(_volumePercentLabel);
		soundRow.Controls.Add(volLabel);
		var spacer = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Color.White };

		head.Controls.Add(sub);
		head.Controls.Add(soundRow);
		head.Controls.Add(spacer);
		head.Controls.Add(titleRow);

		_list = new ListView
		{
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			MultiSelect = false,
			HeaderStyle = ColumnHeaderStyle.Nonclickable,
			BorderStyle = BorderStyle.None,
			Font = new Font("맑은 고딕", 9f),
			HideSelection = true
		};
		_list.Columns.Add("Time", 60);
		_list.Columns.Add("System", 80);
		_list.Columns.Add("Jumps", 50);
		_list.Columns.Add("Alliance", 140);
		_list.Columns.Add("Ship Type", 120);
		_list.MouseDown += (_, e) =>
		{
			var hit = _list.HitTest(e.Location);
			_clickIndex = hit.Item?.Index ?? -1;
		};
		_list.MouseUp += (_, e) =>
		{
			if (e.Button != MouseButtons.Left) return;
			_list.SelectedIndices.Clear();
			// 선택 해제 직후 강제 재그리기 — 안 하면 클릭한 행 근처에 흰 얼룩이 남을 때가 있다.
			_list.Invalidate();
		};
		_list.DoubleClick += (_, _) => OpenKill(_clickIndex);
		// 휠로 스크롤한 직후에도 클릭 때와 같은 흰 얼룩이 남는 경우가 있어 동일하게 강제 재그리기.
		_list.MouseWheel += (_, _) => _list.Invalidate();
		_list.Resize += (_, _) => LayoutColumns();

		inner.Controls.Add(_list);
		inner.Controls.Add(head);
		outer.Controls.Add(inner);
		Controls.Add(outer);
	}

	/// <summary>"알림음 테스트" 버튼 클릭 — 경보기(원본) 쪽 현재 선택된 경고음을 1회 재생해 달라는 요청.
	/// 원본 창의 네이티브 컨트롤을 찾아야 해서 실제 재생 로직은 ControlBarForm이 처리한다.</summary>
	public event Action? AlarmSoundTestRequested;

	public void Attach(IntPtr parent) => AttachAsChild(parent);

	public void LayoutInParent(int x, int y, int w, int h, bool force = false) =>
		PlaceInParent(x, y, w, h, bringToFront: false, visible: true, force: force);

	public void SetVisible(bool visible)
	{
		if (!IsHandleCreated) return;
		if (visible)
		{
			// 이미 정상 부착·표시 중이면 재부착 생략 — 경보기 뷰에서 매 200ms틱마다 호출되므로
			// 여기서 매번 ReparentToHost(SetParent + SWP_FRAMECHANGED)를 하면 원본 창의 형제
			// 컨트롤(예: 경고음 선택 콤보박스) 리페인트가 깨져 눈에 띄게 깜빡인다.
			if (!IsShownInHost)
			{
				// 이전 Bounds로 PlaceInParent 하지 않음 — 낡은 크기로 인텔 로그를 덮는 검은 영역 방지
				ReparentToHost();
				Visible = true;
				ShowWindow(Handle, SW_SHOW);
				// 방금 새로 보이게 됐다면 5초 주기를 기다리지 않고 바로 최신 상태를 그려준다 —
				// 변경 없으면 RebuildIfChanged 내부에서 자체적으로 아무것도 안 건드리니 안전하다.
				RebuildIfChanged();
			}
		}
		else
			HideInParent();
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
	private const int SW_SHOW = 5;

	public void Bind(IntelEngine engine)
	{
		if (_bound || engine is null) return;
		_engine = engine;
		_bound = true;
		_engine.ZkbRegionOnly = true;
		_engine.ZkbLoss += OnLoss;
		_engine.ZkbStatus += msg =>
		{
			if (IsDisposed) return;
			try { BeginInvoke(() => { _status.Text = msg; }); } catch { }
		};
		// 킬이 들어올 때마다, 혹은 2초마다 네이티브 리스트뷰를 직접 건드리던 예전 방식이
		// 반복적인 파편 손상(점멸)의 원인이었다 — 이제 새 킬은 _items(순수 데이터)에만
		// 바로 반영하고, 실제 화면(_list)은 5초 주기로 한 번씩만, 그것도 내용이 실제로
		// 달라졌을 때만 통째로 다시 그린다.
		_redrawTimer = new System.Windows.Forms.Timer { Interval = 5000 };
		_redrawTimer.Tick += (_, _) => RebuildIfChanged();
		_redrawTimer.Start();
	}

	private void OnLoss(ZkbLossEvent ev)
	{
		if (IsDisposed) return;
		try
		{
			BeginInvoke(() =>
			{
				_items.Insert(0, ev);
				while (_items.Count > 300) _items.RemoveAt(_items.Count - 1);
			});
		}
		catch { }
	}

	/// <summary>5초마다 호출. 현재 _items로부터 화면에 표시될 내용을 계산해, 마지막으로
	/// 실제로 그린 내용과 다를 때만 리스트를 통째로(Clear + 재생성) 다시 그린다.
	/// 변경이 없으면 네이티브 컨트롤을 아예 건드리지 않는다 — "새로운 정보가 없으면
	/// 그대로 둔다"는 원칙. 스크롤 위치는 맨 위에 보이던 킬메일 ID로 다시 찾아 복원한다.</summary>
	private void RebuildIfChanged(bool force = false)
	{
		if (IsDisposed || !_list.IsHandleCreated) return;

		var rows = new List<(string time, string system, string jumps, string alliance, string ship, Color color, ZkbLossEvent ev)>(_items.Count);
		var sig = new StringBuilder();
		foreach (var ev in _items)
		{
			string time = FormatAgo(ev.KillTimeUtc);
			string jumps = FormatJumps(ev.SystemName);
			string alliance = ResolveAlliance(ev);
			string ship = ResolveShip(ev);
			Color color = RowBackColor(ev);
			rows.Add((time, ev.SystemName, jumps, alliance, ship, color, ev));
			sig.Append(ev.KillmailId).Append('|').Append(time).Append('|').Append(jumps).Append('|')
				.Append(alliance).Append('|').Append(ship).Append('|').Append(color.ToArgb()).Append(';');
		}
		string signature = sig.ToString();
		if (!force && signature == _lastRenderedSignature) return;
		_lastRenderedSignature = signature;

		long? topKillmailId = (_list.TopItem?.Tag as ZkbLossEvent)?.KillmailId;

		_list.BeginUpdate();
		try
		{
			_list.Items.Clear();
			foreach (var r in rows)
			{
				var item = new ListViewItem(new[] { r.time, r.system, r.jumps, r.alliance, r.ship })
				{
					Tag = r.ev,
					ForeColor = r.ev.IsNpc ? Color.FromArgb(120, 120, 120) : Color.FromArgb(30, 30, 30),
					BackColor = r.color
				};
				_list.Items.Add(item);
			}
		}
		finally
		{
			_list.EndUpdate();
		}

		if (topKillmailId is long tid)
		{
			foreach (ListViewItem item in _list.Items)
			{
				if (item.Tag is ZkbLossEvent ev2 && ev2.KillmailId == tid)
				{
					_list.TopItem = item;
					break;
				}
			}
		}
	}

	/// <summary>감시 캐릭터와 같은 얼라이언스면 설정된 저채도 색, 아니면 그 외 색. 내 얼라이언스를 아직 모르면 기본 흰색.</summary>
	private Color RowBackColor(ZkbLossEvent ev)
	{
		int myAlliance = _engine?.Character?.AllianceId ?? 0;
		if (myAlliance <= 0) return Color.White;
		bool same = ev.VictimAllianceId is int a && a == myAlliance;
		int argb = same ? _settings.ZkbSameAllianceColorArgb : _settings.ZkbOtherAllianceColorArgb;
		return Color.FromArgb(255, Color.FromArgb(argb));
	}

	/// <summary>사용자 컴퓨터 현재 시각 기준 경과 시간. "방금", "N분 전", "N시간 전".</summary>
	private static string FormatAgo(DateTime whenUtc)
	{
		TimeSpan span = DateTime.UtcNow - whenUtc;
		if (span < TimeSpan.Zero) span = TimeSpan.Zero;
		if (span.TotalMinutes < 1) return "방금";
		if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}분 전";
		if (span.TotalDays < 1) return $"{(int)span.TotalHours}시간 전";
		return $"{(int)span.TotalDays}일 전";
	}

	private string ResolveAlliance(ZkbLossEvent ev)
	{
		if (_engine is null) return ev.AllianceText;
		if (ev.VictimAllianceId is int a && _engine.TryEsiName(a) is string an) return an;
		if (ev.VictimCorpId is int c && _engine.TryEsiName(c) is string cn) return cn;
		return ev.AllianceText;
	}

	private string ResolveShip(ZkbLossEvent ev)
	{
		if (_engine is null) return ev.ShipText;
		if (ev.ShipTypeId > 0 && _engine.TryEsiName(ev.ShipTypeId) is string sn) return sn;
		return ev.ShipText;
	}

	private void LayoutColumns()
	{
		if (_list.Columns.Count < 5) return;
		int w = Math.Max(60, _list.ClientSize.Width - 8);
		int timeW = 56;
		int jumpsW = 44;
		_list.Columns[0].Width = timeW;
		_list.Columns[1].Width = Math.Max(60, w * 18 / 100);
		_list.Columns[2].Width = jumpsW;
		_list.Columns[3].Width = Math.Max(90, w * 34 / 100);
		_list.Columns[4].Width = Math.Max(70, w - timeW - _list.Columns[1].Width - jumpsW - _list.Columns[3].Width);
	}

	private string FormatJumps(string systemName)
	{
		int? d = _engine?.TryJumpDistance(systemName);
		if (d is null) return "?";
		return d.Value + "j";
	}

	private void OpenColorSettingsDialog()
	{
		using var dlg = new Form
		{
			Text = "ZKB 얼라이언스 색상",
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			ClientSize = new Size(300, 150),
			MaximizeBox = false,
			MinimizeBox = false,
			ShowInTaskbar = false,
			TopMost = true
		};

		Color sameColor = Color.FromArgb(255, Color.FromArgb(_settings.ZkbSameAllianceColorArgb));
		Color otherColor = Color.FromArgb(255, Color.FromArgb(_settings.ZkbOtherAllianceColorArgb));

		var sameLbl = new Label { Text = "같은 얼라이언스", Location = new Point(16, 20), AutoSize = true };
		var sameSwatch = new Button { Location = new Point(150, 14), Size = new Size(60, 26), BackColor = sameColor, FlatStyle = FlatStyle.Flat };
		var otherLbl = new Label { Text = "그 외 얼라이언스", Location = new Point(16, 60), AutoSize = true };
		var otherSwatch = new Button { Location = new Point(150, 54), Size = new Size(60, 26), BackColor = otherColor, FlatStyle = FlatStyle.Flat };

		sameSwatch.Click += (_, _) =>
		{
			using var cd = new ColorDialog { Color = sameSwatch.BackColor, FullOpen = true };
			if (cd.ShowDialog(dlg) == DialogResult.OK) sameSwatch.BackColor = cd.Color;
		};
		otherSwatch.Click += (_, _) =>
		{
			using var cd = new ColorDialog { Color = otherSwatch.BackColor, FullOpen = true };
			if (cd.ShowDialog(dlg) == DialogResult.OK) otherSwatch.BackColor = cd.Color;
		};

		var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(120, 104), Size = new Size(75, 28) };
		var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(201, 104), Size = new Size(75, 28) };
		dlg.Controls.Add(sameLbl);
		dlg.Controls.Add(sameSwatch);
		dlg.Controls.Add(otherLbl);
		dlg.Controls.Add(otherSwatch);
		dlg.Controls.Add(ok);
		dlg.Controls.Add(cancel);
		dlg.AcceptButton = ok;
		dlg.CancelButton = cancel;
		if (dlg.ShowDialog() != DialogResult.OK) return;

		_settings.ZkbSameAllianceColorArgb = sameSwatch.BackColor.ToArgb();
		_settings.ZkbOtherAllianceColorArgb = otherSwatch.BackColor.ToArgb();
		_settings.Save();
		RebuildIfChanged(force: true);
	}

	private void OpenKill(int index)
	{
		if (index < 0 || index >= _list.Items.Count) return;
		if (_list.Items[index].Tag is not ZkbLossEvent ev) return;
		string url = $"https://zkillboard.com/kill/{ev.KillmailId}/";
		try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_redrawTimer?.Stop();
		_redrawTimer?.Dispose();
		base.OnFormClosed(e);
	}
}