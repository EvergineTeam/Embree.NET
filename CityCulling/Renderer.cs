using Evergine.Common.Graphics;
using Evergine.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Buffer = Evergine.Common.Graphics.Buffer;

namespace CityCulling
{
	/// <summary>Per-instance data: a world matrix and a colour.</summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct InstanceData
	{
		public Matrix4x4 World;
		public Vector4 Colour;
	}

	/// <summary>
	/// Draws the city, one draw call per visible object.
	/// </summary>
	/// <remarks>
	/// One call each rather than instancing the lot, because that is what makes the culling
	/// visible in the numbers: the frame goes from a thousand draw calls to whatever survives.
	/// Per-object data still arrives through a per-instance vertex buffer, selected with
	/// <c>startInstanceLocation</c>. Updating a constant buffer between draws would be the
	/// obvious alternative and it is illegal: the validation layer rejects buffer updates inside
	/// a render pass, and while DX11 lets it through, Vulkan and DX12 do not.
	/// </remarks>
	internal sealed unsafe class Renderer : IDisposable
	{
		private const string ShaderSource = @"
cbuffer Frame : register(b0)
{
	float4x4 ViewProjection;
	float4   LightDirection;
};

struct VS_IN
{
	float3 position : POSITION;
	float3 normal   : NORMAL;
	float4 world0   : TEXCOORD0;
	float4 world1   : TEXCOORD1;
	float4 world2   : TEXCOORD2;
	float4 world3   : TEXCOORD3;
	float4 colour   : TEXCOORD4;
};

struct PS_IN
{
	float4 position : SV_POSITION;
	float3 normal   : NORMAL;
	float4 colour   : COLOR;
};

PS_IN VS(VS_IN input)
{
	float4x4 world = float4x4(input.world0, input.world1, input.world2, input.world3);

	PS_IN output;
	float4 worldPosition = mul(float4(input.position, 1.0), world);
	output.position = mul(worldPosition, ViewProjection);
	output.normal = normalize(mul(float4(input.normal, 0.0), world).xyz);
	output.colour = input.colour;
	return output;
}

float4 PS(PS_IN input) : SV_Target
{
	// Flat directional term plus ambient. Only so the city reads as solid volumes; the
	// culling measurement is entirely on the CPU side and none of this touches it.
	float ndotl = saturate(dot(normalize(input.normal), -LightDirection.xyz));
	float shade = 0.35 + (0.65 * ndotl);
	return float4(input.colour.rgb * shade, 1.0);
}
";

		private readonly GraphicsContext graphicsContext;
		private readonly Dictionary<Primitive, (uint StartIndex, uint IndexCount, uint BaseVertex)> ranges = new();

		private Buffer vertexBuffer;
		private Buffer indexBuffer;
		private Buffer instanceBuffer;
		private Buffer frameBuffer;
		private Buffer[] vertexBuffers;
		private GraphicsPipelineState pipelineState;
		private ResourceSet resourceSet;
		private InstanceData[] instances;

		public Renderer(GraphicsContext graphicsContext, City city, FrameBuffer target)
		{
			this.graphicsContext = graphicsContext;
			this.instances = new InstanceData[city.Count + 16];

			this.BuildGeometry(city);
			this.BuildPipeline(target);
		}

		/// <summary>Index of the ground's slot in the shared vertex and index buffers.</summary>
		private (uint StartIndex, uint IndexCount, uint BaseVertex) groundRange;

		public void Dispose()
		{
			this.vertexBuffer?.Dispose();
			this.indexBuffer?.Dispose();
			this.instanceBuffer?.Dispose();
			this.frameBuffer?.Dispose();
		}

