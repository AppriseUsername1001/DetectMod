using System.Reflection;
using System.Runtime.InteropServices;

namespace EVEAA.Mod;

/// <summary>
/// OwnerDraw ListBox: fill empty client with BackColor; no selection flash.
/// </summary>
internal sealed class FlickerFreeListBox : ListBox
{
	private const int WM_ERASEBKGND = 0x0014;
	private const int WM_SETREDRAW = 0x000B;

	public FlickerFreeListBox()
	{
		BackColor = Color.White;
		ForeColor = Color.Black;
		SelectionMode = SelectionMode.None;
		typeof(Control)
			.GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic)
			?.Invoke(this, new object[]
			{
				ControlStyles.OptimizedDoubleBuffer |
				ControlStyles.AllPaintingInWmPaint |
				ControlStyles.ResizeRedraw,
				true
			});
	}

	protected override void WndProc(ref Message m)
	{
		if (m.Msg == WM_ERASEBKGND)
		{
			IntPtr hdc = m.WParam;
			if (hdc != IntPtr.Zero)
			{
				try
				{
					using var g = Graphics.FromHdc(hdc);
					using var brush = new SolidBrush(BackColor);
					g.FillRectangle(brush, ClientRectangle);
				}
				catch { }
			}
			m.Result = (IntPtr)1;
			return;
		}
		base.WndProc(ref m);
	}

	public void BeginSilentUpdate()
	{
		if (IsHandleCreated)
			SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
	}

	public void EndSilentUpdate()
	{
		if (!IsHandleCreated) return;
		SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
		Invalidate(false);
		Update();
	}

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}