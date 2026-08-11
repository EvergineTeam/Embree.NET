using System;
using System.Runtime.CompilerServices;

namespace Evergine.Bindings.Embree
{
	/// <summary>
	/// Hand-written counterparts of the Embree API entry points that the headers declare
	/// <c>RTC_FORCEINLINE</c>. Those are compiled into the caller, so the shared library exports
	/// no symbol for them and the generator skips them (see EmbreeGen/CsCodeGenerator.cs,
	/// InlineOnlyFunctions).
	/// </summary>
	public static unsafe partial class Embree
	{
		/// <summary>
		/// Initializes a ray query context.
		/// </summary>
		public static void InitRayQueryContext(RayQueryContext* context)
		{
			for (uint l = 0; l < MAX_INSTANCE_LEVEL_COUNT; ++l)
			{
				context->InstID[l] = INVALID_GEOMETRY_ID;
				context->InstPrimID[l] = INVALID_GEOMETRY_ID;
			}
		}

		/// <summary>
		/// Initializes a point query context.
		/// </summary>
		public static void InitPointQueryContext(PointQueryContext* context)
		{
			context->InstStackSize = 0;

			for (uint l = 0; l < MAX_INSTANCE_LEVEL_COUNT; ++l)
			{
				context->InstID[l] = INVALID_GEOMETRY_ID;
				context->InstPrimID[l] = INVALID_GEOMETRY_ID;
			}
		}

		/// <summary>
		/// Initializes the additional arguments of an rtcIntersect1/4/8/16 call.
		/// </summary>
		public static void InitIntersectArguments(IntersectArguments* args)
		{
			args->Flags = RayQueryFlags.Incoherent;
			args->FeatureMask = FeatureFlags.All;
			args->Context = null;
			args->Filter = null;
			args->Intersect = null;
		}

		/// <summary>
		/// Initializes the additional arguments of an rtcOccluded1/4/8/16 call.
		/// </summary>
		public static void InitOccludedArguments(OccludedArguments* args)
		{
			args->Flags = RayQueryFlags.Incoherent;
			args->FeatureMask = FeatureFlags.All;
			args->Context = null;
			args->Filter = null;
			args->Occluded = null;
		}

		/// <summary>
		/// Returns the default BVH build settings.
		/// </summary>
		public static BuildArguments DefaultBuildArguments()
		{
			BuildArguments args = default;
			args.ByteSize = (nuint)sizeof(BuildArguments);
			args.BuildQuality = BuildQuality.Medium;
			args.BuildFlags = BuildFlags.None;
			args.MaxBranchingFactor = 2;
			args.MaxDepth = 32;
			args.SahBlockSize = 1;
			args.MinLeafSize = 1;
			args.MaxLeafSize = (uint)BuildConstants.BuildMaxPrimitivesPerLeaf;
			args.TraversalCost = 1.0f;
			args.IntersectionCost = 1.0f;
			return args;
		}

		/// <summary>
		/// Initializes a quaternion decomposition to the identity transform.
		/// </summary>
		public static void InitQuaternionDecomposition(QuaternionDecomposition* qdecomp)
		{
			qdecomp->ScaleX = 1.0f;
			qdecomp->ScaleY = 1.0f;
			qdecomp->ScaleZ = 1.0f;
			qdecomp->SkewXy = 0.0f;
			qdecomp->SkewXz = 0.0f;
			qdecomp->SkewYz = 0.0f;
			qdecomp->ShiftX = 0.0f;
			qdecomp->ShiftY = 0.0f;
			qdecomp->ShiftZ = 0.0f;
			qdecomp->QuaternionR = 1.0f;
			qdecomp->QuaternionI = 0.0f;
			qdecomp->QuaternionJ = 0.0f;
			qdecomp->QuaternionK = 0.0f;
			qdecomp->TranslationX = 0.0f;
			qdecomp->TranslationY = 0.0f;
			qdecomp->TranslationZ = 0.0f;
		}

		public static void QuaternionDecompositionSetQuaternion(QuaternionDecomposition* qdecomp, float r, float i, float j, float k)
		{
			qdecomp->QuaternionR = r;
			qdecomp->QuaternionI = i;
			qdecomp->QuaternionJ = j;
			qdecomp->QuaternionK = k;
		}

		public static void QuaternionDecompositionSetScale(QuaternionDecomposition* qdecomp, float scaleX, float scaleY, float scaleZ)
		{
			qdecomp->ScaleX = scaleX;
			qdecomp->ScaleY = scaleY;
			qdecomp->ScaleZ = scaleZ;
		}

		public static void QuaternionDecompositionSetSkew(QuaternionDecomposition* qdecomp, float skewXy, float skewXz, float skewYz)
		{
			qdecomp->SkewXy = skewXy;
			qdecomp->SkewXz = skewXz;
			qdecomp->SkewYz = skewYz;
		}

		public static void QuaternionDecompositionSetShift(QuaternionDecomposition* qdecomp, float shiftX, float shiftY, float shiftZ)
		{
			qdecomp->ShiftX = shiftX;
			qdecomp->ShiftY = shiftY;
			qdecomp->ShiftZ = shiftZ;
		}

