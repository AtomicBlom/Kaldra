namespace Loom.Sandbox;

/// <summary>Which backend the sandbox launches on — chosen by <c>--backend</c>, defaulting to Vulkan.</summary>
internal enum BackendChoice
{
	Vulkan,
	Dx12,
}
