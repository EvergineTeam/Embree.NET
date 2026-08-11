using System;
using System.Collections.Generic;
using System.Numerics;

namespace CityCulling
{
	/// <summary>A unit mesh: positions in [-1, 1] on each axis, scaled per object.</summary>
	internal sealed class Mesh
	{
		public Vector3[] Positions;
		public Vector3[] Normals;
		public uint[] Indices;
	}

	/// <summary>
	/// The four primitives the city is built from, generated once and shared.
	/// </summary>
	/// <remarks>
	/// Unit-sized and centred, so one mesh serves every object of that kind: the GPU scales it
	/// with the per-instance matrix, and the Embree side bakes the same scale into world-space
	/// triangles. Faces are not shared between sides, because each needs its own normal for the
	/// flat shading to read as edges.
	/// </remarks>
	internal static class Meshes
	{
		private static readonly Dictionary<Primitive, Mesh> Cache = new();

		public static Mesh Get(Primitive primitive)
		{
			if (!Cache.TryGetValue(primitive, out Mesh mesh))
			{
				mesh = primitive switch
				{
					Primitive.Box => Box(),
					Primitive.Cylinder => Cylinder(12),
					Primitive.Cone => Cone(12),
					Primitive.Wedge => Wedge(),
					_ => Box(),
				};

				Cache[primitive] = mesh;
			}

			return mesh;
		}

		private static Mesh Box()
		{
			var positions = new List<Vector3>();
			var normals = new List<Vector3>();
			var indices = new List<uint>();

			Span<Vector3> faceNormals = stackalloc Vector3[6]
			{
				new(0, 0, -1), new(0, 0, 1), new(0, -1, 0),
				new(0, 1, 0), new(-1, 0, 0), new(1, 0, 0),
			};

			foreach (Vector3 n in faceNormals)
			{
				// Two vectors spanning the face, chosen so the winding comes out clockwise when
				// seen from outside, which is what CullBack expects.
				Vector3 u = MathF.Abs(n.Y) > 0.5f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
				Vector3 v = Vector3.Cross(n, u);

				uint b = (uint)positions.Count;
				positions.Add(n - u - v);
				positions.Add(n - u + v);
				positions.Add(n + u + v);
				positions.Add(n + u - v);

				for (int i = 0; i < 4; i++)
				{
					normals.Add(n);
				}

				indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
				indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
			}

			return Build(positions, normals, indices);
		}

		private static Mesh Cylinder(int segments)
		{
			var positions = new List<Vector3>();
			var normals = new List<Vector3>();
			var indices = new List<uint>();

			for (int i = 0; i < segments; i++)
			{
				float a0 = i / (float)segments * MathF.Tau;
				float a1 = (i + 1) / (float)segments * MathF.Tau;

				var d0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
				var d1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
				Vector3 n = Vector3.Normalize(d0 + d1);

				uint b = (uint)positions.Count;
				positions.Add(new Vector3(d0.X, -1, d0.Z));
				positions.Add(new Vector3(d0.X, 1, d0.Z));
				positions.Add(new Vector3(d1.X, 1, d1.Z));
				positions.Add(new Vector3(d1.X, -1, d1.Z));

				for (int k = 0; k < 4; k++)
				{
					normals.Add(n);
				}

				indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
				indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);

				// Cap, as a fan around the centre.
				uint c = (uint)positions.Count;
				positions.Add(new Vector3(0, 1, 0));
				positions.Add(new Vector3(d0.X, 1, d0.Z));
				positions.Add(new Vector3(d1.X, 1, d1.Z));
				normals.Add(Vector3.UnitY); normals.Add(Vector3.UnitY); normals.Add(Vector3.UnitY);
				indices.Add(c); indices.Add(c + 2); indices.Add(c + 1);
			}

			return Build(positions, normals, indices);
		}

		private static Mesh Cone(int segments)
		{
			var positions = new List<Vector3>();
			var normals = new List<Vector3>();
			var indices = new List<uint>();

			for (int i = 0; i < segments; i++)
			{
				float a0 = i / (float)segments * MathF.Tau;
				float a1 = (i + 1) / (float)segments * MathF.Tau;

				var d0 = new Vector3(MathF.Cos(a0), -1, MathF.Sin(a0));
				var d1 = new Vector3(MathF.Cos(a1), -1, MathF.Sin(a1));
				var apex = new Vector3(0, 1, 0);

				Vector3 n = Vector3.Normalize(Vector3.Cross(d1 - d0, apex - d0));

				uint b = (uint)positions.Count;
				positions.Add(d0); positions.Add(apex); positions.Add(d1);
				normals.Add(n); normals.Add(n); normals.Add(n);
				indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);

				uint c = (uint)positions.Count;
				positions.Add(new Vector3(0, -1, 0)); positions.Add(d0); positions.Add(d1);
				normals.Add(-Vector3.UnitY); normals.Add(-Vector3.UnitY); normals.Add(-Vector3.UnitY);
				indices.Add(c); indices.Add(c + 1); indices.Add(c + 2);
			}

			return Build(positions, normals, indices);
		}

		/// <summary>A box with a sloped roof — a house shape, to break up the skyline.</summary>
		private static Mesh Wedge()
		{
			var positions = new List<Vector3>();
			var normals = new List<Vector3>();
			var indices = new List<uint>();

			void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
				uint i0 = (uint)positions.Count;
				positions.Add(a); positions.Add(b); positions.Add(c); positions.Add(d);
				for (int k = 0; k < 4; k++) { normals.Add(n); }
				indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
				indices.Add(i0); indices.Add(i0 + 2); indices.Add(i0 + 3);
			}

			void Tri(Vector3 a, Vector3 b, Vector3 c)
			{
				Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
				uint i0 = (uint)positions.Count;
				positions.Add(a); positions.Add(b); positions.Add(c);
				normals.Add(n); normals.Add(n); normals.Add(n);
				indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
			}

			const float Eaves = 0.45f;
			var ridgeBack = new Vector3(0, 1, -1);
			var ridgeFront = new Vector3(0, 1, 1);

			var blb = new Vector3(-1, -1, -1); var brb = new Vector3(1, -1, -1);
			var blf = new Vector3(-1, -1, 1); var brf = new Vector3(1, -1, 1);
			var tlb = new Vector3(-1, Eaves, -1); var trb = new Vector3(1, Eaves, -1);
			var tlf = new Vector3(-1, Eaves, 1); var trf = new Vector3(1, Eaves, 1);

			Quad(brb, trb, tlb, blb);   // -Z wall
			Quad(blf, tlf, trf, brf);   // +Z wall
			Quad(blb, tlb, tlf, blf);   // -X wall
			Quad(brf, trf, trb, brb);   // +X wall
			Quad(blb, blf, brf, brb);   // floor
			Quad(tlb, ridgeBack, ridgeFront, tlf);  // -X roof slope
			Quad(trf, ridgeFront, ridgeBack, trb);  // +X roof slope
			Tri(tlb, trb, ridgeBack);   // back gable
			Tri(trf, tlf, ridgeFront);  // front gable

			return Build(positions, normals, indices);
		}

		private static Mesh Build(List<Vector3> positions, List<Vector3> normals, List<uint> indices) =>
			new()
			{
				Positions = positions.ToArray(),
				Normals = normals.ToArray(),
				Indices = indices.ToArray(),
			};
	}
}
