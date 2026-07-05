using System.Collections.Immutable;
using System.Numerics;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Materials;
using SharpGLTF.Schema2;

using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace Loom.Assets;

/// <summary>
/// Loads a glTF / GLB model into Loom's neutral description via SharpGLTF (managed, MIT). Static triangle
/// geometry + base-colour and normal textures, plus — when the model is rigged — the skeleton, per-vertex
/// bone indices/weights, and animation clips. Like <see cref="ImageLoader"/> it is a neutral producer: it emits
/// <see cref="Vertex"/>/<see cref="SkinnedVertex"/> arrays, a <see cref="Skeleton"/>,
/// <see cref="AnimationClip"/>s, and <see cref="TextureData"/> — never a graphics-API type, so the engine
/// boundary stays clean.
/// </summary>
public static class GltfLoader
{
	/// <summary>Load and flatten a glTF/GLB file at <paramref name="path"/> into drawable primitives (plus a skeleton + clips when rigged).</summary>
	public static LoadedModel Load(string path) => FromModel(ModelRoot.Load(path));

	/// <summary>Load a binary glTF (<c>.glb</c>) already in memory (e.g. fetched over HTTP by a browser host,
	/// where there is no file system to <see cref="Load"/> from). GLB only: a text <c>.gltf</c> references
	/// sibling files, which bytes alone cannot resolve.</summary>
	public static LoadedModel LoadGlb(byte[] glb) => FromModel(ModelRoot.ParseGLB(glb));

	private static LoadedModel FromModel(ModelRoot model)
	{
		var primitives = new List<ModelPrimitive>();
		var materialCache = new Dictionary<GltfMaterial, ModelMaterial?>();

		// A single-character model has one skin. Build the neutral skeleton from the first skin
		// and a node→joint-index map; the map retargets animation channels and tells which nodes are skinned.
		var gltfSkin = model.LogicalSkins.Count > 0 ? model.LogicalSkins[0] : null;
		var nodeToJoint = new Dictionary<Node, int>();
		var skeleton = gltfSkin is not null ? BuildSkeleton(gltfSkin, nodeToJoint) : null;

		// Place geometry by walking the scene graph. A node's WorldMatrix positions its STATIC mesh, baked into
		// the vertices here — so a mesh authored away from the origin (rotated/scaled), or instanced by several
		// nodes, lands correctly; iterating the mesh table alone would collapse every mesh to the origin and load
		// a multiply-referenced mesh only once. A SKINNED mesh is positioned by its skin's joints, so per the
		// glTF spec the node transform is ignored and its bind-pose geometry stays mesh-local; emit it once.
		var emittedSkinned = new HashSet<GltfMesh>();
		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh is not { } mesh)
				continue;

