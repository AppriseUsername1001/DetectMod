using System.Net.Http.Headers;
using System.Text.Json;

namespace EVEAA.Mod.Intel;

/// <summary>zKillboard 검색/킬 URL 해석.</summary>
internal static class ZkillLinkHelper
{
	private static readonly HttpClient Http;

	static ZkillLinkHelper()
	{
		Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		Http.DefaultRequestHeaders.UserAgent.ParseAdd("EVEAA-Mod-Intel/2.26");
		Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	}

	public static string SearchUrl(string name)
	{
		string q = (name ?? "").Trim();
		if (q.Length == 0) return "https://zkillboard.com/search/";
		return "https://zkillboard.com/search/" + Uri.EscapeDataString(q) + "/";
	}

	public static string CharacterUrl(int characterId) =>
		$"https://zkillboard.com/character/{characterId}/";

	public static string KillUrl(long killmailId) =>
		$"https://zkillboard.com/kill/{killmailId}/";

	public static string NormalizeCharacterName(string? name)
	{
		string n = (name ?? "").Trim();
		if (n.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
			n = n[..^2].Trim();
		else if (n.EndsWith('\'') || n.EndsWith('\u2019'))
			n = n[..^1].Trim();
		int comma = n.IndexOf(',');
		if (comma > 0) n = n[..comma].Trim();
		return n;
	}

	public static async Task<int?> TryResolveCharacterIdAsync(string name, CancellationToken ct = default)
	{
		name = NormalizeCharacterName(name);
		if (name.Length < 3) return null;
		try
		{
			string url = "https://zkillboard.com/autocomplete/characterID/" +
			             Uri.EscapeDataString(name) + "/";
			using var resp = await Http.GetAsync(url, ct);
			if (!resp.IsSuccessStatusCode) return null;
			string body = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

			int? exact = null;
			int? fuzzy = null;
			foreach (var el in doc.RootElement.EnumerateArray())
			{
				if (!el.TryGetProperty("id", out var idEl)) continue;
				if (!el.TryGetProperty("name", out var nameEl)) continue;
				string n = nameEl.GetString() ?? "";
				int id = idEl.GetInt32();
				if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
					exact = id;
				else if (fuzzy is null &&
				         n.StartsWith(name, StringComparison.OrdinalIgnoreCase))
					fuzzy = id;
			}
			return exact ?? fuzzy;
		}
		catch
		{
			return null;
		}
	}

	public static async Task<int?> TryResolveShipTypeIdAsync(string shipName, CancellationToken ct = default)
	{
		shipName = (shipName ?? "").Trim();
		if (shipName.Length == 0) return null;
		if (string.Equals(shipName, "Capsule", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(shipName, "Pod", StringComparison.OrdinalIgnoreCase))
			return 670;
		try
		{
			string url = "https://zkillboard.com/autocomplete/typeID/" +
			             Uri.EscapeDataString(shipName) + "/";
			using var resp = await Http.GetAsync(url, ct);
			if (!resp.IsSuccessStatusCode) return null;
			string body = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
			foreach (var el in doc.RootElement.EnumerateArray())
			{
				if (!el.TryGetProperty("id", out var idEl)) continue;
				if (!el.TryGetProperty("name", out var nameEl)) continue;
				string n = nameEl.GetString() ?? "";
				if (string.Equals(n, shipName, StringComparison.OrdinalIgnoreCase))
					return idEl.GetInt32();
			}
			if (doc.RootElement.GetArrayLength() > 0 &&
			    doc.RootElement[0].TryGetProperty("id", out var first))
				return first.GetInt32();
		}
		catch { }
		return null;
	}

	/// <summary>캐릭터 최근 로스메일 중 함선이 맞는 킬(없으면 최신) URL.</summary>
	public static async Task<string> ResolveKillRelatedUrlAsync(
		string victimName, string? shipName, CancellationToken ct = default)
	{
		victimName = NormalizeCharacterName(victimName);
		if (victimName.Length == 0) return "https://zkillboard.com/search/";

		int? charId = await TryResolveCharacterIdAsync(victimName, ct);
		if (charId is null)
			return SearchUrl(victimName);

		int? shipTypeId = null;
		if (!string.IsNullOrWhiteSpace(shipName) && shipName != "-")
			shipTypeId = await TryResolveShipTypeIdAsync(shipName, ct);

		long? killId = await TryFindRecentLossAsync(charId.Value, shipTypeId, ct);
		if (killId is long kid)
			return KillUrl(kid);

		return CharacterUrl(charId.Value);
	}

	public static async Task<string> ResolveCharacterUrlAsync(string name, CancellationToken ct = default)
	{
		name = NormalizeCharacterName(name);
		if (name.Length == 0) return "https://zkillboard.com/search/";
		int? id = await TryResolveCharacterIdAsync(name, ct);
		if (id is int cid) return CharacterUrl(cid);
		return SearchUrl(name);
	}

	private static async Task<long?> TryFindRecentLossAsync(
		int characterId, int? shipTypeId, CancellationToken ct)
	{
		try
		{
			string url = $"https://zkillboard.com/api/losses/characterID/{characterId}/";
			using var resp = await Http.GetAsync(url, ct);
			if (!resp.IsSuccessStatusCode) return null;
			string body = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

			long? first = null;
			foreach (var el in doc.RootElement.EnumerateArray())
			{
				if (!el.TryGetProperty("killmail_id", out var kidEl)) continue;
				long kid = kidEl.GetInt64();
				first ??= kid;
				if (shipTypeId is null) return kid;
				if (el.TryGetProperty("victim", out var vic) &&
				    vic.TryGetProperty("ship_type_id", out var st) &&
				    st.TryGetInt32(out int sid) &&
				    sid == shipTypeId.Value)
					return kid;
			}
			return first;
		}
		catch
		{
			return null;
		}
	}
}
