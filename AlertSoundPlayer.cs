using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace EVEAA.Mod;

/// <summary>WAV는 SoundPlayer(+볼륨 스케일), MP3 등은 winmm MCI로 재생.</summary>
internal sealed class AlertSoundPlayer : IDisposable
{
	private SoundPlayer? _player;
	private MemoryStream? _stream;
	private string? _path;
	private int _volume = 80;
	private bool _useMci;
	private const string MciAlias = "eveaa_alert_snd";

	public void Load(string path, int volumePercent)
	{
		DisposePlayer();
		_path = path;
		_volume = Math.Clamp(volumePercent, 0, 100);
		_useMci = false;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return;

		string ext = Path.GetExtension(path);
		if (!string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
		{
			_useMci = true;
			return;
		}

		try
		{
			byte[] raw = File.ReadAllBytes(path);
			byte[] scaled = ScaleWavVolume(raw, _volume / 100.0);
			_stream = new MemoryStream(scaled);
			_player = new SoundPlayer(_stream);
			_player.Load();
		}
		catch
		{
			DisposePlayer();
			try
			{
				_player = new SoundPlayer(path);
				_player.Load();
			}
			catch { _player = null; }
		}
	}

	public void SetVolume(int volumePercent)
	{
		volumePercent = Math.Clamp(volumePercent, 0, 100);
		if (volumePercent == _volume) return;
		if (string.IsNullOrEmpty(_path)) return;
		Load(_path, volumePercent);
	}

	public void Play()
	{
		try
		{
			if (_useMci)
			{
				PlayMci(_path!, _volume);
				return;
			}
			_player?.Play();
		}
		catch
		{
			try { SystemSounds.Exclamation.Play(); } catch { }
		}
	}

	private static void PlayMci(string path, int volumePercent)
	{
		Mci("close " + MciAlias);
		string escaped = path.Replace("\"", "");
		int err = Mci($"open \"{escaped}\" type mpegvideo alias {MciAlias}");
		if (err != 0)
			err = Mci($"open \"{escaped}\" alias {MciAlias}");
		if (err != 0)
			throw new InvalidOperationException("MCI open failed: " + err);

		int vol = Math.Clamp(volumePercent, 0, 100) * 10; // MCI: 0~1000
		Mci($"setaudio {MciAlias} volume to {vol}");
		err = Mci($"play {MciAlias}");
		if (err != 0)
			throw new InvalidOperationException("MCI play failed: " + err);
	}

	private static int Mci(string command)
	{
		return mciSendString(command, null, 0, IntPtr.Zero);
	}

	private void DisposePlayer()
	{
		try { Mci("close " + MciAlias); } catch { }
		try { _player?.Dispose(); } catch { }
		_player = null;
		try { _stream?.Dispose(); } catch { }
		_stream = null;
	}

	public void Dispose() => DisposePlayer();

	public static byte[] ScaleWavVolume(byte[] wav, double volume)
	{
		volume = Math.Clamp(volume, 0, 1);
		if (volume >= 0.999) return wav;
		if (wav.Length < 44) return wav;
		if (wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
			return wav;

		int pos = 12;
		int dataOffset = -1;
		int dataSize = 0;
		ushort bits = 16;
		while (pos + 8 <= wav.Length)
		{
			string id = Encoding.ASCII.GetString(wav, pos, 4);
			int size = BitConverter.ToInt32(wav, pos + 4);
			if (size < 0 || pos + 8 + size > wav.Length) break;
			if (id == "fmt " && size >= 16)
				bits = BitConverter.ToUInt16(wav, pos + 8 + 14);
			if (id == "data")
			{
				dataOffset = pos + 8;
				dataSize = size;
				break;
			}
			pos += 8 + size + (size & 1);
		}
		if (dataOffset < 0 || dataSize <= 0 || bits != 16)
			return wav;

		byte[] outb = (byte[])wav.Clone();
		int end = Math.Min(outb.Length, dataOffset + dataSize);
		for (int i = dataOffset; i + 1 < end; i += 2)
		{
			short sample = (short)(outb[i] | (outb[i + 1] << 8));
			int scaled = (int)Math.Round(sample * volume);
			if (scaled > short.MaxValue) scaled = short.MaxValue;
			if (scaled < short.MinValue) scaled = short.MinValue;
			outb[i] = (byte)(scaled & 0xFF);
			outb[i + 1] = (byte)((scaled >> 8) & 0xFF);
		}
		return outb;
	}

	[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
	private static extern int mciSendString(string command, StringBuilder? returnString, int returnLength, IntPtr callback);
}