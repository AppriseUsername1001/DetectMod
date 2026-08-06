using System.Net.Http;
using System.Text.Json;

namespace EVEAA.Mod;

/// <summary>GitHub Releases(공개 저장소) 기준 최신 버전 확인/다운로드.</summary>
internal static class UpdateChecker
{
	private const string ReleasesApiUrl = "https://api.github.com/repos/AppriseUsername1001/DetectMod/releases/latest";
	private const string UserAgent = "EVEDetectmod-UpdateChecker";

	public sealed record UpdateInfo(string Version, string DownloadUrl, string AssetName, long SizeBytes);

	public static async Task<UpdateInfo?> CheckAsync()
	{
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
			using HttpResponseMessage resp = await http.GetAsync(ReleasesApiUrl);
			if (!resp.IsSuccessStatusCode)
				return null;

			using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("tag_name", out JsonElement tagProp))
				return null;

			string latest = (tagProp.GetString() ?? "").TrimStart('v', 'V');
			if (!IsNewer(latest, ModVersion.Current))
				return null;

			if (!root.TryGetProperty("assets", out JsonElement assets))
				return null;

			foreach (JsonElement asset in assets.EnumerateArray())
			{
				string name = asset.GetProperty("name").GetString() ?? "";
				if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					continue;
				string url = asset.GetProperty("browser_download_url").GetString() ?? "";
				if (string.IsNullOrEmpty(url))
					continue;
				long size = asset.TryGetProperty("size", out JsonElement sizeProp) ? sizeProp.GetInt64() : 0;
				return new UpdateInfo(latest, url, name, size);
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	public static async Task DownloadAsync(string url, string destPath)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
		resp.EnsureSuccessStatusCode();
		await using Stream stream = await resp.Content.ReadAsStreamAsync();
		await using FileStream file = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
		await stream.CopyToAsync(file);
	}

	/// <summary>"1.12" vs "1.11" 같은 major.minor 버전 비교. 파싱 실패 항목은 0 취급.</summary>
	private static bool IsNewer(string latest, string current)
	{
		static (int major, int minor) Parse(string s)
		{
			string[] parts = s.Split('.');
			int major = parts.Length > 0 && int.TryParse(parts[0], out int ma) ? ma : 0;
			int minor = parts.Length > 1 && int.TryParse(parts[1], out int mi) ? mi : 0;
			return (major, minor);
		}

		(int lm, int ln) = Parse(latest);
		(int cm, int cn) = Parse(current);
		return lm != cm ? lm > cm : ln > cn;
	}
}