		/// <summary>
		/// Fills the instance buffer and issues the draws. <paramref name="visible"/> selects the
		/// objects; when <paramref name="drawCulled"/> is set, the discarded ones are drawn too,
		/// tinted red, which is what makes the culling's mistakes visible.
		/// </summary>
		public int Draw(CommandBuffer commandBuffer, City city, Matrix4x4 viewProjection, Vector3 lightDirection, bool[] visible, bool drawCulled, List<(Matrix4x4 World, Vector4 Colour)> markers = null)
		{
			int slot = 0;

			// The ground always goes in: it is in the Embree scene so rays can stop on it, but it
			// is never a culling candidate.
			this.instances[slot++] = new InstanceData
			{
				World = Matrix4x4.CreateScale(city.Extent * 1.6f, 1.0f, city.Extent * 1.6f),
				Colour = new Vector4(0.20f, 0.21f, 0.24f, 1.0f),
			};

			var drawList = new List<(Primitive Primitive, int Slot)>(city.Count);

			for (int i = 0; i < city.Count; i++)
			{
				bool kept = visible[i];
				if (!kept && !drawCulled)
				{
					continue;
				}

				ref CityObject o = ref city.Objects[i];

				Matrix4x4 world =
					Matrix4x4.CreateScale(o.HalfExtent.X, o.HalfExtent.Y, o.HalfExtent.Z) *
					Matrix4x4.CreateRotationY(o.Rotation) *
					Matrix4x4.CreateTranslation(o.Centre.X, o.Centre.Y, o.Centre.Z);

				Vector4 colour = kept
					? new Vector4(
						((o.Colour >> 16) & 0xFF) / 255.0f,
						((o.Colour >> 8) & 0xFF) / 255.0f,
						(o.Colour & 0xFF) / 255.0f,
						1.0f)
					: new Vector4(0.85f, 0.16f, 0.16f, 1.0f);

				this.instances[slot] = new InstanceData { World = world, Colour = colour };
				drawList.Add((o.Primitive, slot));
				slot++;
			}

			// Markers for the diagnostic views: the camera and its frustum edges, drawn as boxes.
			int markerStart = slot;
			if (markers != null)
			{
				foreach ((Matrix4x4 world, Vector4 colour) in markers)
				{
					this.instances[slot++] = new InstanceData { World = world, Colour = colour };
				}
			}

			// Written before the render pass opens, which is the rule the validation layer
			// enforces and the reason the per-object data rides in a vertex buffer.
			MappedResource mapped = this.graphicsContext.MapMemory(this.instanceBuffer, MapMode.Write);
			fixed (InstanceData* source = this.instances)
			{
				System.Buffer.MemoryCopy(source, (void*)mapped.Data, (long)slot * sizeof(InstanceData), (long)slot * sizeof(InstanceData));
			}

			this.graphicsContext.UnmapMemory(this.instanceBuffer);

			var frame = new FrameConstants { ViewProjection = viewProjection, LightDirection = new Vector4(lightDirection, 0.0f) };
			commandBuffer.UpdateBufferData(this.frameBuffer, ref frame);

			var renderPass = new RenderPassDescription(this.Target, ClearValue.Default);
			commandBuffer.BeginRenderPass(ref renderPass);
			commandBuffer.SetGraphicsPipelineState(this.pipelineState);
			commandBuffer.SetResourceSet(this.resourceSet);
			commandBuffer.SetVertexBuffers(this.vertexBuffers);
			commandBuffer.SetIndexBuffer(this.indexBuffer, IndexFormat.UInt32);

			commandBuffer.DrawIndexedInstanced(this.groundRange.IndexCount, 1, this.groundRange.StartIndex, this.groundRange.BaseVertex, 0);

			foreach ((Primitive primitive, int instanceSlot) in drawList)
			{
				var range = this.ranges[primitive];
				commandBuffer.DrawIndexedInstanced(range.IndexCount, 1, range.StartIndex, range.BaseVertex, (uint)instanceSlot);
			}

			if (markers != null)
			{
				var box = this.ranges[Primitive.Box];
				for (int i = 0; i < markers.Count; i++)
				{
					commandBuffer.DrawIndexedInstanced(box.IndexCount, 1, box.StartIndex, box.BaseVertex, (uint)(markerStart + i));
				}
			}

			commandBuffer.EndRenderPass();

			return drawList.Count + 1;
		}

		/// <summary>The framebuffer this renderer's pipeline was built for.</summary>
		public FrameBuffer Target { get; private set; }

		[StructLayout(LayoutKind.Sequential)]
		private struct FrameConstants
		{
			public Matrix4x4 ViewProjection;
			public Vector4 LightDirection;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct VertexPositionNormal
		{
			public Vector3 Position;
			public Vector3 Normal;
		}

		private void BuildGeometry(City city)
		{
			var vertices = new List<VertexPositionNormal>();
			var indices = new List<uint>();

			// A unit quad for the ground, then one copy of each primitive. Everything shares one
			// vertex and one index buffer; a draw picks its mesh with startIndex/baseVertex.
			this.groundRange = Append(
				vertices,
				indices,
				new[]
				{
					new VertexPositionNormal { Position = new Vector3(-1, 0, -1), Normal = Vector3.Up },
					new VertexPositionNormal { Position = new Vector3(1, 0, -1), Normal = Vector3.Up },
					new VertexPositionNormal { Position = new Vector3(1, 0, 1), Normal = Vector3.Up },
					new VertexPositionNormal { Position = new Vector3(-1, 0, 1), Normal = Vector3.Up },
				},
				new uint[] { 0, 1, 2, 0, 2, 3 });

			foreach (Primitive primitive in Enum.GetValues<Primitive>())
			{
				Mesh mesh = Meshes.Get(primitive);
				var meshVertices = new VertexPositionNormal[mesh.Positions.Length];
				for (int i = 0; i < mesh.Positions.Length; i++)
				{
					meshVertices[i] = new VertexPositionNormal
					{
						Position = new Vector3(mesh.Positions[i].X, mesh.Positions[i].Y, mesh.Positions[i].Z),
						Normal = new Vector3(mesh.Normals[i].X, mesh.Normals[i].Y, mesh.Normals[i].Z),
					};
				}

				this.ranges[primitive] = Append(vertices, indices, meshVertices, mesh.Indices);
			}

			var vertexArray = vertices.ToArray();
			var indexArray = indices.ToArray();

			var vertexDescription = new BufferDescription(
				(uint)(sizeof(VertexPositionNormal) * vertexArray.Length), BufferFlags.VertexBuffer, ResourceUsage.Default);
			this.vertexBuffer = this.graphicsContext.Factory.CreateBuffer(vertexArray, ref vertexDescription);

			var indexDescription = new BufferDescription(
				(uint)(sizeof(uint) * indexArray.Length), BufferFlags.IndexBuffer, ResourceUsage.Default);
			this.indexBuffer = this.graphicsContext.Factory.CreateBuffer(indexArray, ref indexDescription);

			var instanceDescription = new BufferDescription(
				(uint)(sizeof(InstanceData) * this.instances.Length), BufferFlags.VertexBuffer, ResourceUsage.Dynamic, ResourceCpuAccess.Write);
			this.instanceBuffer = this.graphicsContext.Factory.CreateBuffer(ref instanceDescription);

			this.vertexBuffers = new[] { this.vertexBuffer, this.instanceBuffer };
		}

