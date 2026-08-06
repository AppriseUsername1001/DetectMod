using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EVEAA.Mod.Intel;

internal sealed class ZkbLossEvent
{
	public long KillmailId { get; init; }
	public int SolarSystemId { get; init; }
	public string SystemName { get; init; } = "";
	public int RegionId { get; init; }
	public string RegionName { get; init; } = "";
	public int? VictimAllianceId { get; init; }
	public int? VictimCorpId { get; init; }
	public int ShipTypeId { get; init; }
	public string AllianceText { get; init; } = "";
	public string ShipText { get; init; } = "";
	public bool IsNpc { get; init; }
	public DateTime KillTimeUtc { get; init; }
}

/// <summary>
/// zKillboard R2Z2 ephemeral feed.
/// 킬메일 1건 = 로스 1행 (attacker 별 중복 없음). 현재 리전만 통과.
/// </summary>
internal sealed class ZkbFeedService : IDisposable
{
	private const string BaseUrl = "https://r2z2.zkillboard.com/ephemeral";
	private const int SleepWhenCaughtUpMs = 6000; // R2Z2 공식 최소 대기
	private const int SleepBetweenOkMs = 100;
	private const int MaxSeen = 800;

	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
	private readonly SystemsDatabase _systems;
	private readonly MapLayoutDatabase _maps;
	private readonly EsiNameCache _names;
	private readonly ConcurrentDictionary<long, byte> _seen = new();
	private readonly ConcurrentQueue<long> _seenOrder = new();
	private CancellationTokenSource? _cts;
	private Task? _loop;
	private long _sequence;

	public int? FilterRegionId { get; set; }
	public bool RegionOnly { get; set; } = true;

	public event Action<ZkbLossEvent>? LossReceived;
	public event Action<string>? Status;

	public ZkbFeedService(SystemsDatabase systems, MapLayoutDatabase maps, EsiNameCache names)
	{
		_systems = systems;
		_maps = maps;
		_names = names;
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("EVEAA-Mod-ZKB/1.0 (local)");
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	}

	public void Start()
	{
		Stop();
		_cts = new CancellationTokenSource();
		_loop = Task.Run(() => LoopAsync(_cts.Token));
	}

	public void Stop()
	{
		try { _cts?.Cancel(); } catch { }
		_cts = null;
	}

