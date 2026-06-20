using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

using Loom.Core;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The backend's <see cref="IRenderContext"/> — what a pass's <c>.Draw</c> callback issues GPU work
/// through. It is a thin recorder over the frame's command buffer: it resolves a technique name to a baked
/// pipeline (baked against the current pass's target format), wires up any sampled input as a descriptor
/// set, and emits the draw. State that changes per frame/pass (the command buffer, the target format) is
/// pushed in by the <see cref="VulkanEngine"/> via <see cref="BeginFrame"/>/<see cref="BeginPass"/> so the
/// callbacks stay the pure, capture-free closures the design wants (Planning.md §8).
/// </summary>
internal sealed unsafe class RenderContext : IRenderContext, IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly TechniqueCache _techniques;
	private readonly IReadOnlyDictionary<string, TechniqueTemplate> _registry;
	// Set per active graph by the engine (SetTransients): a graph swap rebinds this to the new graph's transients
	// while the rest of the context (samplers, technique cache, the registry) is reused.
	private TransientResources _resources = null!;
	private readonly DescriptorAllocator _descriptors;
	private readonly MeshStore _meshes;
	private readonly TextureStore _textures;
	private readonly MaterialStore _materials;
	private readonly InstanceUploadBuffer _instanceUpload;
	// M15-0: per-frame host-visible ring for the uniform seam (CSM matrices, lighting params, wind) — the same
	// retire-not-free ring as the instance stream, but created UniformBufferBit and aligned to 256 (≥ any
	// device's minUniformBufferOffsetAlignment). Bound as the last descriptor of a technique that reflects a UBO.
	private readonly InstanceUploadBuffer _uniformUpload;
	// M15-2: per-frame host-visible ring for the bone palette the compute pre-skin pass reads as a structured
	// buffer — StorageBufferBit, 256-aligned (≥ minStorageBufferOffsetAlignment).
	private readonly InstanceUploadBuffer _storageUpload;
	private readonly Sampler _nearestSampler;
	private readonly Sampler _linearSampler;
	// Repeat-addressing, mip-sampling samplers for material textures (tiled across geometry) — a linear and a
	// nearest variant, selected by the technique's LinearFilter flag exactly as the DX12 static sampler is
	// (Wrap + MinMagMip{Linear,Point}). The existing nearest/linear samplers above clamp (post passes never tile).
	private readonly Sampler _repeatLinearSampler;
	private readonly Sampler _repeatNearestSampler;
#if LOOM_IMGUI
	// The host's ImGui renderer (backend-specific), set by the executor when the host enables ImGui. The HUD
	// pass's ctx.DrawImGui() forwards this frame's draw data to it. Null when ImGui is not in use.
	private ImGuiRenderer? _imgui;