		private static (uint StartIndex, uint IndexCount, uint BaseVertex) Append(
			List<VertexPositionNormal> vertices, List<uint> indices, VertexPositionNormal[] meshVertices, uint[] meshIndices)
		{
			uint baseVertex = (uint)vertices.Count;
			uint startIndex = (uint)indices.Count;

			vertices.AddRange(meshVertices);
			indices.AddRange(meshIndices);

			return (startIndex, (uint)meshIndices.Length, baseVertex);
		}

		private void BuildPipeline(FrameBuffer target)
		{
			this.Target = target;

			var vertexShaderDescription = new ShaderDescription(
				ShaderStages.Vertex, "VS", this.graphicsContext.ShaderCompile(ShaderSource, "VS", ShaderStages.Vertex).ByteCode);
			var pixelShaderDescription = new ShaderDescription(
				ShaderStages.Pixel, "PS", this.graphicsContext.ShaderCompile(ShaderSource, "PS", ShaderStages.Pixel).ByteCode);

			var vertexShader = this.graphicsContext.Factory.CreateShader(ref vertexShaderDescription);
			var pixelShader = this.graphicsContext.Factory.CreateShader(ref pixelShaderDescription);

			var frameDescription = new BufferDescription((uint)sizeof(FrameConstants), BufferFlags.ConstantBuffer, ResourceUsage.Default);
			this.frameBuffer = this.graphicsContext.Factory.CreateBuffer(ref frameDescription);

			var layoutDescription = new ResourceLayoutDescription(
				new LayoutElementDescription(0, ResourceType.ConstantBuffer, ShaderStages.Vertex | ShaderStages.Pixel));
			var resourceLayout = this.graphicsContext.Factory.CreateResourceLayout(ref layoutDescription);

			var resourceSetDescription = new ResourceSetDescription(resourceLayout, this.frameBuffer);
			this.resourceSet = this.graphicsContext.Factory.CreateResourceSet(ref resourceSetDescription);

			var layouts = new InputLayouts()
				.Add(new LayoutDescription()
					.Add(new ElementDescription(ElementFormat.Float3, ElementSemanticType.Position))
					.Add(new ElementDescription(ElementFormat.Float3, ElementSemanticType.Normal)))
				.Add(new LayoutDescription(VertexStepFunction.PerInstanceData, 1)
					.Add(new ElementDescription(ElementFormat.Float4, ElementSemanticType.TexCoord, 0))
					.Add(new ElementDescription(ElementFormat.Float4, ElementSemanticType.TexCoord, 1))
					.Add(new ElementDescription(ElementFormat.Float4, ElementSemanticType.TexCoord, 2))
					.Add(new ElementDescription(ElementFormat.Float4, ElementSemanticType.TexCoord, 3))
					.Add(new ElementDescription(ElementFormat.Float4, ElementSemanticType.TexCoord, 4)));

			var pipelineDescription = new GraphicsPipelineDescription()
			{
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				InputLayouts = layouts,
				ResourceLayouts = new[] { resourceLayout },
				Shaders = new GraphicsShaderStateDescription
				{
					VertexShader = vertexShader,
					PixelShader = pixelShader,
				},
				RenderStates = new RenderStateDescription
				{
					RasterizerState = RasterizerStates.CullBack,
					BlendState = BlendStates.Opaque,

					// Evergine's depth is reversed: ReadWrite compares GreaterEqual and
					// ClearValue.Default clears depth to 0. The projection must be built with
					// reverseDepthBuffer: true to match, or nothing draws.
					DepthStencilState = DepthStencilStates.ReadWrite,
				},
				Outputs = target.OutputDescription,
			};

			this.pipelineState = this.graphicsContext.Factory.CreateGraphicsPipeline(ref pipelineDescription);
		}
	}
}
