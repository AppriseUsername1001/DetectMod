using System.Text.Json;

namespace EVEAA.Mod;

internal sealed class ModSettings
{
	public bool LaunchWithEve { get; set; }
	public string? EveaaExePath { get; set; }

	public int IntelCharacterId { get; set; }
	public string? IntelCharacterName { get; set; }
	public string? IntelRefreshToken { get; set; }
	public string? IntelAccessToken { get; set; }
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
					return s;
			}
		}
		catch { }
		return new ModSettings();
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
