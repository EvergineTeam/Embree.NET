using Evergine.Bindings.Embree;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EmbreeScene = Evergine.Bindings.Embree.Scene;

namespace OcclusionCulling
{
	/// <summary>
	/// The occlusion culling passes under measurement.
	/// </summary>
	/// <remarks>
	/// Two families, because they answer different questions and their cost scales with
	/// different things:
	/// <list type="bullet">
	/// <item>Per-object rays scale with the object count. This is what an engine runs to decide
	/// which draw calls to submit.</item>
	/// <item>A visibility buffer scales with resolution. It is exact up to its sampling density
	/// and does not care how many objects there are.</item>
	/// </list>
	/// Each comes in a single-ray and a 16-wide packet form.
	/// </remarks>
	internal static unsafe class Culling
	{
		/// <summary>Sample points shot at each box: eight corners and the centre.</summary>
		public const int SamplesPerBox = 9;

		private const float TargetEpsilon = 1e-3f;

		/// <summary>
		/// Frustum stage. Fills <paramref name="candidates"/> with the boxes that survive and
		/// returns how many there are.
		/// </summary>
		public static int Frustum(Scene scene, Camera camera, int[] candidates)
		{
			int count = 0;
			for (int i = 0; i < scene.BoxCount; i++)
			{
				if (camera.Intersects(scene.Min[i], scene.Max[i]))
				{
					candidates[count++] = i;
				}
			}

			return count;
		}

		// -----------------------------------------------------------------------------------
		// A — per-object rays
		// -----------------------------------------------------------------------------------

		/// <summary>
		/// One occlusion ray per sample point, stopping at the first sample that gets through.
		/// </summary>
		public static long PerObjectSingle(Scene scene, Camera camera, int[] candidates, int candidateCount, bool[] visible)
		{
			Array.Clear(visible);
			long rays = 0;

			EmbreeScene handle = scene.Handle;
			Vector3 origin = camera.Position;

			object counterLock = new();

			Parallel.For(
				0,
				candidateCount,
				() => (Buffer: (IntPtr)NativeMemory.AlignedAlloc((nuint)sizeof(RayHit), 64), Rays: 0L),
				(index, _, state) =>
				{
					RayHit* rayhit = (RayHit*)state.Buffer;
					IntersectArguments args;
					Embree.InitIntersectArguments(&args);
					args.Flags = RayQueryFlags.Coherent;

					int box = candidates[index];
					Span<Vector3> points = stackalloc Vector3[SamplesPerBox];
					scene.GetSamplePoints(box, points);

					long local = state.Rays;

					for (int s = 0; s < SamplesPerBox; s++)
					{
						local++;
						if (ReachesBox(handle, rayhit, &args, origin, points[s], (uint)box))
						{
							visible[box] = true;
							break;
						}
					}

					return (state.Buffer, local);
				},
				state =>
				{
					NativeMemory.AlignedFree((void*)state.Buffer);
					lock (counterLock)
					{
						rays += state.Rays;
					}
				});

			return rays;
		}

