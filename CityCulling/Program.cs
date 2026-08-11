using Evergine.Common.Graphics;
using Evergine.DirectX11;
using Evergine.Forms;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EvergineMath = Evergine.Mathematics;
using Vector3 = System.Numerics.Vector3;

namespace CityCulling
{
	/// <summary>
	/// A city of primitives drawn with the Evergine low-level API, with Embree deciding which
	/// objects reach the GPU.
	/// </summary>
	internal static class Program
	{
		private const uint Width = 1280;
		private const uint Height = 720;

		private const int ObjectCount = 1000;
		private const float BlockSize = 34.0f;
		private const int BlocksPerSide = 15;
		private const int Seed = 20260811;

		private const float EyeHeight = 2.2f;      // street level, against buildings 4 to 46 tall
		private const float OrbitRadius = 0.62f;   // as a fraction of the city half-extent
		private const float CaptureAngle = 0.9f;   // fixed, so the captures are reproducible

		private static GraphicsContext graphicsContext;
		private static SwapChain swapChain;
		private static CommandQueue commandQueue;
		private static MainForm form;
		private static City city;
		private static Renderer renderer;

		private static int[] candidates;
		private static bool[] visible;
		private static bool[] everything;

		private static Stopwatch clock;
		private static float cameraAngle = CaptureAngle;
		private static bool captureRequested;
		private static bool exitAfterCapture;
		private static string outputDirectory;
		private static int lastCandidateCount;
		private static bool benchmark;
		private static bool noCull;
		private static int benchFrame;
		private static readonly System.Collections.Generic.List<double> BenchCull = new();
		private static readonly System.Collections.Generic.List<double> BenchFrame = new();
		private static readonly System.Collections.Generic.List<int> BenchVisible = new();
		private static double lastMissedScreen;

		[STAThread]
		private static int Main(string[] args)
		{
			benchmark = args.Contains("--bench");
			noCull = args.Contains("--no-cull");
			bool capture = args.Contains("--capture");
			exitAfterCapture = args.Contains("--exit");
			captureRequested = capture;

			outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

			System.Windows.Forms.Application.EnableVisualStyles();
			System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

			form = new MainForm((int)Width, (int)Height);
			form.CaptureButton.Click += (s, e) => captureRequested = true;
			form.CreateControl();

			graphicsContext = new DX11GraphicsContext();
			graphicsContext.CreateDevice();

			var swapChainDescription = new SwapChainDescription()
			{
				Width = (uint)form.RenderControl.ClientSize.Width,
				Height = (uint)form.RenderControl.ClientSize.Height,
				SurfaceInfo = new SurfaceInfo(form.RenderControl.Handle, SurfaceInfo.SurfaceTypes.Forms),
				ColorTargetFormat = PixelFormat.R8G8B8A8_UNorm,
				ColorTargetFlags = TextureFlags.RenderTarget | TextureFlags.ShaderResource,
				DepthStencilTargetFormat = PixelFormat.D24_UNorm_S8_UInt,
				DepthStencilTargetFlags = TextureFlags.DepthStencil,
				SampleCount = TextureSampleCount.None,
				IsWindowed = true,
				RefreshRate = 60,
			};

			swapChain = graphicsContext.CreateSwapChain(swapChainDescription);
			// VSync would clamp every frame to the refresh rate and hide what the culling costs.
			swapChain.VerticalSync = !benchmark;

			var windowSystem = new FormsWindowsSystem { AutoRegisterWindow = false };
			windowSystem.RegisterLoopThreadControl(form);
			windowSystem.Run(Load, Draw);

			return 0;
		}

		private static void Load()
		{
			city = new City(ObjectCount, BlockSize, BlocksPerSide, Seed);
			renderer = new Renderer(graphicsContext, city, swapChain.FrameBuffer);

			candidates = new int[city.Count];
			visible = new bool[city.Count];
			everything = new bool[city.Count];
			Array.Fill(everything, true);

			commandQueue = graphicsContext.Factory.CreateCommandQueue();
			clock = Stopwatch.StartNew();

			int triangles = city.Objects.Sum(o => Meshes.Get(o.Primitive).Indices.Length / 3) + 2;
			form.SetSceneInfo(city.Count, triangles);
		}

