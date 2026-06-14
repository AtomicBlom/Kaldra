using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The DX12 <see cref="IRenderContext"/> — the per-frame command recorder handed to each pass's
/// <c>.Draw</c> callback (the counterpart of the Vulkan backend's <c>RenderContext</c>). It is stateful: the
/// engine pushes the frame inputs + slot (<see cref="BeginFrame"/>) and, per pass, the actual render-target
/// formats (<see cref="BeginPass"/>) so a technique bakes a pipeline matching the pass's attachments.
/// <para>
/// Implements the fullscreen verbs (from-scratch and sampling). Mesh-draw, compute, push constants and ImGui
/// land in following slices and currently throw.
/// </para>
/// </summary>
internal sealed unsafe class DX12RenderContext : IRenderContext
{
	private const string NextSlice = "DX12: this render verb lands in a following executor slice.";
	private const uint DefaultShader4ComponentMapping = 0x1688; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

	private readonly DX12Context _ctx;
	private readonly DX12TechniqueCache _techniques;
	private readonly DX12DescriptorRing _ring;
	private DX12Transients? _transients;

	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	private DxgiFormat[] _colorFormats = [];
	private int _colorCount;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public uint HoverId { get; private set; }
	public Camera SunView { get; private set; } = Camera.Identity;
	public Camera Camera { get; private set; } = Camera.Identity;
	public bool HdrOutput { get; private set; }

	public DX12RenderContext(DX12Context ctx, DX12TechniqueCache techniques, DX12DescriptorRing ring)
	{
		_ctx = ctx;
		_techniques = techniques;
		_ring = ring;
	}

	/// <summary>Bind the active graph's transient targets (the source of sampled-input SRVs). Called from SetGraph.</summary>
	public void SetTransients(DX12Transients transients) => _transients = transients;

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput, uint slot)
	{
		_cmd = cmd;
		_slot = slot;
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
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount));
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		if (inputs.Length > 0)
			BindInputs(inputs);

		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.DrawInstanced(3, 1, 0, 0); // fullscreen triangle, vertices fabricated from SV_VertexID
	}

	// Write the sampled inputs' SRVs into a fresh descriptor-ring range and bind it as root descriptor table 0.
	private void BindInputs(ReadOnlySpan<ResourceHandle> inputs)
	{
		if (_transients is null)
			throw new InvalidOperationException("DX12: sampled inputs require an active graph (SetGraph).");

		var (cpu, gpu) = _ring.Allocate((uint)inputs.Length);
		for (var i = 0; i < inputs.Length; i++)
		{
			var id = inputs[i].Id;
			if (!_transients.IsTransient(id))
				throw new NotSupportedException("DX12: sampling the backbuffer or a persistent/buffer resource lands in a later slice.");

			var srv = new ShaderResourceViewDesc
			{
				Format = _transients.Format(_slot, id),
				ViewDimension = SrvDimension.Texture2D,
				Shader4ComponentMapping = DefaultShader4ComponentMapping,
				Anonymous = new ShaderResourceViewDescUnion
				{
					Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f }
				}
			};
			_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, id), &srv, _ring.Offset(cpu, (uint)i));
		}
		_cmd.SetGraphicsRootDescriptorTable(0, gpu);
	}

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
		=> throw new NotSupportedException("DX12: fullscreen push constants land in a later executor slice.");

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
		=> throw new NotSupportedException(NextSlice);

	public void Draw(Mesh mesh, string technique) => throw new NotSupportedException(NextSlice);
	public void Draw(Mesh mesh, string technique, Camera view) => throw new NotSupportedException(NextSlice);
	public void DrawParticles(ResourceHandle buffer, int count, string technique) => throw new NotSupportedException(NextSlice);
	public void DrawImGui() { } // neutral no-op until the DX12 ImGui renderer lands
}
