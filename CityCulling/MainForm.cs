using Evergine.Forms;
using System.Drawing;
using System.Windows.Forms;

namespace CityCulling
{
	/// <summary>
	/// A plain Windows Forms window with an <see cref="EvergineControl"/> to render into and a
	/// status bar showing what the culling did this frame.
	/// </summary>
	internal sealed class MainForm : Form
	{
		private readonly ToolStripStatusLabel cullingLabel;
		private readonly ToolStripStatusLabel sceneLabel;

		public MainForm(int width, int height)
		{
			this.Text = "CityCulling - Embree occlusion culling on the Evergine low-level API";
			this.StartPosition = FormStartPosition.CenterScreen;
			this.ClientSize = new Size(width, height + 60);
			this.MinimumSize = new Size(640, 400);

			this.RenderControl = new EvergineControl { Dock = DockStyle.Fill };

			this.ShowCulledButton = new ToolStripButton("Show discarded in red")
			{
				CheckOnClick = true,
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			this.PauseButton = new ToolStripButton("Pause camera")
			{
				CheckOnClick = true,
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			this.CaptureButton = new ToolStripButton("Save captures")
			{
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			var toolStrip = new ToolStrip
			{
				GripStyle = ToolStripGripStyle.Hidden,
				RenderMode = ToolStripRenderMode.System,
			};

			toolStrip.Items.Add(this.PauseButton);
			toolStrip.Items.Add(new ToolStripSeparator());
			toolStrip.Items.Add(this.ShowCulledButton);
			toolStrip.Items.Add(new ToolStripSeparator());
			toolStrip.Items.Add(this.CaptureButton);

			this.cullingLabel = new ToolStripStatusLabel(string.Empty)
			{
				Spring = true,
				TextAlign = ContentAlignment.MiddleLeft,
			};

			this.sceneLabel = new ToolStripStatusLabel(string.Empty);

			var statusStrip = new StatusStrip();
			statusStrip.Items.Add(this.cullingLabel);
			statusStrip.Items.Add(this.sceneLabel);

			this.Controls.Add(this.RenderControl);
			this.Controls.Add(toolStrip);
			this.Controls.Add(statusStrip);
		}

		public EvergineControl RenderControl { get; }

		public ToolStripButton ShowCulledButton { get; }

		public ToolStripButton PauseButton { get; }

		public ToolStripButton CaptureButton { get; }

		public void SetSceneInfo(int objects, int triangles) =>
			this.sceneLabel.Text = $"{objects:N0} objects   |   {triangles:N0} triangles";

		public void SetFrameInfo(int drawn, int total, double cullMs, double frameMs)
		{
			double culled = 100.0 * (total - drawn) / total;
			this.cullingLabel.Text =
				$"draw calls {drawn,5:N0} / {total,5:N0}   |   culled {culled,5:F1}%   |   culling {cullMs,5:F2} ms   |   frame {frameMs,5:F2} ms";
		}
	}
}
