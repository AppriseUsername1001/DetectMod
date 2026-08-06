namespace EVEAA.Mod;

/// <summary>임시 진단용 — ZKB feed 점멸 원인 추적. 원인 파악 후 제거할 것.</summary>
internal static class DiagLog
{
	private static readonly object Lock = new();

	public static void Write(string msg)
	{
		try
		{
			lock (Lock)
			{
				string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
				File.AppendAllText(Path.Combine(dir, "eveaa_mod_diag.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
			}
		}
		catch { }
	}
}
