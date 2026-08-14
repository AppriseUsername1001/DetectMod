using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EVEAA.Mod.Intel;

internal enum CharResolveStatus
{
	Unknown = 0,
	Exists = 1,
	DoesNotExist = 2
}

internal sealed class CharacterResolver : IDisposable
{
	private static readonly Regex NameRe = new(
		@"^[A-Za-z0-9][A-Za-z0-9 '.-]{1,35}[A-Za-z0-9]$|^[A-Za-z0-9]{3,37}$",
		RegexOptions.Compiled);

	private readonly ConcurrentDictionary<string, CharResolveStatus> _cache =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<string> _queue = new();
	private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
	private readonly object _saveLock = new();
	private CancellationTokenSource? _cts;
	private Task? _worker;
	private readonly string _cachePath;

	public CharacterResolver()
	{
		string dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"EVEAA.Mod");
		Directory.CreateDirectory(dir);
		_cachePath = Path.Combine(dir, "char_cache.json");
		LoadCache();
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("EVEAA-Mod-Intel/2.26");
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		_cts = new CancellationTokenSource();
		_worker = Task.Run(() => WorkerAsync(_cts.Token));
	}

	public static bool LooksLikeCharacterName(string token)
	{
		token = (token ?? "").Trim();
		if (token.Length < 3 || token.Length > 37) return false;
		if (token.Contains("://", StringComparison.Ordinal)) return false;
		return NameRe.IsMatch(token);
	}

	/// <summary>
	/// RIFT는 자체 영단어 사전으로 "are kidding now" 같은 평범한 소문자 문장 조각이 캐릭터로
	/// 오인식되는 걸 막는다. 우리는 사전이 없으므로 대신 대문자 시작(Title Case) 여부를 대리 신호로
	/// 쓴다 — EVE 캐릭터명은 항상 각 단어가 대문자로 시작하므로, 아직 ESI로 미확인(Unknown)인
	/// 후보는 이 조건을 통과해야만 캐릭터 후보 점수를 받는다. 이미 ESI로 실존 확인(Exists)된 이름은
	/// 이 검사를 건너뛴다(신뢰할 수 있는 정답이므로).
	/// </summary>
	public static bool IsTitleCaseName(string phrase)
	{
		phrase = (phrase ?? "").Trim();
		if (phrase.Length == 0) return false;
		foreach (string word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			char c0 = word[0];
			if (char.IsLetter(c0) && !char.IsUpper(c0)) return false;
		}
		return true;
	}

	/// <summary>ESI로 확인된(또는 아직 미확인인) 상태 조회. 캐시에 없으면 Unknown. 후보 분할 채점용.</summary>
	public CharResolveStatus GetStatus(string name)
	{
		name = (name ?? "").Trim();
		return _cache.TryGetValue(name, out CharResolveStatus st) ? st : CharResolveStatus.Unknown;
	}

	/// <summary>하이브리드: 캐시에 DoesNotExist면 false, 그 외(존재/미확인)는 true.</summary>
	public bool AcceptAsCharacter(string name)
	{
		if (!LooksLikeCharacterName(name)) return false;
		if (_cache.TryGetValue(name, out CharResolveStatus st))
		{
			if (st == CharResolveStatus.DoesNotExist) return false;
			return true;
		}
		Enqueue(name);
		return true;
	}

	public void Enqueue(string name)
	{
		name = (name ?? "").Trim();
		if (name.Length < 3) return;
		if (_cache.ContainsKey(name)) return;
		if (!_queued.TryAdd(name, 0)) return;
		_queue.Enqueue(name);
	}

	public void EnqueueMany(IEnumerable<string> names)
	{
		foreach (string n in names)
			Enqueue(n);
	}

	private async Task WorkerAsync(CancellationToken ct)
	{
		var batch = new List<string>();
		while (!ct.IsCancellationRequested)
		{
			batch.Clear();
			while (batch.Count < 80 && _queue.TryDequeue(out string? name))
			{
				_queued.TryRemove(name, out _);
				if (!_cache.ContainsKey(name))
					batch.Add(name);
			}

			if (batch.Count == 0)
			{
				try { await Task.Delay(400, ct); } catch { break; }
				continue;
			}

			try
			{
				await ResolveBatchAsync(batch, ct);
				SaveCache();
			}
			catch
			{
				try { await Task.Delay(1500, ct); } catch { break; }
			}
		}
	}

	private async Task ResolveBatchAsync(List<string> names, CancellationToken ct)
	{
		// ESI universe/ids accepts up to ~500 names; we send smaller batches
		string json = JsonSerializer.Serialize(names);
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _http.PostAsync(
			"https://esi.evetech.net/latest/universe/ids/?datasource=tranquility",
			content, ct);
		if (!resp.IsSuccessStatusCode)
		{
			// soft-fail: leave unknown
			return;
		}

		string body = await resp.Content.ReadAsStringAsync(ct);
		using var doc = JsonDocument.Parse(body);
		var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (doc.RootElement.TryGetProperty("characters", out JsonElement chars) &&
		    chars.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement c in chars.EnumerateArray())
			{
				if (c.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
				{
					string name = n.GetString() ?? "";
					if (name.Length > 0)
					{
						found.Add(name);
						_cache[name] = CharResolveStatus.Exists;
					}
				}
			}
		}

		foreach (string name in names)
		{
			if (!_cache.ContainsKey(name))
				_cache[name] = found.Contains(name) ? CharResolveStatus.Exists : CharResolveStatus.DoesNotExist;
		}
	}

	private void LoadCache()
	{
		try
		{
			if (!File.Exists(_cachePath)) return;
			var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_cachePath));
			if (dict is null) return;
			foreach (var kv in dict)
			{
				if (Enum.IsDefined(typeof(CharResolveStatus), kv.Value))
					_cache[kv.Key] = (CharResolveStatus)kv.Value;
			}
		}
		catch { }
	}

	private void SaveCache()
	{
		try
		{
			lock (_saveLock)
			{
				var dict = _cache.ToDictionary(kv => kv.Key, kv => (int)kv.Value, StringComparer.OrdinalIgnoreCase);
				File.WriteAllText(_cachePath, JsonSerializer.Serialize(dict));
			}
		}
		catch { }
	}

	public void Dispose()
	{
		try { _cts?.Cancel(); } catch { }
		_cts = null;
		try { SaveCache(); } catch { }
		_http.Dispose();
	}
}