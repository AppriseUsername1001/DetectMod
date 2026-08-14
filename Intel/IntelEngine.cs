namespace EVEAA.Mod.Intel;

internal sealed class IntelThreatEvent
{
	public string TimeText { get; init; } = "";
	public string Channel { get; init; } = "";
	public string System { get; init; } = "";
	public string Character { get; init; } = "";
	public string Ship { get; init; } = "";
	public string Message { get; init; } = "";
	public string Speaker { get; init; } = "";
	public int Jumps { get; init; } = -1;
	public bool IsAlert { get; init; }
	public bool IsClear { get; init; }
	public bool IsKillReport { get; init; }
	public string Raw { get; init; } = "";

	public int? HostileCount { get; init; }
	public bool HostileCountIsPlus { get; init; }
	public bool HostileCountIsExact { get; init; }
	public string? GateSystem { get; init; }
	public bool GateIsAnsiblex { get; init; }
	public string? MovementVerb { get; init; }
	public string? MovementSystem { get; init; }
	public bool MovementIsGate { get; init; }
	public bool IsQuestion { get; init; }
	public string? QuestionType { get; init; }

	/// <summary>구조화 필드(게이트/이동/적 카운트/질문)를 사람이 읽는 접미사로 변환.</summary>
	public string ExtraSuffix()
	{
		if (!string.IsNullOrEmpty(MovementVerb) && !string.IsNullOrEmpty(MovementSystem))
		{
			string via = MovementIsGate ? " 게이트" : "";
			return $" · {MovementSystem}{via}로 이동({MovementVerb})";
		}
		if (!string.IsNullOrEmpty(GateSystem))
			return $" · {GateSystem} {(GateIsAnsiblex ? "안시블렉스" : "게이트")}";
		if (HostileCount is int hc)
		{
			string sign = HostileCountIsPlus ? "+" : HostileCountIsExact ? "=" : "";
			return $" · 적 {sign}{hc}";
		}
		if (IsQuestion)
			return $" · [질문:{QuestionType}]";
		return "";
	}

	public string JumpSuffix()
	{
		if (Jumps >= 0) return $"({Jumps}j)";
		if (!string.IsNullOrEmpty(System)) return "(?j)";
		return "";
	}

	public string FormatLogLine()
	{
		string t = string.IsNullOrEmpty(TimeText) ? DateTime.Now.ToString("HH:mm:ss") : TimeText;
		string jump = JumpSuffix();
		string prefix = string.IsNullOrEmpty(jump) ? $"[{t}]" : $"[{t}] {jump}";

		if (IsClear)
		{
			string sys = string.IsNullOrEmpty(System) ? "-" : System;
			return $"{prefix}  {sys}  클리어";
		}

		bool hasStruct = !string.IsNullOrEmpty(System) || !string.IsNullOrEmpty(Character) || !string.IsNullOrEmpty(Ship);
		if (hasStruct)
		{
			string sys = string.IsNullOrEmpty(System) ? "-" : System;
			string ch = string.IsNullOrEmpty(Character) ? "-" : Character;
			string sh = string.IsNullOrEmpty(Ship) ? "-" : Ship;
			return $"{prefix}  {sys} / {ch} / {sh}{ExtraSuffix()}";
		}

		string extra = ExtraSuffix();
		if (!string.IsNullOrEmpty(extra))
			return $"{prefix} {extra.TrimStart(' ', '·')}";

		string msg = string.IsNullOrEmpty(Message) ? Raw : Message;
		return $"{prefix}  {msg}";
	}
}

internal sealed class IntelEngine : IDisposable
{
	public const double LocationPollSec = 2.0;
	/// <summary>채팅 폴링 주기. ESI 위치 조회와 분리되어 이 간격으로만 동작.</summary>
	public const double ChatPollSec = 0.05;
	/// <summary>Loop wait cap ms; FSW wakes immediately.</summary>
	public const int ChatLoopWaitMs = 25;

	private readonly SystemsDatabase _systems = new();
	private readonly JumpNav _jump = new();
	private readonly ShipDatabase _ships = new();
	private readonly CharacterResolver _chars = new();
	private readonly MapLayoutDatabase _maps = new();
	private readonly EveSso _sso = new();
	private readonly ChatlogWatcher _chat = new();
	private readonly EsiNameCache _esiNames = new();
	private readonly ZkbFeedService _zkb;
	private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
	private CancellationTokenSource? _cts;
	private Task? _loop;
	private string? _lastActiveLog;
	private readonly AutoResetEvent _chatPulse = new(false);

	public IntelEngine()
	{
		_zkb = new ZkbFeedService(_systems, _maps, _esiNames);
		_zkb.LossReceived += ev => ZkbLoss?.Invoke(ev);
		_zkb.Status += msg =>
		{
			Status?.Invoke(msg);
			ZkbStatus?.Invoke(msg);
		};
		_chat.FileChanged += () =>
		{
			try { _chatPulse.Set(); } catch { }
		};
	}

