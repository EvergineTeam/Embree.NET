using Evergine.Forms;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace HelloEmbree
{
	/// <summary>
	/// Plain Windows Forms window hosting an <see cref="EvergineControl"/>. Evergine renders into
	/// that control's HWND, so the ray traced image lives inside a normal WinForms layout together
	/// with regular controls (a status bar and a couple of buttons here).
	/// </summary>
	internal sealed class MainForm : Form
	{
		private readonly ToolStripStatusLabel timingsLabel;
		private readonly ToolStripStatusLabel sceneLabel;

		public MainForm(int renderWidth, int renderHeight)
		{
			this.Text = "HelloEmbree - Embree.NET on the Evergine low-level API";
			this.StartPosition = FormStartPosition.CenterScreen;
			this.ClientSize = new Size(renderWidth, renderHeight + 60);
			this.MinimumSize = new Size(400, 300);
			this.DoubleBuffered = false;

			this.RenderControl = new EvergineControl
			{
				Dock = DockStyle.Fill,
			};

			var toolStrip = new ToolStrip
			{
				GripStyle = ToolStripGripStyle.Hidden,
				RenderMode = ToolStripRenderMode.System,
			};

			this.AnimateButton = new ToolStripButton("Pause camera")
			{
				CheckOnClick = true,
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			this.ScreenshotButton = new ToolStripButton("Save screenshot")
			{
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			toolStrip.Items.Add(this.AnimateButton);
			toolStrip.Items.Add(new ToolStripSeparator());
			toolStrip.Items.Add(this.ScreenshotButton);

			this.timingsLabel = new ToolStripStatusLabel("Tracing...")
			{
				Spring = true,
				TextAlign = ContentAlignment.MiddleLeft,
			};

			this.sceneLabel = new ToolStripStatusLabel(string.Empty);

			var statusStrip = new StatusStrip();
			statusStrip.Items.Add(this.timingsLabel);
			statusStrip.Items.Add(this.sceneLabel);

			// Fill last so the docked control gets the remaining area.
			this.Controls.Add(this.RenderControl);
			this.Controls.Add(toolStrip);
			this.Controls.Add(statusStrip);
		}

		/// <summary>
		/// Gets the control Evergine renders into.
		/// </summary>
		public EvergineControl RenderControl { get; }

		/// <summary>
		/// Gets the toggle that freezes the orbiting camera.
		/// </summary>
		public ToolStripButton AnimateButton { get; }

		/// <summary>
		/// Gets the button that writes a PNG of the current frame.
		/// </summary>
		public ToolStripButton ScreenshotButton { get; }

		public void SetSceneInfo(int triangleCount, int geometryCount, int renderWidth, int renderHeight)
		{
			this.sceneLabel.Text =
				$"{triangleCount:N0} triangles / {geometryCount} geometries   |   trace {renderWidth}x{renderHeight}";
		}

		public void SetTimings(double traceMs, double uploadMs, double gpuMs)
		{
			double total = traceMs + uploadMs + gpuMs;
			this.timingsLabel.Text =
				$"trace {traceMs,6:F2} ms   upload {uploadMs,5:F2} ms   gpu {gpuMs,5:F2} ms   |   {total,6:F2} ms ({1000.0 / total,5:F1} fps)";
		}
	}
}