		private static void Draw()
		{
			long frameStart = Stopwatch.GetTimestamp();

			if (benchmark)
			{
				// A full orbit in fixed steps, so the sweep covers every direction the camera can
				// face rather than whichever ones a wall-clock animation happened to land on.
				cameraAngle = CaptureAngle + ((float)benchFrame / BenchFrames * MathF.Tau);
			}
			else if (!form.PauseButton.Checked && !captureRequested)
			{
				cameraAngle = CaptureAngle + ((float)clock.Elapsed.TotalSeconds * 0.16f);
			}

			uint width = (uint)Math.Max(form.RenderControl.ClientSize.Width, 1);
			uint height = (uint)Math.Max(form.RenderControl.ClientSize.Height, 1);

			Camera camera = MakeCamera(cameraAngle, (float)width / height);

			// The culling pass: frustum, then Embree occlusion. This is the whole point of the
			// sample and the only part that is measured.
			long cullStart = Stopwatch.GetTimestamp();
			int candidateCount = Culling.Frustum(city, camera, candidates);
			lastCandidateCount = candidateCount;

			if (noCull)
			{
				// The counterfactual: draw everything and pay for it on the GPU instead.
				Array.Fill(visible, true);
			}
			else
			{
				Culling.Occlusion(city, camera, candidates, candidateCount, visible);
			}

			double cullMs = Milliseconds(Stopwatch.GetTimestamp() - cullStart);

			swapChain.InitFrame();

			var viewProjection = ViewProjection(camera);
			var light = new EvergineMath.Vector3(-0.42f, -0.78f, -0.46f);
			light.Normalize();

			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();
			commandBuffer.SetViewports(new[] { new Viewport(0, 0, width, height) });
			commandBuffer.SetScissorRectangles(new[] { new EvergineMath.Rectangle(0, 0, (int)width, (int)height) });

			int drawn = renderer.Draw(commandBuffer, city, viewProjection, light, visible, form.ShowCulledButton.Checked);

			commandBuffer.End();
			commandBuffer.Commit();
			commandQueue.Submit();
			commandQueue.WaitIdle();

			if (captureRequested)
			{
				captureRequested = false;
				WriteCaptures(camera, viewProjection, light, width, height);

				if (exitAfterCapture)
				{
					Environment.Exit(0);
				}
			}

			swapChain.Present();

			double frameMs = Milliseconds(Stopwatch.GetTimestamp() - frameStart);
			form.SetFrameInfo(drawn, city.Count + 1, cullMs, frameMs);

			if (benchmark)
			{
				benchFrame++;

				// The first frames pay JIT and driver warm-up.
				if (benchFrame > BenchWarmup)
				{
					BenchCull.Add(cullMs);
					BenchFrame.Add(frameMs);
					BenchVisible.Add(drawn);
				}

				if (benchFrame >= BenchWarmup + BenchFrames)
				{
					ReportBenchmark();
					Environment.Exit(0);
				}
			}
		}

		/// <summary>
		/// The camera orbits the city centre at street level, looking horizontally across it.
		/// </summary>
		/// <remarks>
		/// The eye height is the whole reason this scene culls well. Lift it above the rooftops
		/// and almost everything is visible at once; at 2.2 units, with buildings from 4 to 46,
		/// the first row of facades hides most of what is behind it.
		/// </remarks>
		private static Camera MakeCamera(float angle, float aspect)
		{
			float radius = city.Extent * OrbitRadius;
			var position = new Vector3(MathF.Cos(angle) * radius, EyeHeight, MathF.Sin(angle) * radius);
			var target = new Vector3(MathF.Cos(angle + 2.2f) * radius * 0.35f, EyeHeight * 2.4f, MathF.Sin(angle + 2.2f) * radius * 0.35f);

			return new Camera(position, target, 62.0f, aspect, 0.25f, city.Extent * 6.0f);
		}