	public TrackedCharacter? Character { get; private set; }
	public SystemsDatabase Systems => _systems;
	public ShipDatabase Ships => _ships;
	public MapLayoutDatabase Maps => _maps;
	public string? TryEsiName(int id) => _esiNames.TryGet(id);

	/// <summary>현재 캐릭터 위치 → 성계 점프. 위치/맵 없으면 null.</summary>
	public int? TryJumpDistance(string systemName)
	{
		var loc = Character?.LocationSystem;
		if (string.IsNullOrWhiteSpace(loc) || string.IsNullOrWhiteSpace(systemName))
			return null;
		return _jump.Distance(loc, systemName);
	}
	public bool ZkbRegionOnly
	{
		get => _zkb.RegionOnly;
		set => _zkb.RegionOnly = value;
	}
	public int JumpRange { get; set; } = 4;
	public string ChatlogsDir { get => _chat.ChatlogsDir; set => _chat.ChatlogsDir = value; }
	public string ChannelName { get => _chat.ChannelName; set => _chat.ChannelName = value; }
	public string? ActiveLogPath => _chat.ActiveLogPath;
	public double AlertCooldownSec { get; set; } = 45;

	public event Action<TrackedCharacter>? LocationUpdated;
	public event Action<IntelThreatEvent>? ThreatDetected;
	public event Action<string>? Status;
	public event Action<string?>? ActiveLogChanged;
	public event Action<ZkbLossEvent>? ZkbLoss;
	public event Action<string>? ZkbStatus;

	public void LoadData()
	{
		string sysPath = SystemsDatabase.DefaultPath();
		string mapPath = MapLayoutDatabase.DefaultPath();
		if (!File.Exists(sysPath))
			throw new FileNotFoundException(
				"Systems.dat 없음. eveaa_mod_data 폴더를 exe 옆에 두세요.\n" + sysPath, sysPath);
		if (!File.Exists(mapPath))
			throw new FileNotFoundException(
				"MapLayout.dat 없음. eveaa_mod_data 폴더를 exe 옆에 두세요.\n" + mapPath, mapPath);
		int n = _systems.LoadFromDat(sysPath);
		_jump.Rebuild(_systems);
		int ships = _ships.Load(ShipDatabase.DefaultShipsPath(), ShipDatabase.DefaultAliasesPath());
		int r = _maps.LoadFromDat(mapPath);
		Status?.Invoke($"성계 {n} / 함선 {ships} / 리전맵 {r} 로드");
	}

	public bool MapsReady => _maps.Loaded && _systems.Loaded;

	public void SetCharacter(TrackedCharacter? c) => Character = c;

	public async Task LoginAsync()
	{
		Status?.Invoke("EVE SSO 로그인 중…");
		Character = await _sso.LoginInteractiveAsync();
		Status?.Invoke($"로그인: {Character.CharacterName}");
		await RefreshLocationAsync();
	}

	public async Task RefreshLocationAsync()
	{
		if (Character is null)
		{
			return;
		}
		var idMap = new Dictionary<int, string>(_systems.IdToName);
		await _sso.FetchLocationAsync(Character, idMap);
		if (Character.AllianceId == 0)
		{
			try { Character.AllianceId = await _sso.FetchAllianceIdAsync(Character.CharacterId); }
			catch { }
		}
		UpdateZkbRegionFilter();
		LocationUpdated?.Invoke(Character);
	}

	private void UpdateZkbRegionFilter()
	{
		var c = Character;
		if (c is null || string.IsNullOrEmpty(c.LocationSystem))
		{
			_zkb.FilterRegionId = null;
			return;
		}
		var map = _maps.GetRegionForSystem(c.LocationSystem);
		if (map is null)
		{
			var info = _systems.Get(c.LocationSystem);
			if (info is not null)
				map = _maps.GetRegion(info.Region);
		}
		_zkb.FilterRegionId = map is not null && map.RegionId > 0 ? map.RegionId : null;
	}

	public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

	public void Start()
	{
		// 레이아웃/포커스마다 재시작되면 ZKB·채팅 루프가 취소됨 → 이미 돌면 유지
		if (IsRunning)
		{
			_chat.EnsureWatching();
			UpdateZkbRegionFilter();
			return;
		}
		UpdateZkbRegionFilter();
		_zkb.Start();
		_cts = new CancellationTokenSource();
		var ct = _cts.Token;
		_loop = Task.Run(() => ChatTailLoop(ct), ct);
		_ = Task.Run(() => LocationLoop(ct), ct);
		_chat.EnsureWatching();
		try { _chatPulse.Set(); } catch { }
	}

	/// <summary>UI가 감시 파일을 정하면 끝부터 테일 시작.</summary>
	public void PinChatLog(string? path)
	{
		_chat.PinActiveLog(path);
		string? active = _chat.ActiveLogPath;
		if (!string.Equals(active, _lastActiveLog, StringComparison.OrdinalIgnoreCase))
		{
			_lastActiveLog = active;
			ActiveLogChanged?.Invoke(active);
		}
		try { _chatPulse.Set(); } catch { }
	}

