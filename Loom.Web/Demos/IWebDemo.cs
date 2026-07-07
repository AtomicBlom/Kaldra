using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace Loom.Web.Demos;

/// <summary>
/// A hand-ported browser demo. Construction (each demo's constructor takes the <see cref="IEngine"/>) creates its
/// meshes and defines its techniques; <see cref="Build"/> returns the render graph, <see cref="Update"/> advances
/// per-frame state (usually an orbiting camera), and <see cref="Resize"/> re-fits the camera aspect after a window
/// resize.
/// </summary>
internal interface IWebDemo
{
	RenderGraph Build(int width, int height);
	void Update(float time);
	void Resize(int width, int height);
}

/// <summary>The set of ported demos, keyed by the name used in <c>?demo=</c> / <c>?capture=</c>.</summary>
internal static class WebDemos
{
	private static readonly Dictionary<string, Func<IEngine, IWebDemo>> Factories = new(StringComparer.OrdinalIgnoreCase)
	{
		["cube"] = engine => new CubeLightingDemo(engine),
		["particles"] = engine => new ParticlesDemo(engine),
		["highlighting"] = engine => new HighlightingDemo(engine),
		["bloom"] = engine => new BloomDemo(engine),
		["tonemapping"] = engine => new TonemappingDemo(engine),
		["ssao"] = engine => new SsaoDemo(engine),
		["cascadedshadows"] = engine => new CascadedShadowsDemo(engine),
		["skinning"] = engine => new SkinningDemo(engine),
	};

	public static IEnumerable<string> Names => Factories.Keys;

	/// <summary>Create the named demo, or the default (cube) when the name is unknown/empty.</summary>
	public static IWebDemo Create(string? name, IEngine engine) =>
		(name is not null && Factories.TryGetValue(name, out var factory) ? factory : Factories["cube"])(engine);
}
