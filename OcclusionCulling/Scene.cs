using Evergine.Bindings.Embree;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OcclusionCulling
{
	/// <summary>
	/// A grid of axis-aligned boxes, one Embree geometry each so every box gets its own
	/// geomID. That identity is the whole point: occlusion culling answers "which objects do
	/// I still have to draw", which needs per-object granularity, not per-triangle.
	/// </summary>
	internal sealed unsafe class Scene : IDisposable
	{
		private Device device;

		public Scene(int sideCount, float boxSize, float spacing)
		{
			this.SideCount = sideCount;
			this.BoxCount = sideCount * sideCount * sideCount;
			this.Min = new Vector3[this.BoxCount];
			this.Max = new Vector3[this.BoxCount];

			this.device = Embree.NewDevice(null);
			if (this.device.IsNull)
			{
				throw new InvalidOperationException($"rtcNewDevice failed: {Embree.GetDeviceError(Device.Null)}");
			}

			this.Handle = Embree.NewScene(this.device);

			// The fastest traversal configuration: a high-quality static BVH, and none of
			// Dynamic, Compact or Robust, each of which trades traversal speed for something
			// this benchmark does not need.
			Embree.SetSceneFlags(this.Handle, SceneFlags.None);
			Embree.SetSceneBuildQuality(this.Handle, BuildQuality.High);

			float half = boxSize * 0.5f;
			float step = boxSize * spacing;
			float origin = -step * (sideCount - 1) * 0.5f;

			int index = 0;
			for (int z = 0; z < sideCount; z++)
			{
				for (int y = 0; y < sideCount; y++)
				{
					for (int x = 0; x < sideCount; x++)
					{
						var center = new Vector3(origin + (x * step), origin + (y * step), origin + (z * step));
						this.Min[index] = center - new Vector3(half);
						this.Max[index] = center + new Vector3(half);
						this.AddBox(this.Min[index], this.Max[index]);
						index++;
					}
				}
			}

			Embree.CommitScene(this.Handle);

			Error error = Embree.GetDeviceError(this.device);
			if (error != Error.None)
			{
				throw new InvalidOperationException($"Embree scene setup failed: {error}");
			}
		}

		/// <summary>Gets the Embree scene.</summary>
		public Evergine.Bindings.Embree.Scene Handle { get; }

		/// <summary>
		/// The widest ray packet this device actually supports.
		/// </summary>
		/// <remarks>
		/// This has to be asked, not assumed. Embree only allows rtcIntersectN/rtcOccludedN when
		/// the matching property is set, and calling a wider one anyway is undefined behaviour —
		/// in practice it corrupts the heap and takes the process down somewhere unrelated. The
		/// binaries this package ships are built with EMBREE_MAX_ISA=AVX2, which tops out at 8;
		/// 16 needs AVX-512.
		/// </remarks>
		public int MaxPacketWidth =>
			Embree.GetDeviceProperty(this.device, DeviceProperty.NativeRay16Supported) != 0 ? 16
			: Embree.GetDeviceProperty(this.device, DeviceProperty.NativeRay8Supported) != 0 ? 8
			: Embree.GetDeviceProperty(this.device, DeviceProperty.NativeRay4Supported) != 0 ? 4
			: 1;

		/// <summary>Gets the number of boxes per axis.</summary>
		public int SideCount { get; }

		/// <summary>Gets the total number of boxes, which is also the number of geomIDs.</summary>
		public int BoxCount { get; }

		/// <summary>Gets the lower corner of each box's AABB, indexed by geomID.</summary>
		public Vector3[] Min { get; }

		/// <summary>Gets the upper corner of each box's AABB, indexed by geomID.</summary>
		public Vector3[] Max { get; }

		/// <summary>Gets the total triangle count.</summary>
		public int TriangleCount => this.BoxCount * 12;

		public void Dispose()
		{
			Embree.ReleaseScene(this.Handle);
			Embree.ReleaseDevice(this.device);
		}

		/// <summary>
		/// The eight corners of a box, plus its centre. These are the sample points the
		/// per-object method shoots at.
		/// </summary>
		public void GetSamplePoints(int box, Span<Vector3> points)
		{
			Vector3 lo = this.Min[box];
			Vector3 hi = this.Max[box];

			points[0] = new Vector3(lo.X, lo.Y, lo.Z);
			points[1] = new Vector3(hi.X, lo.Y, lo.Z);
			points[2] = new Vector3(lo.X, hi.Y, lo.Z);
			points[3] = new Vector3(hi.X, hi.Y, lo.Z);
			points[4] = new Vector3(lo.X, lo.Y, hi.Z);
			points[5] = new Vector3(hi.X, lo.Y, hi.Z);
			points[6] = new Vector3(lo.X, hi.Y, hi.Z);
			points[7] = new Vector3(hi.X, hi.Y, hi.Z);
			points[8] = (lo + hi) * 0.5f;
		}

		private void AddBox(Vector3 lo, Vector3 hi)
		{
			Geometry geometry = Embree.NewGeometry(this.device, GeometryType.Triangle);

			float* vertices = (float*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Vertex, 0, Format.Float3, 3 * sizeof(float), 8);

			int v = 0;
			for (int corner = 0; corner < 8; corner++)
			{
				vertices[v++] = (corner & 1) == 0 ? lo.X : hi.X;
				vertices[v++] = (corner & 2) == 0 ? lo.Y : hi.Y;
				vertices[v++] = (corner & 4) == 0 ? lo.Z : hi.Z;
			}

			uint* indices = (uint*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Index, 0, Format.Uint3, 3 * sizeof(uint), 12);

			// Corner bit 0 is X, bit 1 is Y, bit 2 is Z, so 0..7 indexes the cube corners.
			ReadOnlySpan<uint> box = stackalloc uint[36]
			{
				0, 2, 1, 1, 2, 3, // -Z
				4, 5, 6, 5, 7, 6, // +Z
				0, 1, 4, 1, 5, 4, // -Y
				2, 6, 3, 3, 6, 7, // +Y
				0, 4, 2, 2, 4, 6, // -X
				1, 3, 5, 3, 7, 5, // +X
			};

			for (int i = 0; i < box.Length; i++)
			{
				indices[i] = box[i];
			}

			Embree.CommitGeometry(geometry);
			Embree.AttachGeometry(this.Handle, geometry);
			Embree.ReleaseGeometry(geometry);
		}
	}
}
