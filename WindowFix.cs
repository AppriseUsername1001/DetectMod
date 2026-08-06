using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EVEAA.Mod;

internal static class WindowFix
{
	private static readonly Regex PointRegex = new(
		@"^\s*(-?\d+)\s*,\s*(-?\d+)\s*$",
		RegexOptions.Compiled);

	/// <summary>
	/// 최소화된 창 좌표(-32000) 또는 화면 밖 좌표를 제거해 EVEAA 창이 안 뜨는 문제를 막는다.
	/// </summary>
	public static int SanitizeAllUserConfigs()
	{
		string root = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"EVEAA");
		if (!Directory.Exists(root))
		{
			return 0;
		}

		int fixedCount = 0;
		foreach (string path in Directory.EnumerateFiles(root, "user.config", SearchOption.AllDirectories))
		{
			if (SanitizeConfigFile(path))
			{
				fixedCount++;
			}
		}
		return fixedCount;
	}

	public static bool SanitizeConfigFile(string path)
	{
		try
		{
			XDocument doc = XDocument.Load(path);
			XElement? setting = doc
				.Descendants("setting")
				.FirstOrDefault(e => (string?)e.Attribute("name") == "StartLocation");
			XElement? valueNode = setting?.Element("value");
			if (valueNode == null)
			{
				return false;
			}

			string raw = valueNode.Value?.Trim() ?? "";
			Match m = PointRegex.Match(raw);
			if (!m.Success)
			{
				return false;
			}

			int x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
			int y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
			if (IsRestorable(x, y))
			{
				return false;
			}

			valueNode.Value = "0, 0";
			doc.Save(path);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsRestorable(int x, int y)
	{
		if (x <= -10000 || y <= -10000)
		{
			return false;
		}

		var probe = new Rectangle(x, y, 120, 40);
		foreach (Screen screen in Screen.AllScreens)
		{
			if (screen.WorkingArea.IntersectsWith(probe))
			{
				return true;
			}
		}
		return false;
	}
}