		/// <summary>
		/// The same test, eight rays at a time.
		/// </summary>
		/// <remarks>
		/// Packets cost the early exit: every sample of every candidate is traced, because the
		/// lanes are filled before any of them is known to have got through. That is the trade
		/// this benchmark exists to measure — more rays, but each one much cheaper.
		/// </remarks>
		public static long PerObjectPacket(Scene scene, Camera camera, int[] candidates, int candidateCount, bool[] visible)
		{
			Array.Clear(visible);

			int total = candidateCount * SamplesPerBox;
			int packets = (total + 7) / 8;

			EmbreeScene handle = scene.Handle;
			Vector3 origin = camera.Position;

			Parallel.For(
				0,
				packets,
				// Rays and mask in separate allocations. Packing the mask after the packet in one
				// block put it exactly at the end of the reservation, and anything overrunning
				// the packet by a byte then corrupted the allocator's bookkeeping instead of
				// failing where the mistake was.
				() => (Rays: (IntPtr)NativeMemory.AlignedAlloc((nuint)sizeof(RayHit8), 32),
					   Valid: (IntPtr)NativeMemory.AlignedAlloc(8 * sizeof(int), 32)),
				(packet, _, buffers) =>
				{
					RayHit8* rays = (RayHit8*)buffers.Rays;
					int* valid = (int*)buffers.Valid;

					IntersectArguments args;
					Embree.InitIntersectArguments(&args);
					args.Flags = RayQueryFlags.Coherent;

					// The buffer is reused across packets and starts uninitialised. Every lane
					// has to hold a well-formed ray even when it is masked off: the traversal
					// works on all eight lanes at once, and garbage in a disabled lane
					// becomes a NaN in the SIMD maths that takes the whole packet down.
					*rays = default;

					int start = packet * 8;
					Span<Vector3> points = stackalloc Vector3[SamplesPerBox];
					int lastBox = -1;

					for (int lane = 0; lane < 8; lane++)
					{
						int flat = start + lane;
						if (flat >= total)
						{
							// Disabled, and empty: tnear > tfar leaves nothing to intersect.
							valid[lane] = 0;
							rays->Ray.DirZ[lane] = 1.0f;
							rays->Ray.Tnear[lane] = 1.0f;
							rays->Ray.Tfar[lane] = 0.0f;
							continue;
						}

						int box = candidates[flat / SamplesPerBox];
						if (box != lastBox)
						{
							scene.GetSamplePoints(box, points);
							lastBox = box;
						}

						Vector3 direction = Vector3.Normalize(points[flat % SamplesPerBox] - origin);

						valid[lane] = -1;
						rays->Ray.OrgX[lane] = origin.X;
						rays->Ray.OrgY[lane] = origin.Y;
						rays->Ray.OrgZ[lane] = origin.Z;
						rays->Ray.DirX[lane] = direction.X;
						rays->Ray.DirY[lane] = direction.Y;
						rays->Ray.DirZ[lane] = direction.Z;
						rays->Ray.Tnear[lane] = 0.0f;
						rays->Ray.Tfar[lane] = float.PositiveInfinity;
						rays->Ray.Mask[lane] = uint.MaxValue;
						rays->Hit.GeomID[lane] = Embree.INVALID_GEOMETRY_ID;
					}

					Embree.Intersect8(valid, handle, rays, &args);

					for (int lane = 0; lane < 8; lane++)
					{
						int flat = start + lane;
						if (flat >= total || valid[lane] == 0)
						{
							continue;
						}

						// This sample reached the box before anything else, so it is visible.
						int box = candidates[flat / SamplesPerBox];
						if (rays->Hit.GeomID[lane] == (uint)box)
						{
							visible[box] = true;
						}
					}

					return buffers;
				},
				buffers =>
				{
					NativeMemory.AlignedFree((void*)buffers.Rays);
					NativeMemory.AlignedFree((void*)buffers.Valid);
				});

			return total;
		}

		// -----------------------------------------------------------------------------------
		// B — visibility buffer
		// -----------------------------------------------------------------------------------

		/// <summary>
		/// Casts a primary ray per sample and marks whatever geometry it lands on. Exact up to
		/// the sampling density.
		/// </summary>
		public static long VisibilityBufferSingle(Scene scene, Camera camera, int width, int height, bool[] visible, uint[] ids = null)
		{
			Array.Clear(visible);

			EmbreeScene handle = scene.Handle;
			Vector3 origin = camera.Position;

			Parallel.For(
				0,
				height,
				() => (IntPtr)NativeMemory.AlignedAlloc((nuint)sizeof(RayHit), 64),
				(y, _, buffer) =>
				{
					RayHit* rayhit = (RayHit*)buffer;
					IntersectArguments args;
					Embree.InitIntersectArguments(&args);
					args.Flags = RayQueryFlags.Coherent;

					for (int x = 0; x < width; x++)
					{
						Vector3 direction = camera.RayDirection((x + 0.5f) / width, (y + 0.5f) / height);

						*rayhit = default;
						rayhit->Ray.OrgX = origin.X;
						rayhit->Ray.OrgY = origin.Y;
						rayhit->Ray.OrgZ = origin.Z;
						rayhit->Ray.DirX = direction.X;
						rayhit->Ray.DirY = direction.Y;
						rayhit->Ray.DirZ = direction.Z;
						rayhit->Ray.Tnear = 0.0f;
						rayhit->Ray.Tfar = float.PositiveInfinity;
						rayhit->Ray.Mask = uint.MaxValue;
						rayhit->Hit.GeomID = Embree.INVALID_GEOMETRY_ID;

						Embree.Intersect1(handle, rayhit, &args);

						uint id = rayhit->Hit.GeomID;
						if (ids != null)
						{
							ids[(y * width) + x] = id;
						}

						if (id != Embree.INVALID_GEOMETRY_ID)
						{
							visible[id] = true;
						}
					}

					return buffer;
				},
				buffer => NativeMemory.AlignedFree((void*)buffer));

			return (long)width * height;
		}

