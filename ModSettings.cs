using System.Text.Json;
using EVEAA.Mod.Intel;

namespace EVEAA.Mod;

internal sealed class ModSettings
{
	public bool LaunchWithEve { get; set; }
	public string? EveaaExePath { get; set; }

	// 구버전(단일 캐릭터) 필드 — 마이그레이션 전용으로 남겨둠. Load()에서 IntelCharacters가
	// 비어있고 이 필드에 로그인 정보가 있으면 1회 이관 후 더는 쓰이지 않는다.
	public int IntelCharacterId { get; set; }
	public string? IntelCharacterName { get; set; }
	public string? IntelRefreshToken { get; set; }
	public string? IntelAccessToken { get; set; }

	/// <summary>로그인된 전체 캐릭터 목록 — 캐릭터별 알림(경고음)은 각자 독립적으로 동작한다.</summary>
	public List<TrackedCharacter> IntelCharacters { get; set; } = new();
	/// <summary>인텔 로그에 점프 수를 표시할 대상으로 지정된 캐릭터. 0이면 미지정(목록의 첫 캐릭터로 대체).</summary>
	public int IntelMainCharacterId { get; set; }

	/// <summary>점프거리 표시/위치 추적 대상으로 지정된 캐릭터 — 없으면(또는 지정된 ID가 이미
	/// 목록에서 빠졌으면) 목록의 첫 캐릭터로 대체. IntelPanel/ZkbFeedPanel이 공통으로 쓴다.</summary>
	public TrackedCharacter? GetMainCharacter() =>
		IntelCharacters.FirstOrDefault(c => c.CharacterId == IntelMainCharacterId) ?? IntelCharacters.FirstOrDefault();

	public int JumpRange { get; set; } = 4;
	public string? ChatlogsDir { get; set; }
	public string? IntelChannel { get; set; }
	public bool AlertSoundEnabled { get; set; } = true;
	public string? AlertSoundPath { get; set; }
	/// <summary>0~100. 알림음 재생 볼륨.</summary>
	public int AlertSoundVolume { get; set; } = 80;

	/// <summary>리전 지도 오버레이 위치/크기. X가 int.MinValue면 미설정.</summary>
	public int MapOverlayX { get; set; } = int.MinValue;
	public int MapOverlayY { get; set; } = int.MinValue;
	public int MapOverlayW { get; set; } = 420;
	public int MapOverlayH { get; set; } = 360;
	public bool MapOverlayLocked { get; set; }
	/// <summary>ZKB 킬 성계를 지도에 표시하는 시간(초). 기본 30.</summary>
	public int MapZkbDisplaySec { get; set; } = 30;
	/// <summary>인텔 로그 성계를 지도에 표시하는 시간(초). 기본 120.</summary>
	public int MapIntelDisplaySec { get; set; } = 120;
	/// <summary>새 인텔 성계 강조 유지 시간(초). 기본 30.</summary>
	public int MapIntelHighlightSec { get; set; } = 30;

	/// <summary>ZKB Feed: 감시 캐릭터와 같은 얼라이언스 로스 행 배경색 (ARGB). 저채도 파랑 기본값.</summary>
	public int ZkbSameAllianceColorArgb { get; set; } = unchecked((int)0xFFD8E4F0);
	/// <summary>ZKB Feed: 그 외 얼라이언스 로스 행 배경색 (ARGB). 저채도 빨강 기본값.</summary>
	public int ZkbOtherAllianceColorArgb { get; set; } = unchecked((int)0xFFF0DCDC);

	/// <summary>0~100. 경보기 "알림음 테스트" 재생 볼륨.</summary>
	public int AlarmSoundTestVolume { get; set; } = 80;

	/// <summary>인텔 이벤트를 중앙 서버(디스코드 봇)로 전송할지. 기본 꺼짐(옵트인) — URL/키는 내부용 서버로 미리 채워져 있지만, 실제 전송은 사용자가 대시보드의 토글을 켜야만 시작됨.</summary>
	public bool IntelReportEnabled { get; set; }
	/// <summary>인텔 수신 서버 base URL. 내부 corp 전용 hole-observer 인스턴스로 기본값 고정 — 새로 배포받는 사람도 별도 설정 없이 토글만 켜면 됨.</summary>
	public string? IntelReportUrl { get; set; } = "https://port-0-hole-observer-mmxg2b7w38a04dd1.sel3.cloudtype.app";
	/// <summary>인텔 수신 서버 인증용 공유 API 키. 내부 corp 전용 값으로 기본 고정.</summary>
	public string? IntelReportApiKey { get; set; } = "0053a25135139abc3d202fce997b1836";
	/// <summary>이 설치본을 식별하는 고정 ID. 최초 1회 자동 생성 후 저장됨.</summary>
	public string? IntelReportClientId { get; set; }
	/// <summary>인텔 로그 서버 전송 동의 알림문을 이미 한 번 물어봤는지. true가 되면 이후
	/// 로그인 때마다 다시 묻지 않는다 — 이 시점의 선택은 IntelReportEnabled에 반영됨.</summary>
	public bool IntelReportConsentAsked { get; set; }

	private static string SettingsPath
	{
		get
		{
			string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
			return Path.Combine(baseDir, "eveaa_mod_settings.json");
		}
	}

	public static ModSettings Load()
	{
		try
		{
			if (File.Exists(SettingsPath))
			{
				ModSettings? s = JsonSerializer.Deserialize<ModSettings>(File.ReadAllText(SettingsPath));
				if (s != null)
				{
					s.MigrateSingleCharacterIfNeeded();
					return s;
				}
			}
		}
		catch { }
		return new ModSettings();
	}

	/// <summary>구버전 단일 캐릭터 필드에 로그인 정보가 있고 새 리스트가 비어있으면
	/// 1회 이관한다 — 이미 이 기능이 있는 상태로 설치됐다면 아무 것도 하지 않는다.</summary>
	private void MigrateSingleCharacterIfNeeded()
	{
		if (IntelCharacters.Count > 0) return;
		if (IntelCharacterId <= 0 || string.IsNullOrEmpty(IntelRefreshToken)) return;
		IntelCharacters.Add(new TrackedCharacter
		{
			CharacterId = IntelCharacterId,
			CharacterName = IntelCharacterName ?? "",
			RefreshToken = IntelRefreshToken,
			AccessToken = IntelAccessToken ?? ""
		});
		IntelMainCharacterId = IntelCharacterId;
	}

	public void Save()
	{
		try
		{
			File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch { }
	}
}
