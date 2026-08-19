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

	/// <summary>함선명에 안 붙은 독립 적 카운트 보고 (예: "+5", "5+", "=15", "15 neuts"). 없으면 null.</summary>
	public int? HostileCount { get; init; }
	public bool HostileCountIsPlus { get; init; }
	public bool HostileCountIsExact { get; init; }

	/// <summary>"{성계} gate" / "{성계} ansiblex" 형태의 게이트 언급.</summary>
	public string? GateSystem { get; init; }
	public bool GateIsAnsiblex { get; init; }

	/// <summary>"going/jumped/jumping {성계|게이트}" 형태의 이동 보고.</summary>
	public string? MovementVerb { get; init; }
	public string? MovementSystem { get; init; }
	public bool MovementIsGate { get; init; }

	public bool IsQuestion { get; init; }
	public string? QuestionType { get; init; }
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
		"kill", "killed", "pod", "podded",
		// RIFT은 영단어 사전으로 일상 문장 조각을 캐릭터명 후보에서 배제하지만 우리는 사전이 없다.
		// 대문자로 시작해도(Title Case 통과) 자주 등장하는 일상 단어는 여기서 추가로 막아 오탐을 줄인다.
		"kidding", "now", "right", "you", "really", "sure", "yeah", "yes", "lol", "lmao",
		"haha", "ok", "okay", "please", "thanks", "thank", "here", "there", "what", "why",
		"who", "when", "where", "how", "can", "could", "would", "should", "will", "not",
		"dont", "im", "its", "up", "down", "out", "over", "under", "again", "still", "just",
		"like", "so", "but", "if", "then", "than", "guys", "everyone", "anyone", "someone"
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

	private static readonly HashSet<string> MovementKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"going", "jumped", "jumping"
	};

	private static readonly HashSet<string> GateWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"gate"
	};
	private static readonly HashSet<string> AnsiblexWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"ansiblex", "ansi"
	};

	/// <summary>RIFT류 질문 문구 사전 (전체 메시지가 이 문구와 일치할 때만 인식).</summary>
	private static readonly Dictionary<string, string> Questions = new(StringComparer.OrdinalIgnoreCase)
	{
		["where is he"] = "Location", ["where is he?"] = "Location",
		["loc?"] = "Location", ["loc ?"] = "Location", ["location?"] = "Location", ["location ?"] = "Location",
		["shiptypes?"] = "ShipTypes", ["shiptypes ?"] = "ShipTypes", ["shiptype?"] = "ShipTypes", ["shiptype ?"] = "ShipTypes",
		["ship types?"] = "ShipTypes", ["ship types ?"] = "ShipTypes", ["ship type?"] = "ShipTypes", ["ship type ?"] = "ShipTypes",
		["ships?"] = "ShipTypes", ["ships ?"] = "ShipTypes", ["ship?"] = "ShipTypes", ["ship ?"] = "ShipTypes",
		["what ships"] = "ShipTypes", ["what ships?"] = "ShipTypes",
		["how many"] = "Number", ["how many?"] = "Number",
		["status?"] = "Status", ["status ?"] = "Status", ["status please"] = "Status", ["status pls"] = "Status",
		["status"] = "Status", ["sts?"] = "Status", ["clr?"] = "Status",
	};

	/// <summary>키릴 동형이의 문자(с а е о р х у 등)를 라틴 문자로 치환 — "сlr"처럼 키릴 с가 섞인
	/// 위장/오타 클리어 보고를 놓치지 않기 위함 (RIFT의 "cyrillic c" 방어를 일반화).</summary>
	private static string NormalizeHomoglyphs(string s)
	{
		if (string.IsNullOrEmpty(s)) return s;
		return s
			.Replace('с', 'c') // с
			.Replace('а', 'a') // а
			.Replace('е', 'e') // е
			.Replace('о', 'o') // о
			.Replace('р', 'p') // р
			.Replace('х', 'x') // х
			.Replace('у', 'y') // у
			.Replace('С', 'C')
			.Replace('А', 'A')
			.Replace('Е', 'E')
			.Replace('О', 'O')
			.Replace('Р', 'P')
			.Replace('Х', 'X')
			.Replace('У', 'Y');
	}

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

		string lower = NormalizeHomoglyphs(message.ToLowerInvariant());
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

		string questionKey = NormalizeHomoglyphs(message.Trim().ToLowerInvariant());
		string? questionType = Questions.TryGetValue(questionKey, out string? qt) ? qt : null;

		string[] rawTokens = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var used = new bool[rawTokens.Length];
		var foundSystems = new List<string>();
		var foundShips = new List<string>();
		var systemHits = new List<(int start, int len, string name)>();
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
					systemHits.Add((i, len, sys));
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

		(string system, bool isAnsiblex)? gate = TryFindGate(rawTokens, used, systemHits);
		(string verb, string system, bool isGate)? movement = TryFindMovement(rawTokens, used, systemHits, gate);
		(int count, bool isPlus, bool isExact)? hostileCount = TryFindStandaloneHostileCount(rawTokens, used);

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
				TimestampUtc = tsUtc,
				IsQuestion = questionType is not null,
				QuestionType = questionType
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
			TimestampUtc = tsUtc,
			HostileCount = hostileCount?.count,
			HostileCountIsPlus = hostileCount?.isPlus ?? false,
			HostileCountIsExact = hostileCount?.isExact ?? false,
			GateSystem = gate?.system,
			GateIsAnsiblex = gate?.isAnsiblex ?? false,
			MovementVerb = movement?.verb,
			MovementSystem = movement?.system,
			MovementIsGate = movement?.isGate ?? false,
			IsQuestion = questionType is not null,
			QuestionType = questionType
		};
	}

	private static bool IsNoiseToken(string token)
	{
		if (string.IsNullOrWhiteSpace(token)) return true;
		if (UrlRe.IsMatch(token)) return true;
		if (QtyRe.IsMatch(token)) return true;
		if (Slang.Contains(token) || Slang.Contains(NormalizeHomoglyphs(token))) return true;
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
				// ESI로 아직 미확인인 후보는 Title Case(단어별 대문자 시작)일 때만 점수를 준다.
				// RIFT가 영단어 사전으로 걸러내는 "평범한 소문자 문장 조각"을 우리는 이 방식으로 배제 —
				// 이미 실존 확인된(Exists) 이름은 신뢰할 수 있으므로 그대로 통과. 예외: 숫자가 섞인
				// 토큰("heqiya3" 같은 부계정/알트 이름 흔한 패턴)은 평범한 영단어일 수 없으므로
				// Title Case가 아니어도 후보로 인정 — 그래야 첫 등장에도 바로 ESI 조회 큐에 들어간다.
				if (status != CharResolveStatus.Exists && !CharacterResolver.IsTitleCaseName(phrase)
					&& !phrase.Any(char.IsDigit))
					continue;

				// 점수 등급을 완전히 분리된 구간으로 둔다 (겹치면 아래에서 설명하는 역전 버그가 남는다):
				//   3등급(가장 신뢰): len단어 조합 자체가 ESI로 실존 확인됨      → 100000×len
				//   2등급:            "조합은 미확인이지만 단어 하나하나는 모두   →   1000×len + 500
				//                      따로 실존 확인된 상태" — 아래 설명 참고
				//   1등급:            단일 단어가 ESI로 실존 확인됨              →   1000 (len=1 전용)
				//   0등급:            그 외 미확인 Title Case 후보               →  80/250/380
				if (status == CharResolveStatus.Exists && len > 1)
				{
					segScore[i, len] = 100_000 * len;
					continue;
				}
				if (len > 1 && status == CharResolveStatus.Unknown)
				{
					// 아직 이 조합 자체("Prime Dallocort")로는 ESI에 물어본 적이 없다 — 백그라운드로
					// 큐에 넣어 결과를 캐시에 쌓는다. 나중에 이 조합이 실존 확인되면 위 3등급으로
					// 넘어가 확실히 이기고, "존재 안 함"으로 확인되면 위의 DoesNotExist continue에
					// 걸려 자동으로 개별 단어 분리로 복귀한다.
					chars?.Enqueue(phrase);

					// 이 자리의 개별 단어가 이미 각각 따로 실존 확인돼 있으면("Prime"도 실존,
					// "Dallocort"도 실존하는 서로 다른 실제 캐릭터), 단순 합산 점수(1000×len, 1등급들의
					// 합)가 아직 검증 안 된 이 조합의 기본 점수(0등급, 80~380)를 항상 이겨버려서
					// "Firstname Lastname" 형태의 진짜 캐릭터명이 매번 낱말로 쪼개지는 문제가 있었다.
					// EVE 캐릭터명 대부분이 2단어 형태이므로, 조합을 아직 안 물어봤을 땐 "개별
					// 단어들이 우연히 각각 다른 실존 캐릭터와 겹치는 경우"보다 "하나의 2~3단어
					// 이름"일 가능성을 우선한다 — 2등급 점수(1000×len+500)는 1등급 단어들의 합계
					// (len×1000)보다는 항상 높고, 3등급(조합 자체 확인, 100000×len)보다는 항상 낮다.
					bool allPartsExist = true;
					for (int k = 0; k < len; k++)
					{
						string part = CleanToken(rawTokens[i + k]);
						if ((chars?.GetStatus(part) ?? CharResolveStatus.Unknown) != CharResolveStatus.Exists)
						{
							allPartsExist = false;
							break;
						}
					}
					if (allPartsExist)
					{
						segScore[i, len] = 1000 * len + 500;
						continue;
					}
				}

				// 단어 수에 따라 가중치를 크게 벌려, "1단어 조각 여러 개"가 "2~3단어 온전한 이름"보다
				// 점수 합계에서 이기지 못하게 한다.
				segScore[i, len] = status == CharResolveStatus.Exists
					? 1000 // len == 1인 경우만 여기 도달 (len>1은 위에서 3등급으로 이미 처리)
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

	/// <summary>"{성계} gate" / "{성계} ansiblex/ansi" — 성계 매칭 직후 토큰이 게이트 단어인지 확인 (RIFT findGates 포팅).</summary>
	private static (string system, bool isAnsiblex)? TryFindGate(
		string[] rawTokens, bool[] used, List<(int start, int len, string name)> systemHits)
	{
		foreach (var hit in systemHits)
		{
			int after = hit.start + hit.len;
			if (after >= rawTokens.Length || used[after]) continue;
			string word = CleanToken(rawTokens[after]);
			if (GateWords.Contains(word))
			{
				used[after] = true;
				return (hit.name, false);
			}
			if (AnsiblexWords.Contains(word))
			{
				used[after] = true;
				return (hit.name, true);
			}
		}
		return null;
	}

	/// <summary>"going/jumped/jumping {성계|게이트}" — 성계(또는 게이트) 매칭 직전 토큰이 이동 동사인지 확인 (RIFT findMovement 포팅).</summary>
	private static (string verb, string system, bool isGate)? TryFindMovement(
		string[] rawTokens, bool[] used, List<(int start, int len, string name)> systemHits,
		(string system, bool isAnsiblex)? gate)
	{
		int? targetStart = null;
		string? targetSystem = null;
		bool isGate = false;
		if (gate is not null)
		{
			var hit = systemHits.FirstOrDefault(h => string.Equals(h.name, gate.Value.system, StringComparison.OrdinalIgnoreCase));
			if (hit.name is not null)
			{
				targetStart = hit.start;
				targetSystem = gate.Value.system;
				isGate = true;
			}
		}
		if (targetStart is null && systemHits.Count > 0)
		{
			var hit = systemHits[0];
			targetStart = hit.start;
			targetSystem = hit.name;
			isGate = false;
		}
		if (targetStart is null || targetSystem is null) return null;

		int before = targetStart.Value - 1;
		if (before < 0 || used[before]) return null;
		string word = CleanToken(rawTokens[before]);
		if (!MovementKeywords.Contains(word)) return null;
		used[before] = true;
		return (word, targetSystem, isGate);
	}

	/// <summary>함선명에 붙지 않은 독립 적 카운트 보고: "+5", "5+", "=15", "15 neuts" (RIFT의 COUNT_PLUS/EQUALS_REGEX 포팅).
	/// "2x"/"x2"/"2*" 류는 함선 인접 수량 표기 전용이라 여기서는 다루지 않는다(오탐 방지).</summary>
	private static (int count, bool isPlus, bool isExact)? TryFindStandaloneHostileCount(string[] rawTokens, bool[] used)
	{
		for (int i = 0; i < rawTokens.Length; i++)
		{
			if (used[i]) continue;
			string tok = CleanToken(rawTokens[i]);

			if (i + 1 < rawTokens.Length && !used[i + 1] &&
			    int.TryParse(tok, out int nn) && nn > 0 && nn <= 999)
			{
				string next = CleanToken(rawTokens[i + 1]).ToLowerInvariant();
				if (next is "neut" or "neuts")
				{
					used[i] = true;
					used[i + 1] = true;
					return (nn, false, true); // "N neuts" == RIFT의 COUNT_EQUALS_REGEX, 정확 카운트로 취급
				}
			}

			var m = QtyCaptureRe.Match(tok);
			if (!m.Success) continue;
			if (m.Groups["n1"].Success && int.TryParse(m.Groups["n1"].Value, out int n1) && n1 > 0 && n1 <= 999)
			{
				used[i] = true;
				return (n1, true, false); // "+N"
			}
			if (m.Groups["n2"].Success && int.TryParse(m.Groups["n2"].Value, out int n2) && n2 > 0 && n2 <= 999)
			{
				used[i] = true;
				return (n2, true, false); // "N+"
			}
			if (m.Groups["n7"].Success && int.TryParse(m.Groups["n7"].Value, out int n7) && n7 > 0 && n7 <= 999)
			{
				used[i] = true;
				return (n7, false, true); // "=N"
			}
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