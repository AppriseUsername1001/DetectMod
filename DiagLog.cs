namespace EVEAA.Mod;

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
				File.AppendAllText(Path.Combine(dir, "eveaa_mod_diag3.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
			}
		}
		catch { }
	}
}
