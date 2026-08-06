using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EVEAA.Mod.Intel;

internal sealed class ChatlogWatcher : IDisposable
{
	private (string path, long offset)? _state;
	private byte? _utf16Leftover;
	private DateTime _lastRescanUtc = DateTime.MinValue;
	private string? _cachedPath;
	private string _cachedChannel = "";
	private string _cachedDir = "";
	private FileSystemWatcher? _fsw;
	private string _fswDir = "";
	private readonly object _fswGate = new();
	private FileStream? _openStream;
	private string? _openStreamPath;
	private string _pendingLine = "";

	private const double RescanSec = 3.0;
	private const double StaleWriteSec = 40.0;
	private const double SeedWindowMinutes = 5.0;
	/// <summary>이보다 오래된 인텔 라인은 실시간 경보로 취급하지 않고 버린다 (RIFT의 10분 스테일 컷오프 참고).</summary>
	private const double StaleIntelMinutes = 10.0;

	private static readonly Regex FileRe = new(
		@"^(?<chan>.+)_(?<ymd>\d{8})_(?<hms>\d{6})(?:_(?<cid>\d+))?\.txt$",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public string ChatlogsDir { get; set; } = "";
	public string ChannelName { get; set; } = "";
	public int PreferredCharacterId { get; set; }
	public string? ActiveLogPath { get; private set; }
	public event Action? FileChanged;

	public static string DefaultChatlogsDir()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		foreach (string sub in new[] { @"EVE\logs\Chatlogs", @"EVE\Logs\Chatlogs" })
		{
			string p = Path.Combine(home, sub);
			if (Directory.Exists(p)) return p;
		}
		return Path.Combine(home, @"EVE\logs\Chatlogs");
	}

