using Evergine.Bindings.Embree;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EmbreeScene = Evergine.Bindings.Embree.Scene;

namespace CityCulling
{
	/// <summary>
	/// The occlusion pass: for each object that survives the frustum, decide whether anything
	/// in the city hides it.
	/// </summary>
	/// <remarks>
	/// Per-object rays with a single-ray query. Benchmarked against a screen-space visibility
	/// buffer on a thousand objects, this came out at about 0.2 ms against 0.6 ms, and discarded
	/// more. Eight-wide packets lost too: filling eight lanes means giving up the early exit, and
	/// rays aimed at eight different buildings diverge immediately, which is the case packet
	/// traversal is worst at.
	/// </remarks>
	internal static unsafe class Culling
	{
		private const int SamplesPerObject = 9;

		/// <summary>
		/// Frustum stage: fills <paramref name="candidates"/> with surviving indices.
		/// </summary>
		public static int Frustum(City city, Camera camera, int[] candidates)
		{
			int count = 0;
			for (int i = 0; i < city.Count; i++)
			{
				if (camera.Intersects(city.Min[i], city.Max[i]))
				{
					candidates[count++] = i;
				}
			}

			return count;
		}

		/// <summary>
		/// Occlusion stage. Marks <paramref name="visible"/> for every candidate that at least
		/// one sample ray reaches before anything else, and returns how many rays that took.
		/// </summary>
		public static long Occlusion(City city, Camera camera, int[] candidates, int candidateCount, bool[] visible)
		{
			Array.Clear(visible);

			EmbreeScene handle = city.Handle;
			Vector3 origin = camera.Position;
			long rays = 0;
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

					int obj = candidates[index];
					uint geomID = city.GeomIDOf(obj);

					Span<Vector3> points = stackalloc Vector3[SamplesPerObject];
					city.GetSamplePoints(obj, points);

					long local = state.Rays;

					for (int s = 0; s < SamplesPerObject; s++)
					{
						local++;
						if (ReachesObject(handle, rayhit, &args, origin, points[s], geomID))
						{
							visible[obj] = true;
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
		/// Whether a ray aimed at <paramref name="target"/> reaches <paramref name="geomID"/>
		/// before anything else.
		/// </summary>
		/// <remarks>
		/// Closest-hit rather than the cheaper any-hit. An occlusion ray that stops just short of
		/// the sample point has the object occlude itself, because the point sits on its own
		/// surface — measured on a thousand boxes that called a quarter of the plainly visible
		/// ones hidden.
		/// </remarks>
		private static bool ReachesObject(EmbreeScene scene, RayHit* rayhit, IntersectArguments* args, Vector3 origin, Vector3 target, uint geomID)
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

			return rayhit->Hit.GeomID == geomID;
		}
	}
}
