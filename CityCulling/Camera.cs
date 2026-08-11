using System;
using System.Numerics;

namespace CityCulling
{
	/// <summary>
	/// The camera, in two flavours of maths on purpose.
	/// </summary>
	/// <remarks>
	/// The frustum planes come from a <see cref="System.Numerics"/> projection, because culling
	/// only needs the six planes and does not care about the depth convention. The matrix that
	/// goes to the GPU is built separately in the renderer with Evergine.Mathematics and
	/// <c>reverseDepthBuffer: true</c>, because Evergine's depth is reversed. Keeping the two
	/// apart avoids converting vector types on the hot path, and the two describe the same view
	/// volume either way.
	/// </remarks>
	internal sealed class Camera
	{
		private readonly Vector4[] planes = new Vector4[6];

		public Camera(Vector3 position, Vector3 target, float fovDegrees, float aspect, float near, float far)
		{
			this.Position = position;
			this.Target = target;
			this.FovDegrees = fovDegrees;
			this.Aspect = aspect;
			this.Near = near;
			this.Far = far;

			var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(
				fovDegrees * MathF.PI / 180.0f, aspect, near, far);

			this.ExtractPlanes(view * projection);
		}

		public Vector3 Position { get; }

		public Vector3 Target { get; }

		public float FovDegrees { get; }

		public float Aspect { get; }

		public float Near { get; }

		public float Far { get; }

		/// <summary>Conservative AABB test: a box straddling a plane counts as inside.</summary>
		public bool Intersects(in Vector3 min, in Vector3 max)
		{
			Vector3 centre = (min + max) * 0.5f;
			Vector3 extent = (max - min) * 0.5f;

			for (int i = 0; i < 6; i++)
			{
				Vector4 p = this.planes[i];
				var normal = new Vector3(p.X, p.Y, p.Z);

				if (Vector3.Dot(normal, centre) + p.W + Vector3.Dot(Vector3.Abs(normal), extent) < 0.0f)
				{
					return false;
				}
			}

			return true;
		}

		private void ExtractPlanes(Matrix4x4 m)
		{
			this.planes[0] = Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41));
			this.planes[1] = Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41));
			this.planes[2] = Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42));
			this.planes[3] = Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42));
			this.planes[4] = Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43));
			this.planes[5] = Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43));
		}

		private static Vector4 Normalize(Vector4 plane)
		{
			float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
			return length > 0.0f ? plane / length : plane;
		}
	}
}
