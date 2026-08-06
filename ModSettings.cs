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
