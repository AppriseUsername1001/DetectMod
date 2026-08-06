using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace EVEAA.Mod.Intel;

internal enum MapMarkerKind
{
	Zkb,
	IntelFresh,
	IntelStale
}

internal sealed class MapMarker
{
	public string System { get; init; } = "";
	public MapMarkerKind Kind { get; set; }
	public DateTime ExpiresUtc { get; set; }
	public DateTime FreshUntilUtc { get; set; }
	/// <summary>이 마커를 만든 인텔/ZKB 이벤트 요약. 최신이 앞. 호버 툴팁에 표시.</summary>
	public List<string> Reasons { get; } = new();
}

/// <summary>리전 성계 그래프. 오버레이(다크) / 임베드(라이트) 공용.</summary>
internal sealed class RegionMapControl : Control
{
	private RegionMap? _region;
	private SystemsDatabase? _systems;
	private string _currentSystem = "";
	private readonly List<MapMarker> _markers = new();
	private bool _darkTheme = true;
	private string _hoverSystem = "";
	private Point _hoverClient = Point.Empty;
	private const float HitRadiusPx = 14f;

	public RegionMapControl()
	{
		DoubleBuffered = true;
		BackColor = Color.FromArgb(28, 28, 30);
		ResizeRedraw = true;
		Cursor = Cursors.Default;
	}

	[Browsable(false)]
	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public bool DarkTheme
	{
		get => _darkTheme;
		set
		{
			if (_darkTheme == value) return;
			_darkTheme = value;
			BackColor = value ? Color.FromArgb(28, 28, 30) : Color.White;
			Invalidate();
		}
	}

	public string CurrentSystem => _currentSystem;
	public string RegionTitle => _region?.Name.Replace('_', ' ') ?? "";

	public void SetRegion(RegionMap? region, SystemsDatabase systems, string currentSystem)
	{
		bool sameRegion = ReferenceEquals(_region, region)
			|| (_region is not null && region is not null
				&& _region.Name == region.Name && _region.Systems.Count == region.Systems.Count);
		bool sameSys = string.Equals(_currentSystem, currentSystem ?? "", StringComparison.OrdinalIgnoreCase);
		_region = region;
		_systems = systems;
		_currentSystem = currentSystem ?? "";
		if (!(sameRegion && sameSys))
			Invalidate();
	}

	public void SetCurrentSystem(string system)
	{
		if (string.Equals(_currentSystem, system ?? "", StringComparison.OrdinalIgnoreCase))
			return;
		_currentSystem = system ?? "";
		Invalidate();
	}

	private const int MaxReasonsPerMarker = 6;

	public void UpsertMarker(string system, MapMarkerKind kind, TimeSpan display, TimeSpan? fresh = null, string? reason = null)
	{
		if (string.IsNullOrWhiteSpace(system)) return;
		system = system.Trim();
		DateTime now = DateTime.UtcNow;
		var existing = _markers.Find(m => string.Equals(m.System, system, StringComparison.OrdinalIgnoreCase)
			&& (m.Kind == kind
				|| (kind != MapMarkerKind.Zkb && m.Kind != MapMarkerKind.Zkb)));
		if (existing is not null)
		{
			existing.Kind = kind;
			existing.ExpiresUtc = now + display;
			if (kind == MapMarkerKind.IntelFresh || kind == MapMarkerKind.IntelStale)
				existing.FreshUntilUtc = now + (fresh ?? TimeSpan.Zero);
			AddReason(existing, reason);
			Invalidate();
			return;
		}
		var marker = new MapMarker
		{
			System = system,
			Kind = kind,
			ExpiresUtc = now + display,
			FreshUntilUtc = kind == MapMarkerKind.Zkb ? DateTime.MinValue : now + (fresh ?? TimeSpan.Zero)
		};
		AddReason(marker, reason);
		_markers.Add(marker);
		Invalidate();
	}

	private static void AddReason(MapMarker marker, string? reason)
	{
		if (string.IsNullOrWhiteSpace(reason)) return;
		marker.Reasons.Insert(0, reason);
		while (marker.Reasons.Count > MaxReasonsPerMarker)
			marker.Reasons.RemoveAt(marker.Reasons.Count - 1);
	}

