using System.Collections.Immutable;
using System.Text.Json;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>
/// The D3D registers a shader's parameters were given, read from the DXIL compile's own reflection sidecar
/// (<c>&lt;name&gt;.dxil.json</c>), laid out in the order the root signature binds them: element <c>k</c> of
/// <see cref="Textures"/> is the register of the <c>k</c>-th of <see cref="ShaderBindingMap.SampledTextures"/>, and so
/// on for each category.
/// <para>
/// Shaders name no registers. Slang gives each kind its registers in declaration order (<c>t</c>, <c>s</c>, <c>b</c>,
/// <c>u</c> counted apart), the same for every stage of a module, and the root signature is built from what it gave
/// rather than from a convention the shaders had to be numbered to match.
/// </para>
/// </summary>
internal readonly record struct DirectX12Registers(
	ImmutableArray<int> Textures,
	ImmutableArray<int> Samplers,
	ImmutableArray<int> UniformBuffers,
	ImmutableArray<int> InputBuffers,
	ImmutableArray<int> StorageImages,
	ImmutableArray<int> ReadWriteBuffers,
	int Push)
{
	/// <summary>
	/// Match the DXIL sidecar's registers to <paramref name="map"/>'s bindings by name. The push block (a
	/// <c>[[vk::push_constant]]</c>, which D3D lays out as an ordinary constant buffer) is the one constant buffer the
	/// map doesn't hold; with none, the root constants take the register after the shader's last, where they can't
	/// overlap one it reads.
	/// </summary>
	public static DirectX12Registers Resolve(byte[] json, in ShaderBindingMap map, string shader)
	{
		var registers = Parse(json, shader);
		int Register(ShaderBinding binding, char kind)
		{
			if (registers.TryGetValue(binding.Name, out var of) && of.TryGetValue(kind, out var register))
				return register;
			throw new InvalidOperationException(
				$"DirectX12: shader '{shader}' reflects '{binding.Name}' for SPIR-V but its DXIL reflection gives it no " +
				$"'{kind}' register. Were its .spv and .dxil built from the same source?");
		}

		var uniformNames = new HashSet<string>(map.UniformBuffers.Select(b => b.Name));
		var push = -1;
		var lastConstant = -1;
		foreach (var (name, of) in registers)
		{
			if (!of.TryGetValue('b', out var b))
				continue;
			lastConstant = Math.Max(lastConstant, b);
			if (uniformNames.Contains(name))
				continue;
			if (push >= 0)
				throw new InvalidOperationException(
					$"DirectX12: shader '{shader}' has more than one constant buffer that isn't a descriptor (a push block, or " +
					"loose global uniforms). A technique has one block of root constants.");
			push = b;
		}

		return new DirectX12Registers(
			[.. map.SampledTextures.Select(b => Register(b, 't'))],
			[.. map.SampledTextures.Select(b => Register(b, 's'))],
			[.. map.UniformBuffers.Select(b => Register(b, 'b'))],
			[.. map.StorageBuffers.Where(b => b.Kind == ShaderBindingKind.StorageBufferReadOnly).Select(b => Register(b, 't'))],
			[.. map.StorageImages.Select(b => Register(b, 'u'))],
			[.. map.StorageBuffers.Where(b => b.Kind == ShaderBindingKind.StorageBufferReadWrite).Select(b => Register(b, 'u'))],
			push >= 0 ? push : lastConstant + 1);
	}

	// Each top-level parameter's registers by kind: a Sampler2D has a texture (t) and its sampler (s). Spaces other
	// than 0 are refused: nothing here declares one, and the root signature puts everything in space 0.
	private static Dictionary<string, Dictionary<char, int>> Parse(byte[] json, string shader)
	{
		using var doc = JsonDocument.Parse(json);
		var result = new Dictionary<string, Dictionary<char, int>>();
		if (!doc.RootElement.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
			return result;
		foreach (var p in parameters.EnumerateArray())
		{
			var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
			var of = new Dictionary<char, int>();
			if (p.TryGetProperty("binding", out var binding))
				Add(binding);
			if (p.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
				foreach (var b in bindings.EnumerateArray())
					Add(b);
			if (of.Count > 0)
				result[name] = of;

			void Add(JsonElement b)
			{
				char? kind = b.TryGetProperty("kind", out var k) ? k.GetString() switch
				{
					"constantBuffer" => 'b',
					"shaderResource" => 't',
					"unorderedAccess" => 'u',
					"samplerState" => 's',
					_ => null,
				} : null;
				if (kind is null || !b.TryGetProperty("index", out var index))
					return;
				if (b.TryGetProperty("space", out var space) && space.GetInt32() != 0)
					throw new NotSupportedException(
						$"DirectX12: shader '{shader}' puts '{name}' in register space {space.GetInt32()}; Kaldra's root signatures use space 0 alone.");
				of[kind.Value] = index.GetInt32();
			}
		}
		return result;
	}
}
