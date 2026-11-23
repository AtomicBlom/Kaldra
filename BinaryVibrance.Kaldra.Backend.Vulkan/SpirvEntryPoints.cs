using System.Text;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>
/// Reads the entry points a SPIR-V module declares, from its <c>OpEntryPoint</c> instructions — the names
/// <c>vkCreateGraphicsPipelines</c>/<c>vkCreateComputePipelines</c> match <c>pName</c> against.
/// <para>
/// This exists because a module's entry names need not match the shader source's. slangc renames a lone entry point
/// to <c>main</c> when emitting SPIR-V, so a shader that declares <c>computeMain</c> compiles to a module whose only
/// entry is <c>main</c> — while the DXIL emit for the same shader is driven by <c>-entry computeMain</c>. Resolving
/// the actual name here keeps that artifact-level quirk out of the neutral describe layer, so an author always names
/// the entry as the source declares it.
/// </para>
/// The module is read rather than the reflection sidecar because the module is authoritative: the string in
/// <c>OpEntryPoint</c> is literally what pipeline creation is matched against.
/// </summary>
public static class SpirvEntryPoints
{
	private const uint Magic = 0x07230203;
	private const uint MagicReversed = 0x03022307;
	private const int HeaderWords = 5;
	private const ushort OpEntryPoint = 15;

	/// <summary>Execution models this backend creates pipelines for, as they appear in <c>OpEntryPoint</c>'s first word.</summary>
	private const uint ModelVertex = 0;
	private const uint ModelFragment = 4;
	private const uint ModelCompute = 5;

	/// <summary>The pipeline stage an entry point is being resolved for.</summary>
	public enum Stage
	{
		Vertex,
		Fragment,
		Compute,
	}

	/// <summary>
	/// The entry name to hand pipeline creation for <paramref name="stage"/>: <paramref name="requested"/> when
	/// <paramref name="spirv"/> declares it for that stage, otherwise the module's sole entry point for the stage
	/// (covering slangc's rename of a lone entry to <c>main</c>).
	/// </summary>
	/// <exception cref="VulkanException">
	/// The module is not SPIR-V, declares no entry point for the stage, or declares several for it and none is
	/// <paramref name="requested"/> — an ambiguity no fallback can resolve without guessing.
	/// </exception>
	public static string Resolve(ReadOnlySpan<byte> spirv, string requested, Stage stage, string shaderName)
	{
		ArgumentException.ThrowIfNullOrEmpty(requested);

		Span<string> found = new string[8];
		var count = Collect(spirv, stage, shaderName, found, out var overflow);

		for (var i = 0; i < count; i++)
			if (string.Equals(found[i], requested, StringComparison.Ordinal))
				return requested;

		if (count == 1 && !overflow)
			return found[0];

		if (count == 0)
		{
			throw new VulkanException(
				$"Shader '{shaderName}' declares no {Describe(stage)} entry point in its SPIR-V, but technique registration asked for '{requested}'. " +
				$"Check that the shader declares [shader(\"{Describe(stage)}\")] and that the build emitted it.");
		}

		throw new VulkanException(
			$"Shader '{shaderName}' declares {count}{(overflow ? "+" : "")} {Describe(stage)} entry points in its SPIR-V " +
			$"({string.Join(", ", found[..count].ToArray().Select(n => $"'{n}'"))}) and none is the requested '{requested}'. " +
			"Name the entry point as the shader declares it.");
	}

	/// <summary>
	/// Fill <paramref name="into"/> with the names of every entry point declared for <paramref name="stage"/> and
	/// return how many were written; <paramref name="overflow"/> reports that the module declared more than
	/// <paramref name="into"/> could hold (so a count of 1 is not necessarily "the only one").
	/// </summary>
	private static int Collect(ReadOnlySpan<byte> spirv, Stage stage, string shaderName, Span<string> into, out bool overflow)
	{
		overflow = false;
		if (spirv.Length < HeaderWords * sizeof(uint) || spirv.Length % sizeof(uint) != 0)
			throw new VulkanException($"Shader '{shaderName}' is not a valid SPIR-V module: {spirv.Length} bytes is too short or not a whole number of words.");

		var magic = ReadWord(spirv, 0);
		if (magic == MagicReversed)
			throw new VulkanException($"Shader '{shaderName}' is byte-swapped SPIR-V (magic 0x{magic:X8}); this backend reads little-endian modules only.");
		if (magic != Magic)
			throw new VulkanException($"Shader '{shaderName}' is not SPIR-V: expected magic 0x{Magic:X8}, got 0x{magic:X8}.");

		var want = stage switch
		{
			Stage.Vertex => ModelVertex,
			Stage.Fragment => ModelFragment,
			Stage.Compute => ModelCompute,
			_ => throw new VulkanException($"No SPIR-V execution model for stage {stage}."),
		};

		var words = spirv.Length / sizeof(uint);
		var count = 0;
		// Instructions run from the header to the end; word 0 of each packs (wordCount << 16) | opcode.
		for (var i = HeaderWords; i < words;)
		{
			var instruction = ReadWord(spirv, i);
			var wordCount = (int)(instruction >> 16);
			var opcode = (ushort)(instruction & 0xFFFF);
			// A zero word count would not advance — treat it as corruption rather than spinning.
			if (wordCount <= 0 || i + wordCount > words)
				throw new VulkanException($"Shader '{shaderName}' has a malformed SPIR-V instruction at word {i} (word count {wordCount}).");

			// OpEntryPoint: word 1 execution model, word 2 the entry's <id>, words 3.. a NUL-terminated literal.
			if (opcode == OpEntryPoint && wordCount >= 4 && ReadWord(spirv, i + 1) == want)
			{
				var name = ReadLiteralString(spirv, i + 3, i + wordCount);
				if (count < into.Length)
					into[count++] = name;
				else
					overflow = true;
			}

			i += wordCount;
		}

		return count;
	}

	private static uint ReadWord(ReadOnlySpan<byte> spirv, int wordIndex)
	{
		var b = spirv[(wordIndex * sizeof(uint))..];
		return b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24);
	}

	/// <summary>
	/// Decode a SPIR-V literal string: UTF-8 bytes packed four per word, little-endian within each word, NUL
	/// terminated and NUL padded to the word boundary.
	/// </summary>
	private static string ReadLiteralString(ReadOnlySpan<byte> spirv, int firstWord, int endWord)
	{
		var start = firstWord * sizeof(uint);
		var end = endWord * sizeof(uint);
		var bytes = spirv[start..end];
		var nul = bytes.IndexOf((byte)0);
		return Encoding.UTF8.GetString(nul < 0 ? bytes : bytes[..nul]);
	}

	private static string Describe(Stage stage) => stage switch
	{
		Stage.Vertex => "vertex",
		Stage.Fragment => "fragment",
		Stage.Compute => "compute",
		_ => throw new VulkanException($"No SPIR-V stage name for {stage}."),
	};
}
