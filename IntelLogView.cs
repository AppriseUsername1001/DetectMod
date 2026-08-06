using System.Runtime.InteropServices;
using EVEAA.Mod.Intel;

namespace EVEAA.Mod;

/// <summary>
/// 인텔 로그 뷰. AutoScroll 없이 VScrollBar + 오프셋 페인트로 스크롤 깨짐을 방지.
/// </summary>
internal sealed class IntelLogView : Control
{
	private static readonly Color ColTime = Color.FromArgb(110, 110, 110);
	private static readonly Color ColSystem = Color.FromArgb(0, 110, 200);
	private static readonly Color ColCharacter = Color.FromArgb(190, 95, 20);
	private static readonly Color ColShip = Color.FromArgb(130, 55, 170);
	private static readonly Color ColSep = Color.FromArgb(140, 140, 140);
	private static readonly Color ColMsg = Color.FromArgb(40, 40, 40);
	private static readonly Color ColAlert = Color.FromArgb(180, 40, 40);
	private static readonly Color ColClear = Color.FromArgb(0, 140, 70);

	private const int MaxItems = 400;

	// 96 DPI 기준 여백 — 실제 모니터 배율에 맞춰 스케일링 (고배율 화면에서 줄간격이
	// 상대적으로 너무 좁아 보이는 것 방지). Font는 GDI에서 이미 DPI에 맞게 렌더링되므로 손대지 않는다.
	private int PadX => (int)Math.Round(4 * DeviceDpi / 96.0);
	private int PadY => (int)Math.Round(2 * DeviceDpi / 96.0);
	private int LineGap => (int)Math.Round(2 * DeviceDpi / 96.0);
	private const int WM_MOUSEWHEEL = 0x020A;
	private const int WM_ERASEBKGND = 0x0014;

	private readonly List<IntelThreatEvent> _items = new();
	private readonly List<int> _heights = new();
	private readonly VScrollBar _scroll;
	private int _contentWidth = -1;
	private int _scrollY;
	private int _clickIndex = -1;
	private Point _clickPoint;
	private bool _updatingScroll;

	public IntelLogView()
	{
		SetStyle(ControlStyles.AllPaintingInWmPaint |
		         ControlStyles.UserPaint |
		         ControlStyles.OptimizedDoubleBuffer |
		         ControlStyles.ResizeRedraw |
		         ControlStyles.Selectable |
		         ControlStyles.Opaque, true);
		DoubleBuffered = true;
		BackColor = Color.White;
		TabStop = true;
		Font = new Font("Segoe UI", 9.5f);

		_scroll = new VScrollBar
		{
			Dock = DockStyle.Right,
			SmallChange = 24,
			LargeChange = 120,
			Visible = false
		};
		_scroll.Scroll += (_, e) =>
		{
			if (_updatingScroll) return;
			_scrollY = e.NewValue;
			Invalidate();
		};
		_scroll.ValueChanged += (_, _) =>
		{
			if (_updatingScroll) return;
			_scrollY = _scroll.Value;
			Invalidate();
		};
		Controls.Add(_scroll);
	}

	public int Count => _items.Count;
	public IntelThreatEvent? this[int index] =>
		index >= 0 && index < _items.Count ? _items[index] : null;
	public int LastClickIndex => _clickIndex;
	public Point LastClickPoint => _clickPoint;

	public void InsertTop(IntelThreatEvent ev)
	{
		_items.Insert(0, ev);
		_heights.Insert(0, 0);
		while (_items.Count > MaxItems)
		{
			_items.RemoveAt(_items.Count - 1);
			_heights.RemoveAt(_heights.Count - 1);
		}
		_contentWidth = -1;
		RecalcHeights();
		_scrollY = 0;
		UpdateScrollBar();
		Invalidate();
		Update();
	}

	public void ForceRepaint()
	{
		_contentWidth = -1;
		if (_items.Count > 0)
			RecalcHeights();
		_scrollY = 0;
		UpdateScrollBar();
		Invalidate();
		Update();
	}

