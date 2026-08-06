using System.Xml;
using System.Xml.Linq;
using System.Drawing;

namespace EVEAA.Mod.Intel;

internal sealed class MapSystemLayout
{
	public string Name { get; init; } = "";
	public float X { get; init; }
	public float Y { get; init; }
}

internal sealed class RegionMap
{
	public string Name { get; init; } = "";
	public int RegionId { get; init; }
	public Dictionary<string, MapSystemLayout> Systems { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MapLayoutDatabase
{
	private readonly Dictionary<string, RegionMap> _byRegion = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _systemToRegion = new(StringComparer.OrdinalIgnoreCase);

	public bool Loaded => _byRegion.Count > 0;

	public RegionMap? GetRegion(string regionName)
	{
		string key = NormalizeRegionName(regionName);
		if (key.Length == 0) return null;
		if (_byRegion.TryGetValue(key, out var r)) return r;
		// Systems.dat: "The Forge" / MapLayout: "The_Forge"
		string alt = key.Replace(' ', '_');
		if (_byRegion.TryGetValue(alt, out r)) return r;
		alt = key.Replace('_', ' ');
		if (_byRegion.TryGetValue(alt, out r)) return r;
		return null;
	}

	private static string NormalizeRegionName(string? name) => (name ?? "").Trim();

	public RegionMap? GetRegionForSystem(string systemName)
	{
		if (_systemToRegion.TryGetValue((systemName ?? "").Trim(), out string? region))
			return GetRegion(region);
		return null;
	}

	public int LoadFromDat(string path)
	{
		_byRegion.Clear();
		_systemToRegion.Clear();
		if (!File.Exists(path))
			throw new FileNotFoundException("MapLayout.dat 없음", path);

		using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element || reader.Name != "MapRegion")
				continue;

			string regionName = "";
			int regionId = 0;
			var systems = new Dictionary<string, MapSystemLayout>(StringComparer.OrdinalIgnoreCase);
			var inRegion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			using var sub = reader.ReadSubtree();
			sub.Read();
			while (sub.Read())
			{
				if (sub.NodeType != XmlNodeType.Element)
					continue;
				switch (sub.Name)
				{
					case "DotLanRef":
						regionName = sub.ReadElementContentAsString().Trim();
						break;
					case "ID":
						int.TryParse(sub.ReadElementContentAsString(), out regionId);
						break;
					case "MapSystems":
						ParseMapSystems(sub, systems, inRegion);
						break;
				}
			}

			if (string.IsNullOrEmpty(regionName))
				continue;
			var map = new RegionMap { Name = regionName, RegionId = regionId };
			foreach (var kv in systems)
			{
				map.Systems[kv.Key] = kv.Value;
				// 이웃 리전(OutOfRegion) 스텁이 본래 리전 매핑을 덮어쓰지 않게
				if (inRegion.Contains(kv.Key) || !_systemToRegion.ContainsKey(kv.Key))
					_systemToRegion[kv.Key] = regionName;
			}
			_byRegion[regionName] = map;
			// Systems.dat "Vale of the Silent" / DotLan "Vale_of_the_Silent" 양방향
			string spaced = regionName.Replace('_', ' ');
			string underscored = regionName.Replace(' ', '_');
			if (!_byRegion.ContainsKey(spaced)) _byRegion[spaced] = map;
			if (!_byRegion.ContainsKey(underscored)) _byRegion[underscored] = map;
		}
		return _byRegion.Count;
	}

	private static void ParseMapSystems(XmlReader parent, Dictionary<string, MapSystemLayout> systems, HashSet<string> inRegionNames)
	{
		using var sub = parent.ReadSubtree();
		sub.Read();
		while (sub.Read())
		{
			if (sub.NodeType != XmlNodeType.Element || sub.Name != "item")
				continue;

			string key = "";
			float lx = 0, ly = 0;
			bool outOfRegion = false;
			using var item = sub.ReadSubtree();
			item.Read();
			while (item.Read())
			{
				if (item.NodeType != XmlNodeType.Element)
					continue;
				if (item.Name == "key")
				{
					using var ksub = item.ReadSubtree();
					var kx = System.Xml.Linq.XDocument.Load(ksub);
					key = ((string?)kx.Root?.Element("string") ?? "").Trim();
				}
				else if (item.Name == "value")
				{
					using var vsub = item.ReadSubtree();
					var vx = System.Xml.Linq.XDocument.Load(vsub);
					var mapSys = vx.Root?.Element("MapSystem") ?? vx.Root;
					if (mapSys is null) continue;
					var lay = mapSys.Element("Layout");
					if (lay is not null)
					{
						float.TryParse((string?)lay.Element("X"), System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out lx);
						float.TryParse((string?)lay.Element("Y"), System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out ly);
					}
					string oor = ((string?)mapSys.Element("OutOfRegion") ?? "").Trim();
					outOfRegion = oor.Equals("true", StringComparison.OrdinalIgnoreCase);
				}
			}

			if (string.IsNullOrEmpty(key))
				continue;
			systems[key] = new MapSystemLayout { Name = key, X = lx, Y = ly };
			if (!outOfRegion)
				inRegionNames.Add(key);
		}
	}

	public static string DefaultPath() => ModDataPaths.Resolve("MapLayout.dat");
}