#endif
	// M13 resource viewer: GetDebugTextureId returns (resourceId + DebugTextureBase) — a non-zero id the host uses to
	// gate "inspectable?" — and records the resource for this frame's thumbnail grid (the engine draws the images;
	// ImGui.Image can't be used on win-arm64). Cleared after each HUD pass draws them.
	internal const int DebugTextureBase = 0x10000;
	private readonly List<int> _thumbRequests = [];
	// M13 frustum-freeze wireframe: screen-space line segments (x0,y0,x1,y1 in pixels) the host sets each frame.
	private Vector4[] _debugLines = [];
	private int _debugLineCount;
	private const uint DebugLineColor = 0xFF00FFFFu; // IM_COL32 yellow

	// The native ImDrawData* the host produced this frame; stays a plain nint so the field (and the neutral
	// ctx.DrawImGui() seam) exist even when the engine is built without ImGui (UseImGui=false).
	private nint _imguiDrawData;

	// The host's game-UI renderer (the twin of _imgui), set by the engine when the host calls EnableUI. The "ui"
	// pass's ctx.DrawUI(surface) forwards that surface's draw data to it. Null until UI is enabled; MU0 Phase A's
	// renderer is a no-op (PlanningUI.md MU0). Ungated — the neutral seam exists unconditionally (no native dep).
	private UIRenderer? _ui;
	// This frame's composited UI surfaces, pulled from FrameInputs.UISurfaces; ctx.DrawUI looks one up by id.
	private IReadOnlyList<(UISurfaceId Id, UIDrawData Draw)> _uiSurfaces = [];

	private Extent2D _fbExtent;
	// Per-draw cache keyed by (name + attachment formats); keeps the hot path off TechniqueCache's
	// per-call SPIR-V file read. Misses fall through to TechniqueCache (which bakes + content-hashes once).
	// The key is a value type (PipelineKey) so the per-draw lookup allocates nothing (Planning.md §8: 0 GC
	// in the render loop) — the old interpolated-string key heap-allocated on every draw.
	private readonly Dictionary<PipelineKey, Technique> _baked = [];

	private CommandBuffer _cmd;
	// The current pass's color formats, stored inline (not a heap array) so BeginPass copies into them per
	// pass without allocating; _colorCount is how many of the MaxColorFormats slots are live.
	private ColorFormatBuffer _colorFormats;
	private int _colorCount;
	private VkFormat _depthFormat;
	private bool _depthReadOnly;
	private int _frameSlot;
	private Camera _camera;
	private Camera _sunView;
	private IReadOnlyList<Camera> _cascades = [];
	private IReadOnlyList<float> _cascadeSplits = [];
	private uint _hoverId;

	public float DeltaTime { get; private set; }

	public float Time { get; private set; }

	public Camera Camera => _camera;

	public uint HoverId => _hoverId;

	public Camera SunView => _sunView;

	public IReadOnlyList<Camera> Cascades => _cascades;

	public IReadOnlyList<float> CascadeSplits => _cascadeSplits;

	public bool HdrOutput { get; private set; }

	public RenderContext(
		VulkanContext ctx,
		TechniqueCache techniques,
		IReadOnlyDictionary<string, TechniqueTemplate> registry,
		DescriptorAllocator descriptors,
		MeshStore meshes,
		TextureStore textures,
		MaterialStore materials,
		VMASharp.VulkanMemoryAllocator allocator,
		int framesInFlight)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_techniques = techniques;
		_registry = registry;
		_descriptors = descriptors;
		_meshes = meshes;
		_textures = textures;
		_materials = materials;
		_instanceUpload = new InstanceUploadBuffer(ctx, allocator, framesInFlight);
		_uniformUpload = new InstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.UniformBufferBit, alignment: 256);
		_storageUpload = new InstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.StorageBufferBit, alignment: 256);
		_nearestSampler = CreateSampler(Filter.Nearest);
		_linearSampler = CreateSampler(Filter.Linear);
		_repeatLinearSampler = CreateSampler(Filter.Linear, SamplerAddressMode.Repeat);
		_repeatNearestSampler = CreateSampler(Filter.Nearest, SamplerAddressMode.Repeat);
	}

	/// <summary>Bind the context to the active graph's transients — called by the engine on each <c>SetGraph</c>.</summary>
	public void SetTransients(TransientResources resources) => _resources = resources;

	/// <summary>Drop the per-(name,formats) baked-pipeline cache so the next draw re-bakes (shader hot-reload, M13).
	/// The Technique objects are owned by the TechniqueCache; the engine resets that separately after draining.</summary>
	public void ClearBaked() => _baked.Clear();

	/// <summary>Request that <paramref name="resourceId"/> be shown as a thumbnail in this frame's resource-viewer
	/// grid (M13). Called by the engine's GetDebugTextureId as the host enumerates resources to display.</summary>
	public void RequestThumbnail(int resourceId)
	{
		if (!_thumbRequests.Contains(resourceId))
			_thumbRequests.Add(resourceId);
	}

	/// <summary>Set this frame's debug line segments (screen pixels, x0,y0,x1,y1) — the frustum-freeze wireframe the
	/// HUD pass draws as an overlay (M13). Pass an empty span to clear. Overwritten each frame by the host.</summary>
	public void SetDebugLines(ReadOnlySpan<Vector4> segments)
	{
		if (_debugLines.Length < segments.Length)
			_debugLines = new Vector4[segments.Length];
		segments.CopyTo(_debugLines);
		_debugLineCount = segments.Length;
	}

