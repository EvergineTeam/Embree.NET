using System;
using System.Numerics;

namespace OcclusionCulling
{
	/// <summary>
	/// A pinhole camera plus the six frustum planes derived from it.
	/// </summary>
	/// <remarks>
	/// The AABB test is the same scalar six-plane centre/half-extent form Evergine's
	/// <c>BoundingFrustum.Intersects(ref BoundingBox, out bool)</c> uses, reproduced here so the
	/// sample stays free of engine dependencies and runs on every RID the binding ships.
	/// </remarks>
	internal sealed class Camera
	{
		private readonly Vector4[] planes = new Vector4[6];

		public Camera(Vector3 position, Vector3 target, float fovDegrees, float aspect, float near, float far)
		{
			this.Position = position;
			this.Forward = Vector3.Normalize(target - position);
			this.Right = Vector3.Normalize(Vector3.Cross(this.Forward, Vector3.UnitY));
			this.Up = Vector3.Cross(this.Right, this.Forward);

			this.TanHalfFov = MathF.Tan(fovDegrees * MathF.PI / 180.0f * 0.5f);
			this.Aspect = aspect;

			var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(
				fovDegrees * MathF.PI / 180.0f, aspect, near, far);
			this.ExtractPlanes(view * projection);
		}

		public Vector3 Position { get; }

		public Vector3 Forward { get; }

		public Vector3 Right { get; }

		public Vector3 Up { get; }

		public float TanHalfFov { get; }

		public float Aspect { get; }

		/// <summary>
		/// Ray direction through a normalised screen position, both in [0, 1].
		/// </summary>
		public Vector3 RayDirection(float u, float v)
		{
			float x = ((u * 2.0f) - 1.0f) * this.TanHalfFov * this.Aspect;
			float y = (1.0f - (v * 2.0f)) * this.TanHalfFov;
			return Vector3.Normalize(this.Forward + (this.Right * x) + (this.Up * y));
		}

		/// <summary>
		/// Tests an AABB against the six planes. Conservative: a box straddling a plane counts
		/// as inside.
		/// </summary>
		public bool Intersects(in Vector3 min, in Vector3 max)
		{
			Vector3 centre = (min + max) * 0.5f;
			Vector3 extent = (max - min) * 0.5f;

			for (int i = 0; i < 6; i++)
			{
				Vector4 p = this.planes[i];
				var normal = new Vector3(p.X, p.Y, p.Z);

				float distance = Vector3.Dot(normal, centre) + p.W;
				float radius = Vector3.Dot(Vector3.Abs(normal), extent);

				if (distance + radius < 0.0f)
				{
					return false;
				}
			}

			return true;
		}

		private void ExtractPlanes(Matrix4x4 m)
		{
			this.planes[0] = Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)); // left
			this.planes[1] = Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)); // right
			this.planes[2] = Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)); // bottom
			this.planes[3] = Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)); // top
			this.planes[4] = Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43));                                 // near
			this.planes[5] = Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)); // far
		}

		private static Vector4 Normalize(Vector4 plane)
		{
			float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
			return length > 0.0f ? plane / length : plane;
		}
	}
}
