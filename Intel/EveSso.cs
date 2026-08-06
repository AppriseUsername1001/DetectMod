using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EVEAA.Mod.Intel;

internal sealed class TrackedCharacter
{
	public int CharacterId { get; set; }
	public string CharacterName { get; set; } = "";
	public string RefreshToken { get; set; } = "";
	public string AccessToken { get; set; } = "";
	public DateTimeOffset ExpiresAt { get; set; }
	public string LocationSystem { get; set; } = "";
	public int LocationSystemId { get; set; }
	/// <summary>0이면 아직 조회 전. ZKB 로스의 얼라이언스 색 구분에 사용.</summary>
	public int AllianceId { get; set; }
}

internal sealed class EveSso
{
	public const string ClientId = "4f510f2903a7480cb0a286a333303c6a";
	public const string CallbackUrl = "http://localhost:8762/callback/";
	public const int CallbackPort = 8762;
	public const string Scopes = "esi-location.read_location.v1 esi-location.read_online.v1";
	private const string SsoAuth = "https://login.eveonline.com/v2/oauth/authorize/";
	private const string SsoToken = "https://login.eveonline.com/v2/oauth/token";
	private const string EsiBase = "https://esi.evetech.net/latest";
	private const string UserAgent = "EVEAA-Mod-Intel/2.26.5";

	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

	public EveSso()
	{
		_http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
	}

