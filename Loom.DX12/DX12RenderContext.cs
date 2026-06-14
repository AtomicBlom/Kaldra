using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The DX12 <see cref="IRenderContext"/> — the per-frame command recorder handed to each pass's
/// <c>.Draw</c> callback (the counterpart of the Vulkan backend's <c>RenderContext</c>). It is stateful: the
/// engine pushes the frame inputs (<see cref="BeginFrame"/>) and, per pass, the actual render-target formats
/// (<see cref="BeginPass"/>) so a technique bakes a pipeline matching the pass's attachments.
/// <para>
/// This first executor step implements the from-scratch fullscreen verb (no sampled inputs). The sampling/
/// blit, mesh-draw, compute and ImGui verbs land in following slices and currently throw.
/// </para>
/// </summary>
internal sealed unsafe class DX12RenderContext : IRenderContext
{
	private const string NextSlice = "DX12: this render verb lands in a following executor slice.";

	private readonly DX12TechniqueCache _techniques;
	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private DxgiFormat[] _colorFormats = [];
	private int _colorCount;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public uint HoverId { get; private set; }
	public Camera SunView { get; private set; } = Camera.Identity;
	public Camera Camera { get; private set; } = Camera.Identity;
	public bool HdrOutput { get; private set; }

	public DX12RenderContext(DX12TechniqueCache techniques) => _techniques = techniques;

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput)
	{
		_cmd = cmd;
		DeltaTime = (float)inputs.DeltaSeconds;
		Time = inputs.Time;
		HoverId = inputs.HoverId;
		Camera = inputs.Camera;
		SunView = inputs.SunView;
		HdrOutput = hdrOutput;
	}

	/// <summary>Tell the context the current pass's colour-target formats, so techniques bake matching pipelines.</summary>
	public void BeginPass(ReadOnlySpan<DxgiFormat> colorFormats)
	{
		if (_colorFormats.Length < colorFormats.Length)
			_colorFormats = new DxgiFormat[colorFormats.Length];
		colorFormats.CopyTo(_colorFormats);
		_colorCount = colorFormats.Length;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs)
	{
		if (inputs.Length > 0)
			throw new NotSupportedException("DX12: sampled fullscreen inputs land with the transient/sample executor slice.");

		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount));
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.DrawInstanced(3, 1, 0, 0); // fullscreen triangle, vertices fabricated from SV_VertexID
	}

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
		=> throw new NotSupportedException("DX12: fullscreen push constants land with the transient/sample executor slice.");

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
		=> throw new NotSupportedException(NextSlice);

	public void Draw(Mesh mesh, string technique) => throw new NotSupportedException(NextSlice);
	public void Draw(Mesh mesh, string technique, Camera view) => throw new NotSupportedException(NextSlice);
	public void DrawParticles(ResourceHandle buffer, int count, string technique) => throw new NotSupportedException(NextSlice);
	public void DrawImGui() { } // neutral no-op until the DX12 ImGui renderer lands
}
