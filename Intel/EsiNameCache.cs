using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EVEAA.Mod.Intel;

/// <summary>ESI /universe/names 로 type/alliance/corp 이름 캐시.</summary>
internal sealed class EsiNameCache : IDisposable
{
	private readonly ConcurrentDictionary<int, string> _names = new();
	private readonly ConcurrentQueue<int> _queue = new();
	private readonly ConcurrentDictionary<int, byte> _queued = new();
	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
	private readonly string _cachePath;
	private CancellationTokenSource? _cts;
	private Task? _worker;

	public EsiNameCache()
	{
		string dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"EVEAA.Mod");
		Directory.CreateDirectory(dir);
		_cachePath = Path.Combine(dir, "esi_names.json");
		Load();
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("EVEAA-Mod-ZKB/1.0");
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		_cts = new CancellationTokenSource();
		_worker = Task.Run(() => WorkerAsync(_cts.Token));
	}

	public string? TryGet(int id) =>
		id > 0 && _names.TryGetValue(id, out string? n) ? n : null;

	public void Enqueue(int id)
	{
		if (id <= 0 || _names.ContainsKey(id)) return;
		if (!_queued.TryAdd(id, 0)) return;
		_queue.Enqueue(id);
	}

	public void EnqueueMany(IEnumerable<int> ids)
	{
		foreach (int id in ids)
			Enqueue(id);
	}

	private async Task WorkerAsync(CancellationToken ct)
	{
		var batch = new List<int>(100);
		while (!ct.IsCancellationRequested)
		{
			batch.Clear();
			while (batch.Count < 80 && _queue.TryDequeue(out int id))
			{
				_queued.TryRemove(id, out _);
				if (!_names.ContainsKey(id))
					batch.Add(id);
			}

			if (batch.Count == 0)
			{
				try { await Task.Delay(400, ct); } catch { break; }
				continue;
			}

			try
			{
				await ResolveBatchAsync(batch, ct);
				Save();
			}
			catch
			{
				foreach (int id in batch)
					Enqueue(id);
				try { await Task.Delay(2000, ct); } catch { break; }
			}

			try { await Task.Delay(200, ct); } catch { break; }
		}
	}

	private async Task ResolveBatchAsync(List<int> ids, CancellationToken ct)
	{
		string json = JsonSerializer.Serialize(ids);
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _http.PostAsync(
			"https://esi.evetech.net/latest/universe/names/", content, ct);
		if (!resp.IsSuccessStatusCode) return;
		string body = await resp.Content.ReadAsStringAsync(ct);
		using var doc = JsonDocument.Parse(body);
		foreach (var el in doc.RootElement.EnumerateArray())
		{
			if (!el.TryGetProperty("id", out var idEl)) continue;
			if (!el.TryGetProperty("name", out var nameEl)) continue;
			int id = idEl.GetInt32();
			string name = nameEl.GetString() ?? "";
			if (id > 0 && name.Length > 0)
				_names[id] = name;
		}
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(_cachePath)) return;
			string body = File.ReadAllText(_cachePath);
			var map = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
			if (map is null) return;
			foreach (var kv in map)
			{
				if (int.TryParse(kv.Key, out int id) && !string.IsNullOrWhiteSpace(kv.Value))
					_names[id] = kv.Value;
			}
		}
		catch { }
	}

	private void Save()
	{
		try
		{
			var map = new Dictionary<string, string>();
			foreach (var kv in _names)
				map[kv.Key.ToString()] = kv.Value;
			File.WriteAllText(_cachePath, JsonSerializer.Serialize(map));
		}
		catch { }
	}

	public void Dispose()
	{
		try { _cts?.Cancel(); } catch { }
		_cts = null;
		_http.Dispose();
		Save();
	}
}