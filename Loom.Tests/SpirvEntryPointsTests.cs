using Loom.Backend.Vulkan;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// Covers <see cref="SpirvEntryPoints.Resolve"/> — the resolution that lets a technique name its entry point as the
/// shader source declares it while the backend copes with what the emitted module actually calls it. Runs on hand-built
/// modules (no GPU, no shader build) plus a guard over the real compiled artifacts.
/// </summary>
public class SpirvEntryPointsTests
{
	private const uint Magic = 0x07230203;
	private const uint ModelVertex = 0;
	private const uint ModelFragment = 4;
	private const uint ModelCompute = 5;

	[Test]
	public void An_entry_the_module_declares_resolves_to_itself()
	{
		var spirv = Module((ModelCompute, "computeMain"));

		SpirvEntryPoints.Resolve(spirv, "computeMain", SpirvEntryPoints.Stage.Compute, "s").ShouldBe("computeMain");
	}

	[Test]
	public void A_sole_entry_for_the_stage_resolves_even_under_a_different_name()
	{
		// slangc renames a lone entry point to "main" when emitting SPIR-V, so the source's `computeMain` is not the
		// name pipeline creation must be handed. One candidate for the stage means no ambiguity to resolve.
		var spirv = Module((ModelCompute, "main"));

		SpirvEntryPoints.Resolve(spirv, "computeMain", SpirvEntryPoints.Stage.Compute, "s").ShouldBe("main");
	}

	[Test]
	public void Entry_points_are_matched_per_stage()
	{
		var spirv = Module((ModelVertex, "vertexMain"), (ModelFragment, "fragmentMain"));

		SpirvEntryPoints.Resolve(spirv, "vertexMain", SpirvEntryPoints.Stage.Vertex, "s").ShouldBe("vertexMain");
		SpirvEntryPoints.Resolve(spirv, "fragmentMain", SpirvEntryPoints.Stage.Fragment, "s").ShouldBe("fragmentMain");
		// A vertex entry named "main" must not satisfy a fragment lookup.
		var vertexOnly = Module((ModelVertex, "main"));
		Should.Throw<VulkanException>(() => SpirvEntryPoints.Resolve(vertexOnly, "fragmentMain", SpirvEntryPoints.Stage.Fragment, "s"));
	}

	[Test]
	public void Several_entries_for_one_stage_with_no_match_is_an_error_rather_than_a_guess()
	{
		var spirv = Module((ModelCompute, "reduce"), (ModelCompute, "scatter"));

		var ex = Should.Throw<VulkanException>(() =>
			SpirvEntryPoints.Resolve(spirv, "computeMain", SpirvEntryPoints.Stage.Compute, "twoEntries"));
		ex.Message.ShouldContain("twoEntries");
		ex.Message.ShouldContain("reduce");
		ex.Message.ShouldContain("scatter");
		ex.Message.ShouldContain("computeMain");
	}

	[Test]
	public void A_stage_with_no_entry_names_the_shader_and_the_stage()
	{
		var spirv = Module((ModelVertex, "vertexMain"));

		var ex = Should.Throw<VulkanException>(() =>
			SpirvEntryPoints.Resolve(spirv, "computeMain", SpirvEntryPoints.Stage.Compute, "rasterOnly"));
		ex.Message.ShouldContain("rasterOnly");
		ex.Message.ShouldContain("compute");
	}

	[Test]
	public void A_non_spirv_blob_fails_loudly()
	{
		// Long enough to clear the header check, so it is the magic that rejects it.
		var notSpirv = new byte[64];
		Should.Throw<VulkanException>(() => SpirvEntryPoints.Resolve(notSpirv, "vertexMain", SpirvEntryPoints.Stage.Vertex, "junk"))
			.Message.ShouldContain("not SPIR-V");

		var tooShort = new byte[8];
		Should.Throw<VulkanException>(() => SpirvEntryPoints.Resolve(tooShort, "vertexMain", SpirvEntryPoints.Stage.Vertex, "stub"))
			.Message.ShouldContain("too short");

		// A big-endian module: the magic reads reversed. Rejected explicitly rather than mis-parsed as garbage.
		var swapped = Module((ModelVertex, "main")).ToArray();
		(swapped[0], swapped[1], swapped[2], swapped[3]) = (swapped[3], swapped[2], swapped[1], swapped[0]);
		Should.Throw<VulkanException>(() => SpirvEntryPoints.Resolve(swapped, "main", SpirvEntryPoints.Stage.Vertex, "swapped"))
			.Message.ShouldContain("byte-swapped");
	}

