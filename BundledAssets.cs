using System.Reflection;

namespace EVEAA.Mod;

/// <summary>
/// exe 하나만 배포해도 동작하도록, 어셈블리에 내장한 원본 EVEAA exe/데이터/사운드를
/// 첫 실행 시 로컬 폴더로 풀어놓는다. 이미 같은 폴더에 원본이 있으면(구버전 방식 배포) 그쪽을
/// 그대로 쓰고 여기서는 아무것도 하지 않는다 — EveaaLocator/ModDataPaths가 우선순위를 정한다.
/// </summary>
internal static class BundledAssets
{
	private static readonly (string logicalName, string relativePath)[] Assets =
	{
		("Bundled.EveaaOriginal.exe", "EVEAA v2.26.exe"),
		("Bundled.Sound.Beep.wav", @"sound\BEEP.wav"),
		("Bundled.Sound.Clink.wav", @"sound\Clink.wav"),
		("Bundled.Sound.Pop.wav", @"sound\pop.wav"),
		("Bundled.Data.Systems.dat", @"data\Systems.dat"),
		("Bundled.Data.MapLayout.dat", @"data\MapLayout.dat"),
		("Bundled.Data.Ships.txt", @"data\Ships.txt"),
		("Bundled.Data.ShipAliases.txt", @"data\ShipAliases.txt"),
		// 모드 자체 데이터 폴더(eveaa_mod_data)도 같은 내용으로 채운다 — ModDataPaths.Resolve 참고.
		("Bundled.Data.Systems.dat", @"eveaa_mod_data\Systems.dat"),
		("Bundled.Data.MapLayout.dat", @"eveaa_mod_data\MapLayout.dat"),
		("Bundled.Data.Ships.txt", @"eveaa_mod_data\Ships.txt"),
		("Bundled.Data.ShipAliases.txt", @"eveaa_mod_data\ShipAliases.txt"),
	};

	public static string ExtractedRoot =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVEDetectmod");

	/// <summary>없는 파일만 골라 내장 리소스에서 풀어놓는다. 실패해도 앱 시작을 막지 않는다.</summary>
	public static void EnsureExtracted()
	{
		try
		{
			Assembly asm = Assembly.GetExecutingAssembly();
			foreach (var (logicalName, relativePath) in Assets)
			{
				string target = Path.Combine(ExtractedRoot, relativePath);
				if (File.Exists(target))
					continue;

				using Stream? res = asm.GetManifestResourceStream(logicalName);
				if (res is null)
					continue;

				string? dir = Path.GetDirectoryName(target);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				string tmp = target + ".tmp";
				using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write))
					res.CopyTo(fs);
				File.Move(tmp, target, overwrite: true);
			}
		}
		catch
		{
			// 추출 실패 시에도 EveaaLocator/ModDataPaths가 다른 후보 경로를 계속 찾는다.
		}
	}
}
