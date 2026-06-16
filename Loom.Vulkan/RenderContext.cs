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
	private readonly Sampler _nearestSampler;
	private readonly Sampler _linearSampler;
#if LOOM_IMGUI
	// The host's ImGui renderer (backend-specific), set by the executor when the host enables ImGui. The HUD
	// pass's ctx.DrawImGui() forwards this frame's draw data to it. Null when ImGui is not in use.
	private ImGuiRenderer? _imgui;
#endif
	// The native ImDrawData* the host produced this frame; stays a plain nint so the field (and the neutral
	// ctx.DrawImGui() seam) exist even when the engine is built without ImGui (UseImGui=false).
	private nint _imguiDrawData;
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
	private uint _hoverId;

	public float DeltaTime { get; private set; }

	public float Time { get; private set; }

	public Camera Camera => _camera;

	public uint HoverId => _hoverId;

	public Camera SunView => _sunView;

	public bool HdrOutput { get; private set; }

	public RenderContext(
		VulkanContext ctx,
		TechniqueCache techniques,
		IReadOnlyDictionary<string, TechniqueTemplate> registry,
		DescriptorAllocator descriptors,
		MeshStore meshes)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_techniques = techniques;
		_registry = registry;
		_descriptors = descriptors;
		_meshes = meshes;
		_nearestSampler = CreateSampler(Filter.Nearest);
		_linearSampler = CreateSampler(Filter.Linear);
	}

	/// <summary>Bind the context to the active graph's transients — called by the engine on each <c>SetGraph</c>.</summary>
	public void SetTransients(TransientResources resources) => _resources = resources;

#if LOOM_IMGUI
	/// <summary>The host's ImGui renderer (backend-specific), wired in by the engine when ImGui is enabled.</summary>
	public void SetImGui(ImGuiRenderer renderer) => _imgui = renderer;
#endif

	/// <summary>Bind the context to this frame's command buffer, frame slot, cameras, hover, timing, and HDR-output state before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime, float time, int frameSlot, Camera camera, Camera sunView, uint hoverId, nint imguiDrawData, Extent2D fbExtent, bool hdrOutput)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
		Time = time;
		_frameSlot = frameSlot;
		_camera = camera;
		_sunView = sunView;
		_hoverId = hoverId;
		_imguiDrawData = imguiDrawData;
		_fbExtent = fbExtent;
		HdrOutput = hdrOutput;
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
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);

		if (push.Length > 0)
		{
			// The fragment scalar channel: pad to the layout's fixed range so the shader always reads defined
			// memory, then push to the fragment stage at offset 0.
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.FragmentBit, 0, (uint)Technique.FragmentPushSize, block);
		}

		if (inputs.Length > 0)
		{
			// Bind each sampled input as a combined image sampler at bindings 0..N-1 (the graph put them in
			// ShaderReadOnly before this pass). One transient descriptor set, recycled with the frame slot.
			var sampler = _registry[technique].LinearFilter ? _linearSampler : _nearestSampler;
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
			var writes = stackalloc WriteDescriptorSet[inputs.Length];
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
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)inputs.Length, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
		}

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
					imageInfos[i] = new DescriptorImageInfo { Sampler = _nearestSampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
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

	// Forward this frame's ImGui draw data to the backend renderer, recording into the (already-open) HUD-pass
	// rendering scope. No-op when ImGui isn't enabled or the host produced no draw data this frame — and a
	// compiled-out no-op entirely when the engine is built without ImGui (UseImGui=false).
	public void DrawImGui()
	{
#if LOOM_IMGUI
		_imgui?.Render(_cmd, _imguiDrawData, _fbExtent, _frameSlot);
#endif
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
			StorageBufferCount: template.StorageBufferCount, ParticleInput: template.ParticleInput), technique);
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
	private Sampler CreateSampler(Filter filter)
	{
		var mip = filter == Filter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = filter,
			MinFilter = filter,
			MipmapMode = mip,
			AddressModeU = SamplerAddressMode.ClampToEdge,
			AddressModeV = SamplerAddressMode.ClampToEdge,
			AddressModeW = SamplerAddressMode.ClampToEdge,
			MaxLod = Vk.LodClampNone,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out var sampler).Check("vkCreateSampler");
		return sampler;
	}

	public void Dispose()
	{
		if (_nearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _nearestSampler, null);
		if (_linearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _linearSampler, null);
	}
}