		/// <summary>
		/// The matrix that goes to the GPU, built with Evergine.Mathematics because Evergine's
		/// depth is reversed and only its projection takes <c>reverseDepthBuffer</c>.
		/// </summary>
		private static EvergineMath.Matrix4x4 ViewProjection(Camera camera)
		{
			var view = EvergineMath.Matrix4x4.CreateLookAt(
				new EvergineMath.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
				new EvergineMath.Vector3(camera.Target.X, camera.Target.Y, camera.Target.Z),
				EvergineMath.Vector3.Up);

			var projection = EvergineMath.Matrix4x4.CreatePerspectiveFieldOfView(
				camera.FovDegrees * MathF.PI / 180.0f, camera.Aspect, camera.Near, camera.Far, reverseDepthBuffer: true);

			return view * projection;
		}

		private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

		private const int BenchWarmup = 30;
		private const int BenchFrames = 360;   // one full orbit, one degree at a time

		/// <summary>
		/// What the culling costs across a full orbit, and what share of a 60 Hz frame that is.
		/// </summary>
		private static void ReportBenchmark()
		{
			var cull = BenchCull.ToArray();
			var frame = BenchFrame.ToArray();
			var drawn = BenchVisible.ToArray();
			Array.Sort(cull);
			Array.Sort(frame);
			Array.Sort(drawn);

			double medianCull = cull[cull.Length / 2];

			Console.WriteLine();
			Console.WriteLine($"{city.Count:N0} objects, {BenchFrames} frames over a full orbit, VSync off{(noCull ? ", occlusion culling OFF" : string.Empty)}");
			Console.WriteLine();
			Console.WriteLine("                        min     median       mean        max");
			Console.WriteLine($"culling            {cull[0],8:F3} {medianCull,10:F3} {cull.Average(),10:F3} {cull[^1],10:F3}  ms");
			Console.WriteLine($"whole frame        {frame[0],8:F3} {frame[frame.Length / 2],10:F3} {frame.Average(),10:F3} {frame[^1],10:F3}  ms");
			Console.WriteLine($"draw calls         {drawn[0],8:N0} {drawn[drawn.Length / 2],10:N0} {drawn.Average(),10:F0} {drawn[^1],10:N0}");
			Console.WriteLine();
			if (!noCull)
			{
				Console.WriteLine($"Culling is {100.0 * medianCull / 16.6:F1}% of a 16.6 ms frame at 60 Hz ({medianCull:F2} ms of budget spent to avoid {city.Count - drawn[drawn.Length / 2]:N0} draw calls).");
			}
		}

		/// <summary>
		/// Three views of the same frame: what was drawn, what was thrown away, and the whole
		/// city from above so the occlusion shadows behind each building are visible.
		/// </summary>
		private static void WriteCaptures(Camera camera, EvergineMath.Matrix4x4 viewProjection, EvergineMath.Vector3 light, uint width, uint height)
		{
			var lightForTopDown = new EvergineMath.Vector3(-0.35f, -0.9f, -0.25f);
			lightForTopDown.Normalize();

			RenderTo(viewProjection, light, visible, drawCulled: false, width, height, "city.png");
			RenderTo(viewProjection, light, visible, drawCulled: true, width, height, "culled.png");
			RenderTo(TopDown(camera), lightForTopDown, visible, drawCulled: true, width, height, "topdown.png", CameraMarkers(camera));
			RayTraceEmbree(camera, width, height, "embree.png");

			int visibleCount = 0;
			for (int i = 0; i < visible.Length; i++)
			{
				if (visible[i]) { visibleCount++; }
			}

			Console.WriteLine($"objects {city.Count}, frustum candidates {lastCandidateCount}, visible {visibleCount}, culled {100.0 * (city.Count - visibleCount) / city.Count:F1}%");
			Console.WriteLine($"screen area held by discarded objects (from Embree's own geometry): {lastMissedScreen:F2}%");
			Console.WriteLine($"Wrote city.png, culled.png, topdown.png and embree.png to {outputDirectory}");
		}