	[Test]
	public void Names_whose_length_is_a_whole_number_of_words_are_read_without_their_padding()
	{
		// "abcd" is exactly one word with no room for the terminator, so SPIR-V appends a whole NUL word. Reading the
		// literal must stop at the terminator rather than trailing four NULs into the name.
		var spirv = Module((ModelCompute, "abcd"));

		SpirvEntryPoints.Resolve(spirv, "abcd", SpirvEntryPoints.Stage.Compute, "s").ShouldBe("abcd");
	}

	[Test]
	public void The_real_compute_artifacts_still_rename_their_lone_entry_to_main()
	{
		// Pins the behaviour the resolution exists for. If a future slangc stops renaming, this fails and the fallback
		// path above becomes dead code that should be reconsidered — rather than silently never running again.
		var shaders = Path.Combine(AppContext.BaseDirectory, "shaders");
		if (!Directory.Exists(shaders))
			throw new TUnit.Core.Exceptions.SkipTestException($"No compiled shaders beside the test assembly ({shaders}).");

		var computeShaders = new[] { "exposure", "skin", "particle_update", "particle_glow_update", "firefly_update" };
		var probed = 0;
		var failures = new List<string>();
		foreach (var name in computeShaders)
		{
			var path = Path.Combine(shaders, name + ".spv");
			if (!File.Exists(path))
				continue;

			probed++;
			// Every one of these declares `computeMain` in Slang; resolution must land on whatever the module calls it.
			var resolved = SpirvEntryPoints.Resolve(
				File.ReadAllBytes(path), TechniqueDescriptor.DefaultComputeEntry, SpirvEntryPoints.Stage.Compute, name);
			if (resolved != "main")
				failures.Add($"{name}: resolved to '{resolved}', expected the renamed 'main'");
		}

		probed.ShouldBeGreaterThan(0, $"found no compute .spv to check in {shaders} — the guard would pass vacuously");
		failures.ShouldBeEmpty(string.Join("\n  ", failures));
	}

	/// <summary>
	/// Build a minimal SPIR-V module: the 5-word header followed by one <c>OpEntryPoint</c> per given stage/name. Only
	/// the fields <see cref="SpirvEntryPoints"/> reads are populated — enough to exercise the instruction walk.
	/// </summary>
	private static byte[] Module(params (uint Model, string Name)[] entries)
	{
		var words = new List<uint> { Magic, 0x00010000, 0, 1, 0 };
		var id = 1u;
		foreach (var (model, name) in entries)
		{
			var nameWords = LiteralWords(name);
			// word 0 packs (wordCount << 16) | opcode; the instruction is opcode + model + <id> + the literal.
			words.Add(((uint)(3 + nameWords.Count) << 16) | 15u);
			words.Add(model);
			words.Add(id++);
			words.AddRange(nameWords);
		}

		var bytes = new byte[words.Count * sizeof(uint)];
		for (var i = 0; i < words.Count; i++)
			BitConverter.GetBytes(words[i]).CopyTo(bytes, i * sizeof(uint));
		return bytes;
	}

	/// <summary>UTF-8 packed four bytes per word, little-endian, NUL terminated and NUL padded to a word boundary.</summary>
	private static List<uint> LiteralWords(string name)
	{
		var utf8 = System.Text.Encoding.UTF8.GetBytes(name);
		// Always at least one NUL, and pad up to the next whole word.
		var padded = new byte[(utf8.Length / 4 + 1) * 4];
		utf8.CopyTo(padded, 0);
		var result = new List<uint>();
		for (var i = 0; i < padded.Length; i += 4)
			result.Add(BitConverter.ToUInt32(padded, i));
		return result;
	}
}
