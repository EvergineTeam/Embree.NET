using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace OcclusionCulling
{
	/// <summary>
	/// Measures what a CPU occlusion culling pass costs over a thousand boxes, with Embree
	/// doing the visibility queries.
	/// </summary>
	internal static class Program
	{
		private const int SideCount = 10;                 // 10^3 = 1000 boxes
		private const float BoxSize = 1.0f;
		private const float Spacing = 1.8f;               // centre-to-centre, in box sizes

		private const int CullWidth = 320;                // resolution of the visibility-buffer pass
		private const int CullHeight = 180;
		private const int TruthWidth = 1280;              // ground truth, far denser
		private const int TruthHeight = 720;

		private const int WarmupIterations = 20;
		private const int MeasuredIterations = 100;

		private static int Main(string[] args)
		{
			bool images = args.Contains("--images");

			using var scene = new Scene(SideCount, BoxSize, Spacing);

			// Outside the grid, looking at its centre, close enough that the near faces hide
			// most of the interior. That is the situation occlusion culling exists for.
			float extent = BoxSize * Spacing * (SideCount - 1) * 0.5f;
			var camera = new Camera(
				position: new Vector3(extent * 2.5f, extent * 1.4f, extent * 2.9f),
				target: Vector3.Zero,
				fovDegrees: 55.0f,
				aspect: (float)CullWidth / CullHeight,
				near: 0.1f,
				far: extent * 20.0f);

			Console.WriteLine($"Scene          : {scene.BoxCount:N0} boxes ({SideCount}x{SideCount}x{SideCount}), {scene.TriangleCount:N0} triangles, one geometry each");
			Console.WriteLine($"Logical cores  : {Environment.ProcessorCount}");
			Console.WriteLine($"Widest packet  : {scene.MaxPacketWidth} rays (asked of the device, not assumed)");
			Console.WriteLine();

			var candidates = new int[scene.BoxCount];
			var visible = new bool[scene.BoxCount];
			var truth = new bool[scene.BoxCount];

			// Ground truth: a visibility buffer far denser than any of the passes below. What it
			// finds is what is genuinely visible; everything else is a sampling artefact.
			//
			// Its per-box pixel counts matter as much as the visibility flags. In a grid this
			// dense most of what is "visible" is visible through a gap a pixel or two wide, and
			// counting those the same as a box filling a quarter of the screen would make the
			// accuracy column say nothing useful.
			var truthIds = new uint[TruthWidth * TruthHeight];
			Culling.VisibilityBufferSingle(scene, camera, TruthWidth, TruthHeight, truth, truthIds);
			int trulyVisible = truth.Count(v => v);

			var pixelsPerBox = new long[scene.BoxCount];
			long coveredPixels = 0;
			foreach (uint id in truthIds)
			{
				if (id != Embree_INVALID)
				{
					pixelsPerBox[id]++;
					coveredPixels++;
				}
			}

			int frustumCount = Culling.Frustum(scene, camera, candidates);
			Console.WriteLine($"Frustum pass   : {frustumCount:N0} of {scene.BoxCount:N0} boxes survive");
			Console.WriteLine($"Ground truth   : {trulyVisible:N0} boxes actually visible at {TruthWidth}x{TruthHeight}");
			Console.WriteLine();

			var results = new[]
			{
				Measure("frustum only", scene, camera, candidates, visible, truth, pixelsPerBox, coveredPixels,
					(s, c, cand, n, vis) => { Array.Clear(vis); for (int i = 0; i < n; i++) { vis[cand[i]] = true; } return 0; }),

				Measure("per-object, single ray", scene, camera, candidates, visible, truth, pixelsPerBox, coveredPixels,
					(s, c, cand, n, vis) => Culling.PerObjectSingle(s, c, cand, n, vis)),

				Measure("per-object, 8-wide packets", scene, camera, candidates, visible, truth, pixelsPerBox, coveredPixels,
					(s, c, cand, n, vis) => Culling.PerObjectPacket(s, c, cand, n, vis)),

				Measure($"visibility buffer {CullWidth}x{CullHeight}, single ray", scene, camera, candidates, visible, truth, pixelsPerBox, coveredPixels,
					(s, c, cand, n, vis) => Culling.VisibilityBufferSingle(s, c, CullWidth, CullHeight, vis)),

				Measure($"visibility buffer {CullWidth}x{CullHeight}, 8-wide packets", scene, camera, candidates, visible, truth, pixelsPerBox, coveredPixels,
					(s, c, cand, n, vis) => Culling.VisibilityBufferPacket(s, c, CullWidth, CullHeight, vis)),
			};

			Console.WriteLine("Method                                        median      mean       min       max      rays   visible  culled   miss  screen  extra");
			foreach (var r in results)
			{
				Console.WriteLine(
					$"{r.Name,-42} {r.Median,7:F3}ms {r.Mean,7:F3}ms {r.Min,7:F3}ms {r.Max,7:F3}ms {r.Rays,9:N0} {r.Visible,9:N0} {r.CulledPercent,6:F1}% {r.FalseNegatives,6:N0} {r.MissedScreenPercent,6:F2}% {r.FalsePositives,6:N0}");
			}

			Console.WriteLine();
			Console.WriteLine("miss   = visible boxes the pass discarded (these pop on screen)");
			Console.WriteLine("screen = how much of the covered screen area those discarded boxes actually held");
			Console.WriteLine("extra  = hidden boxes the pass kept (these only cost draw calls)");

			if (images)
			{
				WriteImages(scene, camera, candidates, frustumCount);
			}

			return 0;
		}

		private delegate long Pass(Scene scene, Camera camera, int[] candidates, int candidateCount, bool[] visible);

		private static Result Measure(string name, Scene scene, Camera camera, int[] candidates, bool[] visible, bool[] truth, long[] pixelsPerBox, long coveredPixels, Pass pass)
		{
			int candidateCount = Culling.Frustum(scene, camera, candidates);

			for (int i = 0; i < WarmupIterations; i++)
			{
				pass(scene, camera, candidates, candidateCount, visible);
			}

			var samples = new double[MeasuredIterations];
			long rays = 0;

			for (int i = 0; i < MeasuredIterations; i++)
			{
				long start = Stopwatch.GetTimestamp();
				rays = pass(scene, camera, candidates, candidateCount, visible);
				samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
			}

			int visibleCount = 0, falseNegatives = 0, falsePositives = 0;
			long missedPixels = 0;
			for (int i = 0; i < visible.Length; i++)
			{
				if (visible[i])
				{
					visibleCount++;
					if (!truth[i])
					{
						falsePositives++;
					}
				}
				else if (truth[i])
				{
					falseNegatives++;
					missedPixels += pixelsPerBox[i];
				}
			}

			Array.Sort(samples);

			return new Result
			{
				Name = name,
				Median = samples[samples.Length / 2],
				Mean = samples.Average(),
				Min = samples[0],
				Max = samples[^1],
				Rays = rays,
				Visible = visibleCount,
				CulledPercent = 100.0 * (scene.BoxCount - visibleCount) / scene.BoxCount,
				FalseNegatives = falseNegatives,
				FalsePositives = falsePositives,
				MissedScreenPercent = coveredPixels > 0 ? 100.0 * missedPixels / coveredPixels : 0.0,
			};
		}

		private static void WriteImages(Scene scene, Camera camera, int[] candidates, int candidateCount)
		{
			const int Width = 960;
			const int Height = 540;

			var ids = new uint[Width * Height];
			var seen = new bool[scene.BoxCount];
			Culling.VisibilityBufferSingle(scene, camera, Width, Height, seen, ids);

			var verdict = new bool[scene.BoxCount];
			Culling.PerObjectSingle(scene, camera, candidates, candidateCount, verdict);

			var truth = new bool[scene.BoxCount];
			Culling.VisibilityBufferSingle(scene, camera, TruthWidth, TruthHeight, truth);

			// 1. The scene itself, flat-coloured per object.
			var scenePixels = new byte[Width * Height * 3];
			for (int i = 0; i < ids.Length; i++)
			{
				Colour(ids[i], out byte r, out byte g, out byte b);
				scenePixels[(i * 3) + 0] = r;
				scenePixels[(i * 3) + 1] = g;
				scenePixels[(i * 3) + 2] = b;
			}

			Png.Write("scene.png", Width, Height, scenePixels);

			// 2. The same view, coloured by what the per-object pass decided. Red on screen is a
			//    box the pass discarded while it was in fact visible: that is popping.
			var verdictPixels = new byte[Width * Height * 3];
			for (int i = 0; i < ids.Length; i++)
			{
				uint id = ids[i];
				byte r, g, b;
				if (id == Embree_INVALID)
				{
					r = 24; g = 28; b = 36;
				}
				else if (verdict[id])
				{
					r = 60; g = 190; b = 90;
				}
				else
				{
					r = 220; g = 60; b = 60;
				}

				verdictPixels[(i * 3) + 0] = r;
				verdictPixels[(i * 3) + 1] = g;
				verdictPixels[(i * 3) + 2] = b;
			}

			Png.Write("verdict.png", Width, Height, verdictPixels);

			// 3. A side view of the grid: what got kept, and what got thrown away.
			WriteSlice(scene, camera, verdict, "slice.png");

			Console.WriteLine();
			Console.WriteLine($"Wrote scene.png, verdict.png and slice.png to {Directory.GetCurrentDirectory()}");
		}

		private const uint Embree_INVALID = uint.MaxValue;

		/// <summary>
		/// A horizontal slice through the middle layer of the grid, seen from above. Boxes the
		/// pass kept are bright, boxes it discarded are dark, and the camera is the cross.
		/// </summary>
		/// <remarks>
		/// One layer, not all ten. Projecting the whole grid onto the ground plane stacks every
		/// layer into the same cell, so the picture would show one arbitrary layer's verdict and
		/// look like the culling was deciding at random.
		/// </remarks>
		private static void WriteSlice(Scene scene, Camera camera, bool[] visible, string path)
		{
			const int Width = 720;
			const int Height = 720;

			float extent = BoxSize * Spacing * (SideCount - 1) * 0.5f;
			float span = extent * 3.2f;

			var pixels = new byte[Width * Height * 3];
			for (int i = 0; i < pixels.Length; i += 3)
			{
				pixels[i] = 18; pixels[i + 1] = 20; pixels[i + 2] = 26;
			}

			// Only the middle layer: geomIDs run x fastest, then y, then z.
			int layer = SideCount / 2;
			int perLayer = SideCount;

			for (int box = 0; box < scene.BoxCount; box++)
			{
				if ((box / perLayer) % SideCount != layer)
				{
					continue;
				}

				Vector3 lo = scene.Min[box];
				Vector3 hi = scene.Max[box];

				int x0 = ToPixel(lo.X, span, Width);
				int x1 = ToPixel(hi.X, span, Width);
				int y0 = ToPixel(-hi.Z, span, Height);
				int y1 = ToPixel(-lo.Z, span, Height);

				byte r, g, b;
				if (visible[box])
				{
					r = 235; g = 235; b = 245;
				}
				else
				{
					r = 52; g = 56; b = 68;
				}

				for (int y = Math.Max(y0, 0); y <= Math.Min(y1, Height - 1); y++)
				{
					for (int x = Math.Max(x0, 0); x <= Math.Min(x1, Width - 1); x++)
					{
						int p = ((y * Width) + x) * 3;
						pixels[p] = r; pixels[p + 1] = g; pixels[p + 2] = b;
					}
				}
			}

			int cx = ToPixel(camera.Position.X, span, Width);
			int cy = ToPixel(-camera.Position.Z, span, Height);
			for (int d = -9; d <= 9; d++)
			{
				Plot(pixels, Width, Height, cx + d, cy, 255, 170, 40);
				Plot(pixels, Width, Height, cx, cy + d, 255, 170, 40);
			}

			Png.Write(path, Width, Height, pixels);
		}

		private static int ToPixel(float world, float span, int size) =>
			(int)MathF.Round((world + span) / (span * 2.0f) * size);

		private static void Plot(byte[] pixels, int width, int height, int x, int y, byte r, byte g, byte b)
		{
			if (x < 0 || y < 0 || x >= width || y >= height)
			{
				return;
			}

			int p = ((y * width) + x) * 3;
			pixels[p] = r; pixels[p + 1] = g; pixels[p + 2] = b;
		}

		/// <summary>A stable, well-spread colour per geomID; grey for "nothing was hit".</summary>
		private static void Colour(uint id, out byte r, out byte g, out byte b)
		{
			if (id == Embree_INVALID)
			{
				r = 24; g = 28; b = 36;
				return;
			}

			uint h = (id * 2654435761u) ^ (id << 13);
			r = (byte)(80 + (h & 0x7F));
			g = (byte)(80 + ((h >> 8) & 0x7F));
			b = (byte)(80 + ((h >> 16) & 0x7F));
		}

		private sealed class Result
		{
			public string Name;
			public double Median;
			public double Mean;
			public double Min;
			public double Max;
			public long Rays;
			public int Visible;
			public double CulledPercent;
			public int FalseNegatives;
			public int FalsePositives;
			public double MissedScreenPercent;
		}
	}
}
