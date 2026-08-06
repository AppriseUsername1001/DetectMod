using System.Globalization;
using System.Text.RegularExpressions;

namespace EVEAA.Mod.Intel;

internal sealed class ParsedIntelLine
{
	public string TimeText { get; init; } = "";
	public string Speaker { get; init; } = "";
	public string Message { get; init; } = "";
	public List<string> Systems { get; init; } = new();
	public List<string> Characters { get; init; } = new();
	public List<string> Ships { get; init; } = new();
	public bool IsClear { get; init; }
	public bool IsKillReport { get; init; }
	public string Raw { get; init; } = "";
	/// <summary>로그 줄의 실제 UTC 타임스탬프. 신선도(스테일) 판정에 사용.</summary>
	public DateTime? TimestampUtc { get; init; }
}

internal static class IntelParser
{
	private static readonly Regex LineRe = new(
		@"^\[\s*(?<date>[\d.]+)\s+(?<time>[\d:]+)\s+\]\s+(?<speaker>.+?)\s>\s+(?<msg>.+)$",
		RegexOptions.Compiled);

	private static readonly HashSet<string> ClearWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"clr", "clear", "c", "gc"
	};

	private static readonly HashSet<string> Slang = new(StringComparer.OrdinalIgnoreCase)
	{
		"nv", "clr", "clear", "c", "gc", "wh", "wormhole", "k162", "spike", "ess", "skyhook",
		"bubble", "bubbles", "neut", "neuts", "hostile", "hostiles", "blue", "blues",
		"red", "reds", "gate", "camp", "probes", "combat", "dscan", "status", "status?",
		"?", "+", "-", "x", "xx", "xxx", "and", "or", "the", "a", "an", "in", "on", "at",
		"to", "from", "with", "no", "visual", "vision", "seen", "see", "is", "are", "was",
		"were", "he", "she", "they", "them", "his", "her", "their", "blob", "fleet",
		"standing", "standings", "local", "docked", "undock", "undocked",
		"kill", "killed", "pod", "podded"
	};

	private static readonly Regex QtyRe = new(@"^(\+\d+|\d+\+|\d+x|x\d+|\*\d+|\d+\*|=?\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex QtyCaptureRe = new(
		@"^(?:\+(?<n1>\d+)|(?<n2>\d+)\+|(?<n3>\d+)x|x(?<n4>\d+)|\*(?<n5>\d+)|(?<n6>\d+)\*|=(?<n7>\d+))$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex UrlRe = new(@"^https?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly Regex KillRe = new(
		@"^Kill:\s*(?<name>.+?)\s*(?:\((?<ship>[^)]+)\))?\s*$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly HashSet<string> IgnorableOnly = new(StringComparer.OrdinalIgnoreCase)
	{
		"gj", "gf", "np", "ty", "thx", "wb", "o/"
	};

	public static ParsedIntelLine? ParseIntelLine(string line, SystemsDatabase systems, ShipDatabase ships, CharacterResolver? chars = null)
	{
		string cleaned = (line ?? "").Trim().TrimStart('﻿').Trim();
		int bracket = cleaned.IndexOf('[');
		if (bracket > 0) cleaned = cleaned[bracket..];
		var m = LineRe.Match(cleaned);
		if (!m.Success) return null;

		string timeText = m.Groups["time"].Value.Trim();
		string speaker = m.Groups["speaker"].Value.Trim();
		string message = m.Groups["msg"].Value.Trim();
		if (message.Length == 0) return null;

		// EVE System 알림(채널 변경/MOTD 등)은 실제 인텔 발언이 아니므로 제외
		if (string.Equals(speaker, "EVE System", StringComparison.OrdinalIgnoreCase))
			return null;

		// gj 등 의미 없는 단문 채팅은 인텔 로그에 넣지 않음
		if (IsIgnorableMessage(message))
			return null;

		DateTime? tsUtc = TryParseTimestampUtc(m.Groups["date"].Value.Trim(), timeText);

		var killMatch = KillRe.Match(message);
		if (killMatch.Success)
		{
			string victim = NormalizeKillVictim(killMatch.Groups["name"].Value);
			string ship = killMatch.Groups["ship"].Success
				? killMatch.Groups["ship"].Value.Trim()
				: "";
			if (!string.IsNullOrEmpty(ship))
			{
				string? resolved = ships.Resolve(ship);
				if (resolved is not null) ship = resolved;
			}
			if (!string.IsNullOrEmpty(victim))
				chars?.Enqueue(victim);
			return new ParsedIntelLine
			{
				TimeText = timeText,
				Speaker = speaker,
				Message = message,
				Characters = string.IsNullOrEmpty(victim) ? new List<string>() : new List<string> { victim },
				Ships = string.IsNullOrEmpty(ship) ? new List<string>() : new List<string> { ship },
				IsKillReport = true,
				Raw = cleaned,
				TimestampUtc = tsUtc
			};
		}

		string lower = message.ToLowerInvariant();
		bool isClear = false;
		foreach (string w in ClearWords)
		{
			if (lower == w || lower.StartsWith(w + " ", StringComparison.Ordinal) ||
			    lower.EndsWith(" " + w, StringComparison.Ordinal) ||
			    lower.Contains(" " + w + " ", StringComparison.Ordinal))
			{
				isClear = true;
				break;
			}
		}

		string[] rawTokens = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var used = new bool[rawTokens.Length];
		var foundSystems = new List<string>();
		var foundShips = new List<string>();
		var sysSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var shipSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Longest-first multi-token match for systems then ships (3..1)
		for (int len = 3; len >= 1; len--)
		{
			for (int i = 0; i <= rawTokens.Length - len; i++)
			{
				if (RangeUsed(used, i, len)) continue;
				string phrase = JoinTokens(rawTokens, i, len);
				if (IsNoiseToken(phrase)) continue;

				string? sys = systems.MatchName(phrase);
				if (sys is not null)
				{
					if (sysSeen.Add(sys))
						foundSystems.Add(sys);
					MarkUsed(used, i, len);
					continue;
				}

				string? ship = len == 1 ? ships.Resolve(CleanToken(rawTokens[i])) : ships.Resolve(phrase);
				if (ship is not null)
				{
					MarkUsed(used, i, len);
					int? qty = TryConsumeAdjacentQty(rawTokens, used, i, len);
					string display = qty is int n && n > 1 ? $"{ship} x{n}" : ship;
					if (shipSeen.Add(ship))
						foundShips.Add(display);
				}
			}
		}

		// Character candidates from remaining tokens (1..3 word windows); skip speaker
		var foundChars = new List<string>();
		var charSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (isClear)
		{
			return new ParsedIntelLine
			{
				TimeText = timeText,
				Speaker = speaker,
				Message = message,
				Systems = foundSystems,
				IsClear = true,
				Raw = cleaned,
				TimestampUtc = tsUtc
			};
		}
		foreach (var (start, len) in SegmentCharacterCandidates(rawTokens, used, speaker, systems, ships, chars))
		{
			string phrase = JoinTokens(rawTokens, start, len);
			bool accept = chars?.AcceptAsCharacter(phrase) ?? true;
			if (!accept) continue;
			if (charSeen.Add(phrase))
			{
				foundChars.Add(phrase);
				chars?.Enqueue(phrase);
			}
			MarkUsed(used, start, len);
		}

		return new ParsedIntelLine
		{
			TimeText = timeText,
			Speaker = speaker,
			Message = message,
			Systems = foundSystems,
			Characters = foundChars,
			Ships = foundShips,
			Raw = cleaned,
			TimestampUtc = tsUtc
		};
	}

	private static bool IsNoiseToken(string token)
	{
		if (string.IsNullOrWhiteSpace(token)) return true;
		if (UrlRe.IsMatch(token)) return true;
		if (QtyRe.IsMatch(token)) return true;
		if (Slang.Contains(token)) return true;
		if (token.All(ch => !char.IsLetterOrDigit(ch))) return true;
		return false;
	}

	private static string JoinTokens(string[] tokens, int start, int len)
	{
		if (len == 1) return CleanToken(tokens[start]);
		return CleanToken(string.Join(' ', tokens.Skip(start).Take(len)));
	}

	/// <summary>Strip trailing * ? etc from intel tokens (e.g. AZBR-2*).</summary>
	private static string CleanToken(string token)
	{
		if (string.IsNullOrEmpty(token)) return "";
		string s = token.Trim();
		static bool IsJunk(char c) =>
			c is '*' or '?' or '!' or ',' or '.' or ';' or ':' or '(' or ')' or '\'' or '"' or '[' or ']';
		while (s.Length > 0 && IsJunk(s[^1]))
			s = s[..^1].TrimEnd();
		while (s.Length > 0 && IsJunk(s[0]))
			s = s[1..].TrimStart();
		return s;
	}

	/// <summary>
	/// 남은(미사용) 토큰들을 캐릭터 이름 후보로 최적 분할한다 (DP).
	/// 탐욕적 최장일치 방식은 "JIM 01 JIM 02"(두 캐릭터) 같은 줄을 "JIM 01 JIM"(엉터리 한 덩어리) + "02"(버려짐)로
	/// 잘못 묶는 문제가 있었음 — RIFT처럼 여러 분할 후보에 점수를 매겨 가장 나은 조합을 선택하도록 교체.
	/// 점수: ESI로 실존 확인된 이름 > 2~3단어 조합(미확인) > 1단어(미확인) > (건너뜀). 확인된 비존재 이름은 제외.
	/// </summary>
	private static List<(int start, int len)> SegmentCharacterCandidates(
		string[] rawTokens, bool[] used, string speaker, SystemsDatabase systems, ShipDatabase ships, CharacterResolver? chars)
	{
		int n = rawTokens.Length;
		var segScore = new int?[n, 4]; // [start, len] len=1..3
		for (int i = 0; i < n; i++)
		{
			for (int len = 1; len <= 3 && i + len <= n; len++)
			{
				if (RangeUsed(used, i, len)) continue;
				string phrase = JoinTokens(rawTokens, i, len);
				if (IsNoiseToken(phrase)) continue;
				if (!CharacterResolver.LooksLikeCharacterName(phrase)) continue;
				if (string.Equals(phrase, speaker, StringComparison.OrdinalIgnoreCase)) continue;
				if (systems.MatchName(phrase) is not null) continue;
				if (ships.Resolve(phrase) is not null) continue;

				CharResolveStatus status = chars?.GetStatus(phrase) ?? CharResolveStatus.Unknown;
				if (status == CharResolveStatus.DoesNotExist) continue;
				// 단어 수에 따라 가중치를 크게 벌려, "1단어 조각 여러 개"가 "2~3단어 온전한 이름"보다
				// 점수 합계에서 이기지 못하게 한다 (실존 확인 시 압도적으로 우선).
				segScore[i, len] = status == CharResolveStatus.Exists
					? 1000
					: len switch { 1 => 80, 2 => 250, _ => 380 };
			}
		}

		var best = new int[n + 1];
		var choiceLen = new int[n + 1];
		for (int end = 1; end <= n; end++)
		{
			int bestScore = best[end - 1] - 1; // 건너뛰기(이 토큰은 캐릭터 아님) — 약한 페널티로 커버리지를 우선
			int bestLen = 0;
			for (int len = 1; len <= 3 && len <= end; len++)
			{
				int start = end - len;
				if (segScore[start, len] is int s)
				{
					int total = best[start] + s;
					if (total > bestScore)
					{
						bestScore = total;
						bestLen = len;
					}
				}
			}
			best[end] = bestScore;
			choiceLen[end] = bestLen;
		}

		var result = new List<(int start, int len)>();
		int pos = n;
		while (pos > 0)
		{
			int len = choiceLen[pos];
			if (len == 0)
			{
				pos -= 1;
			}
			else
			{
				result.Add((pos - len, len));
				pos -= len;
			}
		}
		result.Reverse();
		return result;
	}

	/// <summary>함선 매칭 앞/뒤 토큰에서 "2x", "x2", "2*", "=15" 같은 수량 표기를 찾아 소비한다 (RIFT류 척수 인식).</summary>
	private static int? TryConsumeAdjacentQty(string[] rawTokens, bool[] used, int shipStart, int shipLen)
	{
		int before = shipStart - 1;
		if (before >= 0 && !used[before])
		{
			int? q = TryExtractQty(CleanToken(rawTokens[before]));
			if (q is not null) { MarkUsed(used, before, 1); return q; }
		}
		int after = shipStart + shipLen;
		if (after < rawTokens.Length && !used[after])
		{
			int? q = TryExtractQty(CleanToken(rawTokens[after]));
			if (q is not null) { MarkUsed(used, after, 1); return q; }
		}
		return null;
	}

	private static int? TryExtractQty(string token)
	{
		var m = QtyCaptureRe.Match(token);
		if (!m.Success) return null;
		foreach (string g in new[] { "n1", "n2", "n3", "n4", "n5", "n6", "n7" })
		{
			if (m.Groups[g].Success && int.TryParse(m.Groups[g].Value, out int n) && n > 0 && n <= 999)
				return n;
		}
		return null;
	}

	private static DateTime? TryParseTimestampUtc(string dateText, string timeText)
	{
		if (DateTime.TryParseExact(
			dateText + " " + timeText, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
			return dt;
		return null;
	}

	private static bool RangeUsed(bool[] used, int start, int len)
	{
		for (int i = start; i < start + len; i++)
			if (used[i]) return true;
		return false;
	}

	private static void MarkUsed(bool[] used, int start, int len)
	{
		for (int i = start; i < start + len; i++)
			used[i] = true;
	}

	private static bool IsIgnorableMessage(string message)
	{
		string t = message.Trim().TrimEnd('!', '.', '*', '~');
		if (t.Length == 0) return true;
		return IgnorableOnly.Contains(t);
	}

	private static string NormalizeKillVictim(string name)
	{
		string n = (name ?? "").Trim().Trim('"');
		if (n.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
			n = n[..^2].Trim();
		else if (n.Length > 0 && n[^1] == '\'')
			n = n[..^1].Trim();
		return n;
	}
}