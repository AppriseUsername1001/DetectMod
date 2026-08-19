using System.Diagnostics;
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
	private Panel _charListPanel = null!;

	/// <summary>목록이 비게 되면 IntelPanel이 로그인 화면으로 돌아가야 한다.</summary>
	public event Action? AllCharactersRemoved;
	/// <summary>"+ 캐릭터 추가" 클릭 — 실제 SSO 로그인 흐름은 IntelPanel 소관이라 요청만 올린다.</summary>
	public event Action? AddCharacterRequested;

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

		// 좌측 1/3: 로그인된 캐릭터 목록 (경보기/인텔 알림 어느 탭에서도 항상 보이도록 여기 둔다).
		// 우측 2/3: 기존 ZKB 리스트. 탭 전환과 무관하게 항상 보이는 자리라 캐릭터 전환 UI를
		// 여기 두면 어느 화면에서든 바로 메인 캐릭터를 바꿀 수 있다.
		var contentSplit = new Panel { Dock = DockStyle.Fill };
		_charListPanel = new Panel { Dock = DockStyle.Left, Width = 240, AutoScroll = true, BackColor = Color.White };
		var charListDivider = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Color.FromArgb(210, 210, 210) };
		contentSplit.Resize += (_, _) =>
			_charListPanel.Width = Math.Max(140, contentSplit.Width / 3);
		contentSplit.Controls.Add(_list);
		contentSplit.Controls.Add(charListDivider);
		contentSplit.Controls.Add(_charListPanel);

		inner.Controls.Add(contentSplit);
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
				// 방금 새로 보이게 된 시점(예: 최소화 후 복원 직후)은 원본 창 자체도 OS가
				// 강제로 다시 그리는 중이라 컴포지터 경합이 심하다 — 그 자리에서 바로
				// RebuildIfChanged를 동기 호출하면 Clear~재생성 중간 상태가 그대로 그려져
				// 보일 수 있다(점멸). BeginInvoke로 한 틱 미뤄 그 경합 창을 피한다.
				try { BeginInvoke(() => RebuildIfChanged()); } catch { }
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
		RebuildCharacterList();
	}

	/// <summary>클릭한 캐릭터를 점프거리 표시/위치 추적 대상("메인")으로 지정한다. 알림(경고음)은
	/// 이후 단계에서 캐릭터별로 각자 독립 처리할 예정 — 지금은 메인 캐릭터 기준으로 위치·
	/// 점프거리만 갱신한다.</summary>
	private void SelectMainCharacter(TrackedCharacter c)
	{
		_settings.IntelMainCharacterId = c.CharacterId;
		_settings.Save();
		c.ExpiresAt = DateTimeOffset.UtcNow; // 즉시 새로 갱신되도록
		_engine?.SetCharacter(c);
		_engine?.Start();
		_ = _engine?.RefreshLocationAsync(); // IntelPanel은 IntelEngine.LocationUpdated로 자기 화면을 갱신
		RebuildCharacterList();
	}

	/// <summary>목록에서 캐릭터를 제거(로그아웃)한다. 제거 대상이 메인이었다면 남은 캐릭터 중
	/// 하나로 메인을 자동 교체하고, 아무도 안 남으면 AllCharactersRemoved로 알린다
	/// (로그인 화면 전환은 IntelPanel 소관이라 직접 건드리지 않는다).</summary>
	public void RemoveCharacter(TrackedCharacter c)
	{
		_settings.IntelCharacters.RemoveAll(x => x.CharacterId == c.CharacterId);
		if (_settings.IntelMainCharacterId == c.CharacterId)
			_settings.IntelMainCharacterId = _settings.IntelCharacters.FirstOrDefault()?.CharacterId ?? 0;
		_settings.Save();

		var newMain = _settings.GetMainCharacter();
		if (newMain is null)
		{
			_engine?.Stop();
			_engine?.SetCharacter(null);
			RebuildCharacterList();
			AllCharactersRemoved?.Invoke();
		}
		else
		{
			SelectMainCharacter(newMain);
		}
	}

	/// <summary>IntelPanel의 로그인 흐름이 캐릭터를 추가/이관한 뒤 호출한다.</summary>
	public void RefreshCharacterList() => RebuildCharacterList();

	private static readonly Color MainCharBorder = Color.FromArgb(60, 180, 90);
	private static readonly Color OtherCharBorder = Color.FromArgb(210, 210, 210);

	/// <summary>좌측 캐릭터 목록을 현재 _settings.IntelCharacters로 다시 그린다 — 메인 캐릭터는
	/// 초록 테두리로, 그 외는 회색 테두리로 표시한다.</summary>
	private void RebuildCharacterList()
	{
		if (_charListPanel is null) return;
		_charListPanel.SuspendLayout();
		foreach (Control old in _charListPanel.Controls) old.Dispose();
		_charListPanel.Controls.Clear();

		int y = 4;
		int mainId = _settings.GetMainCharacter()?.CharacterId ?? 0;
		foreach (var c in _settings.IntelCharacters)
		{
			var row = BuildCharacterRow(c, c.CharacterId == mainId);
			row.Location = new Point(4, y);
			_charListPanel.Controls.Add(row);
			y += row.Height + 4;
		}

		var addBtn = new Button
		{
			Text = "+ 캐릭터 추가",
			Location = new Point(4, y),
			Size = new Size(214, 26),
			FlatStyle = FlatStyle.Flat,
			Font = new Font("맑은 고딕", 8.5f),
			Cursor = Cursors.Hand,
			TabStop = false
		};
		addBtn.FlatAppearance.BorderSize = 1;
		addBtn.Click += (_, _) => AddCharacterRequested?.Invoke();
		_charListPanel.Controls.Add(addBtn);

		_charListPanel.ResumeLayout();
	}

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

	/// <summary>5초마다 호출. 현재 _items로부터 화면에 표시될 내용을 계산해 실제로 달라진
	/// 부분만 건드린다 — 새 킬은 맨 위에 삽입만 하고, 기존에 이미 떠 있던 행은 "N분 전"
	/// 갱신·ESI 이름 확정·색상 변경처럼 실제로 값이 바뀐 필드만 patch한다.
	/// (예전엔 뭐가 하나라도 바뀌면 리스트 전체를 Clear + 재생성해서, 킬 하나 새로 들어올
	/// 때마다 이미 떠 있던 나머지 행들까지 전부 다시 그려지며 화면 전체가 깜빡였다.)
	/// 변경이 전혀 없으면 네이티브 컨트롤을 아예 건드리지 않는다. 유저가 맨 위(최신)를
	/// 보고 있었다면 새로고침 후에도 계속 맨 위에 고정되고, 휠로 과거 내용을 내려서
	/// 보고 있었다면 보던 킬메일 위치를 그대로 유지한다.</summary>
	private void RebuildIfChanged(bool force = false)
	{
		if (IsDisposed || !_list.IsHandleCreated) return;

		var rows = new List<(string time, string system, string jumps, string alliance, string ship, Color color, ZkbLossEvent ev)>(_items.Count);
		foreach (var ev in _items)
		{
			rows.Add((FormatAgo(ev.KillTimeUtc), ev.SystemName, FormatJumps(ev.SystemName),
				ResolveAlliance(ev), ResolveShip(ev), RowBackColor(ev), ev));
		}

		bool wasAtTop = _list.Items.Count == 0 || (_list.TopItem != null && _list.TopItem.Index == 0);
		long? topKillmailId = wasAtTop ? null : (_list.TopItem?.Tag as ZkbLossEvent)?.KillmailId;

		var existingIds = new HashSet<long>(_list.Items.Count);
		foreach (ListViewItem existing in _list.Items)
			if (existing.Tag is ZkbLossEvent existingEv) existingIds.Add(existingEv.KillmailId);

		// _items는 항상 맨 앞에만 새 킬이 추가되고(OnLoss) 중간 삭제·재정렬은 없다 — 그러니
		// 앞쪽에서부터 "아직 화면에 없는" 킬만 세면 그게 곧 새로 들어온 개수다.
		int newCount = 0;
		while (newCount < rows.Count && !existingIds.Contains(rows[newCount].ev.KillmailId))
			newCount++;

		// 화면에 이미 떠 있던 나머지가 정확히 같은 순서로 이어지는지 검증 — 하나라도 어긋나면
		// (예상 못한 재정렬 등, 최초 로드 시에는 이 검사가 자연히 통과함) 안전하게 목록을
		// 비우고 전부 새 행으로 취급한다.
		bool tailValid = rows.Count - newCount == _list.Items.Count;
		if (tailValid)
		{
			for (int j = 0; j < _list.Items.Count; j++)
			{
				if (_list.Items[j].Tag is not ZkbLossEvent tailEv || tailEv.KillmailId != rows[newCount + j].ev.KillmailId)
				{
					tailValid = false;
					break;
				}
			}
		}
		bool needsClear = !tailValid && _list.Items.Count > 0;
		if (!tailValid) newCount = rows.Count;

		var fieldUpdates = new List<(ListViewItem item, string time, string jumps, string alliance, string ship, Color color)>();
		if (tailValid)
		{
			for (int j = 0; j < _list.Items.Count; j++)
			{
				var item = _list.Items[j];
				var r = rows[newCount + j];
				if (item.SubItems[0].Text != r.time || item.SubItems[2].Text != r.jumps ||
					item.SubItems[3].Text != r.alliance || item.SubItems[4].Text != r.ship ||
					item.BackColor != r.color)
				{
					fieldUpdates.Add((item, r.time, r.jumps, r.alliance, r.ship, r.color));
				}
			}
		}

		if (!force && !needsClear && newCount == 0 && fieldUpdates.Count == 0) return;

		_list.BeginUpdate();
		try
		{
			// 꼬리 검증에 실패했을 때만(예상 못한 재정렬 등) 여기서 통째로 비운다 —
			// BeginUpdate~EndUpdate 안에서 해야 이 Clear() 자체가 즉시 리페인트를 유발하지 않는다.
			if (needsClear) _list.Items.Clear();
			foreach (var (item, time, jumps, alliance, ship, color) in fieldUpdates)
			{
				item.SubItems[0].Text = time;
				item.SubItems[2].Text = jumps;
				item.SubItems[3].Text = alliance;
				item.SubItems[4].Text = ship;
				item.BackColor = color;
			}
			for (int k = newCount - 1; k >= 0; k--)
			{
				var r = rows[k];
				var newItem = new ListViewItem(new[] { r.time, r.system, r.jumps, r.alliance, r.ship })
				{
					Tag = r.ev,
					ForeColor = r.ev.IsNpc ? Color.FromArgb(120, 120, 120) : Color.FromArgb(30, 30, 30),
					BackColor = r.color
				};
				_list.Items.Insert(0, newItem);
			}
			while (_list.Items.Count > 300) _list.Items.RemoveAt(_list.Items.Count - 1);
		}
		finally
		{
			_list.EndUpdate();
		}

		if (wasAtTop)
		{
			if (_list.Items.Count > 0) _list.TopItem = _list.Items[0];
		}
		else if (topKillmailId is long tid)
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
		// TopItem까지 다 맞춘 "최종" 상태 기준으로 딱 한 번만 강제 재그리기 — EndUpdate 직후
		// TopItem 조정 전에 Invalidate하면 조정 전 스크롤 위치가 잠깐 그려질 여지가 생긴다
		// (예전 항목의 잔상, 예: "8"처럼 숫자 일부만 남는 파편, 방지 목적은 동일).
		_list.Invalidate();
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