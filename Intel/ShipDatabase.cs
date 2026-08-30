namespace EVEAA.Mod.Intel;

internal sealed class ShipDatabase
{
	private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

	public bool Loaded => _byName.Count > 0;
	public int Count => _byName.Count;

	public int Load(string shipsPath, string? aliasesPath = null)
	{
		_byName.Clear();
		_aliases.Clear();
		if (File.Exists(shipsPath))
		{
			foreach (string line in File.ReadLines(shipsPath))
			{
				string name = line.Trim();
				if (name.Length == 0 || name.StartsWith('#')) continue;
				_byName[name] = name;
			}
		}
		if (!string.IsNullOrEmpty(aliasesPath) && File.Exists(aliasesPath))
		{
			foreach (string line in File.ReadLines(aliasesPath))
			{
				string t = line.Trim();
				if (t.Length == 0 || t.StartsWith('#')) continue;
				int eq = t.IndexOf('=');
				if (eq <= 0) continue;
				string alias = t[..eq].Trim();
				string canon = t[(eq + 1)..].Trim();
				if (alias.Length == 0 || canon.Length == 0) continue;
				if (_byName.TryGetValue(canon, out string? real))
					_aliases[alias] = real;
				else
					_aliases[alias] = canon;
			}
		}
		return _byName.Count;
	}

	/// <summary>매칭 신뢰도 — IntelParser의 통합 후보 채점이 정확/별칭/복수형 일치와 오타 허용
	/// 퍼지 매칭을 다른 신뢰도로 다루기 위해 구분한다.</summary>
	public enum MatchConfidence { Exact, Fuzzy }

	public string? Resolve(string token) => Resolve(token, out _);

	public string? Resolve(string token, out MatchConfidence confidence)
	{
		confidence = MatchConfidence.Exact;
		token = (token ?? "").Trim();
		if (token.Length == 0) return null;
		if (_byName.TryGetValue(token, out string? name))
			return name;
		if (_aliases.TryGetValue(token, out string? viaAlias))
			return viaAlias;
		// trailing plural s
		if (token.Length > 3 && (token.EndsWith('s') || token.EndsWith('S')))
		{
			string stem = token[..^1];
			if (_byName.TryGetValue(stem, out name))
				return name;
			if (_aliases.TryGetValue(stem, out viaAlias))
				return viaAlias;
		}
		confidence = MatchConfidence.Fuzzy;
		return FuzzyResolve(token);
	}

	/// <summary>
	/// RIFT류 퍼지 매칭: 함선명 오타(1~2자 편집거리) 허용, 후보가 유일할 때만 채택.
	/// 최소 길이 6자 미만은 시도하지 않음 — 짧은 단어(예: "Jane")가 편집거리 1로 실제 짧은 함선명("Bane")과
	/// 우연히 겹쳐 캐릭터 이름을 함선으로 오인식하는 사례가 실측됨.
	/// </summary>
	private string? FuzzyResolve(string token)
	{
		if (token.Length < 6 || !token.All(c => char.IsLetter(c) || c is ' ' or '-' or '\''))
			return null;

		int maxDist = token.Length <= 6 ? 1 : 2;
		string? best = null;
		int bestDist = maxDist + 1;
		bool ambiguous = false;
		foreach (string shipName in _byName.Keys)
		{
			if (Math.Abs(shipName.Length - token.Length) > maxDist) continue;
			int d = FuzzyMatch.Distance(token, shipName, maxDist);
			if (d > maxDist) continue;
			if (d < bestDist)
			{
				bestDist = d;
				best = shipName;
				ambiguous = false;
			}
			else if (d == bestDist && !string.Equals(shipName, best, StringComparison.OrdinalIgnoreCase))
			{
				ambiguous = true;
			}
		}
		return !ambiguous && best is not null ? _byName[best] : null;
	}

	public static string DefaultShipsPath() => ModDataPaths.Resolve("Ships.txt");

	public static string DefaultAliasesPath() => ModDataPaths.Resolve("ShipAliases.txt");
}