	private async Task LoopAsync(CancellationToken ct)
	{
		Status?.Invoke("ZKB: 시퀀스 확인 중…");
		try
		{
			_sequence = await FetchCurrentSequenceAsync(ct);
			Status?.Invoke($"ZKB: 실시간 수신 (seq {_sequence})");
		}
		catch (Exception ex)
		{
			Status?.Invoke("ZKB 시작 실패: " + ex.Message);
			_sequence = 0;
		}

		while (!ct.IsCancellationRequested)
		{
			try
			{
				if (_sequence <= 0)
				{
					_sequence = await FetchCurrentSequenceAsync(ct);
					await Task.Delay(1000, ct);
					continue;
				}

				var (ok, body) = await TryGetSequenceAsync(_sequence, ct);
				if (ok && body is not null)
				{
					TryHandlePackage(body);
					_sequence++;
					await Task.Delay(SleepBetweenOkMs, ct);
				}
				else
				{
					// 404 = 따라잡음. 공식 최소 6초 대기 (더 짧게 폴링하면 403 위험)
					await Task.Delay(SleepWhenCaughtUpMs, ct);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				Status?.Invoke("ZKB: " + ex.Message);
				try { await Task.Delay(3000, ct); } catch { break; }
			}
		}
	}

	private async Task<long> FetchCurrentSequenceAsync(CancellationToken ct)
	{
		using var resp = await _http.GetAsync($"{BaseUrl}/sequence.json", ct);
		resp.EnsureSuccessStatusCode();
		string body = await resp.Content.ReadAsStringAsync(ct);
		using var doc = JsonDocument.Parse(body);
		return doc.RootElement.GetProperty("sequence").GetInt64();
	}

	private async Task<(bool ok, string? body)> TryGetSequenceAsync(long seq, CancellationToken ct)
	{
		using var resp = await _http.GetAsync($"{BaseUrl}/{seq}.json", ct);
		if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
			return (false, null);
		if ((int)resp.StatusCode == 429 || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
		{
			Status?.Invoke($"ZKB HTTP {(int)resp.StatusCode} — 대기");
			await Task.Delay(15000, ct);
			return (false, null);
		}
		resp.EnsureSuccessStatusCode();
		string body = await resp.Content.ReadAsStringAsync(ct);
		return (true, body);
	}

	private void TryHandlePackage(string body)
	{
		using var doc = JsonDocument.Parse(body);
		var root = doc.RootElement;
		if (!root.TryGetProperty("killmail_id", out var kmEl))
			return;
		long killId = kmEl.GetInt64();
		if (!_seen.TryAdd(killId, 0))
			return;
		_seenOrder.Enqueue(killId);
		while (_seenOrder.Count > MaxSeen && _seenOrder.TryDequeue(out long old))
			_seen.TryRemove(old, out _);

		if (!root.TryGetProperty("esi", out var esi))
			return;
		if (!esi.TryGetProperty("solar_system_id", out var sysEl))
			return;
		int systemId = sysEl.GetInt32();
		if (!TryResolveRegion(systemId, out int regionId, out string regionName, out string systemName))
			return;

		if (RegionOnly)
		{
			int? want = FilterRegionId;
			if (want is null || want.Value <= 0 || want.Value != regionId)
				return;
		}

		if (!esi.TryGetProperty("victim", out var victim))
			return;

		int shipTypeId = victim.TryGetProperty("ship_type_id", out var shipEl) ? shipEl.GetInt32() : 0;
		int? allianceId = victim.TryGetProperty("alliance_id", out var aEl) ? aEl.GetInt32() : null;
		int? corpId = victim.TryGetProperty("corporation_id", out var cEl) ? cEl.GetInt32() : null;

		bool npc = false;
		if (root.TryGetProperty("zkb", out var zkb) && zkb.TryGetProperty("npc", out var npcEl))
			npc = npcEl.ValueKind == JsonValueKind.True;

		DateTime killTime = DateTime.UtcNow;
		if (esi.TryGetProperty("killmail_time", out var tEl) &&
		    DateTime.TryParse(tEl.GetString(), null,
			    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
			killTime = parsed.ToUniversalTime();

		_names.Enqueue(shipTypeId);
		if (allianceId is int aid) _names.Enqueue(aid);
		if (corpId is int cid) _names.Enqueue(cid);

		string shipText = _names.TryGet(shipTypeId) ?? (shipTypeId > 0 ? $"#{shipTypeId}" : "-");
		string allianceText;
		if (allianceId is int a2 && _names.TryGet(a2) is string an)
			allianceText = an;
		else if (corpId is int c2 && _names.TryGet(c2) is string cn)
			allianceText = cn;
		else if (allianceId is int)
			allianceText = $"Alliance #{allianceId}";
		else if (corpId is int)
			allianceText = $"Corp #{corpId}";
		else
			allianceText = npc ? "NPC" : "-";

		LossReceived?.Invoke(new ZkbLossEvent
		{
			KillmailId = killId,
			SolarSystemId = systemId,
			SystemName = systemName,
			RegionId = regionId,
			RegionName = regionName,
			VictimAllianceId = allianceId,
			VictimCorpId = corpId,
			ShipTypeId = shipTypeId,
			AllianceText = allianceText,
			ShipText = shipText,
			IsNpc = npc,
			KillTimeUtc = killTime
		});
	}

	private bool TryResolveRegion(int systemId, out int regionId, out string regionName, out string systemName)
	{
		regionId = 0;
		regionName = "";
		systemName = "";
		if (!_systems.IdToName.TryGetValue(systemId, out systemName) || string.IsNullOrEmpty(systemName))
			return false;

		var info = _systems.Get(systemName);
		regionName = info?.Region ?? "";
		var map = _maps.GetRegionForSystem(systemName) ??
		          (string.IsNullOrEmpty(regionName) ? null : _maps.GetRegion(regionName));
		if (map is null || map.RegionId <= 0)
			return false;
		regionId = map.RegionId;
		if (string.IsNullOrEmpty(regionName))
			regionName = map.Name;
		return true;
	}

	public void Dispose()
	{
		Stop();
		_http.Dispose();
	}
}