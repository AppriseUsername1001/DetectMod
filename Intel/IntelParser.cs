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
		"cloaked", "cloaky", "cloak", "theft", "planet", "warp", "warping", "warped", "thrower",
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
		"like", "so", "but", "if", "then", "than", "guys", "everyone", "anyone", "someone",
		"back"
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

		// 통합 후보 채점: 모든 (시작,길이 1~3) 구간에 대해 성계/함선/캐릭터 세 가지 해석을
		// 모두 점수로 매겨두고, 한 번의 DP로 줄 전체에서 겹치지 않는 최선의 조합 하나를
		// 고른다. 예전엔 성계·함선을 먼저 그리디하게 확정한 뒤 남는 토큰만 캐릭터 DP에
		// 넘기고, 성계/함선이 캐릭터 이름을 가로챈 특정 사례(예: "Corto Aihaken"의
		// "Aihaken"이 실제 성계 "Airaken"과 퍼지매칭됨, "Star Maelstrom"의 "Maelstrom"이
		// 실제 전함명과 겹침)마다 별도의 "되돌리기" 패치를 추가해왔다 — 이제는 애초에
		// 하나의 점수 체계 안에서 경쟁시켜, 확실한 매칭(코드형/정확 성계명, 정확 함선명)은
		// 여전히 항상 이기고, 불확실한 매칭(퍼지, 괄호 표기 없이 다른 함선 옆에 홑토큰으로
		// 잡힌 함선)만 강한 캐릭터 후보에 자연스럽게 밀리도록 한다.
		var (winners, n) = SelectBestTokenization(rawTokens, speaker, systems, ships, chars);

		var used = new bool[n];
		var foundSystems = new List<string>();
		var systemHits = new List<(int start, int len, string name)>();
		var sysSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var foundChars = new List<string>();
		var charSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var shipWinners = new List<(int start, int len, string name)>();

		foreach (var (start, len, cand) in winners)
		{
			MarkUsed(used, start, len);
			switch (cand.Kind)
			{
				case SpanKind.System:
					systemHits.Add((start, len, cand.Value));
					if (sysSeen.Add(cand.Value)) foundSystems.Add(cand.Value);
					break;
				case SpanKind.Ship:
					shipWinners.Add((start, len, cand.Value));
					break;
				case SpanKind.Character:
					bool accept = chars?.AcceptAsCharacter(cand.Value) ?? true;
					if (accept && charSeen.Add(cand.Value))
					{
						foundChars.Add(cand.Value);
						chars?.Enqueue(cand.Value);
					}
					break;
			}
		}

		// 함선 인접 수량("2x"/"x2"/"=15") 소비는 다른 모든 승자의 used[]가 이미 반영된
		// 뒤에 해야, 엉뚱하게 다른 승자 구간의 토큰을 수량으로 잘못 먹지 않는다.
		var foundShips = new List<string>();
		var shipSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (start, len, name) in shipWinners)
		{
			int? qty = TryConsumeAdjacentQty(rawTokens, used, start, len);
			string display = qty is int q && q > 1 ? $"{name} x{q}" : name;
			if (shipSeen.Add(name)) foundShips.Add(display);
		}

		(string system, bool isAnsiblex)? gate = TryFindGate(rawTokens, used, systemHits);
		(string verb, string system, bool isGate)? movement = TryFindMovement(rawTokens, used, systemHits, gate);
		(int count, bool isPlus, bool isExact)? hostileCount = TryFindStandaloneHostileCount(rawTokens, used);

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

	/// <summary>토큰 범위가 "(...)" 괄호로 감싸여 있는지 — EVE 인텔 채널의 "Name (ShipType)" 관용구
	/// 함선 표기를 원본(정리 전) 토큰 기준으로 판별한다.</summary>
	private static bool IsParenthesizedToken(string[] rawTokens, int start, int len)
	{
		if (start < 0 || start >= rawTokens.Length) return false;
		int end = Math.Min(start + len - 1, rawTokens.Length - 1);
		return rawTokens[start].Contains('(') && rawTokens[end].Contains(')');
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

	private enum SpanKind { System, Ship, Character }

	private readonly struct SpanCandidate
	{
		public readonly SpanKind Kind;
		public readonly string Value;
		public readonly int Score;
		public SpanCandidate(SpanKind kind, string value, int score) { Kind = kind; Value = value; Score = score; }
	}

	// 확실한(코드형/정확) 성계·함선 매칭은 사실상 항상 이기도록 길이당 매우 큰 점수를 준다 —
	// 캐릭터 최고 등급(길이당 100,000)보다도 한 자릿수 위라, 정말 명백한 매칭은 어떤 캐릭터
	// 후보와 경쟁해도 흔들리지 않는다.
	private const int DominantScorePerLen = 1_000_000;
	// 불확실한(퍼지 매칭, 혹은 다른 함선이 괄호로 명시된 줄에서 홑토큰으로만 잡힌) 성계·함선
	// 매칭 — 약한 캐릭터 추측(0등급, 80점)보다는 위지만, 온전한 2단어 캐릭터 후보(2등급,
	// 길이 2 기준 1500점)에는 확실히 밀리도록 잡은 값. "Corto Aihaken"/"Star Maelstrom"류
	// 충돌을 예전처럼 별도 패치 없이 이 점수 하나로 해결한다.
	private const int UncertainMatchScore = 900;

	/// <summary>
	/// 줄 전체에서 성계·함선·캐릭터 세 가지 해석을 하나의 점수 체계로 통합 채점하고, DP로
	/// 겹치지 않는 최선의 조합(=최적 토큰화) 하나를 고른다. RIFT Intel Fusion Tool의 "여러
	/// 후보 파싱을 만들고 우선순위로 하나를 고른다"는 접근에서 아이디어를 얻었지만(그쪽 소스는
	/// 라이선스가 없어 코드를 참고하지 않았고, GitLab 공개 저장소에서 상위 수준 알고리즘만
	/// 확인) 구현은 독립적 — 우리는 후보 집합을 전부 만드는 대신, 구간별 최고 점수 하나만
	/// 남겨 기존 DP 구조를 그대로 재사용한다.
	/// </summary>
	private static (List<(int start, int len, SpanCandidate cand)> winners, int n) SelectBestTokenization(
		string[] rawTokens, string speaker, SystemsDatabase systems, ShipDatabase ships, CharacterResolver? chars)
	{
		int n = rawTokens.Length;

		// 1단계: 함선의 원(raw) 매칭 정보를 먼저 전부 모아, 줄 안에 "(ShipName)" 괄호로
		// 명시된 함선이 하나라도 있는지 미리 판정한다 — 있으면 그 외의 홑토큰 함선 매칭은
		// 신뢰도를 낮춰서(아래 2단계) 강한 캐릭터 후보에 밀릴 여지를 준다.
		var rawShipMatch = new (string name, ShipDatabase.MatchConfidence conf)?[n, 4];
		bool anyParenShip = false;
		for (int i = 0; i < n; i++)
		{
			for (int len = 1; len <= 3 && i + len <= n; len++)
			{
				string phrase0 = JoinTokens(rawTokens, i, len);
				if (IsNoiseToken(phrase0)) continue;
				string? ship = len == 1
					? ships.Resolve(CleanToken(rawTokens[i]), out var conf)
					: ships.Resolve(phrase0, out conf);
				if (ship is null) continue;
				rawShipMatch[i, len] = (ship, conf);
				if (IsParenthesizedToken(rawTokens, i, len)) anyParenShip = true;
			}
		}

		// 2단계: 구간별 최종 후보 — 성계/함선/캐릭터 중 가장 높은 점수 하나만 남긴다.
		var cell = new SpanCandidate?[n, 4];
		for (int i = 0; i < n; i++)
		{
			for (int len = 1; len <= 3 && i + len <= n; len++)
			{
				string phrase = JoinTokens(rawTokens, i, len);
				if (IsNoiseToken(phrase)) continue;

				SpanCandidate? best = null;

				string? sys = systems.MatchName(phrase, out var sysConf);
				if (sys is not null)
				{
					int score = sysConf == SystemsDatabase.MatchConfidence.Fuzzy
						? UncertainMatchScore
						: DominantScorePerLen * len;
					best = new SpanCandidate(SpanKind.System, sys, score);
				}

				if (rawShipMatch[i, len] is (string shipName, ShipDatabase.MatchConfidence shipConf))
				{
					bool downgrade = len == 1 && shipConf != ShipDatabase.MatchConfidence.Fuzzy &&
						anyParenShip && !IsParenthesizedToken(rawTokens, i, len);
					int score = (shipConf == ShipDatabase.MatchConfidence.Fuzzy || downgrade)
						? UncertainMatchScore
						: DominantScorePerLen * len;
					if (best is null || score > best.Value.Score)
						best = new SpanCandidate(SpanKind.Ship, shipName, score);
				}

				if (TryScoreCharacterCandidate(i, len, phrase, speaker, chars) is int charScore &&
					(best is null || charScore > best.Value.Score))
					best = new SpanCandidate(SpanKind.Character, phrase, charScore);

				cell[i, len] = best;
			}
		}

		// 3단계: 예전 캐릭터 전용 DP와 동일한 구조의 구간분할 DP — 이제 세 종류 후보 전체를
		// 대상으로 줄 전체에서 겹치지 않는 최고 점수 조합을 고른다.
		var best2 = new int[n + 1];
		var choiceLen = new int[n + 1];
		for (int end = 1; end <= n; end++)
		{
			int bestScore = best2[end - 1] - 1; // 건너뛰기(아무 것도 아님) — 약한 페널티로 커버리지를 우선
			int bestLen = 0;
			for (int len = 1; len <= 3 && len <= end; len++)
			{
				int start = end - len;
				if (cell[start, len] is SpanCandidate c)
				{
					int total = best2[start] + c.Score;
					if (total > bestScore) { bestScore = total; bestLen = len; }
				}
			}
			best2[end] = bestScore;
			choiceLen[end] = bestLen;
		}

		var winners = new List<(int start, int len, SpanCandidate cand)>();
		int pos = n;
		while (pos > 0)
		{
			int len = choiceLen[pos];
			if (len == 0) { pos -= 1; continue; }
			winners.Add((pos - len, len, cell[pos - len, len]!.Value));
			pos -= len;
		}
		winners.Reverse();
		return (winners, n);
	}

	/// <summary>캐릭터 후보 점수 계산 — 기존 SegmentCharacterCandidates의 채점 로직을 그대로
	/// 유지한다(이 세션에서 여러 실제 오탐 사례로 다듬어진 부분이라 재검증 없이 재사용).
	/// 예전과 달리 이 구간이 성계·함선으로도 매칭되는지는 더 이상 여기서 배제하지 않는다 —
	/// 통합 DP가 점수로 알아서 경쟁시킨다.</summary>
	private static int? TryScoreCharacterCandidate(int i, int len, string phrase, string speaker, CharacterResolver? chars)
	{
		if (!CharacterResolver.LooksLikeCharacterName(phrase)) return null;
		if (string.Equals(phrase, speaker, StringComparison.OrdinalIgnoreCase)) return null;

		CharResolveStatus status = chars?.GetStatus(phrase) ?? CharResolveStatus.Unknown;
		if (status == CharResolveStatus.DoesNotExist) return null;
		// ESI로 아직 미확인인 후보는 Title Case(단어별 대문자 시작)일 때만 점수를 준다.
		// RIFT가 영단어 사전으로 걸러내는 "평범한 소문자 문장 조각"을 우리는 이 방식으로 배제 —
		// 이미 실존 확인된(Exists) 이름은 신뢰할 수 있으므로 그대로 통과. 예외 둘 다 len==1로
		// 한정하는 이유: 2~3단어 조합까지 허용하면 "nv planet 7"처럼 평범한 소문자 문구까지
		// 통과해버린다(실제로 있었던 회귀).
		//   1) 숫자가 섞인 한 단어("heqiya3" 같은 부계정/알트 이름 흔한 패턴)는 평범한
		//      영단어일 수 없으므로 통과.
		//   2) 첫 글자 이후에 대문자가 섞인 한 단어("xxxGshankxxx" 같은 게이머 태그 스타일
		//      꾸밈 표기)도 마찬가지로 평범한 영어 문장 조각에선 나타나지 않는 패턴이라 통과.
		//      ("whereismyspacebar"처럼 순수 소문자만인 단어는 이 예외에 안 걸린다 — 길이
		//      기준을 넣어보려 했지만 "everything"/"whatever"/"immediately"류 흔한 영단어도
		//      전부 실제 EVE 캐릭터로 이미 존재해서(ESI로 직접 확인) DoesNotExist로 자동
		//      걸러지지도 않는 영구 오탐 위험이 있어 포기함 — 알려진 한계로 남겨둠.)
		if (status != CharResolveStatus.Exists && !CharacterResolver.IsTitleCaseName(phrase)
			&& !(len == 1 && phrase.Any(char.IsDigit))
			&& !(len == 1 && phrase.Skip(1).Any(char.IsUpper)))
			return null;

		// 점수 등급을 완전히 분리된 구간으로 둔다 (겹치면 역전 버그가 남는다):
		//   3등급(가장 신뢰): len단어 조합 자체가 ESI로 실존 확인됨      → 100000×len
		//   2등급:            len>1, Title Case, 조합은 아직 미확인      → 1000×(len-1) + 500
		//   1등급:            단일 단어가 ESI로 실존 확인됨              →   1000 (len=1 전용)
		//   0등급:            그 외 미확인 Title Case 후보(단일 단어뿐)   →   80
		if (status == CharResolveStatus.Exists && len > 1)
			return 100_000 * len;

		if (len > 1 && status == CharResolveStatus.Unknown)
		{
			// 아직 이 조합 자체("Prime Dallocort")로는 ESI에 물어본 적이 없다 — 백그라운드로
			// 큐에 넣어 결과를 캐시에 쌓는다. 나중에 이 조합이 실존 확인되면 위 3등급으로
			// 넘어가 확실히 이기고, "존재 안 함"으로 확인되면 위의 DoesNotExist에 걸려
			// 자동으로 개별 단어 분리로 복귀한다.
			chars?.Enqueue(phrase);

			// "Erador Sul"처럼 단어 하나만 우연히 다른 실존 캐릭터와 겹치는 경우엔 병합을
			// 우선(합산 최악값 1000×(len-1)+80보다 이 점수가 항상 높음)하되, "Bastilia
			// Neekee"처럼 단어 둘 다 각각 이미 다른 실존 캐릭터로 확인된 경우는 오히려 분리를
			// 기본값으로 둔다(합산 1000×len이 이 점수보다 항상 높음) — 사용자 판단: 이
			// 시그널(조합 미확인 + 부분 일치)만으론 "우연히 겹치는 두 사람"과 "진짜 2단어
			// 이름인데 각 부분도 따로 실존"을 구분할 수 없고(Prime Dallocort는 후자, Bastilia
			// Neekee는 전자 — 둘 다 ESI로 직접 확인됨), 첫 등장에 잘못 합쳐서 서로 다른 두
			// 사람을 한 명으로 보이게 하는 쪽이 더 나쁘다고 판단해 분리를 기본값으로 선택함.
			// 조합 자체가 ESI로 실존 확인되면(위 3등급) 다음 등장부터는 정확히 병합된다.
			return 1000 * (len - 1) + 500;
		}

		// len == 1만 여기 도달 (len>1은 위에서 3/2등급으로 이미 전부 처리됨).
		return status == CharResolveStatus.Exists ? 1000 : 80;
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