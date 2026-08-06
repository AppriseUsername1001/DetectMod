namespace EVEAA.Mod;

/// <summary>업데이트 다운로드 중 표시하는 작은 모덜리스 진행창.</summary>
internal sealed class UpdateProgressForm : Form
{
	public UpdateProgressForm()
	{
		Text = "EVEDetectmod 업데이트";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition = FormStartPosition.CenterScreen;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		ClientSize = new Size(320, 90);

		var bar = new ProgressBar
		{
			Dock = DockStyle.Bottom,
			Height = 20,
			Style = ProgressBarStyle.Marquee,
			MarqueeAnimationSpeed = 30
		};
		var label = new Label
		{
			Text = "새 버전을 다운로드하는 중입니다...",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter
		};
		Controls.Add(label);
		Controls.Add(bar);
	}
}
