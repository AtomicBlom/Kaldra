namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Which of a shader's sampled textures it reads by comparison (Slang's <c>Sampler2DShadow</c> and
/// <c>SampleCmp</c>), found in its SPIR-V. Neither Slang's reflection sidecar nor the SPIR-V types tell a shadow sampler
/// from a single-channel one (Slang gives both the same image type, its depth "unknown"); what differs is how it is
/// read. So this follows every depth-comparison sample (<c>OpImageSample*Dref*</c>, <c>OpImageDrefGather</c>) back
/// through the loads, access chains and sampled-image constructions to the variable it reads, and reports that
/// variable's binding. The desktop backends build their samplers from it; WebGPU reads the same fact from the WGSL it
/// parses (<c>texture_depth_2d</c>, <c>sampler_comparison</c>).
/// <para>
/// A comparison whose image can't be followed back to a bound variable (one stored into a local, chosen by an
/// <c>OpPhi</c> or <c>OpSelect</c>, or passed into a function Slang didn't inline) throws rather than going unreported:
/// its slot would keep a plain sampler. Kaldra's shaders read their samplers as globals, which a function body loads
/// directly.
/// </para>
/// </summary>
public static class SpirvDepthTextures
{
	private const uint MagicNumber = 0x07230203;
	private const int OpVariable = 59, OpLoad = 61, OpAccessChain = 65, OpInBoundsAccessChain = 66, OpDecorate = 71,
		OpCopyObject = 83, OpSampledImage = 86,
		OpImageSampleDrefImplicitLod = 89, OpImageSampleDrefExplicitLod = 90,
		OpImageSampleProjDrefImplicitLod = 91, OpImageSampleProjDrefExplicitLod = 92, OpImageDrefGather = 97;
	private const uint DecorationBinding = 33;

	/// <summary>The SPIR-V binding numbers (as the reflection sidecar gives them) of every texture the
	/// shader reads by comparison. Empty for a module that isn't SPIR-V, rather than a guess.</summary>
	/// <param name="spirv">The module.</param>
	/// <param name="shader">The shader's name, for the message when a comparison can't be traced.</param>
	/// <exception cref="InvalidOperationException">A depth-comparison sample reads an image that can't be followed back
	/// to a bound variable: one stored into a local (<c>OpStore</c>), chosen by an <c>OpPhi</c> or <c>OpSelect</c>, or
	/// passed in as a function parameter. Its slot would otherwise keep a plain sampler and compare to undefined results
	/// on one backend and not another.</exception>
	public static IReadOnlySet<int> Bindings(ReadOnlySpan<byte> spirv, string shader)
	{
		var found = new HashSet<int>();
		if (spirv.Length < 20 || spirv.Length % 4 != 0)
			return found;
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spirv);
		if (words[0] != MagicNumber)
			return found;

		var variables = new HashSet<uint>();
		var from = new Dictionary<uint, uint>();      // a value → the value or variable it was made from
		var bindings = new Dictionary<uint, int>();   // a variable → its binding
		var compared = new List<uint>();              // the sampled images comparison samples read

		for (var at = 5; at < words.Length;)
		{
			var word = words[at];
			int count = (int)(word >> 16), op = (int)(word & 0xFFFF);
			if (count == 0 || at + count > words.Length)
				break;
			var operands = words.Slice(at + 1, count - 1);
			switch (op)
			{
				// OpVariable: result type, result, storage class
				case OpVariable when operands.Length >= 3:
					variables.Add(operands[1]);
					break;
				// OpDecorate: target, decoration, literal
				case OpDecorate when operands.Length >= 3 && operands[1] == DecorationBinding:
					bindings[operands[0]] = (int)operands[2];
					break;
				// Result type, result, then the value it comes from (the image, for a sampled image)
				case OpLoad when operands.Length >= 3:
				case OpAccessChain when operands.Length >= 3:
				case OpInBoundsAccessChain when operands.Length >= 3:
				case OpCopyObject when operands.Length >= 3:
				case OpSampledImage when operands.Length >= 3:
					from[operands[1]] = operands[2];
					break;
				// Result type, result, sampled image, ...
				case OpImageSampleDrefImplicitLod or OpImageSampleDrefExplicitLod or OpImageSampleProjDrefImplicitLod
					or OpImageSampleProjDrefExplicitLod or OpImageDrefGather when operands.Length >= 3:
					compared.Add(operands[2]);
					break;
			}
			at += count;
		}

		foreach (var image in compared)
		{
			var id = image;
			for (var steps = 0; steps < 16 && !variables.Contains(id) && from.TryGetValue(id, out var source); steps++)
				id = source;
			if (!variables.Contains(id) || !bindings.TryGetValue(id, out var binding))
			{
				throw new InvalidOperationException(
					$"Shader '{shader}' samples by comparison an image (SPIR-V id %{id}) that can't be followed back to a bound " +
					"texture: it reaches the sample through a local variable, an OpPhi or OpSelect, or a function parameter. " +
					"Read the shadow sampler from its global directly in the function that compares, so its slot gets a " +
					"comparison sampler on every backend.");
			}
			found.Add(binding);
		}
		return found;
	}
}