			if (gltfSkin is not null && node.Skin == gltfSkin)
			{
				if (emittedSkinned.Add(mesh))
					AddMeshPrimitives(mesh, skinned: true, world: null);
			}
			else
			{
				AddMeshPrimitives(mesh, skinned: false, world: node.WorldMatrix);
			}
		}

		return new LoadedModel
		{
			Primitives = primitives,
			Skeleton = skeleton,
			Clips = skeleton is not null ? BuildClips(model, nodeToJoint) : [],
		};

		// Flatten one mesh into drawable primitives. For a static mesh, world != null bakes the node placement
		// into the vertices; for a skinned mesh, world is null (joints place it) and JOINTS_0/WEIGHTS_0 are
		// interleaved with the bind-pose verts.
		void AddMeshPrimitives(GltfMesh mesh, bool skinned, Matrix4x4? world)
		{
			foreach (var prim in mesh.Primitives)
			{
				// Draw triangle lists only; skip points/lines/strips/fans rather than reinterpret their vertices
				// as a triangle list (which would produce scrambled geometry).
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

				// Bake the node's world placement into a static mesh's vertices BEFORE tangents are computed, so
				// any computed tangents match the world-space positions.
				if (world is { } m && m != Matrix4x4.Identity)
					BakeTransform(vertices, m);

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

				// Pull JOINTS_0/WEIGHTS_0 and interleave with the bind-pose verts. Both the
				// static Vertices and the SkinnedVertices are kept, so a consumer can draw either.
				var skinnedVertices = skinned ? BuildSkinnedVertices(prim, vertices) : null;

				primitives.Add(new ModelPrimitive
				{
					Vertices = vertices,
					Indices = indices,
					Material = ResolveMaterial(prim.Material, materialCache),
					SkinnedVertices = skinnedVertices,
				});
			}
		}
	}

	// Bake a node's world transform into a static mesh's vertices: positions by the full matrix, normals by its
	// inverse-transpose (so non-uniform scale keeps them perpendicular to the surface), tangents by its linear
	// part. The tangent handedness (Tangent.W) is preserved.
	private static void BakeTransform(Vertex[] vertices, Matrix4x4 world)
	{
		if (!Matrix4x4.Invert(world, out var inverse))
			inverse = Matrix4x4.Identity;
		var normalMatrix = Matrix4x4.Transpose(inverse);
		for (var i = 0; i < vertices.Length; i++)
		{
			var v = vertices[i];
			var position = Vector3.Transform(v.Position, world);
			var normal = SafeNormalize(Vector3.TransformNormal(v.Normal, normalMatrix));
			var tangent = SafeNormalize(Vector3.TransformNormal(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), world));
			vertices[i] = new Vertex(position, normal, v.Uv, new Vector4(tangent, v.Tangent.W));
		}
	}

	private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;

	// Build the neutral Skeleton from a glTF skin: each joint's parent index (within the joint set, -1 for a
	// root), inverse bind matrix, bind-pose local transform, and name. Also fills <paramref name="nodeToJoint"/>
	// (node → joint index) so the skinned-vertex extraction and animation retargeting can resolve joints.
	private static Skeleton BuildSkeleton(Skin skin, Dictionary<Node, int> nodeToJoint)
	{
		var count = skin.JointsCount;
		for (var j = 0; j < count; j++)
			nodeToJoint[skin.GetJoint(j).Joint] = j;

		var parents = ImmutableArray.CreateBuilder<int>(count);
		var inverseBinds = ImmutableArray.CreateBuilder<Matrix4x4>(count);
		var localBinds = ImmutableArray.CreateBuilder<Matrix4x4>(count);
		var names = ImmutableArray.CreateBuilder<string>(count);
		for (var j = 0; j < count; j++)
		{
			var (joint, inverseBind) = skin.GetJoint(j);
			inverseBinds.Add(inverseBind);
			localBinds.Add(joint.LocalMatrix);
			names.Add(joint.Name ?? $"joint{j}");
			parents.Add(joint.VisualParent is { } parent && nodeToJoint.TryGetValue(parent, out var pj) ? pj : -1);
		}

		return new Skeleton
		{
			ParentIndices = parents.MoveToImmutable(),
			InverseBindMatrices = inverseBinds.MoveToImmutable(),
			LocalBindTransforms = localBinds.MoveToImmutable(),
			JointNames = names.MoveToImmutable(),
		};
	}

	// Interleave a primitive's JOINTS_0/WEIGHTS_0 with its bind-pose verts. SharpGLTF returns both as Vector4
	// arrays (joint indices as floats regardless of the UNSIGNED_BYTE/SHORT component type). Weights are
	// renormalized to sum to 1 (glTF allows un-normalized weights); a zero-sum vertex falls back to joint 0.
	private static SkinnedVertex[] BuildSkinnedVertices(MeshPrimitive prim, Vertex[] vertices)
	{
		var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
		var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
		var result = new SkinnedVertex[vertices.Length];
		for (var i = 0; i < vertices.Length; i++)
		{
			var v = vertices[i];
			var j = joints is not null ? joints[i] : Vector4.Zero;
			var w = weights is not null ? weights[i] : new Vector4(1, 0, 0, 0);
			var sum = w.X + w.Y + w.Z + w.W;
			w = sum > 1e-6f ? w / sum : new Vector4(1, 0, 0, 0);
			result[i] = new SkinnedVertex(
				v.Position, v.Normal, v.Uv, v.Tangent,
				(uint)j.X, (uint)j.Y, (uint)j.Z, (uint)j.W, w);
		}
		return result;
	}

	// Read the model's animations into neutral clips, retargeting each channel's node to a joint index. Channels
	// targeting non-joint nodes are skipped. Step/Linear are read directly; cubic-spline is rejected (not yet sampled).
	private static IReadOnlyList<AnimationClip> BuildClips(ModelRoot model, Dictionary<Node, int> nodeToJoint)
	{
		var clips = new List<AnimationClip>(model.LogicalAnimations.Count);
		foreach (var animation in model.LogicalAnimations)
		{
			// Group the animation's channels by target joint (one JointChannel carries that joint's T/R/S tracks).
			var byJoint = new Dictionary<int, (Vector3Track? T, QuaternionTrack? R, Vector3Track? S)>();
			foreach (var channel in animation.Channels)
			{
				if (!nodeToJoint.TryGetValue(channel.TargetNode, out var jointIndex))
					continue; // a channel that drives a non-joint node (camera, prop) — not skeletal animation

				byJoint.TryGetValue(jointIndex, out var tracks);
				var translation = channel.GetTranslationSampler();
				var rotation = channel.GetRotationSampler();
				var scale = channel.GetScaleSampler();
				if (translation is not null)
					tracks.T = ReadVector3Track(translation, animation.Name, "translation");
				if (rotation is not null)
					tracks.R = ReadQuaternionTrack(rotation, animation.Name);
				if (scale is not null)
					tracks.S = ReadVector3Track(scale, animation.Name, "scale");
				byJoint[jointIndex] = tracks;
			}

			var channels = ImmutableArray.CreateBuilder<JointChannel>(byJoint.Count);
			foreach (var (jointIndex, tracks) in byJoint)
				channels.Add(new JointChannel { JointIndex = jointIndex, Translation = tracks.T, Rotation = tracks.R, Scale = tracks.S });

			clips.Add(new AnimationClip
			{
				Name = animation.Name ?? $"clip{clips.Count}",
				Duration = animation.Duration,
				Channels = channels.ToImmutable(),
			});
		}
		return clips;
	}

	private static Vector3Track ReadVector3Track(IAnimationSampler<Vector3> sampler, string? clip, string what)
	{
		var interpolation = ToInterpolation(sampler.InterpolationMode, clip, what);
		var times = ImmutableArray.CreateBuilder<float>();
		var values = ImmutableArray.CreateBuilder<Vector3>();
		foreach (var (key, value) in sampler.GetLinearKeys())
		{
			times.Add(key);
			values.Add(value);
		}
		return new Vector3Track { Times = times.ToImmutable(), Values = values.ToImmutable(), Interpolation = interpolation };
	}

	private static QuaternionTrack ReadQuaternionTrack(IAnimationSampler<Quaternion> sampler, string? clip)
	{
		var interpolation = ToInterpolation(sampler.InterpolationMode, clip, "rotation");
		var times = ImmutableArray.CreateBuilder<float>();
		var values = ImmutableArray.CreateBuilder<Quaternion>();
		foreach (var (key, value) in sampler.GetLinearKeys())
		{
			times.Add(key);
			values.Add(value);
		}
		return new QuaternionTrack { Times = times.ToImmutable(), Values = values.ToImmutable(), Interpolation = interpolation };
	}

	// Map SharpGLTF's interpolation enum to the neutral one. Cubic-spline isn't sampled yet, so reject it with a
	// clear message rather than silently reading the (differently shaped) cubic keys through the linear accessor.
	private static AnimationInterpolation ToInterpolation(AnimationInterpolationMode mode, string? clip, string what) => mode switch
	{
		AnimationInterpolationMode.STEP => AnimationInterpolation.Step,
		AnimationInterpolationMode.LINEAR => AnimationInterpolation.Linear,
		_ => throw new NotSupportedException($"glTF clip '{clip}' uses CUBICSPLINE interpolation for a joint's {what}, which Loom's animation runtime does not yet sample."),
	};

	private static ModelMaterial? ResolveMaterial(GltfMaterial? material, Dictionary<GltfMaterial, ModelMaterial?> cache)
	{
		if (material is null)
			return null;
		if (cache.TryGetValue(material, out var cached))
			return cached;

		var baseColor = DecodeChannel(material, "BaseColor", TextureFormat.Rgba8Srgb);
		var normal = DecodeChannel(material, "Normal", TextureFormat.Rgba8Unorm);
		// glTF packs roughness (G) + metallic (B) in one linear texture; occlusion (R) often shares that image.
		var metallicRoughness = DecodeChannel(material, "MetallicRoughness", TextureFormat.Rgba8Unorm);
		var occlusion = DecodeChannel(material, "Occlusion", TextureFormat.Rgba8Unorm);
		var emissive = DecodeChannel(material, "Emissive", TextureFormat.Rgba8Srgb);

		// Emit a material only when it carries at least one texture, preserving the texture-bundle contract earlier
		// consumers rely on (a factor-only material still produces null). The scalar factors ride along with the
		// textures so the PBR path can multiply texel × factor and fall back to the factor where a texture is absent.
		var anyTexture = baseColor is not null || normal is not null || metallicRoughness is not null
			|| occlusion is not null || emissive is not null;
		if (!anyTexture)
		{
			cache[material] = null;
			return null;
		}

		var emissiveColor = material.FindChannel("Emissive")?.Color ?? Vector4.Zero;
		var result = new ModelMaterial
		{
			BaseColor = baseColor,
			Normal = normal,
			MetallicRoughness = metallicRoughness,
			Occlusion = occlusion,
			Emissive = emissive,
			BaseColorFactor = material.FindChannel("BaseColor")?.Color ?? Vector4.One,
			MetallicFactor = Factor(material, "MetallicRoughness", "MetallicFactor", 1f),
			RoughnessFactor = Factor(material, "MetallicRoughness", "RoughnessFactor", 1f),
			EmissiveFactor = new Vector3(emissiveColor.X, emissiveColor.Y, emissiveColor.Z),
			OcclusionStrength = Factor(material, "Occlusion", "OcclusionStrength", 1f),
			NormalScale = Factor(material, "Normal", "NormalScale", 1f),
		};
		cache[material] = result;
		return result;
	}

	// Read a named scalar factor from a material channel's parameter list (glTF metallic-roughness factors:
	// "MetallicFactor"/"RoughnessFactor" on the MetallicRoughness channel, "OcclusionStrength" on Occlusion,
	// "NormalScale" on Normal). Returns the glTF default when the channel or named float parameter is absent.
	private static float Factor(GltfMaterial material, string channelKey, string paramName, float fallback)
	{
		if (material.FindChannel(channelKey) is not { } channel)
			return fallback;
		foreach (var parameter in channel.Parameters)
			if (parameter.Name == paramName && parameter.Value is float value)
				return value;
		return fallback;
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