	public void EnsureWatching()
	{
		string dir = ChatlogsDir ?? "";
		string channel = (ChannelName ?? "").Trim();
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || channel.Length == 0)
		{
			StopWatcher();
			return;
		}
		StartWatcher(dir, channel);
	}

	/// <summary>UI에서 감시 파일이 정해지면 즉시 끝부터 테일. 이후 새 줄만 반영.</summary>
	public void PinActiveLog(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return;
		path = Path.GetFullPath(path);
		CloseOpenStream();
		_utf16Leftover = null;
		_pendingLine = "";
		ActiveLogPath = path;
		_cachedPath = path;
		_cachedDir = Path.GetDirectoryName(path) ?? ChatlogsDir;
		_cachedChannel = ChannelName ?? "";
		// force Poll to treat as new path and seed recent lines
		_state = null;
		EnsureDirWatcher(_cachedDir);
		try { FileChanged?.Invoke(); } catch { }
	}

	public List<ParsedIntelLine> Poll(SystemsDatabase systems, ShipDatabase ships, CharacterResolver? chars = null)
	{
		var results = new List<ParsedIntelLine>();
		string channel = (ChannelName ?? "").Trim();
		if (!Directory.Exists(ChatlogsDir) || channel.Length == 0)
		{
			ActiveLogPath = null;
			_state = null;
			_cachedPath = null;
			CloseOpenStream();
			StopWatcher();
			return results;
		}

		EnsureWatching();

		string? path = ResolveActivePath(ChatlogsDir, channel);
		ActiveLogPath = path;
		if (path is null)
		{
			CloseOpenStream();
			return results;
		}

		if (_state is null || !string.Equals(_state.Value.path, path, StringComparison.OrdinalIgnoreCase))
		{
			CloseOpenStream();
			_utf16Leftover = null;
			_pendingLine = "";
			// 파일 전환/최초 핀: 끝으로 붙되, 최근 줄 일부는 즉시 반영 (빈 로그 방지)
			DateTime seedCutoffUtc = DateTime.UtcNow.AddMinutes(-SeedWindowMinutes);
			var seeded = SeedRecentLines(path, seedCutoffUtc);
			_pendingLine = "";
			_utf16Leftover = null;
			_state = (path, seeded.endOffset);
			EnsureDirWatcher(ChatlogsDir);
			foreach (string line in seeded.lines)
			{
				var item = IntelParser.ParseIntelLine(line, systems, ships, chars);
				if (item is not null && IsFreshEnough(item))
					results.Add(item);
			}
			return results;
		}

		long len = SafeLength(path);
		if (len >= 0 && len <= _state.Value.offset)
		{
			if (len >= 0 && len < _state.Value.offset)
			{
				CloseOpenStream();
				_state = (path, 0);
				_utf16Leftover = null;
				_pendingLine = "";
			}
			else
			{
				return results;
			}
		}

		var (lines, newOff) = ReadNewLines(path, _state.Value.offset);
		_state = (path, newOff);
		foreach (string line in lines)
		{
			var item = IntelParser.ParseIntelLine(line, systems, ships, chars);
			if (item is not null && IsFreshEnough(item))
				results.Add(item);
		}
		return results;
	}

	/// <summary>파일 재탐색/오프셋 리셋 등으로 오래된 백로그를 한 번에 읽더라도 실시간 인텔처럼 취급하지 않는다.</summary>
	private static bool IsFreshEnough(ParsedIntelLine item)
	{
		if (item.TimestampUtc is not DateTime ts) return true;
		return (DateTime.UtcNow - ts).TotalMinutes <= StaleIntelMinutes;
	}

	private string? ResolveActivePath(string dir, string channel)
	{
		bool dirChanged = !string.Equals(_cachedDir, dir, StringComparison.OrdinalIgnoreCase);
		bool chanChanged = !string.Equals(_cachedChannel, channel, StringComparison.OrdinalIgnoreCase);
		if (dirChanged || chanChanged)
		{
			_cachedDir = dir;
			_cachedChannel = channel;
			_cachedPath = null;
			_state = null;
			_lastRescanUtc = DateTime.MinValue;
			_pendingLine = "";
			CloseOpenStream();
		}

		if (_state is not null && File.Exists(_state.Value.path))
		{
			if ((DateTime.UtcNow - _lastRescanUtc).TotalSeconds < RescanSec)
				return _state.Value.path;

			_lastRescanUtc = DateTime.UtcNow;
			string? latest = FindLatestChannelLog(dir, channel, PreferredCharacterId);
			if (latest is not null &&
			    !string.Equals(latest, _state.Value.path, StringComparison.OrdinalIgnoreCase) &&
			    (IsDefinitelyNewerSession(latest, _state.Value.path) ||
			     ShouldSwitchToFresherFile(latest, _state.Value.path)))
			{
				_cachedPath = latest;
				return latest;
			}

			_cachedPath = _state.Value.path;
			return _state.Value.path;
		}

		_lastRescanUtc = DateTime.UtcNow;
		_cachedPath = FindLatestChannelLog(dir, channel, PreferredCharacterId);
		return _cachedPath;
	}

	private void StartWatcher(string dir, string channel)
	{
		// 채널 prefix 전체 감시 — 다른 캐릭터 로그가 살아나면 전환 가능
		EnsureDirWatcher(dir);
	}

	private void EnsureFileWatcher(string filePath)
	{
		string? dir = Path.GetDirectoryName(filePath);
		string name = Path.GetFileName(filePath);
		if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
		lock (_fswGate)
		{
			if (_fsw is not null &&
			    string.Equals(_fswDir, dir, StringComparison.OrdinalIgnoreCase) &&
			    string.Equals(_fsw.Filter, name, StringComparison.OrdinalIgnoreCase) &&
			    _fsw.EnableRaisingEvents)
				return;
			StopWatcher_NoLock();
			try
			{
				var fsw = new FileSystemWatcher(dir, name)
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
					IncludeSubdirectories = false,
					InternalBufferSize = 64 * 1024
				};
				fsw.Changed += OnFsEvent;
				fsw.Created += OnFsEvent;
				fsw.Renamed += OnFsRenamed;
				fsw.EnableRaisingEvents = true;
				_fsw = fsw;
				_fswDir = dir;
			}
			catch
			{
				_fsw = null;
				_fswDir = "";
			}
		}
	}

	private void EnsureDirWatcher(string dir)
	{
		lock (_fswGate)
		{
			if (_fsw is not null &&
			    string.Equals(_fswDir, dir, StringComparison.OrdinalIgnoreCase) &&
			    _fsw.Filter == "*.txt" &&
			    _fsw.EnableRaisingEvents)
				return;
			StopWatcher_NoLock();
			try
			{
				var fsw = new FileSystemWatcher(dir, "*.txt")
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
					IncludeSubdirectories = false,
					InternalBufferSize = 64 * 1024
				};
				fsw.Changed += OnFsEvent;
				fsw.Created += OnFsEvent;
				fsw.Renamed += OnFsRenamed;
				fsw.EnableRaisingEvents = true;
				_fsw = fsw;
				_fswDir = dir;
			}
			catch
			{
				_fsw = null;
				_fswDir = "";
			}
		}
	}

	private void StopWatcher()
	{
		lock (_fswGate) StopWatcher_NoLock();
	}

	private void StopWatcher_NoLock()
	{
		if (_fsw is null) return;
		try
		{
			_fsw.EnableRaisingEvents = false;
			_fsw.Changed -= OnFsEvent;
			_fsw.Created -= OnFsEvent;
			_fsw.Renamed -= OnFsRenamed;
			_fsw.Dispose();
		}
		catch { }
		_fsw = null;
		_fswDir = "";
	}

	private void OnFsEvent(object sender, FileSystemEventArgs e)
	{
		// 특정 파일 감시 중이면 이벤트 전부 수락, 폴더 감시면 채널 prefix 필터
		if (_fsw is not null && _fsw.Filter != "*.txt")
		{ /* pinned file */ }
		else if (!IsOurChannelFile(e.Name))
			return;
		_lastRescanUtc = DateTime.MinValue;
		try { FileChanged?.Invoke(); } catch { }
	}

	private void OnFsRenamed(object sender, RenamedEventArgs e)
	{
		if (_fsw is not null && _fsw.Filter != "*.txt")
		{ /* pinned file */ }
		else if (!IsOurChannelFile(e.Name) && !IsOurChannelFile(e.OldName))
			return;
		_lastRescanUtc = DateTime.MinValue;
		try { FileChanged?.Invoke(); } catch { }
	}

	private bool IsOurChannelFile(string? name)
	{
		if (string.IsNullOrEmpty(name)) return false;
		string channel = (ChannelName ?? "").Trim();
		if (channel.Length == 0) return false;
		return name.StartsWith(channel + "_", StringComparison.OrdinalIgnoreCase) &&
		       name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsDefinitelyNewerSession(string candidate, string current)
	{
		if (!TryGetSessionStamp(Path.GetFileName(candidate), out DateTime candStamp))
			return false;
		if (!TryGetSessionStamp(Path.GetFileName(current), out DateTime curStamp))
			return true;
		return candStamp > curStamp;
	}

	private static bool ShouldSwitchToFresherFile(string candidate, string current)
	{
		try
		{
			var cand = new FileInfo(candidate);
			var cur = new FileInfo(current);
			cand.Refresh();
			cur.Refresh();
			if (!cand.Exists || !cur.Exists) return false;
			if (cand.LastWriteTime <= cur.LastWriteTime) return false;
			double curAge = (DateTime.Now - cur.LastWriteTime).TotalSeconds;
			double candAge = (DateTime.Now - cand.LastWriteTime).TotalSeconds;
			if (curAge >= StaleWriteSec && candAge + 5 < curAge)
				return true;
			if ((cand.LastWriteTime - cur.LastWriteTime).TotalSeconds >= 20)
				return true;
			return false;
		}
		catch { return false; }
	}

	private static bool TryGetSessionStamp(string fileName, out DateTime stamp)
	{
		stamp = default;
		var m = FileRe.Match(fileName);
		if (!m.Success) return false;
		return TryParseLogTimestamp(m.Groups["ymd"].Value, m.Groups["hms"].Value, out stamp);
	}

	public static string? FindClosestLogToNow(string dir, string channelName, int preferredCharacterId = 0) =>
		FindLatestChannelLog(dir, channelName, preferredCharacterId);

	public static string? FindLatestChannelLog(string dir, string channelName, int preferredCharacterId = 0)
	{
		channelName = (channelName ?? "").Trim();
		if (string.IsNullOrEmpty(channelName) || !Directory.Exists(dir))
			return null;

		DateTime today = DateTime.Today;
		DateTime now = DateTime.Now;
		var candidates = new List<(string path, DateTime write, long len, bool prefer)>();

		try
		{
			CollectDayCandidates(dir, channelName, today, preferredCharacterId, today, requireWriteOnDay: false, candidates);
			CollectDayCandidates(dir, channelName, today.AddDays(-1), preferredCharacterId, today, requireWriteOnDay: true, candidates);
			if (candidates.Count == 0)
				CollectChannelModifiedOnDay(dir, channelName, preferredCharacterId, today, candidates);

			if (candidates.Count == 0)
				return null;

			// 1) 최근 StaleWriteSec 안에 쓰인 파일 중 preferred 캐릭터 우선
			// 2) 없으면 가장 최근 LastWriteTime 파일 (다른 캐릭터 로그라도 활성 채널 따라감)
			var fresh = candidates
				.Where(c => (now - c.write).TotalSeconds <= StaleWriteSec)
				.OrderByDescending(c => c.prefer)
				.ThenByDescending(c => c.write)
				.ThenByDescending(c => c.len)
				.ToList();
			if (fresh.Count > 0)
				return fresh[0].path;

			return candidates
				.OrderByDescending(c => c.write)
				.ThenByDescending(c => c.prefer)
				.ThenByDescending(c => c.len)
				.Select(c => c.path)
				.FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}

	private static void CollectDayCandidates(
		string dir,
		string channelName,
		DateTime fileNameDay,
		int preferredCharacterId,
		DateTime writeDayFilter,
		bool requireWriteOnDay,
		List<(string path, DateTime write, long len, bool prefer)> sink)
	{
		string ymd = fileNameDay.ToString("yyyyMMdd");
		string pattern = EscapeGlob(channelName) + "_" + ymd + "_*.txt";
		foreach (string file in Directory.EnumerateFiles(dir, pattern))
		{
			if (!TryDescribeChannelFile(file, channelName, preferredCharacterId, out var info))
				continue;
			if (requireWriteOnDay && info.write.Date != writeDayFilter.Date)
				continue;
			if (sink.Exists(x => string.Equals(x.path, file, StringComparison.OrdinalIgnoreCase)))
				continue;
			sink.Add(info);
		}
	}

	private static void CollectChannelModifiedOnDay(
		string dir,
		string channelName,
		int preferredCharacterId,
		DateTime day,
		List<(string path, DateTime write, long len, bool prefer)> sink)
	{
		string pattern = EscapeGlob(channelName) + "_*.txt";
		foreach (string file in Directory.EnumerateFiles(dir, pattern))
		{
			if (!TryDescribeChannelFile(file, channelName, preferredCharacterId, out var info))
				continue;
			if (info.write.Date != day.Date)
				continue;
			sink.Add(info);
		}
	}

	private static bool TryDescribeChannelFile(
		string file,
		string channelName,
		int preferredCharacterId,
		out (string path, DateTime write, long len, bool prefer) info)
	{
		info = default;
		string name = Path.GetFileName(file);
		var m = FileRe.Match(name);
		if (!m.Success) return false;
		if (!string.Equals(m.Groups["chan"].Value, channelName, StringComparison.OrdinalIgnoreCase))
			return false;
		try
		{
			var fi = new FileInfo(file);
			bool prefer = preferredCharacterId > 0 &&
			              m.Groups["cid"].Success &&
			              m.Groups["cid"].Value == preferredCharacterId.ToString();
			info = (file, fi.LastWriteTime, fi.Length, prefer);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string EscapeGlob(string name)
	{
		return name.Replace("[", "[[]", StringComparison.Ordinal)
			.Replace("*", "[*]", StringComparison.Ordinal)
			.Replace("?", "[?]", StringComparison.Ordinal);
	}

	private static long SafeLength(string path)
	{
		try
		{
			var fi = new FileInfo(path);
			fi.Refresh();
			return fi.Length;
		}
		catch { return -1; }
	}

	private static bool TryParseLogTimestamp(string ymd, string hms, out DateTime stamp)
	{
		stamp = default;
		if (ymd.Length != 8 || hms.Length != 6)
			return false;
		return DateTime.TryParseExact(
			ymd + hms,
			"yyyyMMddHHmmss",
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeLocal,
			out stamp);
	}

	private void CloseOpenStream()
	{
		try { _openStream?.Dispose(); } catch { }
		_openStream = null;
		_openStreamPath = null;
	}

	private FileStream? GetOpenStream(string path)
	{
		if (_openStream is not null &&
		    string.Equals(_openStreamPath, path, StringComparison.OrdinalIgnoreCase))
			return _openStream;
		CloseOpenStream();
		try
		{
			_openStream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
			_openStreamPath = path;
			return _openStream;
		}
		catch
		{
			return null;
		}
	}

	private (List<string> lines, long endOffset) SeedRecentLines(string path, DateTime cutoffUtc)
	{
		var empty = new List<string>();
		try
		{
			long len = SafeLength(path);
			if (len <= 0) return (empty, 0);
			if ((len & 1) != 0) len--;
			long readFrom = Math.Max(0, len - 64 * 1024);
			if ((readFrom & 1) != 0) readFrom--;
			var (all, _) = ReadNewLines(path, readFrom);
			var recent = all.Where(line =>
			{
				DateTime? t = TryExtractLineUtc(line);
				return t is null || t.Value >= cutoffUtc;
			}).ToList();
			return (recent, len);
		}
		catch
		{
			long off = SafeLength(path);
			if (off < 0) off = 0;
			if ((off & 1) != 0) off--;
			return (empty, off);
		}
	}

	private static readonly Regex LineTimeRe = new(
		@"^\[\s*(?<date>[\d.]+)\s+(?<time>[\d:]+)\s+\]",
		RegexOptions.Compiled);

	private static DateTime? TryExtractLineUtc(string line)
	{
		var m = LineTimeRe.Match(line);
		if (!m.Success) return null;
		string stamp = m.Groups["date"].Value.Trim() + " " + m.Groups["time"].Value.Trim();
		if (DateTime.TryParseExact(
			stamp, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
			return dt;
		return null;
	}

	private (List<string> lines, long offset) ReadNewLines(string path, long offset)
	{
		try
		{
			// 매 폴링마다 새로 연다 — 열린 핸들 Length 캐시/잠금 이슈 회피
			CloseOpenStream();
			using var fs = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 8192,
				FileOptions.SequentialScan);

			long length = fs.Length;
			if (offset > length)
			{
				offset = 0;
				_utf16Leftover = null;
				_pendingLine = "";
			}
			if ((offset & 1) != 0)
				offset--;

			fs.Seek(offset, SeekOrigin.Begin);
			int available = (int)(length - offset);
			if (available <= 0)
				return (new List<string>(), offset);

			byte[] raw = new byte[available];
			int n = fs.Read(raw, 0, raw.Length);
			long newOff = offset + n;
			if (n <= 0)
				return (new List<string>(), newOff);

			byte[] buf;
			if (_utf16Leftover is byte left)
			{
				buf = new byte[1 + n];
				buf[0] = left;
				Buffer.BlockCopy(raw, 0, buf, 1, n);
				_utf16Leftover = null;
			}
			else
			{
				buf = new byte[n];
				Buffer.BlockCopy(raw, 0, buf, 0, n);
			}

			if ((buf.Length & 1) != 0)
			{
				_utf16Leftover = buf[^1];
				Array.Resize(ref buf, buf.Length - 1);
			}
			if (buf.Length == 0)
				return (new List<string>(), newOff);

			string chunk = Encoding.Unicode.GetString(buf);
			string text = _pendingLine + chunk;
			_pendingLine = "";

			bool endsWithNl = text.EndsWith('\n') || text.EndsWith('\r');
			string[] parts = text.Split('\n');
			var complete = new List<string>();
			for (int i = 0; i < parts.Length; i++)
			{
				string s = parts[i].Trim().TrimEnd('\r');
				while (s.Length > 0 && (s[0] == '\uFEFF' || s[0] == '\uFFFE'))
					s = s[1..].TrimStart();
				// 줄 앞 잡문자 제거 후 [ 로 시작하도록
				int bracket = s.IndexOf('[');
				if (bracket > 0)
					s = s[bracket..];

				bool isLast = i == parts.Length - 1;
				if (isLast && !endsWithNl)
				{
					_pendingLine = parts[i].TrimEnd('\r');
					break;
				}
				if (s.Length > 0)
					complete.Add(s);
			}
			return (complete, newOff);
		}
		catch
		{
			CloseOpenStream();
			return (new List<string>(), offset);
		}
	}

	public void Dispose()
	{
		StopWatcher();
		CloseOpenStream();
	}
}