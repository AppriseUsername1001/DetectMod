using System.Collections.Concurrent;

namespace EVEAA.Mod.Intel;

/// <summary>
/// RIFT-style: bidirectional BFS on gate graph + distance cache.
/// Systems.dat Jumps adjacency is fixed as Int ID arrays (no per-hop string hashing).
/// </summary>
internal sealed class JumpNav
{
	private readonly Dictionary<string, int> _nameToId = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<int, int[]> _adj = new();
	private readonly ConcurrentDictionary<long, int> _distCache = new();

	public bool Ready => _adj.Count > 0;

	public void Rebuild(SystemsDatabase systems)
	{
		_nameToId.Clear();
		_adj.Clear();
		_distCache.Clear();

		if (!systems.Loaded) return;

		foreach (string name in systems.AllNames())
		{
			var info = systems.Get(name);
			if (info is null || info.SystemId <= 0) continue;
			_nameToId[info.Name] = info.SystemId;
		}

		foreach (string name in systems.AllNames())
		{
			var info = systems.Get(name);
			if (info is null || info.SystemId <= 0) continue;
			var neigh = new List<int>(info.Jumps.Count);
			foreach (string j in info.Jumps)
			{
				if (_nameToId.TryGetValue(j, out int nid))
					neigh.Add(nid);
			}
			_adj[info.SystemId] = neigh.Count == 0 ? Array.Empty<int>() : neigh.ToArray();
		}
	}

	public int? Distance(string start, string target)
	{
		start = (start ?? "").Trim();
		target = (target ?? "").Trim();
		if (start.Length == 0 || target.Length == 0) return null;
		if (string.Equals(start, target, StringComparison.OrdinalIgnoreCase)) return 0;
		if (!_nameToId.TryGetValue(start, out int a) || !_nameToId.TryGetValue(target, out int b))
			return null;

		long key = Pack(a, b);
		if (_distCache.TryGetValue(key, out int cached))
			return cached < 0 ? null : cached;

		int? dist = BidirectionalBfs(a, b);
		_distCache[key] = dist ?? -1;
		_distCache[Pack(b, a)] = dist ?? -1;
		return dist;
	}

	/// <summary>Legacy one-sided BFS (kept for callers that pass a Func).</summary>
	public static int? MinJumpDistance(string start, string target, Func<string, IEnumerable<string>> jumpsOf)
	{
		start = (start ?? "").Trim();
		target = (target ?? "").Trim();
		if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(target))
			return null;
		if (string.Equals(start, target, StringComparison.OrdinalIgnoreCase))
			return 0;

		var q = new Queue<(string sys, int depth)>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
		q.Enqueue((start, 0));
		while (q.Count > 0)
		{
			var (sys, depth) = q.Dequeue();
			foreach (string nxt in jumpsOf(sys))
			{
				if (string.Equals(nxt, target, StringComparison.OrdinalIgnoreCase))
					return depth + 1;
				if (seen.Add(nxt))
					q.Enqueue((nxt, depth + 1));
			}
		}
		return null;
	}

	private int? BidirectionalBfs(int start, int goal)
	{
		if (!_adj.ContainsKey(start) || !_adj.ContainsKey(goal))
			return null;
		if (start == goal) return 0;

		var qA = new Queue<int>();
		var qB = new Queue<int>();
		var distA = new Dictionary<int, int> { [start] = 0 };
		var distB = new Dictionary<int, int> { [goal] = 0 };
		qA.Enqueue(start);
		qB.Enqueue(goal);

		while (qA.Count > 0 && qB.Count > 0)
		{
			int? hit = qA.Count <= qB.Count
				? Expand(qA, distA, distB)
				: Expand(qB, distB, distA);
			if (hit is not null)
				return hit;
		}
		return null;
	}

	private int? Expand(Queue<int> frontier, Dictionary<int, int> mine, Dictionary<int, int> other)
	{
		int levelCount = frontier.Count;
		for (int n = 0; n < levelCount; n++)
		{
			int cur = frontier.Dequeue();
			int d = mine[cur];
			if (!_adj.TryGetValue(cur, out int[]? neigh) || neigh is null)
				continue;
			foreach (int nxt in neigh)
			{
				if (mine.ContainsKey(nxt))
					continue;
				int nd = d + 1;
				mine[nxt] = nd;
				if (other.TryGetValue(nxt, out int od))
					return nd + od;
				frontier.Enqueue(nxt);
			}
		}
		return null;
	}

	private static long Pack(int a, int b) => ((long)(uint)a << 32) | (uint)b;
}