	/// <summary>호버 중인 성계의 인텔/ZKB 마커들에서 최신 사유를 모아 반환. 마커가 없으면 빈 리스트.</summary>
	private List<string> GetReasonsForHover(string system)
	{
		var result = new List<string>();
		if (string.IsNullOrEmpty(system)) return result;
		foreach (var m in _markers)
		{
			if (!string.Equals(m.System, system, StringComparison.OrdinalIgnoreCase)) continue;
			if (m.ExpiresUtc <= DateTime.UtcNow) continue;
			result.AddRange(m.Reasons);
		}
		return result;
	}

	/// <summary>만료 마커 제거. 변경 있으면 true.</summary>
	public bool PruneExpired()
	{
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		for (int i = _markers.Count - 1; i >= 0; i--)
		{
			var m = _markers[i];
			if (m.ExpiresUtc <= now)
			{
				_markers.RemoveAt(i);
				changed = true;
				continue;
			}
			if (m.Kind == MapMarkerKind.IntelFresh && m.FreshUntilUtc <= now)
			{
				m.Kind = MapMarkerKind.IntelStale;
				changed = true;
			}
		}
		if (changed) Invalidate();
		return changed;
	}

	public void ClearMarkers()
	{
		if (_markers.Count == 0) return;
		_markers.Clear();
		Invalidate();
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		string hit = HitTestSystem(e.Location);
		bool changed = !string.Equals(hit, _hoverSystem, StringComparison.OrdinalIgnoreCase)
			|| Math.Abs(e.X - _hoverClient.X) > 1
			|| Math.Abs(e.Y - _hoverClient.Y) > 1;
		_hoverSystem = hit;
		_hoverClient = e.Location;
		Cursor = string.IsNullOrEmpty(hit) ? Cursors.Default : Cursors.Hand;
		if (changed) Invalidate();
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		base.OnMouseLeave(e);
		if (string.IsNullOrEmpty(_hoverSystem) && _hoverClient.IsEmpty) return;
		_hoverSystem = "";
		_hoverClient = Point.Empty;
		Cursor = Cursors.Default;
		Invalidate();
	}

	/// <summary>커서 근처 성계 이름. 없으면 빈 문자열.</summary>
	private string HitTestSystem(Point client)
	{
		if (_region is null || _region.Systems.Count == 0) return "";
		if (!TryGetProjection(out float minX, out float minY, out float scale, out float offX, out float offY))
			return "";

		string best = "";
		float bestDist = HitRadiusPx * HitRadiusPx;
		foreach (var sys in _region.Systems.Values)
		{
			float x = offX + (sys.X - minX) * scale;
			float y = offY + (sys.Y - minY) * scale;
			float dx = client.X - x;
			float dy = client.Y - y;
			float d2 = dx * dx + dy * dy;
			if (d2 <= bestDist)
			{
				bestDist = d2;
				best = sys.Name;
			}
		}
		return best;
	}

