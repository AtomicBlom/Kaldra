namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// Allocates the integer handles that name GPU objects across the interop boundary. C# owns allocation —
/// every JS <c>createX(handle, …)</c> call carries the handle as its first argument and the shim stores the
/// object at that index — so creation calls need no return-value marshaling and the JS side holds no logic.
/// One allocator per object table (buffers, textures, views, …); freed handles are recycled.
/// </summary>
internal sealed class WebGpuHandleAllocator
{
	// Table ids must match the TABLES order in kaldra-webgpu.js.
	public const int Buffers = 0;
	public const int Textures = 1;
	public const int Views = 2;
	public const int Samplers = 3;
	public const int ShaderModules = 4;
	public const int BindGroupLayouts = 5;
	public const int PipelineLayouts = 6;
	public const int RenderPipelines = 7;
	public const int ComputePipelines = 8;
	public const int BindGroups = 9;
	public const int TableCount = 10;

	/// <summary>The sentinel view handle the command stream uses for "the canvas's current swapchain view".</summary>
	public const int BackbufferView = -1;

	private readonly Stack<int>[] _free = new Stack<int>[TableCount];
	private readonly int[] _next = new int[TableCount];

	public WebGpuHandleAllocator()
	{
		for (var i = 0; i < TableCount; i++)
			_free[i] = new Stack<int>();
	}

	public int Allocate(int table) => _free[table].Count > 0 ? _free[table].Pop() : _next[table]++;

	/// <summary>Return a handle for reuse and release the JS-side object (destroying buffers/textures).</summary>
	public void Release(int table, int handle)
	{
		WebGpuInterop.ReleaseHandle(table, handle);
		_free[table].Push(handle);
	}
}