	public void Stop()
	{
		try { _zkb.Stop(); } catch { }
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
		_cts = null;
	}

	private void ChatTailLoop(CancellationToken ct)
	{
		_chat.EnsureWatching();
		while (!ct.IsCancellationRequested)
		{
			try
			{
				PollChatOnce();
			}
			catch (Exception ex)
			{
				Status?.Invoke("엔진 오류: " + ex.Message);
			}

			try
			{
				// 파일 변경 시 즉시, 아니면 25ms 백업 폴링
				bool signaled = _chatPulse.WaitOne(ChatLoopWaitMs);
				if (signaled)
				{
					Thread.Sleep(15); // EVE flush 한 박자
					try { PollChatOnce(); } catch { }
				}
			}
			catch (ObjectDisposedException) { break; }
		}
	}

	private async Task LocationLoop(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				if (Character is not null)
					await RefreshLocationAsync();
			}
			catch (Exception ex)
			{
				Status?.Invoke("위치 오류: " + ex.Message);
			}
			try { await Task.Delay(TimeSpan.FromSeconds(LocationPollSec), ct); }
			catch (OperationCanceledException) { break; }
		}
	}

	private void PollChatOnce()
	{
		if (string.IsNullOrWhiteSpace(ChannelName))
			return;

		_chat.PreferredCharacterId = Character?.CharacterId ?? 0;

		bool systemsReady = _systems.Loaded;
		bool hasLoc = Character is not null && !string.IsNullOrEmpty(Character.LocationSystem);
		string channel = ChannelName.Trim();

		foreach (var item in _chat.Poll(_systems, _ships, _chars))
		{
			string character = item.Characters.Count > 0 ? string.Join(", ", item.Characters) : "";
			string ship = item.Ships.Count > 0 ? string.Join(", ", item.Ships) : "";
			if (item.Characters.Count > 0)
				_chars.EnqueueMany(item.Characters);

			string? bestSys = null;
			int bestDist = -1;
			bool isAlert = false;

			if (systemsReady && item.Systems.Count > 0)
			{
				if (hasLoc)
				{
					int nearest = int.MaxValue;
					string? nearestSys = null;
					foreach (string sys in item.Systems)
					{
						int? dist = _jump.Distance(Character!.LocationSystem, sys);
						if (dist is null) continue;
						if (dist.Value < nearest)
						{
							nearest = dist.Value;
							nearestSys = sys;
						}
					}
					bestSys = nearestSys ?? item.Systems[0];
					bestDist = nearestSys is null ? -1 : nearest;
					isAlert = nearestSys is not null && nearest <= JumpRange;
				}
				else
				{
					bestSys = string.Join(", ", item.Systems);
					bestDist = -1;
					isAlert = true;
				}
			}
			else if (item.Systems.Count > 0)
			{
				bestSys = string.Join(", ", item.Systems);
			}

			if (isAlert && !string.IsNullOrEmpty(bestSys))
			{
				string key = bestSys.Split(',')[0].Trim();
				if (_cooldown.TryGetValue(key, out DateTime last) &&
				    (DateTime.UtcNow - last).TotalSeconds < AlertCooldownSec)
				{
					isAlert = false;
				}
				else if (isAlert)
				{
					_cooldown[key] = DateTime.UtcNow;
				}
			}

			ThreatDetected?.Invoke(new IntelThreatEvent
			{
				TimeText = item.TimeText,
				Channel = channel,
				System = bestSys ?? "",
				Character = character,
				Ship = ship,
				Message = item.Message,
				Speaker = item.Speaker,
				Jumps = bestDist,
				IsAlert = isAlert,
				IsClear = item.IsClear,
				IsKillReport = item.IsKillReport,
				Raw = item.Raw,
				HostileCount = item.HostileCount,
				HostileCountIsPlus = item.HostileCountIsPlus,
				HostileCountIsExact = item.HostileCountIsExact,
				GateSystem = item.GateSystem,
				GateIsAnsiblex = item.GateIsAnsiblex,
				MovementVerb = item.MovementVerb,
				MovementSystem = item.MovementSystem,
				MovementIsGate = item.MovementIsGate,
				IsQuestion = item.IsQuestion,
				QuestionType = item.QuestionType
			});
		}

		string? active = _chat.ActiveLogPath;
		if (!string.Equals(active, _lastActiveLog, StringComparison.OrdinalIgnoreCase))
		{
			_lastActiveLog = active;
			ActiveLogChanged?.Invoke(active);
			if (active != null)
				Status?.Invoke("인텔 로그: " + Path.GetFileName(active));
		}
	}

	public void Dispose()
	{
		Stop();
		try { _chat.Dispose(); } catch { }
		try { _chatPulse.Dispose(); } catch { }
		_chars.Dispose();
		_zkb.Dispose();
		_esiNames.Dispose();
	}
}