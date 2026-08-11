using Evergine.Bindings.Embree;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace HelloEmbree
{
	/// <summary>
	/// CPU ray tracer built on Embree: primary rays with rtcIntersect1, hard shadows with
	/// rtcOccluded1, Lambert shading, per-geometry albedo and a sky gradient background.
	/// </summary>
	internal sealed unsafe class Raytracer : IDisposable
	{
		private Device device;
		private Scene scene;

		private readonly Vector3 lightDir = Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.35f));
		private readonly Dictionary<uint, Vector3> albedoByGeomID = new();

		/// <summary>
		/// Gets the total number of triangles in the scene.
		/// </summary>
		public int TriangleCount { get; private set; }

		/// <summary>
		/// Gets the number of geometries attached to the scene.
		/// </summary>
		public int GeometryCount => this.albedoByGeomID.Count;

		public Raytracer()
		{
			this.device = Embree.NewDevice(null);
			if (this.device.IsNull)
			{
				throw new InvalidOperationException("rtcNewDevice failed");
			}

			Embree.SetDeviceErrorFunction(this.device, &OnDeviceError, null);

			this.scene = Embree.NewScene(this.device);
			this.BuildScene();
			Embree.CommitScene(this.scene);

			Error error = Embree.GetDeviceError(this.device);
			if (error != Error.None)
			{
				throw new InvalidOperationException($"Embree scene setup failed: {error}");
			}
		}

		public void Dispose()
		{
			Embree.ReleaseScene(this.scene);
			Embree.ReleaseDevice(this.device);
		}

		/// <summary>
		/// Renders the scene into an RGBA8 buffer. The camera orbits slowly with <paramref name="time"/>.
		/// </summary>
		public void Render(byte[] pixels, int width, int height, float time)
		{
			float angle = time * 0.4f;
			Vector3 eye = new Vector3(MathF.Sin(angle) * 7.0f, 3.2f, MathF.Cos(angle) * 7.0f);
			Vector3 target = new Vector3(0.0f, 0.8f, 0.0f);

			Vector3 forward = Vector3.Normalize(target - eye);
			Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
			Vector3 up = Vector3.Cross(right, forward);

			float fov = 50.0f * MathF.PI / 180.0f;
			float tanHalfFov = MathF.Tan(fov * 0.5f);
			float aspect = (float)width / height;

			Scene localScene = this.scene;

			// Embree requires RTCRayHit/RTCRay to be 16-byte aligned (its kernels use aligned
			// SIMD loads), which C# locals do not guarantee: each worker thread gets its own
			// aligned allocation.
			Parallel.For(
				0,
				height,
				() => (IntPtr)NativeMemory.AlignedAlloc((nuint)(sizeof(RayHit) + sizeof(Ray)), 64),
				(y, _, buffers) =>
				{
					RayHit* rayhit = (RayHit*)buffers;
					Ray* shadowRay = (Ray*)((byte*)buffers + sizeof(RayHit));

					IntersectArguments intersectArgs;
					Embree.InitIntersectArguments(&intersectArgs);
					OccludedArguments occludedArgs;
					Embree.InitOccludedArguments(&occludedArgs);

					int rowOffset = y * width * 4;

					for (int x = 0; x < width; x++)
					{
						float ndcX = ((x + 0.5f) / width * 2.0f - 1.0f) * tanHalfFov * aspect;
						float ndcY = (1.0f - (y + 0.5f) / height * 2.0f) * tanHalfFov;
						Vector3 dir = Vector3.Normalize(forward + right * ndcX + up * ndcY);

						Vector3 color = this.Trace(localScene, rayhit, shadowRay, &intersectArgs, &occludedArgs, eye, dir);

						int o = rowOffset + x * 4;
						pixels[o + 0] = ToSrgbByte(color.X);
						pixels[o + 1] = ToSrgbByte(color.Y);
						pixels[o + 2] = ToSrgbByte(color.Z);
						pixels[o + 3] = 255;
					}

					return buffers;
				},
				buffers => NativeMemory.AlignedFree((void*)buffers));
		}

		private Vector3 Trace(Scene localScene, RayHit* rayhit, Ray* shadowRay, IntersectArguments* intersectArgs, OccludedArguments* occludedArgs, Vector3 origin, Vector3 dir)
		{
			*rayhit = default;
			rayhit->Ray.OrgX = origin.X;
			rayhit->Ray.OrgY = origin.Y;
			rayhit->Ray.OrgZ = origin.Z;
			rayhit->Ray.DirX = dir.X;
			rayhit->Ray.DirY = dir.Y;
			rayhit->Ray.DirZ = dir.Z;
			rayhit->Ray.Tnear = 0.0f;
			rayhit->Ray.Tfar = float.PositiveInfinity;
			rayhit->Ray.Mask = uint.MaxValue;
			rayhit->Hit.GeomID = Embree.INVALID_GEOMETRY_ID;

			Embree.Intersect1(localScene, rayhit, intersectArgs);

			if (rayhit->Hit.GeomID == Embree.INVALID_GEOMETRY_ID)
			{
				// Sky gradient.
				float t = 0.5f * (dir.Y + 1.0f);
				return Vector3.Lerp(new Vector3(0.85f, 0.90f, 1.0f), new Vector3(0.25f, 0.45f, 0.85f), t);
			}

			Vector3 n = Vector3.Normalize(new Vector3(rayhit->Hit.NgX, rayhit->Hit.NgY, rayhit->Hit.NgZ));
			if (Vector3.Dot(n, dir) > 0.0f)
			{
				n = -n;
			}

			Vector3 hitPoint = origin + dir * rayhit->Ray.Tfar;
			Vector3 albedo = this.albedoByGeomID.TryGetValue(rayhit->Hit.GeomID, out var a) ? a : new Vector3(0.8f);

			// Hard shadow towards the directional light.
			Vector3 toLight = -this.lightDir;
			*shadowRay = default;
			shadowRay->OrgX = hitPoint.X;
			shadowRay->OrgY = hitPoint.Y;
			shadowRay->OrgZ = hitPoint.Z;
			shadowRay->DirX = toLight.X;
			shadowRay->DirY = toLight.Y;
			shadowRay->DirZ = toLight.Z;
			shadowRay->Tnear = 1e-3f;
			shadowRay->Tfar = float.PositiveInfinity;
			shadowRay->Mask = uint.MaxValue;

			Embree.Occluded1(localScene, shadowRay, occludedArgs);
			bool inShadow = float.IsNegativeInfinity(shadowRay->Tfar);

			float diffuse = MathF.Max(Vector3.Dot(n, toLight), 0.0f);
			float lighting = 0.18f + (inShadow ? 0.0f : 0.82f * diffuse);

			// Checkerboard on the ground plane.
			if (this.albedoByGeomID.TryGetValue(rayhit->Hit.GeomID, out _) && rayhit->Hit.GeomID == 0)
			{
				int check = ((int)MathF.Floor(hitPoint.X) + (int)MathF.Floor(hitPoint.Z)) & 1;
				albedo = check == 0 ? new Vector3(0.9f, 0.9f, 0.9f) : new Vector3(0.35f, 0.35f, 0.4f);
			}

			return albedo * lighting;
		}

		private static byte ToSrgbByte(float linear)
		{
			float srgb = MathF.Pow(Math.Clamp(linear, 0.0f, 1.0f), 1.0f / 2.2f);
			return (byte)(srgb * 255.0f + 0.5f);
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
		private static void OnDeviceError(void* userPtr, Error code, byte* message)
		{
			Console.Error.WriteLine($"[embree] {code}: {Marshal.PtrToStringUTF8((IntPtr)message)}");
		}

		// -----------------------------------------------------------------------------------
		// Scene construction
		// -----------------------------------------------------------------------------------

		private void BuildScene()
		{
			// geomID 0: ground plane (checkerboard, see Trace).
			this.AddMesh(
				new[]
				{
					new Vector3(-12, 0, -12), new Vector3(12, 0, -12),
					new Vector3(12, 0, 12), new Vector3(-12, 0, 12),
				},
				new uint[] { 0, 1, 2, 0, 2, 3 },
				new Vector3(0.9f, 0.9f, 0.9f));

			// geomID 1: cube.
			this.AddCube(center: new Vector3(-1.7f, 0.75f, 0.4f), size: 1.5f, yaw: 0.5f, albedo: new Vector3(0.85f, 0.25f, 0.2f));

			// geomID 2: icosphere.
			this.AddIcosphere(center: new Vector3(1.5f, 1.0f, -0.6f), radius: 1.0f, subdivisions: 3, albedo: new Vector3(0.2f, 0.5f, 0.9f));

			// geomID 3: small sphere.
			this.AddIcosphere(center: new Vector3(0.4f, 0.45f, 1.8f), radius: 0.45f, subdivisions: 3, albedo: new Vector3(0.95f, 0.8f, 0.25f));
		}

		private void AddCube(Vector3 center, float size, float yaw, Vector3 albedo)
		{
			float h = size * 0.5f;
			var corners = new Vector3[]
			{
				new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h),
				new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h),
			};

			var rotation = Matrix4x4.CreateRotationY(yaw);
			for (int i = 0; i < corners.Length; i++)
			{
				corners[i] = Vector3.Transform(corners[i], rotation) + center;
			}

			uint[] indices =
			{
				0, 2, 1, 0, 3, 2,   // back
				4, 5, 6, 4, 6, 7,   // front
				0, 1, 5, 0, 5, 4,   // bottom
				3, 7, 6, 3, 6, 2,   // top
				0, 4, 7, 0, 7, 3,   // left
				1, 2, 6, 1, 6, 5,   // right
			};

			this.AddMesh(corners, indices, albedo);
		}

		private void AddIcosphere(Vector3 center, float radius, int subdivisions, Vector3 albedo)
		{
			// Icosahedron base.
			float t = (1.0f + MathF.Sqrt(5.0f)) / 2.0f;
			var vertices = new List<Vector3>
			{
				new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
				new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
				new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
			};

			var faces = new List<(uint A, uint B, uint C)>
			{
				(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
				(1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
				(3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
				(4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
			};

			var midpointCache = new Dictionary<(uint, uint), uint>();

			uint Midpoint(uint a, uint b)
			{
				var key = a < b ? (a, b) : (b, a);
				if (midpointCache.TryGetValue(key, out uint cached))
				{
					return cached;
				}

				vertices.Add(Vector3.Normalize((vertices[(int)a] + vertices[(int)b]) * 0.5f));
				uint index = (uint)(vertices.Count - 1);
				midpointCache[key] = index;
				return index;
			}

			for (int s = 0; s < subdivisions; s++)
			{
				var refined = new List<(uint, uint, uint)>(faces.Count * 4);
				foreach (var (a, b, c) in faces)
				{
					uint ab = Midpoint(a, b);
					uint bc = Midpoint(b, c);
					uint ca = Midpoint(c, a);
					refined.Add((a, ab, ca));
					refined.Add((b, bc, ab));
					refined.Add((c, ca, bc));
					refined.Add((ab, bc, ca));
				}

				faces = refined;
			}

			var positions = new Vector3[vertices.Count];
			for (int i = 0; i < vertices.Count; i++)
			{
				positions[i] = Vector3.Normalize(vertices[i]) * radius + center;
			}

			var indices = new uint[faces.Count * 3];
			for (int i = 0; i < faces.Count; i++)
			{
				indices[(i * 3) + 0] = faces[i].A;
				indices[(i * 3) + 1] = faces[i].B;
				indices[(i * 3) + 2] = faces[i].C;
			}

			this.AddMesh(positions, indices, albedo);
		}

		private void AddMesh(Vector3[] positions, uint[] indices, Vector3 albedo)
		{
			Geometry geometry = Embree.NewGeometry(this.device, GeometryType.Triangle);

			float* vertexBuffer = (float*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Vertex, 0, Format.Float3, 3 * sizeof(float), (nuint)positions.Length);

			for (int i = 0; i < positions.Length; i++)
			{
				vertexBuffer[(i * 3) + 0] = positions[i].X;
				vertexBuffer[(i * 3) + 1] = positions[i].Y;
				vertexBuffer[(i * 3) + 2] = positions[i].Z;
			}

			uint* indexBuffer = (uint*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Index, 0, Format.Uint3, 3 * sizeof(uint), (nuint)(indices.Length / 3));

			for (int i = 0; i < indices.Length; i++)
			{
				indexBuffer[i] = indices[i];
			}

			Embree.CommitGeometry(geometry);
			uint geomID = Embree.AttachGeometry(this.scene, geometry);
			Embree.ReleaseGeometry(geometry);

			this.albedoByGeomID[geomID] = albedo;
			this.TriangleCount += indices.Length / 3;
		}
	}
}
