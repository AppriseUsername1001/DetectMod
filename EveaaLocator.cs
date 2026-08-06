namespace EVEAA.Mod;

internal static class EveaaLocator
{
	public static string? FindOriginalExe(string? configuredPath)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
		{
			return configuredPath;
		}

		string baseDir = AppContext.BaseDirectory;
		string self = Path.GetFullPath(Environment.ProcessPath ?? "");

		foreach (string candidate in EnumerateCandidates(baseDir))
		{
			string full = Path.GetFullPath(candidate);
			if (!string.Equals(full, self, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
			{
				return full;
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateCandidates(string baseDir)
	{
		yield return Path.Combine(baseDir, "EVEAA v2.26.exe");
		yield return Path.Combine(baseDir, "EVEAA_original.exe");
		yield return Path.Combine(baseDir, "original", "EVEAA v2.26.exe");
		// exe 하나만 배포된 경우: 첫 실행 때 내장 리소스에서 풀어놓은 사본 (BundledAssets 참고)
		yield return Path.Combine(BundledAssets.ExtractedRoot, "EVEAA v2.26.exe");

		if (Directory.Exists(baseDir))
		{
			foreach (string path in Directory.EnumerateFiles(baseDir, "EVEAA*.exe"))
			{
				string name = Path.GetFileName(path);
				if (name.Contains("mod", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (name.Equals("EVEAA.exe", StringComparison.OrdinalIgnoreCase))
				{
					continue; // our published name
				}
				yield return path;
			}
		}
	}
}
