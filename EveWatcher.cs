using System.Diagnostics;
using Microsoft.Win32;

namespace EVEAA.Mod;

internal static class EveWatcher
{
	public const string WatchArg = "--eve-watch";
	private const string RunKeyName = "EVEAA_EveWatch";
	private const int PollMs = 3000;
	private const string EveTitlePrefix = "EVE - ";

	public static bool IsWatchMode(string[] args) =>
		args.Any(a => string.Equals(a, WatchArg, StringComparison.OrdinalIgnoreCase));

	public static string ExePath =>
		Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

	private static string PidPath => Path.Combine(Path.GetTempPath(), "eveaa_eve_watch.pid");

	public static void Apply(bool enabled)
	{
		SetStartup(enabled);
		if (enabled)
		{
			StartWatcherProcess();
		}
		else
		{
			StopWatcherProcess();
		}
	}

	public static void SetStartup(bool enabled)
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
				@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
			if (key is null)
			{
				return;
			}

			if (enabled)
			{
				string path = ExePath;
				if (!string.IsNullOrEmpty(path))
				{
					key.SetValue(RunKeyName, $"\"{path}\" {WatchArg}");
				}
			}
			else
			{
				key.DeleteValue(RunKeyName, throwOnMissingValue: false);
			}
		}
		catch
		{
			// ignore registry failures
		}
	}

	public static bool StartWatcherProcess()
	{
		(int pid, string exePath) = ReadPidInfo();
		if (pid > 0 && IsPidAlive(pid))
		{
			// 이미 떠 있는 감시 프로세스가 지금 실행 중인 버전과 같은 exe면 그대로 둔다.
			if (string.Equals(exePath, ExePath, StringComparison.OrdinalIgnoreCase))
				return true;

			// 다른(주로 구) 버전의 감시 프로세스가 떠 있는 경우 — 그대로 두면 새 버전을 아무리
			// 게시해도 이 낡은 백그라운드 프로세스가 EVE 실행 감지 때마다 계속 구버전만
			// 실행한다. 죽이고 지금 버전으로 새로 띄운다.
			try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
			TryDeletePid();
		}

		try
		{
			string path = ExePath;
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			Process.Start(new ProcessStartInfo
			{
				FileName = path,
				Arguments = WatchArg,
				WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static void StopWatcherProcess()
	{
		int pid = ReadPid();
		if (pid > 0 && pid != Environment.ProcessId && IsPidAlive(pid))
		{
			try
			{
				Process.GetProcessById(pid).Kill(entireProcessTree: true);
			}
			catch
			{
			}
		}

		TryDeletePid();
	}

	public static bool IsWatcherRunning()
	{
		int pid = ReadPid();
		if (pid > 0 && IsPidAlive(pid))
		{
			return true;
		}

		TryDeletePid();
		return false;
	}

	public static void RunWatchLoop()
	{
		int existing = ReadPid();
		if (existing > 0 && existing != Environment.ProcessId && IsPidAlive(existing))
		{
			return;
		}

		try
		{
			File.WriteAllText(PidPath, Environment.ProcessId + "\n" + ExePath);
		}
		catch
		{
		}

		// EVE가 "새로 켜질 때"만 자동 실행.
		// (이전에 eve&&!gui 조건이면 사용자가 EVEAA를 닫아도 바로 다시 켜짐)
		bool prevEve = IsEveClientRunning();
		try
		{
			while (true)
			{
				bool eve = IsEveClientRunning();
				bool eveJustStarted = eve && !prevEve;
				prevEve = eve;

				if (eveJustStarted && !IsEveaaGuiRunning())
				{
					try
					{
						WindowFix.SanitizeAllUserConfigs();
						string self = ExePath;
						if (!string.IsNullOrEmpty(self) && File.Exists(self))
						{
							Process.Start(new ProcessStartInfo
							{
								FileName = self,
								WorkingDirectory = Path.GetDirectoryName(self) ?? Environment.CurrentDirectory,
								UseShellExecute = true
							});
						}
					}
					catch
					{
					}
				}

				Thread.Sleep(PollMs);
			}
		}
		finally
		{
			TryDeletePid();
		}
	}

	public static bool IsEveClientRunning()
	{
		try
		{
			return Process.GetProcesses().Any(p =>
			{
				try
				{
					string t = p.MainWindowTitle;
					return !string.IsNullOrEmpty(t) && t.StartsWith(EveTitlePrefix, StringComparison.Ordinal);
				}
				catch
				{
					return false;
				}
			});
		}
		catch
		{
			return false;
		}
	}

	public static bool IsEveaaGuiRunning()
	{
		try
		{
			int self = Environment.ProcessId;
			foreach (Process p in Process.GetProcesses())
			{
				try
				{
					if (p.Id == self)
						continue;

					string name = p.ProcessName ?? "";
					// EVEAA_mod GUI (워치 모드 제외: 메인 창 없음)
					if (name.Equals("EVEAA_mod", StringComparison.OrdinalIgnoreCase))
					{
						if (!string.IsNullOrEmpty(p.MainWindowTitle))
							return true;
						continue;
					}

					string title = p.MainWindowTitle ?? "";
					if (title.StartsWith("EVEAA", StringComparison.OrdinalIgnoreCase) &&
					    !title.Contains("Mod Host", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
				catch
				{
				}
				finally
				{
					p.Dispose();
				}
			}
		}
		catch
		{
		}

		return false;
	}

	private static int ReadPid()
	{
		try
		{
			string first = File.ReadLines(PidPath).FirstOrDefault() ?? "";
			return int.Parse(first.Trim());
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>PID와 함께 그 감시 프로세스가 어느 exe에서 떠 있는지도 읽는다 — 버전이 다르면
	/// StartWatcherProcess()가 교체할 수 있도록.</summary>
	private static (int pid, string exePath) ReadPidInfo()
	{
		try
		{
			string[] lines = File.ReadAllLines(PidPath);
			int pid = lines.Length > 0 ? int.Parse(lines[0].Trim()) : 0;
			string exePath = lines.Length > 1 ? lines[1].Trim() : "";
			return (pid, exePath);
		}
		catch
		{
			return (0, "");
		}
	}

	private static bool IsPidAlive(int pid)
	{
		try
		{
			using Process p = Process.GetProcessById(pid);
			return !p.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private static void TryDeletePid()
	{
		try
		{
			if (File.Exists(PidPath))
			{
				File.Delete(PidPath);
			}
		}
		catch
		{
		}
	}
}
