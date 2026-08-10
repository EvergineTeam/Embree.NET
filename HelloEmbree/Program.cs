using Evergine.Bindings.Embree;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HelloEmbree
{
	internal static unsafe class Program
	{
		private static int Main()
		{
			if (!CheckStructLayouts())
			{
				return 1;
			}

			Device device = Embree.NewDevice(null);
			if (device.IsNull)
			{
				Console.Error.WriteLine($"rtcNewDevice failed: {Embree.GetDeviceError(Device.Null)}");
				return 1;
			}

			Embree.SetDeviceErrorFunction(device, &OnDeviceError, null);
			Console.WriteLine($"Embree {Embree.VERSION_STRING} device created.");

			Scene scene = Embree.NewScene(device);
			Geometry geometry = Embree.NewGeometry(device, GeometryType.Triangle);

			// A single triangle in the z = 0 plane.
			float* vertices = (float*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Vertex, 0, Format.Float3, 3 * sizeof(float), 3);

			vertices[0] = 0.0f; vertices[1] = 0.0f; vertices[2] = 0.0f;
			vertices[3] = 1.0f; vertices[4] = 0.0f; vertices[5] = 0.0f;
			vertices[6] = 0.0f; vertices[7] = 1.0f; vertices[8] = 0.0f;

			uint* indices = (uint*)Embree.SetNewGeometryBuffer(
				geometry, BufferType.Index, 0, Format.Uint3, 3 * sizeof(uint), 1);

			indices[0] = 0; indices[1] = 1; indices[2] = 2;

			Embree.CommitGeometry(geometry);
			uint geomID = Embree.AttachGeometry(scene, geometry);
			Embree.ReleaseGeometry(geometry);
			Embree.CommitScene(scene);

			if (!CheckError(device, "scene setup"))
			{
				return 1;
			}

			Console.WriteLine($"Triangle attached as geomID {geomID}, scene committed.");

			bool ok = true;
			ok &= ExpectHit(scene, origin: (0.25f, 0.25f, -1.0f), expectedGeomID: geomID);
			ok &= ExpectMiss(scene, origin: (2.0f, 2.0f, -1.0f));
			ok &= ExpectOccluded(scene, origin: (0.25f, 0.25f, -1.0f));
			ok &= CheckError(device, "ray queries");

			Embree.ReleaseScene(scene);
			Embree.ReleaseDevice(device);

			Console.WriteLine(ok ? "All checks passed." : "FAILED.");
			return ok ? 0 : 1;
		}

		/// <summary>
		/// The vendored rtcore_config.h has to match the configuration the native binaries were
		/// built with, otherwise the struct layouts drift and every ray query reads garbage.
		/// A size mismatch here is the cheapest way to catch that.
		/// </summary>
		private static bool CheckStructLayouts()
		{
			bool ok = true;

			// Expected values are the C sizeof for Embree 4.4.1 with RTC_MAX_INSTANCE_LEVEL_COUNT=1
			// and RTC_GEOMETRY_INSTANCE_ARRAY defined. They include the tail padding that the
			// RTC_ALIGN attributes add: RTCHit has 36 bytes of fields but is 48 bytes wide.
			ok &= Expect("sizeof(Ray)", sizeof(Ray), 48);
			ok &= Expect("sizeof(Hit)", sizeof(Hit), 48);
			ok &= Expect("sizeof(RayHit)", sizeof(RayHit), sizeof(Ray) + sizeof(Hit));
			ok &= Expect("sizeof(Ray4)", sizeof(Ray4), 4 * sizeof(Ray));
			ok &= Expect("sizeof(Bounds)", sizeof(Bounds), 32);
			ok &= Expect("sizeof(LinearBounds)", sizeof(LinearBounds), 64);
			ok &= Expect("sizeof(PointQuery)", sizeof(PointQuery), 32);
			ok &= Expect("sizeof(QuaternionDecomposition)", sizeof(QuaternionDecomposition), 64);
			ok &= Expect("sizeof(IntersectArguments)", sizeof(IntersectArguments), 32);

			if (!ok)
			{
				Console.Error.WriteLine(
					"Struct layout mismatch: EmbreeGen/Headers/embree4/rtcore_config.h is out of sync " +
					"with the native binaries in Evergine.Bindings.Embree/runtimes/.");
			}

			return ok;
		}

		private static bool Expect(string what, int actual, int expected)
		{
			if (actual == expected)
			{
				return true;
			}

			Console.Error.WriteLine($"  {what} = {actual}, expected {expected}");
			return false;
		}

		private static bool ExpectHit(Scene scene, (float X, float Y, float Z) origin, uint expectedGeomID)
		{
			RayHit* rayhit = AlignedAlloc<RayHit>();

			try
			{
				*rayhit = MakeRayHit(origin);

				IntersectArguments args;
				Embree.InitIntersectArguments(&args);
				Embree.Intersect1(scene, rayhit, &args);

				if (rayhit->Hit.GeomID != expectedGeomID)
				{
					Console.Error.WriteLine($"  expected a hit on geomID {expectedGeomID}, got {rayhit->Hit.GeomID}");
					return false;
				}

				float u = rayhit->Hit.U;
				float v = rayhit->Hit.V;

				Console.WriteLine(
					$"  hit: geomID={rayhit->Hit.GeomID} primID={rayhit->Hit.PrimID} " +
					$"u={u:F3} v={v:F3} tfar={rayhit->Ray.Tfar:F3} " +
					$"Ng=({rayhit->Hit.NgX:F1}, {rayhit->Hit.NgY:F1}, {rayhit->Hit.NgZ:F1})");

				if (u < 0.0f || v < 0.0f || u + v > 1.0f)
				{
					Console.Error.WriteLine($"  barycentric coordinates out of range: u={u} v={v}");
					return false;
				}

				return true;
			}
			finally
			{
				NativeMemory.AlignedFree(rayhit);
			}
		}

		private static bool ExpectMiss(Scene scene, (float X, float Y, float Z) origin)
		{
			RayHit* rayhit = AlignedAlloc<RayHit>();

			try
			{
				*rayhit = MakeRayHit(origin);

				IntersectArguments args;
				Embree.InitIntersectArguments(&args);
				Embree.Intersect1(scene, rayhit, &args);

				if (rayhit->Hit.GeomID != Embree.INVALID_GEOMETRY_ID)
				{
					Console.Error.WriteLine($"  expected a miss, got geomID {rayhit->Hit.GeomID}");
					return false;
				}

				Console.WriteLine("  miss: geomID=INVALID_GEOMETRY_ID");
				return true;
			}
			finally
			{
				NativeMemory.AlignedFree(rayhit);
			}
		}

		private static bool ExpectOccluded(Scene scene, (float X, float Y, float Z) origin)
		{
			Ray* ray = AlignedAlloc<Ray>();

			try
			{
				*ray = MakeRay(origin);

				OccludedArguments args;
				Embree.InitOccludedArguments(&args);
				Embree.Occluded1(scene, ray, &args);

				// rtcOccluded1 signals occlusion by setting tfar to -infinity.
				if (!float.IsNegativeInfinity(ray->Tfar))
				{
					Console.Error.WriteLine($"  expected an occluded ray, tfar is {ray->Tfar}");
					return false;
				}

				Console.WriteLine("  occluded: tfar=-inf");
				return true;
			}
			finally
			{
				NativeMemory.AlignedFree(ray);
			}
		}

		/// <summary>
		/// Allocates a single ray structure with the alignment Embree requires.
		/// </summary>
		/// <remarks>
		/// This is not optional. Embree's traversal kernels use aligned SIMD loads on the ray
		/// structures, and a C# local or array element carries no alignment guarantee beyond the
		/// pointer size, so passing <c>&amp;someLocal</c> crashes on some code paths and silently
		/// works on others. RTCRay/RTCRayHit need 16 bytes, RTCRay8 needs 32 and RTCRay16 needs 64;
		/// the C alignment of every generated struct is recorded above its declaration in
		/// Generated/Structs.cs.
		/// </remarks>
		private static T* AlignedAlloc<T>()
			where T : unmanaged
		{
			return (T*)NativeMemory.AlignedAlloc((nuint)sizeof(T), 64);
		}

		private static Ray MakeRay((float X, float Y, float Z) origin)
		{
			Ray ray = default;
			ray.OrgX = origin.X;
			ray.OrgY = origin.Y;
			ray.OrgZ = origin.Z;
			ray.DirX = 0.0f;
			ray.DirY = 0.0f;
			ray.DirZ = 1.0f;
			ray.Tnear = 0.0f;
			ray.Tfar = float.PositiveInfinity;
			ray.Mask = uint.MaxValue;
			ray.Flags = 0;
			return ray;
		}

		private static RayHit MakeRayHit((float X, float Y, float Z) origin)
		{
			RayHit rayhit = default;
			rayhit.Ray = MakeRay(origin);
			rayhit.Hit.GeomID = Embree.INVALID_GEOMETRY_ID;
			rayhit.Hit.PrimID = Embree.INVALID_GEOMETRY_ID;
			return rayhit;
		}

		private static bool CheckError(Device device, string stage)
		{
			Error error = Embree.GetDeviceError(device);
			if (error == Error.None)
			{
				return true;
			}

			Console.Error.WriteLine($"  Embree error after {stage}: {error}");
			return false;
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
		private static void OnDeviceError(void* userPtr, Error code, byte* message)
		{
			Console.Error.WriteLine($"  [embree] {code}: {Utf8ToString(message)}");
		}

		private static string Utf8ToString(byte* text)
		{
			if (text == null)
			{
				return string.Empty;
			}

			int length = 0;
			while (text[length] != 0)
			{
				length++;
			}

			return Encoding.UTF8.GetString(text, length);
		}
	}
}