#if LOOM_IMGUI
	/// <summary>The host's ImGui renderer (backend-specific), wired in by the engine when ImGui is enabled.</summary>
	public void SetImGui(ImGuiRenderer renderer) => _imgui = renderer;
#endif

	/// <summary>The host's game-UI renderer (backend-specific), wired in by the engine when UI is enabled (EnableUI).</summary>
	public void SetUI(UIRenderer renderer) => _ui = renderer;

	/// <summary>Bind the context to this frame's command buffer, frame slot, cameras, hover, timing, HDR-output state, and composited UI surfaces before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime, float time, int frameSlot, Camera camera, Camera sunView, IReadOnlyList<Camera> cascades, IReadOnlyList<float> cascadeSplits, uint hoverId, nint imguiDrawData, IReadOnlyList<(UISurfaceId Id, UIDrawData Draw)> uiSurfaces, Extent2D fbExtent, bool hdrOutput)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
		Time = time;
		_frameSlot = frameSlot;
		_camera = camera;
		_sunView = sunView;
		_cascades = cascades;
		_cascadeSplits = cascadeSplits;
		_hoverId = hoverId;
		_imguiDrawData = imguiDrawData;
		_uiSurfaces = uiSurfaces;
		_fbExtent = fbExtent;
		HdrOutput = hdrOutput;
		_instanceUpload.BeginFrame(frameSlot); // reset this slot's per-frame instance staging
		_uniformUpload.BeginFrame(frameSlot);  // and its per-frame uniform staging (M15-0)
		_storageUpload.BeginFrame(frameSlot);  // and the bone-palette staging (M15-2)
	}

	/// <summary>
	/// Set the attachment formats of the current pass — the formats its techniques bake against — plus whether
	/// the depth attachment is bound read-only (<c>.ReadsDepth</c>): a read-only pass bakes with depth writes
	/// disabled so a transparent draw tests against, but does not disturb, the opaque depth.
	/// </summary>
	public void BeginPass(ReadOnlySpan<VkFormat> colorFormats, VkFormat depthFormat, bool depthReadOnly)
	{
		// Copy into the inline buffer (no allocation) rather than .ToArray(); the incoming span points into
		// the executor's stackalloc, which is gone once this returns. Throw — never clamp — on an over-wide
		// pass: silently dropping formats would bake a pipeline with fewer attachments than the pass writes.
		if (colorFormats.Length > MaxColorFormats)
			throw new VulkanException($"A pass has {colorFormats.Length} color attachments; the pipeline-key buffer holds {MaxColorFormats}.");
		_colorCount = colorFormats.Length;
		colorFormats.CopyTo(((Span<VkFormat>)_colorFormats)[.._colorCount]);
		_depthFormat = depthFormat;
		_depthReadOnly = depthReadOnly;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);

		// The per-pass data channel: a technique that reflects a uniform buffer (M15-0) routes the float block
		// there (no 16-float cap — CSM matrices, lighting params); otherwise it rides the fixed scalar push range.
		var ubo = default(UniformBinding);
		if (push.Length > 0)
		{
			if (template.UniformBufferCount > 0)
			{
				ubo = UploadUniforms(push);
			}
			else
			{
				// The fragment scalar channel: pad to the layout's fixed range so the shader always reads defined
				// memory, then push to the fragment stage at offset 0.
				var block = stackalloc float[16];
				for (var i = 0; i < push.Length && i < 16; i++)
					block[i] = push[i];
				_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.FragmentBit, 0, (uint)Technique.FragmentPushSize, block);
			}
		}

		if (inputs.Length > 0 || ubo.Present)
			BindSampledInputs(baked, inputs, template.LinearFilter, ubo);

		_vk.CmdDraw(_cmd, 3, 1, 0, 0); // 3 fabricated verts cover the target; no vertex buffer
	}

	public void Draw(Mesh mesh, string technique) => Draw(mesh, technique, _camera);

	public void Draw(Mesh mesh, string technique, Camera view)
	{
		var baked = Bake(technique);
		var mvp = view.ViewProjection; // model is identity for now; per-object transforms come later
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		_meshes.Draw(_cmd, mesh.Id); // binds the mesh's vertex/index buffers and records the indexed draw
	}

	public void Draw(Mesh mesh, Material material, string technique) => Draw(mesh, material, technique, _camera);

	public void Draw(Mesh mesh, Material material, string technique, Camera view)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);

		// Bind the material's textures into the technique's reflected sampler slots 0..N-1 — the same
		// combined-image-sampler mechanism FullScreen uses, but the views come from the TextureStore (loaded
		// textures), not the transients. A technique that reflects nothing (depth-only/shadow) skips this and the
		// material is ignored. Material textures sample with Repeat addressing when the technique asks for it.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);

		_meshes.Draw(_cmd, mesh.Id);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		if (push.Length > 0)
		{
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)Technique.FragmentPushSize, block);
		}

		var total = sampled.Length + storage.Length;
		if (total > 0)
		{
			// Sampled inputs use the technique's declared filter (matching the Draw/FullScreen path and DX12's
			// static sampler) — not a hardcoded nearest, which silently diverged from DX12 for a LinearFilter pass.
			var sampler = _registry[technique].LinearFilter ? _linearSampler : _nearestSampler;
			// Binding order matches the layout: sampled inputs (combined image samplers, ShaderReadOnly), then
			// storage resources — a storage IMAGE (GENERAL) or, for a Buffer-kind resource, a storage BUFFER.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var bufferInfos = stackalloc DescriptorBufferInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			for (var i = 0; i < total; i++)
			{
				var write = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = (uint)i,
					DescriptorCount = 1,
				};
				if (i < sampled.Length)
				{
					ref var image = ref _resources.Get(_frameSlot, sampled[i].Id);
					imageInfos[i] = new DescriptorImageInfo { Sampler = sampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
					write.DescriptorType = DescriptorType.CombinedImageSampler;
					write.PImageInfo = &imageInfos[i];
				}
				else
				{
					var handle = storage[i - sampled.Length];
					if (handle.Kind == ResourceKind.Buffer)
					{
						bufferInfos[i] = new DescriptorBufferInfo { Buffer = _resources.GetBuffer(_frameSlot, handle.Id), Offset = 0, Range = Vk.WholeSize };
						write.DescriptorType = DescriptorType.StorageBuffer;
						write.PBufferInfo = &bufferInfos[i];
					}
					else
					{
						ref var image = ref _resources.Get(_frameSlot, handle.Id);
						imageInfos[i] = new DescriptorImageInfo { ImageView = image.View, ImageLayout = ImageLayout.General };
						write.DescriptorType = DescriptorType.StorageImage;
						write.PImageInfo = &imageInfos[i];
					}
				}
				writes[i] = write;
			}
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)total, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);
		}

		_vk.CmdDispatch(_cmd, groups.X, groups.Y, groups.Z);
	}

	public void DrawParticles(ResourceHandle buffer, int count, string technique)
	{
		var baked = Bake(technique);
		var mvp = _camera.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		var vertexBuffer = _resources.GetBuffer(_frameSlot, buffer.Id);
		ulong offset = 0;
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
		// The buffer IS the per-instance vertex stream: 6 fabricated quad verts × one instance per particle.
		_vk.CmdDraw(_cmd, 6, (uint)count, 0, 0);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var mvp = _camera.ViewProjection; // the per-instance world matrix rides in the instance stream
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced(Mesh mesh, string technique, BufferHandle instances, int count)
	{
		var baked = Bake(technique);
		var mvp = _camera.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The instance stream is a graph storage buffer a compute filled; the pass's .ReadsVertex derived the barrier.
		var buffer = _resources.GetBuffer(_frameSlot, instances.Id);
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, 0ul, (uint)count);
	}

	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (vertexCount <= 0 || palette.Length == 0)
			return;
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		// vertexCount rides the compute scalar push (as a float — exact for any realistic count).
		var block = stackalloc float[16];
		block[0] = vertexCount;
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)Technique.FragmentPushSize, block);

		// Three storage buffers at bindings 0,1,2: bind-pose (mesh, long-lived) · palette (per-frame ring) · output
		// (the transient the pass .ReadWrites). None but the output is a graph resource, so DispatchSkin writes its
		// own set rather than going through the generic Dispatch storage path.
		var paletteBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(palette);
		var (paletteBuffer, paletteOffset) = _storageUpload.Upload(_frameSlot, paletteBytes);
		var inBuffer = _meshes.BindPoseBuffer(skinnedMesh.Id);
		var outBuffer = _resources.GetBuffer(_frameSlot, output.Id);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var bufferInfos = stackalloc DescriptorBufferInfo[3];
		bufferInfos[0] = new DescriptorBufferInfo { Buffer = inBuffer, Offset = 0, Range = Vk.WholeSize };
		bufferInfos[1] = new DescriptorBufferInfo { Buffer = paletteBuffer, Offset = paletteOffset, Range = (ulong)paletteBytes.Length };
		bufferInfos[2] = new DescriptorBufferInfo { Buffer = outBuffer, Offset = 0, Range = Vk.WholeSize };
		var writes = stackalloc WriteDescriptorSet[3];
		for (var i = 0; i < 3; i++)
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[i],
			};
		_vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);

		_vk.CmdDispatch(_cmd, (uint)((vertexCount + 63) / 64), 1, 1);
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique) => DrawSkinned(mesh, skinnedVertices, technique, _camera);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique, Camera view)
	{
		var baked = Bake(technique);
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The per-vertex stream is the compute's skinned output (MeshStandard-shaped); the mesh contributes its index buffer.
		var vertexBuffer = _resources.GetBuffer(_frameSlot, skinnedVertices.Id);
		_meshes.DrawSkinned(_cmd, mesh.Id, vertexBuffer);
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique) => DrawSkinned(mesh, skinnedVertices, material, technique, _camera);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique, Camera view)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var vertexBuffer = _resources.GetBuffer(_frameSlot, skinnedVertices.Id);
		_meshes.DrawSkinned(_cmd, mesh.Id, vertexBuffer);
	}

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _camera.ViewProjection; // the per-instance world data rides in the instance stream
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// Material atlas → the technique's reflected sampler slots, identical to Draw(mesh, material): every
		// instance samples the shared atlas; the per-instance uv-rect (in the stream) picks each quad's region.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<float> uniforms) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _camera.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The per-frame uniform block (M15-0) — e.g. foliage wind, read in the vertex stage; bound alongside the
		// material's alpha texture into the technique's reflected slots. The MVP still rides the vertex push.
		var ubo = uniforms.Length > 0 ? UploadUniforms(uniforms) : default;
		if (template.SampleCount > 0 || ubo.Present)
			BindMaterialTextures(baked, template, material, technique, ubo);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<ResourceHandle> sampled) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var mvp = _camera.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// Sampled graph resources (the G-buffer) → sampled slots 0..N-1, identical to FullScreen: each instanced
		// light volume samples the normal/albedo at its covered pixel. The graph put them in ShaderReadOnly.
		if (sampled.Length > 0)
			BindSampledInputs(baked, sampled, _registry[technique].LinearFilter);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawParticles(ResourceHandle buffer, int count, Material material, string technique)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _camera.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The sprite-sheet atlas → reflected sampler slots, so the render shader can sub-UV sample per frame.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var vertexBuffer = _resources.GetBuffer(_frameSlot, buffer.Id);
		ulong offset = 0;
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
		_vk.CmdDraw(_cmd, 6, (uint)count, 0, 0); // 6 fabricated quad verts × one instance per particle
	}

	// Bind sampled graph resources (transients) as combined image samplers at bindings 0..N-1 — the path
	// FullScreen and the instanced light volumes share. The graph put each in ShaderReadOnly before this pass.
	// A present uniform buffer (M15-0) is written as the last binding (index inputs.Length), matching the set
	// layout's category order (samplers, then the UBO).
	private void BindSampledInputs(Technique baked, ReadOnlySpan<ResourceHandle> inputs, bool linear, in UniformBinding ubo = default)
	{
		var sampler = linear ? _linearSampler : _nearestSampler;
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = inputs.Length + (ubo.Present ? 1 : 0);
		var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
		var bufferInfo = ubo.Info;
		var writes = stackalloc WriteDescriptorSet[count];
		for (var i = 0; i < inputs.Length; i++)
		{
			ref var image = ref _resources.Get(_frameSlot, inputs[i].Id);
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = image.View,
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		if (ubo.Present)
			writes[inputs.Length] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)inputs.Length,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfo,
			};
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// Bind a material's loaded textures (TextureStore views) into the technique's reflected sampler slots 0..N-1,
	// with the technique's address mode + filter. Shared by Draw(mesh, material), textured instancing, and
	// textured particles. The caller checks SampleCount > 0 (or a present UBO). A present uniform buffer (M15-0)
	// is written as the last binding (index SampleCount) — e.g. foliage's wind, read in the vertex stage.
	private void BindMaterialTextures(Technique baked, TechniqueTemplate template, Material material, string technique, in UniformBinding ubo = default)
	{
		var textures = _materials.TexturesOf(material.Id);
		if (textures.Length < template.SampleCount)
			throw new VulkanException($"Material supplies {textures.Length} textures but technique '{technique}' samples {template.SampleCount}.");

		var sampler = template.Address == SamplerAddress.Repeat
			? (template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler)
			: (template.LinearFilter ? _linearSampler : _nearestSampler);
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = template.SampleCount + (ubo.Present ? 1 : 0);
		var imageInfos = stackalloc DescriptorImageInfo[template.SampleCount];
		var bufferInfo = ubo.Info;
		var writes = stackalloc WriteDescriptorSet[count];
		for (var i = 0; i < template.SampleCount; i++)
		{
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = _textures.View(textures[i]),
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		if (ubo.Present)
			writes[template.SampleCount] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)template.SampleCount,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfo,
			};
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// M15-0: a present per-frame uniform buffer to bind as a technique's last descriptor — the resolved
	// DescriptorBufferInfo plus a presence flag (default => absent). Copying Info to a local before taking its
	// address keeps the pointer valid through vkUpdateDescriptorSets.
	private readonly struct UniformBinding
	{
		public readonly DescriptorBufferInfo Info;
		public readonly bool Present;

		public UniformBinding(Silk.NET.Vulkan.Buffer buffer, ulong offset, ulong range)
		{
			Info = new DescriptorBufferInfo { Buffer = buffer, Offset = offset, Range = range };
			Present = true;
		}
	}

	// Stage a per-frame uniform block into the uniform ring and resolve it to a bindable descriptor (M15-0).
	private UniformBinding UploadUniforms(ReadOnlySpan<float> data)
	{
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data);
		var (buffer, offset) = _uniformUpload.Upload(_frameSlot, bytes);
		return new UniformBinding(buffer, offset, (ulong)bytes.Length);
	}

	// Forward this frame's ImGui draw data to the backend renderer, recording into the (already-open) HUD-pass
	// rendering scope. No-op when ImGui isn't enabled or the host produced no draw data this frame — and a
	// compiled-out no-op entirely when the engine is built without ImGui (UseImGui=false).
	public void DrawImGui()
	{
#if LOOM_IMGUI
		if (_imgui is null)
			return;

		// The ImGui UI (menu, windows, the resource-viewer's name list) first.
		_imgui.Render(_cmd, _imguiDrawData, _fbExtent, _frameSlot);

		// M13 resource viewer: draw the requested resources as a thumbnail grid (engine-drawn — ImGui.Image no-ops on
		// win-arm64). The engine injected a HUD .Samples edge for each inspectable resource, so they're live and in
		// ShaderRead here — sampling a real image, not an aliased corpse. Laid out to the right of the name list.
		if (_thumbRequests.Count > 0)
		{
			const float x0 = 300f, y0 = 30f, w = 200f, h = 112f, gap = 10f;
			var cols = Math.Max(1, (int)((_fbExtent.Width - x0 - 10f) / (w + gap)));
			var thumbs = new ImGuiRenderer.Thumbnail[_thumbRequests.Count];
			for (var i = 0; i < _thumbRequests.Count; i++)
			{
				ref var image = ref _resources.Get(_frameSlot, _thumbRequests[i]);
				var gx = x0 + i % cols * (w + gap);
				var gy = y0 + i / cols * (h + gap);
				thumbs[i] = new ImGuiRenderer.Thumbnail(image.View, gx, gy, w, h);
			}
			_imgui.DrawThumbnails(_cmd, thumbs, _fbExtent, _frameSlot);
			_thumbRequests.Clear();
		}

		// M13 frustum-freeze wireframe: the host projected the frozen frustum's edges to screen pixels; draw them.
		if (_debugLineCount > 0)
			_imgui.DrawLines(_cmd, _debugLines.AsSpan(0, _debugLineCount), DebugLineColor, 2f, _fbExtent, _frameSlot);
#endif
	}

	// Forward this frame's composited UI surface to the backend renderer, recording into the (already-open) pass
	// rendering scope — the twin of DrawImGui for the XAML UI engine. No-op when UI isn't enabled, the host
	// supplied no draw data for this surface, or the surface is empty. MU0 Phase A: the renderer itself no-ops.
	public void DrawUI(UISurfaceId surface)
	{
		if (_ui is null)
			return;

		// Indexed loop, not foreach/LINQ: avoids an enumerator allocation on the per-frame render path (§8: 0 GC).
		UIDrawData? draw = null;
		for (var i = 0; i < _uiSurfaces.Count; i++)
		{
			if (_uiSurfaces[i].Id == surface)
			{
				draw = _uiSurfaces[i].Draw;
				break;
			}
		}

		if (draw is null || draw.Commands.IsEmpty)
			return;

		_ui.Render(_cmd, draw, _fbExtent, _frameSlot);
	}

	// The technique's shape (samples an input? reads a vertex buffer?) comes from its registered template;
	// the attachment formats are the current pass's. So one template used in passes with different targets
	// caches distinct pipelines, and the call site only needs the name. The local cache keeps the per-draw
	// path off TechniqueCache's file read.
	private Technique Bake(string technique)
	{
		if (!_registry.TryGetValue(technique, out var template))
			throw new VulkanException($"No technique registered under the name '{technique}'.");

		var colorFormats = ((ReadOnlySpan<VkFormat>)_colorFormats)[.._colorCount];
		var key = new PipelineKey(technique, colorFormats, _depthFormat, _depthReadOnly);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		// A pass that reads depth read-only (.ReadsDepth) bakes with depth writes off; one that writes it
		// (.WritesDepth, or no depth at all) keeps writes on. Materializing the Format[] here (cache miss only)
		// is a one-time warmup allocation per unique pipeline, not per-frame churn.
		var baked = _techniques.GetOrBake(new TechniqueDescription(
			template.Spirv, template.VertexEntry, template.FragmentEntry, colorFormats.ToArray(),
			template.SampleCount, template.HasVertexInput, _depthFormat, template.Blend, DepthWrite: !_depthReadOnly,
			HasFragmentPush: template.HasFragmentPush, IsCompute: template.IsCompute, StorageCount: template.StorageCount,
			StorageBufferCount: template.StorageBufferCount, ParticleInput: template.ParticleInput,
			Layout: template.Layout, Instance: template.Instance, UniformBufferCount: template.UniformBufferCount), technique);
		// Note: template.Blend / LinearFilter / HasFragmentPush need no extra key term — Blend and
		// HasFragmentPush are folded into the TechniqueDescription hash, and LinearFilter only affects the
		// descriptor's sampler, not the pipeline.
		_baked[key] = baked;
		// Name the baked pipeline for GPU debuggers (once per unique pipeline; [Conditional] — stripped without
		// UseNamedGPUObjects). Distinct target-format variants of one technique share the logical name.
		_ctx.SetObjectName(ObjectType.Pipeline, baked.Pipeline.Handle, $"pipeline:{technique}");
		return baked;
	}

	// Cap on a pass's color attachments — well above any realistic device maxColorAttachments (commonly 8),
	// so PipelineKey's inline buffer never overflows in practice; BeginPass throws if a pass exceeds it.
	private const int MaxColorFormats = 16;

	// A fixed-size, inline (non-heap) buffer of color formats — the variable-length part of PipelineKey,
	// stored by value so the key allocates nothing.
	[InlineArray(MaxColorFormats)]
	private struct ColorFormatBuffer
	{
		private VkFormat _element0;
	}

	// Value-type, allocation-free replacement for the old interpolated-string pipeline-cache key. Distinct
	// attachment formats must map to distinct pipelines, so Equals compares the full key — never a hash-only
	// collapse that could alias a wrong-format pipeline.
	private struct PipelineKey : IEquatable<PipelineKey>
	{
		private readonly string _technique;
		private ColorFormatBuffer _colorFormats;
		private readonly int _colorCount;
		private readonly VkFormat _depthFormat;
		private readonly bool _depthReadOnly;

		public PipelineKey(string technique, ReadOnlySpan<VkFormat> colorFormats, VkFormat depthFormat, bool depthReadOnly)
		{
			// Bake only ever passes a span already sliced to <= MaxColorFormats by BeginPass's guard, so this
			// is an internal-contract check — elided in Release, costing nothing on the per-draw hot path.
			Debug.Assert(colorFormats.Length <= MaxColorFormats, "PipelineKey color formats exceed MaxColorFormats.");
			_technique = technique;
			_colorCount = colorFormats.Length;
			for (var i = 0; i < colorFormats.Length; i++)
				_colorFormats[i] = colorFormats[i];
			_depthFormat = depthFormat;
			_depthReadOnly = depthReadOnly;
		}

		public bool Equals(PipelineKey other)
		{
			if (_colorCount != other._colorCount
				|| _depthFormat != other._depthFormat
				|| _depthReadOnly != other._depthReadOnly
				|| !string.Equals(_technique, other._technique, StringComparison.Ordinal))
				return false;
			for (var i = 0; i < _colorCount; i++)
				if (_colorFormats[i] != other._colorFormats[i])
					return false;
			return true;
		}

		public override bool Equals(object? obj) => obj is PipelineKey other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(_technique, StringComparer.Ordinal);
			hash.Add(_colorCount);
			for (var i = 0; i < _colorCount; i++)
				hash.Add(_colorFormats[i]);
			hash.Add(_depthFormat);
			hash.Add(_depthReadOnly);
			return hash.ToHashCode();
		}
	}

	// Nearest keeps the §9 pixel grid crisp (the upscale blit); linear gives bloom's downsample/upsample
	// the smooth filtering a chunky pixel glow still wants internally. The sampler is part of the combined
	// image-sampler descriptor, so the technique's LinearFilter flag just selects which one FullScreen binds.
	private Sampler CreateSampler(Filter filter, SamplerAddressMode address = SamplerAddressMode.ClampToEdge)
	{
		var mip = filter == Filter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = filter,
			MinFilter = filter,
			MipmapMode = mip,
			AddressModeU = address,
			AddressModeV = address,
			AddressModeW = address,
			MaxLod = Vk.LodClampNone,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out var sampler).Check("vkCreateSampler");
		return sampler;
	}

	public void Dispose()
	{
		_instanceUpload.Dispose();
		_uniformUpload.Dispose();
		_storageUpload.Dispose();
		if (_nearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _nearestSampler, null);
		if (_linearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _linearSampler, null);
		if (_repeatLinearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _repeatLinearSampler, null);
		if (_repeatNearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _repeatNearestSampler, null);
	}
}
