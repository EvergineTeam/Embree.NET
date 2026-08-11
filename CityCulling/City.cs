using Evergine.Bindings.Embree;
using System;
using System.Collections.Generic;
using System.Numerics;
using EmbreeScene = Evergine.Bindings.Embree.Scene;

namespace CityCulling
{
	/// <summary>Which mesh an object draws.</summary>
	internal enum Primitive
	{
		Box,
		Cylinder,
		Cone,
		Wedge,
	}

	/// <summary>One drawable object: a primitive, a placement, and a colour.</summary>
	internal struct CityObject
	{
		public Primitive Primitive;
		public Vector3 Centre;
		public Vector3 HalfExtent;
		public float Rotation;
		public uint Colour;
	}

	/// <summary>
	/// A city of primitives on a ground plane, and the Embree scene that mirrors it.
	/// </summary>
	/// <remarks>
	/// One Embree geometry per object, so every building has its own geomID and the occlusion
	/// pass can answer per object rather than per triangle. The triangles go in already
	/// transformed: the city never moves, so there is nothing to gain from instancing them on
	/// the Embree side, and world-space geometry keeps the culling code free of transforms.
	/// </remarks>
	internal sealed unsafe class City : IDisposable
	{
		/// <summary>geomID of the ground plane. It is in the scene so rays can be stopped by it,
		/// but it is never culled — it is always drawn.</summary>
		public const uint GroundGeomID = 0;

		private Device device;

		public City(int objectCount, float blockSize, int blocksPerSide, int seed)
		{
			this.device = Embree.NewDevice(null);
			if (this.device.IsNull)
			{
				throw new InvalidOperationException($"rtcNewDevice failed: {Embree.GetDeviceError(Device.Null)}");
			}

			this.Handle = Embree.NewScene(this.device);
			Embree.SetSceneFlags(this.Handle, SceneFlags.None);
			Embree.SetSceneBuildQuality(this.Handle, BuildQuality.High);

			this.Extent = blockSize * blocksPerSide * 0.5f;

			// Ground first, so it takes geomID 0.
			this.AddGround(this.Extent * 1.6f);

			var random = new Random(seed);
			var objects = new List<CityObject>(objectCount);
			var min = new List<Vector3>(objectCount);
			var max = new List<Vector3>(objectCount);

			// Blocks with streets between them: buildings cluster inside a block and the gaps
			// line up into corridors. At eye level those corridors are the only places you can
			// see far, which is exactly the structure that makes occlusion culling worth doing.
			float street = blockSize * 0.32f;
			float usable = blockSize - street;

			// Footprints already placed, so buildings do not grow through each other. Overlap is
			// not just ugly: two objects sharing space fight for the same pixels, and a discarded
			// one interpenetrating a kept one paints over it, which reads as a culling error in
			// the debug view when the culling was right.
			var placed = new List<(Vector3 Centre, float Radius)>(objectCount);

			for (int i = 0; i < objectCount; i++)
			{
				float footprint = 0.0f;
				float height = 0.0f;
				Vector3 centre = default;
				bool free = false;

				for (int attempt = 0; attempt < 24 && !free; attempt++)
				{
					int bx = random.Next(blocksPerSide);
					int bz = random.Next(blocksPerSide);

					float blockX = (bx - ((blocksPerSide - 1) * 0.5f)) * blockSize;
					float blockZ = (bz - ((blocksPerSide - 1) * 0.5f)) * blockSize;

					footprint = Lerp(random, blockSize * 0.10f, blockSize * 0.22f);
					height = MathF.Pow(Lerp(random, 0.0f, 1.0f), 2.2f);
					height = Lerp2(4.0f, 46.0f, height);

					centre = new Vector3(
						blockX + Lerp(random, -usable * 0.5f, usable * 0.5f),
						height * 0.5f,
						blockZ + Lerp(random, -usable * 0.5f, usable * 0.5f));

					float radius = footprint * 0.71f;
					free = true;

					foreach ((Vector3 other, float otherRadius) in placed)
					{
						float dx = other.X - centre.X;
						float dz = other.Z - centre.Z;
						if ((dx * dx) + (dz * dz) < (radius + otherRadius) * (radius + otherRadius))
						{
							free = false;
							break;
						}
					}
				}

				if (!free)
				{
					// The blocks are full. Stop rather than start stacking buildings inside one
					// another; the object count is a target, not a promise.
					break;
				}

				placed.Add((centre, footprint * 0.71f));

				var half = new Vector3(footprint * 0.5f, height * 0.5f, footprint * 0.5f);

				var primitive = (Primitive)random.Next(4);

				// A cool grey-blue palette with the occasional warm one, so the render reads as
				// a city rather than confetti.
				byte tone = (byte)random.Next(110, 210);
				uint colour = random.Next(10) == 0
					? Pack((byte)(tone + 40), (byte)(tone * 0.72f), (byte)(tone * 0.45f))
					: Pack((byte)(tone * 0.86f), (byte)(tone * 0.92f), tone);

				objects.Add(new CityObject
				{
					Primitive = primitive,
					Centre = centre,
					HalfExtent = half,
					Rotation = (float)random.NextDouble() * MathF.PI,
					Colour = colour,
				});

				// The tight bound of the rotated footprint, not the circumscribed circle. The
				// loose one puts every sample corner outside the solid, where the ray sails past
				// and hits whatever is behind — which reads as "occluded" for an object in plain
				// view.
				float c = MathF.Abs(MathF.Cos(objects[i].Rotation));
				float s2 = MathF.Abs(MathF.Sin(objects[i].Rotation));
				float ex = (c * half.X) + (s2 * half.Z);
				float ez = (s2 * half.X) + (c * half.Z);
				min.Add(new Vector3(centre.X - ex, centre.Y - half.Y, centre.Z - ez));
				max.Add(new Vector3(centre.X + ex, centre.Y + half.Y, centre.Z + ez));

				this.AddObject(objects[i]);
			}

			this.Objects = objects.ToArray();
			this.Min = min.ToArray();
			this.Max = max.ToArray();

			Embree.CommitScene(this.Handle);

			Error error = Embree.GetDeviceError(this.device);
			if (error != Error.None)
			{
				throw new InvalidOperationException($"Embree scene setup failed: {error}");
			}
		}