		/// <summary>
		/// The same view, but traced against the Embree scene and coloured per geomID.
		/// </summary>
		/// <remarks>
		/// A diagnostic, not a feature. The culling can only be as right as the agreement between
		/// the triangles the GPU draws and the triangles Embree holds, and nothing else in the
		/// sample would notice if the two drifted apart: the render would look fine and objects
		/// would simply be culled for no visible reason. Put this next to city.png and any
		/// disagreement is obvious.
		/// </remarks>
		private static unsafe void RayTraceEmbree(Camera camera, uint width, uint height, string fileName)
		{
			var pixels = new byte[width * height * 3];
			var counters = new int[2]; // 0 = pixels on objects, 1 = pixels on discarded objects

			Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
			Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
			Vector3 up = Vector3.Cross(right, forward);
			float tanHalf = MathF.Tan(camera.FovDegrees * MathF.PI / 180.0f * 0.5f);

			System.Threading.Tasks.Parallel.For(
				0,
				(int)height,
				() => (IntPtr)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)sizeof(Evergine.Bindings.Embree.RayHit), 64),
				(y, _, buffer) =>
				{
					var rayhit = (Evergine.Bindings.Embree.RayHit*)buffer;
					Evergine.Bindings.Embree.IntersectArguments args;
					Evergine.Bindings.Embree.Embree.InitIntersectArguments(&args);

					for (int x = 0; x < width; x++)
					{
						float ndcX = (((x + 0.5f) / width * 2.0f) - 1.0f) * tanHalf * camera.Aspect;
						float ndcY = (1.0f - ((y + 0.5f) / height * 2.0f)) * tanHalf;
						Vector3 direction = Vector3.Normalize(forward + (right * ndcX) + (up * ndcY));

						*rayhit = default;
						rayhit->Ray.OrgX = camera.Position.X;
						rayhit->Ray.OrgY = camera.Position.Y;
						rayhit->Ray.OrgZ = camera.Position.Z;
						rayhit->Ray.DirX = direction.X;
						rayhit->Ray.DirY = direction.Y;
						rayhit->Ray.DirZ = direction.Z;
						rayhit->Ray.Tnear = 0.0f;
						rayhit->Ray.Tfar = float.PositiveInfinity;
						rayhit->Ray.Mask = uint.MaxValue;
						rayhit->Hit.GeomID = Evergine.Bindings.Embree.Embree.INVALID_GEOMETRY_ID;

						Evergine.Bindings.Embree.Embree.Intersect1(city.Handle, rayhit, &args);

						uint id = rayhit->Hit.GeomID;
						int p = (int)(((y * width) + x) * 3);

						if (id == Evergine.Bindings.Embree.Embree.INVALID_GEOMETRY_ID)
						{
							pixels[p] = 100; pixels[p + 1] = 140; pixels[p + 2] = 240;
						}
						else if (id == City.GroundGeomID)
						{
							pixels[p] = 52; pixels[p + 1] = 54; pixels[p + 2] = 60;
						}
						else
						{
							// Green when the culling kept it, red when it did not: the same verdict
							// as culled.png, but on Embree's own geometry.
							bool kept = visible[id - 1];
							if (!kept) { System.Threading.Interlocked.Increment(ref counters[1]); }
							System.Threading.Interlocked.Increment(ref counters[0]);
							pixels[p] = (byte)(kept ? 70 : 215);
							pixels[p + 1] = (byte)(kept ? 190 : 55);
							pixels[p + 2] = (byte)(kept ? 95 : 55);
						}
					}

					return buffer;
				},
				buffer => System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)buffer));

			lastMissedScreen = counters[0] > 0 ? 100.0 * counters[1] / counters[0] : 0.0;
			Png.Write(Path.Combine(outputDirectory, fileName), (int)width, (int)height, pixels);
		}

		private static EvergineMath.Matrix4x4 TopDown(Camera camera)
		{
			float span = city.Extent * 1.15f;

			var view = EvergineMath.Matrix4x4.CreateLookAt(
				new EvergineMath.Vector3(0, city.Extent * 3.0f, 0.01f),
				EvergineMath.Vector3.Zero,
				EvergineMath.Vector3.Up);

			var projection = EvergineMath.Matrix4x4.CreateOrthographic(
				span * 2.0f * ((float)Width / Height), span * 2.0f, 0.1f, city.Extent * 8.0f, reverseDepthBuffer: true);

			return view * projection;
		}

		/// <summary>
		/// The camera and its two horizontal frustum edges, as boxes, so the top-down view shows
		/// where the viewer is and which wedge of the city it can see at all.
		/// </summary>
		private static System.Collections.Generic.List<(EvergineMath.Matrix4x4 World, EvergineMath.Vector4 Colour)> CameraMarkers(Camera camera)
		{
			var markers = new System.Collections.Generic.List<(EvergineMath.Matrix4x4, EvergineMath.Vector4)>();
			var amber = new EvergineMath.Vector4(1.0f, 0.72f, 0.16f, 1.0f);

			float marker = city.Extent * 0.02f;
			markers.Add((
				EvergineMath.Matrix4x4.CreateScale(marker, marker * 8.0f, marker) *
				EvergineMath.Matrix4x4.CreateTranslation(camera.Position.X, marker * 8.0f, camera.Position.Z),
				amber));

			Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
			Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
			float tanHalf = MathF.Tan(camera.FovDegrees * MathF.PI / 180.0f * 0.5f) * camera.Aspect;
			float length = city.Extent * 2.2f;

			foreach (float side in new[] { -1.0f, 1.0f })
			{
				Vector3 edge = Vector3.Normalize(forward + (right * tanHalf * side));
				Vector3 mid = camera.Position + (edge * length * 0.5f);
				float yaw = MathF.Atan2(edge.X, edge.Z);

				markers.Add((
					EvergineMath.Matrix4x4.CreateScale(marker * 0.22f, marker * 0.22f, length * 0.5f) *
					EvergineMath.Matrix4x4.CreateRotationY(yaw) *
					EvergineMath.Matrix4x4.CreateTranslation(mid.X, marker, mid.Z),
					amber));
			}

			return markers;
		}

		private static unsafe void RenderTo(EvergineMath.Matrix4x4 viewProjection, EvergineMath.Vector3 light, bool[] set, bool drawCulled, uint width, uint height, string fileName, System.Collections.Generic.List<(EvergineMath.Matrix4x4 World, EvergineMath.Vector4 Colour)> markers = null)
		{
			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();
			commandBuffer.SetViewports(new[] { new Viewport(0, 0, width, height) });
			commandBuffer.SetScissorRectangles(new[] { new EvergineMath.Rectangle(0, 0, (int)width, (int)height) });
			renderer.Draw(commandBuffer, city, viewProjection, light, set, drawCulled, markers);
			commandBuffer.End();
			commandBuffer.Commit();
			commandQueue.Submit();
			commandQueue.WaitIdle();

			SaveColorTarget(Path.Combine(outputDirectory, fileName), width, height);
		}

		/// <summary>
		/// Copies the swapchain colour target into a staging texture and writes it out — the same
		/// staging plus MapMemory pattern Evergine's own SnapShoter uses.
		/// </summary>
		private static unsafe void SaveColorTarget(string path, uint width, uint height)
		{
			Texture source = swapChain.FrameBuffer.ColorTargets[0].Texture;

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
				var pixels = new byte[width * height * 3];
				for (int y = 0; y < height; y++)
				{
					byte* row = (byte*)mapped.Data + (y * mapped.RowPitch);
					for (int x = 0; x < width; x++)
					{
						int destination = (int)(((y * width) + x) * 3);
						pixels[destination + 0] = row[(x * 4) + 0];
						pixels[destination + 1] = row[(x * 4) + 1];
						pixels[destination + 2] = row[(x * 4) + 2];
					}
				}

				Png.Write(path, (int)width, (int)height, pixels);
			}
			finally
			{
				graphicsContext.UnmapMemory(staging);
				staging.Dispose();
			}
		}
	}
}