		/// <summary>
		/// The same buffer, eight pixels at a time. Consecutive pixels of a row share almost all of
		/// their traversal, which is what the packet path is built for.
		/// </summary>
		public static long VisibilityBufferPacket(Scene scene, Camera camera, int width, int height, bool[] visible)
		{
			Array.Clear(visible);

			EmbreeScene handle = scene.Handle;
			Vector3 origin = camera.Position;
			int packetsPerRow = (width + 7) / 8;

			Parallel.For(
				0,
				height,
				() => (Rays: (IntPtr)NativeMemory.AlignedAlloc((nuint)sizeof(RayHit8), 32),
					   Valid: (IntPtr)NativeMemory.AlignedAlloc(8 * sizeof(int), 32)),
				(y, _, buffers) =>
				{
					RayHit8* rayhit = (RayHit8*)buffers.Rays;
					int* valid = (int*)buffers.Valid;

					IntersectArguments args;
					Embree.InitIntersectArguments(&args);
					args.Flags = RayQueryFlags.Coherent;

					for (int packet = 0; packet < packetsPerRow; packet++)
					{
						*rayhit = default;

						for (int lane = 0; lane < 8; lane++)
						{
							int x = (packet * 8) + lane;
							if (x >= width)
							{
								// See the comment in PerObjectPacket: a disabled lane still has
								// to hold a well-formed, empty ray.
								valid[lane] = 0;
								rayhit->Ray.DirZ[lane] = 1.0f;
								rayhit->Ray.Tnear[lane] = 1.0f;
								rayhit->Ray.Tfar[lane] = 0.0f;
								continue;
							}

							Vector3 direction = camera.RayDirection((x + 0.5f) / width, (y + 0.5f) / height);

							valid[lane] = -1;
							rayhit->Ray.OrgX[lane] = origin.X;
							rayhit->Ray.OrgY[lane] = origin.Y;
							rayhit->Ray.OrgZ[lane] = origin.Z;
							rayhit->Ray.DirX[lane] = direction.X;
							rayhit->Ray.DirY[lane] = direction.Y;
							rayhit->Ray.DirZ[lane] = direction.Z;
							rayhit->Ray.Tnear[lane] = 0.0f;
							rayhit->Ray.Tfar[lane] = float.PositiveInfinity;
							rayhit->Ray.Mask[lane] = uint.MaxValue;
							rayhit->Hit.GeomID[lane] = Embree.INVALID_GEOMETRY_ID;
						}

						Embree.Intersect8(valid, handle, rayhit, &args);

						for (int lane = 0; lane < 8; lane++)
						{
							if (valid[lane] == 0)
							{
								continue;
							}

							uint id = rayhit->Hit.GeomID[lane];
							if (id != Embree.INVALID_GEOMETRY_ID)
							{
								visible[id] = true;
							}
						}
					}

					return buffers;
				},
				buffers =>
				{
					NativeMemory.AlignedFree((void*)buffers.Rays);
					NativeMemory.AlignedFree((void*)buffers.Valid);
				});

			return (long)width * height;
		}

		/// <summary>
		/// Whether a ray aimed at <paramref name="target"/> reaches box <paramref name="box"/>
		/// before anything else.
		/// </summary>
		/// <remarks>
		/// Closest-hit, not the cheaper any-hit. The obvious formulation — an occlusion ray that
		/// stops just short of the sample point — has the box occlude itself: the point sits on
		/// its surface, so its own front face is in the way for every sample except the few
		/// silhouette corners. Measured on this scene that answered "hidden" for a quarter of the
		/// boxes that were plainly visible. Asking what the ray hits first costs more per ray and
		/// is the question actually being asked.
		/// </remarks>
		private static bool ReachesBox(EmbreeScene scene, RayHit* rayhit, IntersectArguments* args, Vector3 origin, Vector3 target, uint box)
		{
			Vector3 direction = Vector3.Normalize(target - origin);

			*rayhit = default;
			rayhit->Ray.OrgX = origin.X;
			rayhit->Ray.OrgY = origin.Y;
			rayhit->Ray.OrgZ = origin.Z;
			rayhit->Ray.DirX = direction.X;
			rayhit->Ray.DirY = direction.Y;
			rayhit->Ray.DirZ = direction.Z;
			rayhit->Ray.Tnear = 0.0f;
			rayhit->Ray.Tfar = float.PositiveInfinity;
			rayhit->Ray.Mask = uint.MaxValue;
			rayhit->Hit.GeomID = Embree.INVALID_GEOMETRY_ID;

			Embree.Intersect1(scene, rayhit, args);

			return rayhit->Hit.GeomID == box;
		}
	}
}