		public EmbreeScene Handle { get; }

		public CityObject[] Objects { get; }

		/// <summary>Lower corner of each object's world AABB, indexed the same as Objects.</summary>
		public Vector3[] Min { get; }

		/// <summary>Upper corner of each object's world AABB.</summary>
		public Vector3[] Max { get; }

		/// <summary>Half the side of the ground the city sits on.</summary>
		public float Extent { get; }

		public int Count => this.Objects.Length;

		/// <summary>
		/// geomID of an object. Index 0 is the ground, so the objects start at 1.
		/// </summary>
		public uint GeomIDOf(int index) => (uint)index + 1;

		public void Dispose()
		{
			Embree.ReleaseScene(this.Handle);
			Embree.ReleaseDevice(this.device);
		}

		/// <summary>
		/// Eight corners and the centre: the points the occlusion pass aims at.
		/// </summary>
		/// <remarks>
		/// Pulled 12% in from the AABB corners so they land inside the solid. A ray aimed exactly
		/// at a corner grazes the surface at best, and for a cylinder or a cone the corner is
		/// outside the shape altogether, so the ray misses and reports whatever stands behind.
		/// Inside the volume the ray meets the object's own front face, which is the answer the
		/// test is after.
		/// </remarks>
		public void GetSamplePoints(int index, Span<Vector3> points)
		{
			Vector3 centre = (this.Min[index] + this.Max[index]) * 0.5f;
			Vector3 lo = centre + ((this.Min[index] - centre) * 0.88f);
			Vector3 hi = centre + ((this.Max[index] - centre) * 0.88f);

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

		private static float Lerp(Random random, float min, float max) =>
			min + ((max - min) * (float)random.NextDouble());

		private static float Lerp2(float min, float max, float t) => min + ((max - min) * t);

		private static uint Pack(byte r, byte g, byte b) =>
			((uint)r << 16) | ((uint)g << 8) | b;

		private void AddGround(float extent)
		{
			Geometry geometry = Embree.NewGeometry(this.device, GeometryType.Triangle);

			float* vertices = (float*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Vertex, 0, Format.Float3, 3 * sizeof(float), 4);

			ReadOnlySpan<float> corners = stackalloc float[12]
			{
				-extent, 0, -extent,
				 extent, 0, -extent,
				 extent, 0,  extent,
				-extent, 0,  extent,
			};

			for (int i = 0; i < corners.Length; i++)
			{
				vertices[i] = corners[i];
			}

			uint* indices = (uint*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Index, 0, Format.Uint3, 3 * sizeof(uint), 2);

			indices[0] = 0; indices[1] = 2; indices[2] = 1;
			indices[3] = 0; indices[4] = 3; indices[5] = 2;

			Embree.CommitGeometry(geometry);
			Embree.AttachGeometry(this.Handle, geometry);
			Embree.ReleaseGeometry(geometry);
		}

		private void AddObject(in CityObject o)
		{
			Mesh mesh = Meshes.Get(o.Primitive);

			Geometry geometry = Embree.NewGeometry(this.device, GeometryType.Triangle);

			float* vertices = (float*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Vertex, 0, Format.Float3, 3 * sizeof(float), (nuint)mesh.Positions.Length);

			float cos = MathF.Cos(o.Rotation);
			float sin = MathF.Sin(o.Rotation);

			for (int i = 0; i < mesh.Positions.Length; i++)
			{
				Vector3 p = mesh.Positions[i] * o.HalfExtent;
				// Must match Evergine.Mathematics.Matrix4x4.CreateRotationY exactly. With the
				// row-vector convention the shader uses, that is x' = x·cos + z·sin and
				// z' = -x·sin + z·cos — the opposite sense to the textbook column-vector form.
				// Getting this backwards rotates the geometry Embree sees away from the geometry
				// the GPU draws, and the culling then discards objects that are plainly on screen.
				var world = new Vector3(
					(p.X * cos) + (p.Z * sin),
					p.Y,
					(-p.X * sin) + (p.Z * cos)) + o.Centre;

				vertices[(i * 3) + 0] = world.X;
				vertices[(i * 3) + 1] = world.Y;
				vertices[(i * 3) + 2] = world.Z;
			}

			uint* indices = (uint*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Index, 0, Format.Uint3, 3 * sizeof(uint), (nuint)(mesh.Indices.Length / 3));

			for (int i = 0; i < mesh.Indices.Length; i++)
			{
				indices[i] = mesh.Indices[i];
			}

			Embree.CommitGeometry(geometry);
			Embree.AttachGeometry(this.Handle, geometry);
			Embree.ReleaseGeometry(geometry);
		}
	}
}
