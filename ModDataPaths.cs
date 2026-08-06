namespace EVEAA.Mod;

/// <summary>
/// EVEAA original data/ folder collision avoidance — mod-only data path.
/// </summary>
internal static class ModDataPaths
{
	public const string FolderName = "eveaa_mod_data";

	public static string Resolve(string fileName)
	{
		foreach (string dir in CandidateDirs())
		{
			string path = Path.Combine(dir, fileName);
			if (File.Exists(path))
				return path;
		}
		string fallbackDir = Path.Combine(
			Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
			FolderName);
		return Path.Combine(fallbackDir, fileName);
	}

	private static IEnumerable<string> CandidateDirs()
	{
		string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
		if (!string.IsNullOrEmpty(exeDir))
		{
			yield return Path.Combine(exeDir, FolderName);
			yield return Path.Combine(exeDir, "data");
		}
		string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.IsNullOrEmpty(baseDir))
		{
			yield return Path.Combine(baseDir, FolderName);
			yield return Path.Combine(baseDir, "data");
		}

		// exe 하나만 배포된 경우: 첫 실행 때 내장 리소스에서 풀어놓은 사본 (BundledAssets 참고)
		yield return Path.Combine(BundledAssets.ExtractedRoot, FolderName);
		yield return Path.Combine(BundledAssets.ExtractedRoot, "data");
	}
}