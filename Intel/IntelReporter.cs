using System.Net.Http;
using System.Net.Http.Json;

namespace EVEAA.Mod.Intel;

/// <summary>
/// 인텔 이벤트를 중앙 서버(디스코드 봇)로 전송. 여러 유저 중 "누가 먼저 켰는지"로
/// 활성 소스를 정하는 건 서버 쪽 책임이고, 여기는 그냥 매 줄 + 주기적 하트비트를 쏘기만 함.
/// 네트워크 실패는 절대 로컬 동작(알림/로그)에 영향 주면 안 되므로 전부 무시하고 삼킴.
/// </summary>
internal sealed class IntelReporter : IDisposable
{
	private readonly ModSettings _settings;
	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
	private readonly System.Threading.Timer _heartbeatTimer;
	private readonly DateTime _sessionStartedAtUtc = DateTime.UtcNow;

	public IntelReporter(ModSettings settings)
	{
		_settings = settings;
		if (string.IsNullOrEmpty(_settings.IntelReportClientId))
		{
			_settings.IntelReportClientId = Guid.NewGuid().ToString("N");
			_settings.Save();
		}
		_heartbeatTimer = new System.Threading.Timer(_ => _ = SendHeartbeatAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
	}

	public void ReportThreat(IntelThreatEvent ev)
	{
		if (!IsConfigured()) return;
		_ = PostAsync("/intel/report", new
		{
			client_id = _settings.IntelReportClientId,
			character = _settings.IntelCharacterName ?? "",
			session_started_at = _sessionStartedAtUtc,
			time_text = ev.TimeText,
			channel = ev.Channel,
			system = ev.System,
			character_mentioned = ev.Character,
			ship = ev.Ship,
			message = ev.Message,
			speaker = ev.Speaker,
			jumps = ev.Jumps,
			is_alert = ev.IsAlert,
			is_clear = ev.IsClear,
			is_kill_report = ev.IsKillReport,
			raw = ev.Raw,
			hostile_count = ev.HostileCount,
			hostile_count_is_plus = ev.HostileCountIsPlus,
			hostile_count_is_exact = ev.HostileCountIsExact,
			gate_system = ev.GateSystem,
			gate_is_ansiblex = ev.GateIsAnsiblex,
			movement_verb = ev.MovementVerb,
			movement_system = ev.MovementSystem,
			movement_is_gate = ev.MovementIsGate
		});
	}

	private async Task SendHeartbeatAsync()
	{
		if (!IsConfigured()) return;
		await PostAsync("/intel/heartbeat", new
		{
			client_id = _settings.IntelReportClientId,
			character = _settings.IntelCharacterName ?? "",
			session_started_at = _sessionStartedAtUtc
		}).ConfigureAwait(false);
	}

	private bool IsConfigured() =>
		_settings.IntelReportEnabled && !string.IsNullOrWhiteSpace(_settings.IntelReportUrl);

	private async Task PostAsync(string path, object payload)
	{
		try
		{
			string url = _settings.IntelReportUrl!.TrimEnd('/') + path;
			using var req = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = JsonContent.Create(payload)
			};
			if (!string.IsNullOrEmpty(_settings.IntelReportApiKey))
				req.Headers.Add("X-API-Key", _settings.IntelReportApiKey);
			using HttpResponseMessage _ = await _http.SendAsync(req).ConfigureAwait(false);
		}
		catch
		{
			// 인텔 전송은 부가 기능 — 네트워크 오류가 로컬 감지/알림을 막으면 안 됨
		}
	}

	public void Dispose()
	{
		try { _heartbeatTimer.Dispose(); } catch { }
		try { _http.Dispose(); } catch { }
	}
}
