using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// Covers <see cref="SpirvDepthTextures.Bindings"/>, which tells the desktop backends which sampled textures a shader
/// reads by comparison, and <see cref="ShaderBindingMap.CheckCompare"/>, which holds a technique's SampleCompare to it.
/// Runs on hand-built modules: Slang gives a shadow sampler and a single-channel texture the same type, so what marks the
/// first is a depth-comparison sample reading it, which these modules spell out.
/// </summary>
public class SpirvDepthTexturesTests
{
	private const uint OpVariable = 59, OpLoad = 61, OpAccessChain = 65, OpDecorate = 71, OpSampledImage = 86,
		OpImageSampleExplicitLod = 88, OpImageSampleDrefExplicitLod = 90, DecorationBinding = 33;

	[Test]
	public void A_texture_read_by_comparison_is_found_and_one_sampled_plainly_is_not()
	{
		var spirv = Module(
			[OpDecorate, 10, DecorationBinding, 3],
			[OpDecorate, 11, DecorationBinding, 4],
			[OpVariable, 1, 10, 0],
			[OpVariable, 1, 11, 0],
			[OpLoad, 2, 20, 10],
			[OpLoad, 2, 21, 11],
			[OpImageSampleDrefExplicitLod, 3, 30, 20, 40, 41, 2, 42],
			[OpImageSampleExplicitLod, 4, 31, 21, 40, 2, 42]);

		SpirvDepthTextures.Bindings(spirv).ShouldBe([3], ignoreOrder: true);
	}

	[Test]
	public void A_comparison_is_followed_back_through_access_chains_and_sampled_images()
	{
		// An element of an array of shadow maps, its image paired with a separate sampler
		var spirv = Module(
			[OpDecorate, 10, DecorationBinding, 7],
			[OpVariable, 1, 10, 0],
			[OpAccessChain, 2, 20, 10, 50],
			[OpLoad, 3, 21, 20],
			[OpSampledImage, 4, 22, 21, 60],
			[OpImageSampleDrefExplicitLod, 5, 30, 22, 40, 41, 2, 42]);

		SpirvDepthTextures.Bindings(spirv).ShouldBe([7], ignoreOrder: true);
	}

	[Test]
	public void Something_that_is_not_SPIR_V_reads_as_no_comparisons()
	{
		SpirvDepthTextures.Bindings([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]).ShouldBeEmpty();
		SpirvDepthTextures.Bindings([]).ShouldBeEmpty();
	}

	[Test]
	public void A_technique_must_name_its_compare_exactly_when_its_shader_compares()
	{
		Should.NotThrow(() => ShaderBindingMap.CheckCompare("lit", 3, DepthCompare.GreaterOrEqual));
		Should.NotThrow(() => ShaderBindingMap.CheckCompare("plain", 0, null));
		Should.Throw<InvalidOperationException>(() => ShaderBindingMap.CheckCompare("lit", 3, null)).Message.ShouldContain("SampleCompare");
		Should.Throw<InvalidOperationException>(() => ShaderBindingMap.CheckCompare("plain", 0, DepthCompare.LessOrEqual)).Message.ShouldContain("no texture");
	}

	// A module of the given instructions, each its opcode then its operands
	private static byte[] Module(params uint[][] instructions)
	{
		var words = new List<uint> { 0x07230203, 0x00010300, 0, 100, 0 };
		foreach (var instruction in instructions)
		{
			words.Add(((uint)instruction.Length << 16) | instruction[0]);
			words.AddRange(instruction[1..]);
		}
		var bytes = new byte[words.Count * 4];
		Buffer.BlockCopy(words.ToArray(), 0, bytes, 0, bytes.Length);
		return bytes;
	}
}