	public int IndexFromPoint(Point pt)
	{
		int y = pt.Y + _scrollY;
		int acc = 0;
		for (int i = 0; i < _items.Count; i++)
		{
			int h = HeightAt(i);
			if (y >= acc && y < acc + h) return i;
			acc += h;
		}
		return -1;
	}

	public Rectangle GetItemRectangle(int index)
	{
		if (index < 0 || index >= _items.Count) return Rectangle.Empty;
		int top = 0;
		for (int i = 0; i < index; i++) top += HeightAt(i);
		top -= _scrollY;
		int w = Math.Max(1, ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0));
		return new Rectangle(0, top, w, HeightAt(index));
	}

	public bool HitTestIsCharacter(int index, Point clientPt)
	{
		if (index < 0 || index >= _items.Count) return false;
		var ev = _items[index];
		var itemRect = GetItemRectangle(index);
		if (itemRect.IsEmpty) return false;

		using var g = CreateGraphics();
		int maxW = ContentWidth();
		var wrapped = Wrap(g, Font, BuildParts(ev), maxW);
		int lineH = Font.Height + LineGap;
		int y = itemRect.Top + PadY;
		var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
		foreach (var line in wrapped)
		{
			int x = itemRect.Left + PadX;
			foreach (var (text, color) in line)
			{
				var sz = TextRenderer.MeasureText(g, text, Font, new Size(int.MaxValue, lineH), flags);
				var r = new Rectangle(x, y, sz.Width, lineH);
				if (r.Contains(clientPt) && color.ToArgb() == ColCharacter.ToArgb())
					return true;
				x += sz.Width;
			}
			y += lineH;
		}
		return false;
	}

	protected override void OnPaintBackground(PaintEventArgs pevent)
	{
		// Opaque + 직접 배경 칠하기 — 스크롤 잔상/부모 비침 방지
		pevent.Graphics.FillRectangle(Brushes.White, ClientRectangle);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.FillRectangle(Brushes.White, ClientRectangle);
		if (_items.Count == 0) return;

		int maxW = ContentWidth();
		if (maxW != _contentWidth)
		{
			_contentWidth = maxW;
			RecalcHeights(g);
			UpdateScrollBar();
		}

		int viewH = ClientSize.Height;
		int y = -_scrollY;
		var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
		int lineH = Font.Height + LineGap;

		for (int i = 0; i < _items.Count; i++)
		{
			int h = HeightAt(i);
			if (y + h < 0) { y += h; continue; }
			if (y > viewH) break;

			var wrapped = Wrap(g, Font, BuildParts(_items[i]), maxW);
			int drawY = y + PadY;
			foreach (var line in wrapped)
			{
				int x = PadX;
				foreach (var (text, color) in line)
				{
					var sz = TextRenderer.MeasureText(g, text, Font, new Size(int.MaxValue, lineH), flags);
					TextRenderer.DrawText(g, text, Font, new Rectangle(x, drawY, sz.Width, lineH), color, flags);
					x += sz.Width;
				}
				drawY += lineH;
			}
			y += h;
		}
	}

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		_contentWidth = -1;
		RecalcHeights();
		UpdateScrollBar();
		Invalidate();
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		Focus();
		_clickIndex = IndexFromPoint(e.Location);
		_clickPoint = e.Location;
	}

	protected override void WndProc(ref Message m)
	{
		if (m.Msg == WM_ERASEBKGND)
		{
			m.Result = (IntPtr)1;
			return;
		}
		if (m.Msg == WM_MOUSEWHEEL)
		{
			if (!Focused) Focus();
			int delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
			int step = Math.Max(24, (Font.Height + LineGap) * 3);
			ScrollBy(-Math.Sign(delta) * step * Math.Max(1, Math.Abs(delta) / 120));
			m.Result = IntPtr.Zero;
			return;
		}
		base.WndProc(ref m);
	}

	protected override void OnDoubleClick(EventArgs e)
	{
		base.OnDoubleClick(e);
		ItemDoubleClicked?.Invoke(_clickIndex, _clickPoint);
	}

	protected override bool IsInputKey(Keys keyData) =>
		keyData is Keys.Enter or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown
		|| base.IsInputKey(keyData);

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.KeyCode == Keys.Enter)
			ItemDoubleClicked?.Invoke(_clickIndex, _clickPoint);
		else if (e.KeyCode == Keys.Down) ScrollBy(Font.Height + LineGap);
		else if (e.KeyCode == Keys.Up) ScrollBy(-(Font.Height + LineGap));
		else if (e.KeyCode == Keys.PageDown) ScrollBy(Math.Max(40, ClientSize.Height * 9 / 10));
		else if (e.KeyCode == Keys.PageUp) ScrollBy(-Math.Max(40, ClientSize.Height * 9 / 10));
	}

	public event Action<int, Point>? ItemDoubleClicked;

	private void ScrollBy(int dy)
	{
		int total = TotalHeight();
		int view = Math.Max(1, ClientSize.Height);
		int max = Math.Max(0, total - view);
		int next = Math.Max(0, Math.Min(max, _scrollY + dy));
		if (next == _scrollY) return;
		_scrollY = next;
		_updatingScroll = true;
		try
		{
			if (_scroll.Visible && _scroll.Enabled)
				_scroll.Value = Math.Min(_scroll.Maximum, Math.Max(_scroll.Minimum, _scrollY));
		}
		finally { _updatingScroll = false; }
		Invalidate();
		Update();
	}

	private int ContentWidth()
	{
		int sb = _scroll.Visible ? _scroll.Width : 0;
		return Math.Max(40, ClientSize.Width - sb - PadX * 2);
	}

	private int HeightAt(int i)
	{
		if (i < 0 || i >= _heights.Count) return 22;
		int h = _heights[i];
		return h > 0 ? h : 22;
	}

	private int TotalHeight()
	{
		int total = 0;
		for (int i = 0; i < _heights.Count; i++) total += HeightAt(i);
		return total;
	}

	private void RecalcHeights(Graphics? g = null)
	{
		bool own = g is null;
		if (own) g = CreateGraphics();
		try
		{
			int maxW = ContentWidth();
			_contentWidth = maxW;
			int lineH = Font.Height + LineGap;
			for (int i = 0; i < _items.Count; i++)
			{
				var wrapped = Wrap(g!, Font, BuildParts(_items[i]), maxW);
				_heights[i] = Math.Max(22, PadY * 2 + wrapped.Count * lineH);
			}
		}
		finally
		{
			if (own) g!.Dispose();
		}
	}

	private void UpdateScrollBar()
	{
		int total = TotalHeight();
		int view = Math.Max(1, ClientSize.Height);
		_updatingScroll = true;
		try
		{
			if (total <= view)
			{
				_scroll.Visible = false;
				_scroll.Enabled = false;
				_scrollY = 0;
				_scroll.Minimum = 0;
				_scroll.Maximum = 0;
				_scroll.Value = 0;
				return;
			}

			_scroll.Visible = true;
			_scroll.Enabled = true;
			_scroll.Minimum = 0;
			_scroll.LargeChange = Math.Max(1, view);
			// usable max = Maximum - LargeChange + 1 == total - view
			_scroll.Maximum = Math.Max(0, total - 1);
			int maxVal = Math.Max(0, total - view);
			if (_scrollY > maxVal) _scrollY = maxVal;
			_scroll.Value = Math.Min(maxVal, Math.Max(0, _scrollY));
			_scrollY = _scroll.Value;
		}
		finally { _updatingScroll = false; }
	}

	internal static List<(string text, Color color)> BuildParts(IntelThreatEvent ev)
	{
		var parts = new List<(string, Color)>();
		string time = string.IsNullOrEmpty(ev.TimeText) ? DateTime.Now.ToString("HH:mm:ss") : ev.TimeText;
		parts.Add(($"[{time}] ", ColTime));
		string jump = ev.JumpSuffix();
		if (!string.IsNullOrEmpty(jump))
			parts.Add((jump + "  ", ColAlert));

		if (ev.IsClear)
		{
			parts.Add((string.IsNullOrEmpty(ev.System) ? "-" : ev.System, ColSystem));
			parts.Add(("  클리어", ColClear));
			return parts;
		}
		if (ev.IsKillReport)
		{
			parts.Add(("Kill", ColAlert));
			parts.Add((" / ", ColSep));
			parts.Add((string.IsNullOrEmpty(ev.Character) ? "-" : ev.Character, ColCharacter));
			parts.Add((" / ", ColSep));
			parts.Add((string.IsNullOrEmpty(ev.Ship) ? "-" : ev.Ship, ColShip));
			return parts;
		}
		bool hasStruct = !string.IsNullOrEmpty(ev.System) || !string.IsNullOrEmpty(ev.Character) || !string.IsNullOrEmpty(ev.Ship);
		if (hasStruct)
		{
			parts.Add((string.IsNullOrEmpty(ev.System) ? "-" : ev.System, ColSystem));
			parts.Add((" / ", ColSep));
			parts.Add((string.IsNullOrEmpty(ev.Character) ? "-" : ev.Character, ColCharacter));
			parts.Add((" / ", ColSep));
			parts.Add((string.IsNullOrEmpty(ev.Ship) ? "-" : ev.Ship, ColShip));
			return parts;
		}
		string msg = string.IsNullOrEmpty(ev.Message) ? ev.Raw : ev.Message;
		parts.Add((msg, ColMsg));
		return parts;
	}

	private static List<List<(string text, Color color)>> Wrap(
		Graphics g, Font font, List<(string text, Color color)> parts, int maxWidth)
	{
		var lines = new List<List<(string, Color)>>();
		var cur = new List<(string, Color)>();
		int x = 0;
		var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

		void NewLine()
		{
			if (cur.Count > 0) lines.Add(cur);
			cur = new List<(string, Color)>();
			x = 0;
		}

		foreach (var (text, color) in parts)
		{
			if (string.IsNullOrEmpty(text)) continue;
			int i = 0;
			while (i < text.Length)
			{
				int nextBreak = text.IndexOf(' ', i);
				string chunk;
				if (nextBreak < 0) { chunk = text[i..]; i = text.Length; }
				else { chunk = text[i..(nextBreak + 1)]; i = nextBreak + 1; }

				int w = TextRenderer.MeasureText(g, chunk, font, new Size(int.MaxValue, 20), flags).Width;
				if (x > 0 && x + w > maxWidth) NewLine();

				if (w > maxWidth && chunk.Length > 1)
				{
					string remain = chunk;
					while (remain.Length > 0)
					{
						int take = remain.Length;
						while (take > 1 &&
						       TextRenderer.MeasureText(g, remain[..take], font, new Size(int.MaxValue, 20), flags).Width > maxWidth)
							take--;
						string piece = remain[..take];
						remain = remain[take..];
						if (x > 0 && TextRenderer.MeasureText(g, piece, font, new Size(int.MaxValue, 20), flags).Width + x > maxWidth)
							NewLine();
						int pw = TextRenderer.MeasureText(g, piece, font, new Size(int.MaxValue, 20), flags).Width;
						cur.Add((piece, color));
						x += pw;
						if (remain.Length > 0) NewLine();
					}
				}
				else
				{
					cur.Add((chunk, color));
					x += w;
				}
			}
		}
		if (cur.Count > 0) lines.Add(cur);
		if (lines.Count == 0) lines.Add(new List<(string, Color)> { ("", ColMsg) });
		return lines;
	}
}