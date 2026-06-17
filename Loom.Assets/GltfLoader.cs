using System.Numerics;

using Loom.Core;

using SharpGLTF.Schema2;

using GltfMaterial = SharpGLTF.Schema2.Material;

namespace Loom.Assets;

/// <summary>A material referenced by a loaded primitive — its decoded textures, ready for <c>IEngine.CreateTexture</c>.</summary>
public sealed class ModelMaterial
{
	/// <summary>Base-colour / albedo texture (sRGB), or null if the material has none.</summary>
	public TextureData? BaseColor { get; init; }

	/// <summary>Tangent-space normal map (linear), or null if the material has none.</summary>
	public TextureData? Normal { get; init; }
}

/// <summary>One drawable primitive of a loaded model: geometry in Loom's canonical <see cref="Vertex"/> layout plus its material.</summary>
public sealed class ModelPrimitive
{
	/// <summary>Vertices in Loom's interleaved layout (position/normal/uv/tangent); tangents are taken from the
	/// glTF <c>TANGENT</c> accessor or computed when absent.</summary>
	public required Vertex[] Vertices { get; init; }

	/// <summary>Triangle-list indices.</summary>
	public required uint[] Indices { get; init; }

	/// <summary>The primitive's material (decoded textures), or null if it has none.</summary>
	public ModelMaterial? Material { get; init; }
}

/// <summary>A model flattened to drawable primitives — what an app turns into engine meshes/materials.</summary>
public sealed class LoadedModel
{
	/// <summary>The model's primitives, each ready to become a <c>Mesh</c> + <c>Material</c>.</summary>
	public required IReadOnlyList<ModelPrimitive> Primitives { get; init; }
}

/// <summary>
/// Loads a glTF / GLB model into Loom's neutral description via SharpGLTF (managed, MIT). M11 scope: static
/// triangle geometry + base-colour and normal textures — no skinning/animation (M15) or PBR channels (M16).
/// Like <see cref="ImageLoader"/> it is a neutral producer (PlanningPhase2.md §8): it emits <see cref="Vertex"/>
/// arrays and <see cref="TextureData"/>, never a graphics-API type, so the engine boundary stays clean.
/// </summary>
public static class GltfLoader
{
	/// <summary>Load and flatten a glTF/GLB file at <paramref name="path"/> into drawable primitives.</summary>
	public static LoadedModel Load(string path)
	{
		var model = ModelRoot.Load(path);
		var primitives = new List<ModelPrimitive>();
		var materialCache = new Dictionary<GltfMaterial, ModelMaterial?>();

		foreach (var mesh in model.LogicalMeshes)
			foreach (var prim in mesh.Primitives)
			{
				// M11 draws triangle lists; skip points/lines/strips/fans rather than reinterpret their
				// vertices as a triangle list (which would produce scrambled geometry).
				if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
					continue;

				var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
				if (positions is null || positions.Count == 0)
					continue;

				var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
				var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
				var tangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();

				var vertices = new Vertex[positions.Count];
				for (var i = 0; i < positions.Count; i++)
				{
					var n = normals is not null ? normals[i] : Vector3.UnitY;
					var uv = uvs is not null ? uvs[i] : Vector2.Zero;
					var tan = tangents is not null ? tangents[i] : new Vector4(1, 0, 0, 1);
					vertices[i] = new Vertex(positions[i], n, uv, tan);
				}

				var indexList = prim.GetIndices();
				uint[] indices;
				if (indexList is { Count: > 0 })
				{
					indices = new uint[indexList.Count];
					for (var i = 0; i < indexList.Count; i++)
						indices[i] = indexList[i];
				}
				else
				{
					// Non-indexed: synthesise a sequential triangle list.
					indices = new uint[vertices.Length];
					for (var i = 0; i < indices.Length; i++)
						indices[i] = (uint)i;
				}

				if (tangents is null)
					ComputeTangents(vertices, indices);

				primitives.Add(new ModelPrimitive
				{
					Vertices = vertices,
					Indices = indices,
					Material = ResolveMaterial(prim.Material, materialCache),
				});
			}

		return new LoadedModel { Primitives = primitives };
	}

	private static ModelMaterial? ResolveMaterial(GltfMaterial? material, Dictionary<GltfMaterial, ModelMaterial?> cache)
	{
		if (material is null)
			return null;
		if (cache.TryGetValue(material, out var cached))
			return cached;

		var baseColor = DecodeChannel(material, "BaseColor", TextureFormat.Rgba8Srgb);
		var normal = DecodeChannel(material, "Normal", TextureFormat.Rgba8Unorm);
		var result = baseColor is null && normal is null ? null : new ModelMaterial { BaseColor = baseColor, Normal = normal };
		cache[material] = result;
		return result;
	}

	// Pull a channel's bound texture, open its encoded bytes (SharpGLTF resolves embedded or external URIs), and
	// decode to neutral TextureData via the shared image loader.
	private static TextureData? DecodeChannel(GltfMaterial material, string channelKey, TextureFormat format)
	{
		var channel = material.FindChannel(channelKey);
		var texture = channel?.Texture;
		var image = texture?.PrimaryImage;
		if (image is null || !image.Content.IsValid)
			return null;
		using var stream = image.Content.Open();
		return ImageLoader.Decode(stream, format);
	}

	// glTF-convention tangent fallback when a primitive omits the TANGENT accessor: accumulate a per-triangle
	// tangent from positions + UVs, then orthonormalise against the normal and pick the bitangent handedness.
	private static void ComputeTangents(Vertex[] vertices, uint[] indices)
	{
		var tan = new Vector3[vertices.Length];
		var bit = new Vector3[vertices.Length];
		for (var t = 0; t + 2 < indices.Length; t += 3)
		{
			uint i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
			var p0 = vertices[i0].Position;
			var e1 = vertices[i1].Position - p0;
			var e2 = vertices[i2].Position - p0;
			var d1 = vertices[i1].Uv - vertices[i0].Uv;
			var d2 = vertices[i2].Uv - vertices[i0].Uv;
			var denom = d1.X * d2.Y - d2.X * d1.Y;
			var r = denom != 0f ? 1f / denom : 0f;
			var tt = (e1 * d2.Y - e2 * d1.Y) * r;
			var bb = (e2 * d1.X - e1 * d2.X) * r;
			tan[i0] += tt; tan[i1] += tt; tan[i2] += tt;
			bit[i0] += bb; bit[i1] += bb; bit[i2] += bb;
		}
		for (var i = 0; i < vertices.Length; i++)
		{
			var n = vertices[i].Normal;
			var t = tan[i];
			t = t - n * Vector3.Dot(n, t);
			t = t.LengthSquared() > 1e-12f ? Vector3.Normalize(t) : new Vector3(1, 0, 0);
			var w = Vector3.Dot(Vector3.Cross(n, t), bit[i]) < 0f ? -1f : 1f;
			vertices[i] = vertices[i] with { Tangent = new Vector4(t, w) };
		}
	}
}
