using System.Runtime.InteropServices;

namespace EVEAA.Mod;

/// <summary>AutoScroll 패널 위에서 마우스 휠을 굴렸을 때, 커서가 Label/PictureBox/Button처럼
/// 휠 이벤트를 자체 처리하지 않는 자식 컨트롤 위에 있으면 WM_MOUSEWHEEL이 포커스를 가진 엉뚱한
/// 컨트롤로 가버려 패널이 스크롤되지 않는 WinForms 고질적 문제를 해결한다. 커서 아래 컨트롤부터
/// 부모 방향으로 올라가며 가장 가까운 AutoScroll 패널을 찾아 직접 스크롤시킨다. TrackBar/
/// NumericUpDown/ComboBox/ListBox/ListView처럼 휠로 자기 값을 바꾸는 컨트롤 위에서는 기존 동작을
/// 그대로 둔다.</summary>
internal sealed class ScrollWheelRouter : IMessageFilter
{
	private const int WM_MOUSEWHEEL = 0x020A;

	public bool PreFilterMessage(ref Message m)
	{
		if (m.Msg != WM_MOUSEWHEEL) return false;
		if (Control.FromChildHandle(m.HWnd) is not Control hovered) return false;
		if (hovered is TrackBar or NumericUpDown or ComboBox or ListBox or ListView or TextBox) return false;

		for (Control? c = hovered; c is not null; c = c.Parent)
		{
			if (c is Panel { AutoScroll: true } panel && panel.VerticalScroll.Visible)
			{
				int delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
				var pos = panel.AutoScrollPosition;
				int maxY = Math.Max(0, panel.VerticalScroll.Maximum - panel.VerticalScroll.LargeChange + 1);
				int newY = Math.Clamp(-pos.Y - delta, 0, maxY);
				panel.AutoScrollPosition = new Point(-pos.X, newY);
				return true;
			}
		}
		return false;
	}
}