	public async Task<TrackedCharacter> LoginInteractiveAsync(CancellationToken ct = default)
	{
		byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
		string codeVerifier = Base64Url(verifierBytes);
		string codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
		string state = Base64Url(RandomNumberGenerator.GetBytes(16));

		string authUrl = SsoAuth + "?" + string.Join("&", new[]
		{
			"response_type=code",
			"redirect_uri=" + Uri.EscapeDataString(CallbackUrl),
			"client_id=" + Uri.EscapeDataString(ClientId),
			"scope=" + Uri.EscapeDataString(Scopes),
			"state=" + Uri.EscapeDataString(state),
			"code_challenge=" + Uri.EscapeDataString(codeChallenge),
			"code_challenge_method=S256",
		});

		var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

		// HttpListener needs Windows URL ACL; use TcpListener like eve_intel_alert.
		// Listen on 127.0.0.1 and ::1 (Chrome may resolve localhost to IPv6).
		var listeners = new List<TcpListener>();
		try
		{
			foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
			{
				try
				{
					var l = new TcpListener(ip, CallbackPort);
					l.Start();
					listeners.Add(l);
				}
				catch (SocketException)
				{
				}
			}
			if (listeners.Count == 0)
				throw new SocketException(10048);
		}
		catch (SocketException ex)
		{
			throw new InvalidOperationException(
				$"SSO 콜백 포트 {CallbackPort}를 열 수 없습니다. 다른 프로그램이 사용 중인지 확인하세요.\n{ex.Message}");
		}

		using var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var acceptTasks = listeners
			.Select(l => Task.Run(() => AcceptCallbackAsync(l, state, tcs, listenerCts.Token), CancellationToken.None))
			.ToArray();

		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = authUrl,
				UseShellExecute = true
			});
		}
		catch
		{
			// 브라우저 자동 실행 실패 시에도 사용자가 URL을 열 수 있음
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
		await using (linked.Token.Register(() => tcs.TrySetCanceled()))
		{
			try
			{
				string authCode = await tcs.Task.ConfigureAwait(false);
				if (string.IsNullOrEmpty(authCode))
					throw new InvalidOperationException("인증 코드를 받지 못했습니다.");

				return await ExchangeCodeAsync(authCode, codeVerifier, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested)
			{
				throw new TimeoutException("SSO 로그인 시간 초과 (3분)");
			}
			finally
			{
				listenerCts.Cancel();
				foreach (var l in listeners)
				{
					try { l.Stop(); } catch { }
				}
				try { await Task.WhenAll(acceptTasks).ConfigureAwait(false); } catch { }
			}
		}
	}

	private static async Task AcceptCallbackAsync(
		TcpListener listener,
		string expectedState,
		TaskCompletionSource<string> tcs,
		CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested && !tcs.Task.IsCompleted)
			{
				TcpClient client;
				try
				{
					client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (ObjectDisposedException)
				{
					break;
				}

				_ = Task.Run(async () =>
				{
					try
					{
						await HandleClientAsync(client, expectedState, tcs).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						tcs.TrySetException(ex);
					}
					finally
					{
						try { client.Dispose(); } catch { }
					}
				}, CancellationToken.None);
			}
		}
		catch (Exception ex)
		{
			tcs.TrySetException(ex);
		}
	}

	private static async Task HandleClientAsync(
		TcpClient client,
		string expectedState,
		TaskCompletionSource<string> tcs)
	{
		using var stream = client.GetStream();
		stream.ReadTimeout = 10000;
		stream.WriteTimeout = 10000;

		string request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
		if (string.IsNullOrEmpty(request))
		{
			await WriteHttpAsync(stream, 400, "text/plain", "Bad Request").ConfigureAwait(false);
			return;
		}

		string firstLine = request.Split('\n')[0].Trim();
		string[] parts = firstLine.Split(' ');
		if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
		{
			await WriteHttpAsync(stream, 405, "text/plain", "Method Not Allowed").ConfigureAwait(false);
			return;
		}

		if (!Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out Uri? uri) ||
		    !uri.AbsolutePath.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
		{
			await WriteHttpAsync(stream, 404, "text/plain", "Not Found").ConfigureAwait(false);
			return;
		}

		var q = ParseQuery(uri.Query);
		if ((q.TryGetValue("state", out string? st) ? st : null) != expectedState)
		{
			await WriteHttpAsync(stream, 400, "text/html; charset=utf-8", "<html><body>Invalid state</body></html>").ConfigureAwait(false);
			tcs.TrySetException(new InvalidOperationException("Invalid SSO state"));
			return;
		}

		if (q.ContainsKey("error"))
		{
			string msg = q.GetValueOrDefault("error_description") ?? q.GetValueOrDefault("error") ?? "SSO denied";
			await WriteHttpAsync(stream, 400, "text/html; charset=utf-8", "<html><body>Authorization failed</body></html>").ConfigureAwait(false);
			tcs.TrySetException(new InvalidOperationException(msg));
			return;
		}

		string code = q.GetValueOrDefault("code") ?? "";
		await WriteHttpAsync(
			stream,
			200,
			"text/html; charset=utf-8",
			"<html><body><h2>로그인 완료</h2><p>이 창을 닫고 앱으로 돌아가세요.</p></body></html>").ConfigureAwait(false);
		tcs.TrySetResult(code);
	}

	private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
	{
		var buf = new byte[8192];
		var ms = new MemoryStream();
		while (ms.Length < 65536)
		{
			int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
			if (n <= 0) break;
			ms.Write(buf, 0, n);
			string soFar = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
			if (soFar.Contains("\r\n\r\n", StringComparison.Ordinal))
				return soFar;
		}
		return Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
	}

	private static async Task WriteHttpAsync(NetworkStream stream, int status, string contentType, string body)
	{
		byte[] payload = Encoding.UTF8.GetBytes(body);
		string reason = status switch
		{
			200 => "OK",
			400 => "Bad Request",
			404 => "Not Found",
			405 => "Method Not Allowed",
			_ => "Error"
		};
		string header =
			$"HTTP/1.1 {status} {reason}\r\n" +
			$"Content-Type: {contentType}\r\n" +
			$"Content-Length: {payload.Length}\r\n" +
			"Connection: close\r\n\r\n";
		byte[] headerBytes = Encoding.ASCII.GetBytes(header);
		await stream.WriteAsync(headerBytes).ConfigureAwait(false);
		await stream.WriteAsync(payload).ConfigureAwait(false);
		await stream.FlushAsync().ConfigureAwait(false);
	}

	private async Task<TrackedCharacter> ExchangeCodeAsync(string authCode, string codeVerifier, CancellationToken ct)
	{
		var form = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = authCode,
			["client_id"] = ClientId,
			["code_verifier"] = codeVerifier,
			["redirect_uri"] = CallbackUrl,
		};
		using var content = new FormUrlEncodedContent(form);
		using var resp = await _http.PostAsync(SsoToken, content, ct).ConfigureAwait(false);
		string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
			throw new InvalidOperationException("토큰 교환 실패: " + body);
		using var doc = JsonDocument.Parse(body);
		string access = doc.RootElement.GetProperty("access_token").GetString() ?? "";
		string refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
		int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1200;
		var (charId, charName) = ParseJwtCharacter(access);
		return new TrackedCharacter
		{
			CharacterId = charId,
			CharacterName = charName,
			AccessToken = access,
			RefreshToken = refresh,
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30)
		};
	}

	public async Task EnsureTokenAsync(TrackedCharacter chara, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(chara.RefreshToken))
			return;
		// 액세스 토큰이 아직 유효하면 스킵
		if (!string.IsNullOrEmpty(chara.AccessToken) && DateTimeOffset.UtcNow < chara.ExpiresAt)
			return;
		var form = new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = chara.RefreshToken,
			["client_id"] = ClientId,
		};
		using var content = new FormUrlEncodedContent(form);
		using var resp = await _http.PostAsync(SsoToken, content, ct).ConfigureAwait(false);
		string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
			throw new InvalidOperationException("토큰 갱신 실패");
		using var doc = JsonDocument.Parse(body);
		chara.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? chara.AccessToken;
		if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
			chara.RefreshToken = rt.GetString() ?? chara.RefreshToken;
		int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1200;
		chara.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
	}

	/// <summary>공개 ESI(인증 불필요)로 캐릭터 얼라이언스 ID만 조회. 실패 시 0.</summary>
	public async Task<int> FetchAllianceIdAsync(int characterId, CancellationToken ct = default)
	{
		using var resp = await _http.GetAsync($"{EsiBase}/characters/{characterId}/", ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode) return 0;
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		return doc.RootElement.TryGetProperty("alliance_id", out var a) ? a.GetInt32() : 0;
	}

	public async Task<bool> FetchLocationAsync(TrackedCharacter chara, IDictionary<int, string> idToName, CancellationToken ct = default)
	{
		await EnsureTokenAsync(chara, ct).ConfigureAwait(false);
		using var req = new HttpRequestMessage(HttpMethod.Get, $"{EsiBase}/characters/{chara.CharacterId}/location/");
		req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", chara.AccessToken);
		using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode) return false;
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		int sysId = doc.RootElement.TryGetProperty("solar_system_id", out var sid) ? sid.GetInt32() : 0;
		if (sysId <= 0) return false;
		chara.LocationSystemId = sysId;
		if (idToName.TryGetValue(sysId, out string? known))
		{
			chara.LocationSystem = known;
			return true;
		}
		using var req2 = new HttpRequestMessage(HttpMethod.Get, $"{EsiBase}/universe/systems/{sysId}/");
		req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", chara.AccessToken);
		using var resp2 = await _http.SendAsync(req2, ct).ConfigureAwait(false);
		if (resp2.IsSuccessStatusCode)
		{
			using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
			string name = doc2.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
			chara.LocationSystem = name;
			if (!string.IsNullOrEmpty(name))
				idToName[sysId] = name;
		}
		return !string.IsNullOrEmpty(chara.LocationSystem);
	}

	private static (int id, string name) ParseJwtCharacter(string accessToken)
	{
		string[] parts = accessToken.Split('.');
		if (parts.Length < 2) throw new InvalidOperationException("JWT 파싱 실패");
		string payload = parts[1];
		payload = payload.Replace('-', '+').Replace('_', '/');
		switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
		using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
		string sub = doc.RootElement.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
		string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
		int id = 0;
		if (sub.Contains(':'))
			int.TryParse(sub.Split(':').Last(), out id);
		if (id <= 0) throw new InvalidOperationException("캐릭터 ID 없음");
		return (id, name);
	}

	private static string Base64Url(byte[] data) =>
		Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static Dictionary<string, string> ParseQuery(string query)
	{
		var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(query)) return d;
		if (query.StartsWith('?')) query = query[1..];
		foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int i = part.IndexOf('=');
			if (i < 0) d[Uri.UnescapeDataString(part)] = "";
			else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
		}
		return d;
	}
}