	private bool TryGetProjection(out float minX, out float minY, out float scale, out float offX, out float offY)
	{
		minX = minY = scale = offX = offY = 0;
		if (_region is null || _region.Systems.Count == 0) return false;
		float maxX = _region.Systems.Values.Max(s => s.X);
		float maxY = _region.Systems.Values.Max(s => s.Y);
		minX = _region.Systems.Values.Min(s => s.X);
		minY = _region.Systems.Values.Min(s => s.Y);
		const float pad = 22;
		float w = Math.Max(1, maxX - minX);
		float h = Math.Max(1, maxY - minY);
		scale = Math.Min((ClientSize.Width - pad * 2) / w, (ClientSize.Height - pad * 2) / h);
		float drawW = w * scale;
		float drawH = h * scale;
		offX = pad + Math.Max(0, (ClientSize.Width - pad * 2 - drawW) / 2f);
		offY = pad + Math.Max(0, (ClientSize.Height - pad * 2 - drawH) / 2f);
		return scale > 0;
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(BackColor);

		Color lineColor = _darkTheme ? Color.FromArgb(90, 90, 95) : Color.Black;
		Color nodeFill = _darkTheme ? Color.FromArgb(55, 55, 60) : Color.White;
		Color nodeBorder = _darkTheme ? Color.FromArgb(140, 140, 145) : Color.Black;
		Color emptyText = _darkTheme ? Color.FromArgb(140, 140, 140) : Color.Gray;

		if (_region is null || _region.Systems.Count == 0)
		{
			using var f = new Font("Segoe UI", 9f);
			string msg = string.IsNullOrEmpty(_currentSystem)
				? "지도 없음 (캐릭터 위치 대기)"
				: $"지도 없음\n{_currentSystem}\n(MapLayout.dat 로드 확인)";
			TextRenderer.DrawText(g, msg, f, ClientRectangle, emptyText,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			return;
		}

		if (!TryGetProjection(out float minX, out float minY, out float scale, out float offX, out float offY))
			return;

		PointF Map(MapSystemLayout s)
		{
			float x = offX + (s.X - minX) * scale;
			float y = offY + (s.Y - minY) * scale;
			return new PointF(x, y);
		}

		using var linePen = new Pen(lineColor, 1f);
		var drawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (_systems is not null)
		{
			foreach (var sys in _region.Systems.Values)
			{
				foreach (string nxt in _systems.JumpsOf(sys.Name))
				{
					if (!_region.Systems.TryGetValue(nxt, out var other)) continue;
					string edge = string.Compare(sys.Name, nxt, StringComparison.OrdinalIgnoreCase) < 0
						? sys.Name + "|" + nxt : nxt + "|" + sys.Name;
					if (!drawn.Add(edge)) continue;
					g.DrawLine(linePen, Map(sys), Map(other));
				}
			}
		}

		DateTime now = DateTime.UtcNow;
		var bySys = new Dictionary<string, MapMarker>(StringComparer.OrdinalIgnoreCase);
		foreach (var m in _markers)
		{
			if (m.ExpiresUtc <= now) continue;
			if (!bySys.TryGetValue(m.System, out var prev))
			{
				bySys[m.System] = m;
				continue;
			}
			// Fresh > Zkb > Stale 우선
			int Rank(MapMarker x) => x.Kind switch
			{
				MapMarkerKind.IntelFresh => 3,
				MapMarkerKind.Zkb => 2,
				_ => 1
			};
			if (Rank(m) >= Rank(prev))
				bySys[m.System] = m;
		}

		const float r = 4.5f;
		using var nodeBrush = new SolidBrush(nodeFill);
		using var nodePen = new Pen(nodeBorder, 1.2f);

		foreach (var sys in _region.Systems.Values)
		{
			var p = Map(sys);
			bool current = string.Equals(sys.Name, _currentSystem, StringComparison.OrdinalIgnoreCase);
			bool hover = string.Equals(sys.Name, _hoverSystem, StringComparison.OrdinalIgnoreCase);
			bySys.TryGetValue(sys.Name, out var marker);

			if (current)
			{
				// 캐릭터 위치: 큰 주황/노란 글로우 원
				using var glow = new SolidBrush(Color.FromArgb(70, 255, 170, 40));
				using var ring = new Pen(Color.FromArgb(230, 200, 80), 2.5f);
				using var core = new SolidBrush(Color.FromArgb(255, 180, 40));
				g.FillEllipse(glow, p.X - 16, p.Y - 16, 32, 32);
				g.DrawEllipse(ring, p.X - 11, p.Y - 11, 22, 22);
				g.FillEllipse(core, p.X - r, p.Y - r, r * 2, r * 2);
			}
			else if (marker is not null)
			{
				DrawMarker(g, p, marker);
			}
			else
			{
				g.FillEllipse(nodeBrush, p.X - r, p.Y - r, r * 2, r * 2);
				g.DrawEllipse(nodePen, p.X - r, p.Y - r, r * 2, r * 2);
			}

			if (hover && !current)
			{
				using var hi = new Pen(Color.FromArgb(220, 220, 230), 1.5f);
				g.DrawEllipse(hi, p.X - 8, p.Y - 8, 16, 16);
			}
		}

		if (!string.IsNullOrEmpty(_hoverSystem))
			DrawHoverLabel(g, _hoverSystem, _hoverClient);
	}

	/// <summary>성계 이름 + (마커가 있으면) 그 마커를 만든 인텔/ZKB 사유들을 커서 옆에 표시.</summary>
	private void DrawHoverLabel(Graphics g, string name, Point cursor)
	{
		List<string> reasons = GetReasonsForHover(name);
		using var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
		using var bodyFont = new Font("Segoe UI", 8f);
		const int maxBodyWidth = 300;
		const int padX = 8, padY = 6, lineGap = 2, headerGap = 4;

		Size headerSize = TextRenderer.MeasureText(g, name, headerFont);
		int boxW = headerSize.Width + padX * 2;
		int boxH = headerSize.Height + padY * 2;

		var bodySizes = new List<Size>(reasons.Count);
		if (reasons.Count > 0)
		{
			boxH += headerGap;
			foreach (string r in reasons)
			{
				Size sz = TextRenderer.MeasureText(g, r, bodyFont, new Size(maxBodyWidth, 0),
					TextFormatFlags.WordBreak | TextFormatFlags.Left);
				bodySizes.Add(sz);
				boxW = Math.Max(boxW, sz.Width + padX * 2);
				boxH += sz.Height + lineGap;
			}
		}
		boxW = Math.Min(boxW, maxBodyWidth + padX * 2);

		// 커서 오른쪽 아래; 화면 밖으로 나가면 반대편
		int x = cursor.X + 14;
		int y = cursor.Y + 16;
		if (x + boxW > ClientSize.Width - 2) x = cursor.X - boxW - 8;
		if (y + boxH > ClientSize.Height - 2) y = cursor.Y - boxH - 8;
		x = Math.Clamp(x, 2, Math.Max(2, ClientSize.Width - boxW - 2));
		y = Math.Clamp(y, 2, Math.Max(2, ClientSize.Height - boxH - 2));

		var box = new Rectangle(x, y, boxW, boxH);
		Color fill = _darkTheme ? Color.FromArgb(230, 20, 20, 24) : Color.FromArgb(240, 250, 250, 250);
		Color border = _darkTheme ? Color.FromArgb(200, 180, 180, 190) : Color.FromArgb(160, 80, 80, 80);
		Color headerColor = _darkTheme ? Color.FromArgb(245, 245, 245) : Color.FromArgb(20, 20, 20);
		Color bodyColor = _darkTheme ? Color.FromArgb(210, 210, 215) : Color.FromArgb(60, 60, 60);
		using (var br = new SolidBrush(fill))
			g.FillRectangle(br, box);
		using (var pen = new Pen(border, 1f))
			g.DrawRectangle(pen, box);

		var headerRect = new Rectangle(x + padX, y + padY, boxW - padX * 2, headerSize.Height);
		TextRenderer.DrawText(g, name, headerFont, headerRect, headerColor,
			TextFormatFlags.Left | TextFormatFlags.NoPadding);

		int cy = y + padY + headerSize.Height + headerGap;
		for (int i = 0; i < reasons.Count; i++)
		{
			var sz = bodySizes[i];
			var rect = new Rectangle(x + padX, cy, boxW - padX * 2, sz.Height);
			TextRenderer.DrawText(g, reasons[i], bodyFont, rect, bodyColor,
				TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			cy += sz.Height + lineGap;
		}
	}

	private static void DrawMarker(Graphics g, PointF p, MapMarker marker)
	{
		Color ring = marker.Kind switch
		{
			MapMarkerKind.IntelFresh => Color.FromArgb(255, 80, 80),
			MapMarkerKind.Zkb => Color.FromArgb(180, 90, 220),
			_ => Color.FromArgb(200, 120, 60)
		};
		Color fill = marker.Kind switch
		{
			MapMarkerKind.IntelFresh => Color.FromArgb(220, 50, 50),
			MapMarkerKind.Zkb => Color.FromArgb(140, 60, 200),
			_ => Color.FromArgb(180, 100, 40)
		};
		float outer = marker.Kind == MapMarkerKind.IntelFresh ? 12f : 9f;
		using var glow = new SolidBrush(Color.FromArgb(55, ring));
		using var pen = new Pen(ring, marker.Kind == MapMarkerKind.IntelFresh ? 2.4f : 1.8f);
		using var brush = new SolidBrush(fill);
		g.FillEllipse(glow, p.X - outer - 2, p.Y - outer - 2, (outer + 2) * 2, (outer + 2) * 2);
		g.DrawEllipse(pen, p.X - outer, p.Y - outer, outer * 2, outer * 2);
		g.FillEllipse(brush, p.X - 3.5f, p.Y - 3.5f, 7f, 7f);
	}
}