		public static void QuaternionDecompositionSetTranslation(QuaternionDecomposition* qdecomp, float translationX, float translationY, float translationZ)
		{
			qdecomp->TranslationX = translationX;
			qdecomp->TranslationY = translationY;
			qdecomp->TranslationZ = translationZ;
		}

		/// <summary>
		/// Interpolates vertex data to some u/v location.
		/// </summary>
		public static void Interpolate0(Geometry geometry, uint primID, float u, float v, BufferType bufferType, uint bufferSlot, float* p, uint valueCount)
		{
			InterpolateArguments args = default;
			args.Geometry = geometry;
			args.PrimID = primID;
			args.U = u;
			args.V = v;
			args.BufferType = bufferType;
			args.BufferSlot = bufferSlot;
			args.P = p;
			args.ValueCount = valueCount;
			Interpolate(&args);
		}

		/// <summary>
		/// Interpolates vertex data to some u/v location and calculates first order derivatives.
		/// </summary>
		public static void Interpolate1(Geometry geometry, uint primID, float u, float v, BufferType bufferType, uint bufferSlot, float* p, float* dPdu, float* dPdv, uint valueCount)
		{
			InterpolateArguments args = default;
			args.Geometry = geometry;
			args.PrimID = primID;
			args.U = u;
			args.V = v;
			args.BufferType = bufferType;
			args.BufferSlot = bufferSlot;
			args.P = p;
			args.DPdu = dPdu;
			args.DPdv = dPdv;
			args.ValueCount = valueCount;
			Interpolate(&args);
		}

		/// <summary>
		/// Interpolates vertex data to some u/v location and calculates first and second order derivatives.
		/// </summary>
		public static void Interpolate2(Geometry geometry, uint primID, float u, float v, BufferType bufferType, uint bufferSlot, float* p, float* dPdu, float* dPdv, float* ddPdudu, float* ddPdvdv, float* ddPdudv, uint valueCount)
		{
			InterpolateArguments args = default;
			args.Geometry = geometry;
			args.PrimID = primID;
			args.U = u;
			args.V = v;
			args.BufferType = bufferType;
			args.BufferSlot = bufferSlot;
			args.P = p;
			args.DPdu = dPdu;
			args.DPdv = dPdv;
			args.DdPdudu = ddPdudu;
			args.DdPdvdv = ddPdvdv;
			args.DdPdudv = ddPdudv;
			args.ValueCount = valueCount;
			Interpolate(&args);
		}

		// ---------------------------------------------------------------------------------------
		// SoA accessors for the ray/hit packets handed to filter and user-geometry callbacks.
		//
		// RTCRayN / RTCHitN / RTCRayHitN are opaque in the C API: the packet width N is only known
		// at run time, so the members are addressed as strided lanes over the raw pointer. These
		// mirror the RTCRayN_* / RTCHitN_* helpers from the C++ section of rtcore_ray.h.
		// ---------------------------------------------------------------------------------------

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_org_x(void* ray, uint n, uint i) => ref ((float*)ray)[(0 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_org_y(void* ray, uint n, uint i) => ref ((float*)ray)[(1 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_org_z(void* ray, uint n, uint i) => ref ((float*)ray)[(2 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_tnear(void* ray, uint n, uint i) => ref ((float*)ray)[(3 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_dir_x(void* ray, uint n, uint i) => ref ((float*)ray)[(4 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_dir_y(void* ray, uint n, uint i) => ref ((float*)ray)[(5 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_dir_z(void* ray, uint n, uint i) => ref ((float*)ray)[(6 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_time(void* ray, uint n, uint i) => ref ((float*)ray)[(7 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float RayN_tfar(void* ray, uint n, uint i) => ref ((float*)ray)[(8 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint RayN_mask(void* ray, uint n, uint i) => ref ((uint*)ray)[(9 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint RayN_id(void* ray, uint n, uint i) => ref ((uint*)ray)[(10 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint RayN_flags(void* ray, uint n, uint i) => ref ((uint*)ray)[(11 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float HitN_Ng_x(void* hit, uint n, uint i) => ref ((float*)hit)[(0 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float HitN_Ng_y(void* hit, uint n, uint i) => ref ((float*)hit)[(1 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float HitN_Ng_z(void* hit, uint n, uint i) => ref ((float*)hit)[(2 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float HitN_u(void* hit, uint n, uint i) => ref ((float*)hit)[(3 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref float HitN_v(void* hit, uint n, uint i) => ref ((float*)hit)[(4 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint HitN_primID(void* hit, uint n, uint i) => ref ((uint*)hit)[(5 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint HitN_geomID(void* hit, uint n, uint i) => ref ((uint*)hit)[(6 * n) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint HitN_instID(void* hit, uint n, uint i, uint l) => ref ((uint*)hit)[(7 * n) + (n * l) + i];

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref uint HitN_instPrimID(void* hit, uint n, uint i, uint l) => ref ((uint*)hit)[(7 * n) + (n * MAX_INSTANCE_LEVEL_COUNT) + (n * l) + i];

		/// <summary>
		/// Returns the ray part of a combined ray/hit packet.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void* RayHitN_RayN(void* rayhit, uint n) => &((float*)rayhit)[0 * n];

		/// <summary>
		/// Returns the hit part of a combined ray/hit packet.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void* RayHitN_HitN(void* rayhit, uint n) => &((float*)rayhit)[12 * n];
	}
}
