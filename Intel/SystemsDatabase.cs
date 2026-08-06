using System.Xml;
using System.Xml.Linq;

namespace EVEAA.Mod.Intel;

internal sealed class SystemInfo
{
	public string Name { get; init; } = "";
	public int SystemId { get; init; }
	public string Region { get; init; } = "";
	public List<string> Jumps { get; init; } = new();
}

internal sealed class SystemsDatabase
{
	private readonly Dictionary<string, SystemInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<int, string> _idToName = new();

	public bool Loaded => _byName.Count > 0;
	public int Count => _byName.Count;
	public IReadOnlyDictionary<int, string> IdToName => _idToName;

	public SystemInfo? Get(string name) =>
		_byName.TryGetValue((name ?? "").Trim(), out var s) ? s : null;

	public bool Has(string name) => _byName.ContainsKey((name ?? "").Trim());

	/// <summary>정확 일치 우선, 널섹 코드형 토큰은 유일 prefix 매칭, 일반 이름은 오타 허용 퍼지 매칭.</summary>
	public string? MatchName(string token)
	{
		token = (token ?? "").Trim();
		if (token.Length == 0) return null;
		if (_byName.TryGetValue(token, out SystemInfo? exact))
			return exact.Name;

		bool codeLike = token.IndexOf('-') >= 0 || token.Any(char.IsDigit);
		if (codeLike && token.Length >= 3)
		{
			string? unique = null;
			foreach (var kv in _byName)
			{
				if (kv.Key.StartsWith(token, StringComparison.OrdinalIgnoreCase))
				{
					if (unique is not null)
						return null; // ambiguous
					unique = kv.Value.Name;
				}
			}
			if (unique is not null) return unique;
			return null;
		}

		return FuzzyMatchName(token);
	}

	/// <summary>
	/// RIFT류 퍼지 매칭: 일반 성계명 오타(1~2자 편집거리) 허용, 후보가 유일할 때만 채택.
	/// 최소 길이 6자 미만은 시도하지 않음 — 짧은 단어가 편집거리 1로 짧은 성계명과 우연히 겹쳐
	/// 캐릭터 이름을 성계로 오인식할 위험(ShipDatabase의 "Jane"→"Bane" 사례와 동일)을 피하기 위함.
	/// </summary>
	private string? FuzzyMatchName(string token)
	{
		if (token.Length < 6 || !token.All(char.IsLetter))
			return null;

		int maxDist = token.Length <= 6 ? 1 : 2;
		string? best = null;
		int bestDist = maxDist + 1;
		bool ambiguous = false;
		foreach (var kv in _byName)
		{
			string name = kv.Value.Name;
			if (Math.Abs(name.Length - token.Length) > maxDist) continue;
			int d = FuzzyMatch.Distance(token, name, maxDist);
			if (d > maxDist) continue;
			if (d < bestDist)
			{
				bestDist = d;
				best = name;
				ambiguous = false;
			}
			else if (d == bestDist && !string.Equals(name, best, StringComparison.OrdinalIgnoreCase))
			{
				ambiguous = true;
			}
		}
		return !ambiguous ? best : null;
	}

	public IEnumerable<string> AllNames() => _byName.Keys;

	public IEnumerable<string> JumpsOf(string name)
	{
		var info = Get(name);
		return info?.Jumps ?? Enumerable.Empty<string>();
	}

	public int LoadFromDat(string path)
	{
		_byName.Clear();
		_idToName.Clear();
		if (!File.Exists(path))
			throw new FileNotFoundException("Systems.dat 없음", path);

		using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element || reader.Name != "System")
				continue;

			using var sub = reader.ReadSubtree();
			var el = XElement.Load(sub);
			string name = ((string?)el.Element("Name") ?? "").Trim();
			int.TryParse((string?)el.Element("ID"), out int id);
			string region = ((string?)el.Element("Region") ?? "").Trim();
			var jumps = new List<string>();
			var jumpsEl = el.Element("Jumps");
			if (jumpsEl is not null)
			{
				foreach (var s in jumpsEl.Elements("string"))
				{
					string j = ((string?)s ?? "").Trim();
					if (j.Length > 0) jumps.Add(j);
				}
			}

			if (string.IsNullOrEmpty(name) || id <= 0)
				continue;
			var info = new SystemInfo { Name = name, SystemId = id, Region = region, Jumps = jumps };
			_byName[name] = info;
			_idToName[id] = name;
		}
		return _byName.Count;
	}

	public static string DefaultPath() => ModDataPaths.Resolve("Systems.dat");
}
