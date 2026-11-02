using Loom.Core.Hosting;
using Loom.Graph;

namespace Loom.Backend.DirectX12;

/// <summary>
/// Shader resolution for the DirectX12 backend. Techniques are authored in Slang and the Loom build emits signed DXIL
/// per stage (<c>&lt;name&gt;.vs.dxil</c> / <c>.ps.dxil</c> / <c>.cs.dxil</c>) plus a <c>&lt;name&gt;.json</c>
/// reflection sidecar; this resolves them by logical name, the DirectX12 counterpart of resolving embedded SPIR-V.
/// <para>
/// Nothing is compiled at runtime and there is no substitute shader. A stand-in shades differently, so falling back to
/// one would render a <em>different image on DirectX 12 with no error at all</em> — a silent cross-backend divergence
/// that the parity tests only catch for the demos they happen to cover. A missing artifact throws instead.
/// </para>
/// </summary>
internal static class DirectX12Shaders
{
	// Engine shaders are embedded in this backend assembly under "<assembly>.Shaders." (the SDK's Embed prefix
	// matches $(AssemblyName)); app shaders are loose under the shader directory. Mirrors Vulkan's resolution.
	private static readonly string EmbeddedPrefix = typeof(DirectX12Shaders).Assembly.GetName().Name + ".Shaders.";

	/// <summary>
	/// Resolve a logical shader name + stage to its build-emitted <em>signed DXIL</em> blob
	/// (<c>&lt;name&gt;.&lt;stage&gt;.dxil</c>, stage = <c>vs</c>/<c>ps</c>/<c>cs</c>), or null if absent. Tries the
	/// engine shaders embedded in this backend assembly first, then the app shaders loose under
	/// <paramref name="shaderDir"/> — the DirectX12 counterpart of Vulkan's embedded-then-disk SPIR-V resolution. The
	/// blob is the offline-built signed DXIL the backend loads straight into the PSO (no runtime compiler shipped).
	/// </summary>
	public static byte[]? TryResolveStageDxil(string shaderDir, string name, string stage)
	{
		var embedded = TryReadEmbedded($"{name}.{stage}.dxil");
		if (embedded is not null)
			return embedded;
		var path = Path.Combine(shaderDir, $"{name}.{stage}.dxil");
		return File.Exists(path) ? File.ReadAllBytes(path) : null;
	}

	/// <summary>
	/// The exception for an unresolvable shader artifact, naming both locations that were tried and the likely cause —
	/// matching the Vulkan backend's message, since a packaging slip looks the same on either.
	/// </summary>
	public static ShaderAssetMissingException MissingShader(string name, string shaderDir, string stage)
	{
		var suffix = $"{name}.{stage}.dxil";
		return new ShaderAssetMissingException(
			name,
			$"Shader asset '{suffix}' not found: neither embedded ('{EmbeddedPrefix}{suffix}') nor on disk " +
			$"('{Path.Combine(shaderDir, suffix)}'). Did the app's shaders build, and is DirectX12 DXIL emit enabled for them?");
	}

	private static byte[]? TryReadEmbedded(string suffix)
	{
		var asm = typeof(DirectX12Shaders).Assembly;
		using var stream = asm.GetManifestResourceStream(EmbeddedPrefix + suffix);
		if (stream is null)
			return null;
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	/// <summary>
	/// The descriptor-bound resource shape of a shader, read from its <c>slangc -reflection-json</c> sidecar — the
	/// counts that size the root signature (SRV table, UAV table, root CBV/SRVs) so the app states only pipeline
	/// intent. Tries the engine sidecar embedded in this backend assembly first, then the app sidecar loose beside
	/// the <c>.dxil</c>.
	/// <para>
	/// A missing sidecar throws rather than reflecting as empty. Empty reflection bakes a technique with no samplers,
	/// no constant buffers and no storage bindings, so a resolvable <c>.dxil</c> whose sidecar was left behind would
	/// bind nothing and shade from whatever was already in those slots — the same silent divergence a substitute
	/// shader would cause. Vulkan already throws for the equivalent gap.
	/// </para>
	/// </summary>
	/// <summary>
	/// The shader's SPIR-V, embedded or loose beside the <c>.dxil</c>, or null: read for which textures it samples by
	/// comparison (<see cref="SpirvDepthTextures"/>), which neither the DXIL here nor the reflection sidecar says.
	/// </summary>
	public static byte[]? TryResolveSpirv(string shaderDir, string name)
	{
		var spirv = TryReadEmbedded(name + ".spv");
		if (spirv is not null)
			return spirv;
		var path = Path.Combine(shaderDir, name + ".spv");
		return File.Exists(path) ? File.ReadAllBytes(path) : null;
	}

	public static ShaderReflection ResolveReflection(string shaderDir, string name)
	{
		var json = TryReadEmbedded(name + ".json");
		if (json is null)
		{
			var path = Path.Combine(shaderDir, name + ".json");
			if (!File.Exists(path))
			{
				throw new ShaderAssetMissingException(
					name,
					$"Reflection sidecar '{name}.json' not found: neither embedded ('{EmbeddedPrefix}{name}.json') nor " +
					$"on disk ('{path}'). It carries the resource counts the root signature is built from, so the " +
					"technique cannot be baked without it.");
			}
			json = File.ReadAllBytes(path);
		}
		// The SPIR-V-target sidecar: the DXIL passes emit no reflection of their own, so this backend reads the same
		// one Vulkan does. Its indices size the root signature's tables; the b/t/u register numbers come from the
		// shader's own `register(...)` annotations, which is a separate numbering DXIL honours directly.
		return ShaderReflection.FromBytes(json, ShaderBindingTarget.SpirV);
	}
}
