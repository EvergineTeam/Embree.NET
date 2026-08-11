using Evergine.Common.Graphics;
using Evergine.DirectX11;
using Evergine.Forms;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HelloEmbree
{
	/// <summary>
	/// Renders an Embree CPU-raytraced scene through the Evergine low-level graphics API:
	/// the ray tracer writes an RGBA buffer, which is uploaded to a texture every frame and
	/// drawn to a DX11 swapchain with a fullscreen triangle. Around frame 10 the swapchain
	/// color target is copied to a staging texture and saved as screenshot.png.
	/// Pass --exit to close the app right after the screenshot (used for automation).
	/// </summary>
	internal static class Program
	{
		private const uint Width = 800;
		private const uint Height = 600;
		private const int ScreenshotFrame = 10;
		private const int BenchmarkWarmupFrames = 20;
		private const int BenchmarkFrames = 100;

		private const string ShaderSource = @"
Texture2D DiffuseTexture : register(t0);
SamplerState Sampler : register(s0);

struct PS_IN
{
	float4 pos : SV_POSITION;
	float2 tex : TEXCOORD;
};

PS_IN VS(uint id : SV_VertexID)
{
	PS_IN output = (PS_IN)0;
	output.tex = float2((id << 1) & 2, id & 2);
	output.pos = float4(output.tex * float2(2, -2) + float2(-1, 1), 0, 1);
	return output;
}

float4 PS(PS_IN input) : SV_Target
{
	return DiffuseTexture.Sample(Sampler, input.tex);
}
";

		private static GraphicsContext graphicsContext;
		private static SwapChain swapChain;
		private static CommandQueue commandQueue;
		private static GraphicsPipelineState pipelineState;
		private static ResourceSet resourceSet;
		private static Texture rayTexture;
		private static Viewport[] viewports;
		private static Evergine.Mathematics.Rectangle[] scissors;

		private static Raytracer raytracer;
		private static byte[] pixels;
		private static Stopwatch clock;
		private static int frameIndex;
		private static bool exitAfterScreenshot;
		private static string screenshotPath;
		private static bool benchmark;
		private static double[] traceMs;
		private static double[] uploadMs;
		private static double[] gpuMs;
		private static double sceneBuildMs;
		private static MainForm form;
		private static bool resizePending;
		private static bool screenshotRequested;
		private static float cameraTime;

		[STAThread]
		private static void Main(string[] args)
		{
			exitAfterScreenshot = args.Contains("--exit");
			benchmark = args.Contains("--bench");
			screenshotPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshot.png");
			screenshotPath = Path.GetFullPath(screenshotPath);

			System.Windows.Forms.Application.EnableVisualStyles();
			System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

			form = new MainForm((int)Width, (int)Height);
			form.ScreenshotButton.Click += (s, e) => screenshotRequested = true;

			// The HWND must exist before the swapchain is built, and it must be read *after* the
			// control has been parented: WinForms recreates a control's handle when it is added to
			// a container, which would leave the swapchain bound to a dead window.
			form.CreateControl();
			IntPtr renderHandle = form.RenderControl.Handle;

			graphicsContext = new DX11GraphicsContext();
			graphicsContext.CreateDevice();

			var swapChainDescription = new SwapChainDescription()
			{
				Width = (uint)form.RenderControl.ClientSize.Width,
				Height = (uint)form.RenderControl.ClientSize.Height,
				SurfaceInfo = new SurfaceInfo(renderHandle, SurfaceInfo.SurfaceTypes.Forms),
				ColorTargetFormat = Evergine.Common.Graphics.PixelFormat.R8G8B8A8_UNorm,
				ColorTargetFlags = TextureFlags.RenderTarget | TextureFlags.ShaderResource,
				DepthStencilTargetFormat = Evergine.Common.Graphics.PixelFormat.D24_UNorm_S8_UInt,
				DepthStencilTargetFlags = TextureFlags.DepthStencil,
				SampleCount = TextureSampleCount.None,
				IsWindowed = true,
				RefreshRate = 60,
			};

			swapChain = graphicsContext.CreateSwapChain(swapChainDescription);

			// VSync would clamp the measured frame time to the display refresh rate.
			swapChain.VerticalSync = !benchmark;

			form.RenderControl.ClientSizeChanged += (s, e) => resizePending = true;

			// Drive the render loop from the form itself, so it ends when the window is closed.
			var windowSystem = new FormsWindowsSystem { AutoRegisterWindow = false };
			windowSystem.RegisterLoopThreadControl(form);
			windowSystem.Run(Load, Draw);
		}

		/// <summary>
		/// Matches the swapchain to the current size of the hosting control. The ray traced image
		/// keeps its own fixed resolution and is stretched by the fullscreen triangle, so resizing
		/// costs nothing on the CPU side.
		/// </summary>
		private static void ApplyPendingResize()
		{
			resizePending = false;

			uint width = (uint)Math.Max(form.RenderControl.ClientSize.Width, 1);
			uint height = (uint)Math.Max(form.RenderControl.ClientSize.Height, 1);

			swapChain.ResizeSwapChain(width, height);

			viewports[0] = new Viewport(0, 0, width, height);
			scissors[0] = new Evergine.Mathematics.Rectangle(0, 0, (int)width, (int)height);
		}

		private static void Load()
		{
			long buildStart = Stopwatch.GetTimestamp();
			raytracer = new Raytracer();
			sceneBuildMs = ToMilliseconds(Stopwatch.GetTimestamp() - buildStart);

			pixels = new byte[Width * Height * 4];
			clock = Stopwatch.StartNew();

			traceMs = new double[BenchmarkFrames];
			uploadMs = new double[BenchmarkFrames];
			gpuMs = new double[BenchmarkFrames];

			// CPU-writable render target for the ray traced image.
			var textureDescription = new TextureDescription()
			{
				Type = TextureType.Texture2D,
				Width = Width,
				Height = Height,
				Depth = 1,
				ArraySize = 1,
				MipLevels = 1,
				Format = Evergine.Common.Graphics.PixelFormat.R8G8B8A8_UNorm,
				Usage = ResourceUsage.Default,
				CpuAccess = ResourceCpuAccess.None,
				Flags = TextureFlags.ShaderResource,
				SampleCount = TextureSampleCount.None,
			};
			rayTexture = graphicsContext.Factory.CreateTexture(ref textureDescription);

			// Linear so the fixed-resolution ray traced image scales cleanly when the window is resized.
			var samplerDescription = SamplerStates.LinearClamp;
			var sampler = graphicsContext.Factory.CreateSamplerState(ref samplerDescription);

			var vertexShaderDescription = new ShaderDescription(
				ShaderStages.Vertex, "VS", graphicsContext.ShaderCompile(ShaderSource, "VS", ShaderStages.Vertex).ByteCode);
			var pixelShaderDescription = new ShaderDescription(
				ShaderStages.Pixel, "PS", graphicsContext.ShaderCompile(ShaderSource, "PS", ShaderStages.Pixel).ByteCode);
			var vertexShader = graphicsContext.Factory.CreateShader(ref vertexShaderDescription);
			var pixelShader = graphicsContext.Factory.CreateShader(ref pixelShaderDescription);

			var resourceLayoutDescription = new ResourceLayoutDescription(
				new LayoutElementDescription(0, ResourceType.TextureView, ShaderStages.Pixel),
				new LayoutElementDescription(0, ResourceType.Sampler, ShaderStages.Pixel));
			var resourceLayout = graphicsContext.Factory.CreateResourceLayout(ref resourceLayoutDescription);

			var resourceSetDescription = new ResourceSetDescription(resourceLayout, rayTexture, sampler);
			resourceSet = graphicsContext.Factory.CreateResourceSet(ref resourceSetDescription);

			var pipelineDescription = new GraphicsPipelineDescription()
			{
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				InputLayouts = null,
				ResourceLayouts = new[] { resourceLayout },
				Shaders = new GraphicsShaderStateDescription()
				{
					VertexShader = vertexShader,
					PixelShader = pixelShader,
				},
				RenderStates = new RenderStateDescription()
				{
					RasterizerState = RasterizerStates.CullBack,
					BlendState = BlendStates.Opaque,
					DepthStencilState = DepthStencilStates.None,
				},
				Outputs = swapChain.FrameBuffer.OutputDescription,
			};
			pipelineState = graphicsContext.Factory.CreateGraphicsPipeline(ref pipelineDescription);

			commandQueue = graphicsContext.Factory.CreateCommandQueue();

			viewports = new[] { new Viewport(0, 0, Width, Height) };
			scissors = new[] { new Evergine.Mathematics.Rectangle(0, 0, (int)Width, (int)Height) };

			form.SetSceneInfo(raytracer.TriangleCount, raytracer.GeometryCount, (int)Width, (int)Height);
			ApplyPendingResize();
		}

		private static void Draw()
		{
			if (resizePending)
			{
				ApplyPendingResize();
			}

			swapChain.InitFrame();

			// The camera is frozen while benchmarking so every frame traces the same image.
			if (!benchmark && !form.AnimateButton.Checked)
			{
				cameraTime = (float)clock.Elapsed.TotalSeconds;
			}

			// 1. CPU ray tracing with Embree.
			long t0 = Stopwatch.GetTimestamp();
			raytracer.Render(pixels, (int)Width, (int)Height, cameraTime);
			long t1 = Stopwatch.GetTimestamp();

			// 2. Upload to the GPU texture.
			graphicsContext.UpdateTextureData(rayTexture, pixels, 0);
			long t2 = Stopwatch.GetTimestamp();

			// 3. Fullscreen triangle to the swapchain.
			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();
			commandBuffer.SetViewports(viewports);
			commandBuffer.SetScissorRectangles(scissors);

			var renderPass = new RenderPassDescription(swapChain.FrameBuffer, new ClearValue(ClearFlags.All, Evergine.Common.Graphics.Color.Black));
			commandBuffer.BeginRenderPass(ref renderPass);
			commandBuffer.SetGraphicsPipelineState(pipelineState);
			commandBuffer.SetResourceSet(resourceSet);
			commandBuffer.Draw(3);
			commandBuffer.EndRenderPass();

			commandBuffer.End();
			commandBuffer.Commit();
			commandQueue.Submit();
			commandQueue.WaitIdle();
			long t3 = Stopwatch.GetTimestamp();

			frameIndex++;

			if (benchmark)
			{
				// The first frames pay JIT and lazy driver/BVH warm-up costs.
				if (frameIndex > BenchmarkWarmupFrames)
				{
					int i = frameIndex - BenchmarkWarmupFrames - 1;
					traceMs[i] = ToMilliseconds(t1 - t0);
					uploadMs[i] = ToMilliseconds(t2 - t1);
					gpuMs[i] = ToMilliseconds(t3 - t2);
				}

				if (frameIndex == BenchmarkWarmupFrames + BenchmarkFrames)
				{
					SaveScreenshot();
					ReportBenchmark();
					Environment.Exit(0);
				}

				swapChain.Present();
				return;
			}

			form.SetTimings(ToMilliseconds(t1 - t0), ToMilliseconds(t2 - t1), ToMilliseconds(t3 - t2));

			if (frameIndex == ScreenshotFrame || screenshotRequested)
			{
				screenshotRequested = false;
				SaveScreenshot();

				if (exitAfterScreenshot)
				{
					Environment.Exit(0);
				}
			}

			swapChain.Present();
		}

		private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

		private static void ReportBenchmark()
		{
			Array.Sort(traceMs);
			var upload = (double[])uploadMs.Clone();
			var gpu = (double[])gpuMs.Clone();
			Array.Sort(upload);
			Array.Sort(gpu);

			long pixelCount = (long)Width * Height;

			Console.WriteLine();
			Console.WriteLine($"Resolution     : {Width}x{Height} ({pixelCount:N0} primary rays + 1 shadow ray per hit)");
			Console.WriteLine($"Triangles      : {raytracer.TriangleCount:N0} in {raytracer.GeometryCount} geometries");
			Console.WriteLine($"Logical cores  : {Environment.ProcessorCount}");
			Console.WriteLine($"Scene build    : {sceneBuildMs:F2} ms (geometry upload + rtcCommitScene BVH)");
			Console.WriteLine($"Frames measured: {BenchmarkFrames} (after {BenchmarkWarmupFrames} warm-up frames)");
			Console.WriteLine();
			Console.WriteLine("Stage                    min       median       mean        max");
			Console.WriteLine($"Embree CPU trace   {Min(traceMs),8:F2} ms {Median(traceMs),8:F2} ms {Mean(traceMs),8:F2} ms {Max(traceMs),8:F2} ms");
			Console.WriteLine($"Texture upload     {Min(upload),8:F2} ms {Median(upload),8:F2} ms {Mean(upload),8:F2} ms {Max(upload),8:F2} ms");
			Console.WriteLine($"GPU draw + sync    {Min(gpu),8:F2} ms {Median(gpu),8:F2} ms {Mean(gpu),8:F2} ms {Max(gpu),8:F2} ms");
			Console.WriteLine();

			double medianTrace = Median(traceMs);
			double medianTotal = medianTrace + Median(upload) + Median(gpu);
			Console.WriteLine($"Image generation (trace only) : {medianTrace:F2} ms  ->  {1000.0 / medianTrace:F1} fps, {pixelCount / medianTrace / 1000.0:F2} Mrays/s");
			Console.WriteLine($"Full frame (trace+upload+GPU) : {medianTotal:F2} ms  ->  {1000.0 / medianTotal:F1} fps");
		}

		private static double Min(double[] sorted) => sorted[0];

		private static double Max(double[] sorted) => sorted[sorted.Length - 1];

		private static double Median(double[] sorted) => sorted.Length % 2 == 1
			? sorted[sorted.Length / 2]
			: (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) * 0.5;

		private static double Mean(double[] values)
		{
			double sum = 0.0;
			foreach (double value in values)
			{
				sum += value;
			}

			return sum / values.Length;
		}

		/// <summary>
		/// Copies the swapchain color target into a CPU-readable staging texture and saves it
		/// as a PNG (same staging+map pattern as Evergine's SnapShoter).
		/// </summary>
		private static unsafe void SaveScreenshot()
		{
			Texture source = swapChain.FrameBuffer.ColorTargets[0].Texture;

			// The swapchain follows the control size, which is not the ray tracing resolution.
			int width = (int)source.Description.Width;
			int height = (int)source.Description.Height;

			var stagingDescription = source.Description;
			stagingDescription.Flags = TextureFlags.None;
			stagingDescription.CpuAccess = ResourceCpuAccess.Read;
			stagingDescription.Usage = ResourceUsage.Staging;
			var staging = graphicsContext.Factory.CreateTexture(ref stagingDescription);

			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();
			commandBuffer.CopyTextureDataTo(source, staging);
			commandBuffer.End();
			commandBuffer.Commit();
			commandQueue.Submit();
			commandQueue.WaitIdle();

			MappedResource mapped = graphicsContext.MapMemory(staging, MapMode.Read);

			try
			{
				using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				var bitmapData = bitmap.LockBits(
					new System.Drawing.Rectangle(0, 0, width, height),
					System.Drawing.Imaging.ImageLockMode.WriteOnly,
					System.Drawing.Imaging.PixelFormat.Format32bppArgb);

				try
				{
					for (int y = 0; y < height; y++)
					{
						byte* sourceRow = (byte*)mapped.Data + (y * mapped.RowPitch);
						byte* destinationRow = (byte*)bitmapData.Scan0 + (y * bitmapData.Stride);

						for (int x = 0; x < width; x++)
						{
							// Swapchain is RGBA, GDI+ expects BGRA.
							destinationRow[(x * 4) + 0] = sourceRow[(x * 4) + 2];
							destinationRow[(x * 4) + 1] = sourceRow[(x * 4) + 1];
							destinationRow[(x * 4) + 2] = sourceRow[(x * 4) + 0];
							destinationRow[(x * 4) + 3] = 255;
						}
					}
				}
				finally
				{
					bitmap.UnlockBits(bitmapData);
				}

				bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
				Console.WriteLine($"Screenshot saved to {screenshotPath}");
			}
			finally
			{
				graphicsContext.UnmapMemory(staging);
			}
		}
	}
}
