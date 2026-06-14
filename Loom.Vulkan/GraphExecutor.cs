using Loom.Core;
using Loom.Graph;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The graph-driven renderer (Planning.md §2 Execute/Bake): it derives a <see cref="GraphSchedule"/> and
/// <see cref="BarrierPlan"/> from an immutable <see cref="RenderGraph"/> <em>once</em> (the plan is
/// frame-stable, so the per-frame cost is recording, not deriving), then each frame records the plan into a
/// command buffer — emitting <c>vkCmdPipelineBarrier2</c> from the derived transitions, opening a
/// dynamic-rendering scope per pass with the derived load/store ops, invoking the pass's record callback,
/// and presenting. This replaces M0/M1's hand-written <c>Renderer</c>: the barriers and layout transitions
/// it wrote by hand are now derived (Planning.md §2).
/// <para>
/// <b>Frames in flight (§12 M4).</b> Runs 2 frames in flight, paced on a per-frame fence. The safety this
/// needs — a frame's writes to a transient must not clobber a still-executing earlier frame's reads of the
/// same image — comes from <see cref="TransientResources"/> giving each transient one physical image per
/// frame slot; each frame records against its own slot (<see cref="_currentFrame"/>). The swapchain supplies
/// distinct backbuffer images itself, and recording is serial on the CPU (the image handle is baked into the
/// command buffer), so the backbuffer needs no extra buffering.
/// </para>
/// </summary>
public sealed unsafe class GraphExecutor : IDisposable
{
	private const int MaxFramesInFlight = 2;
	private static readonly ClearValue ClearColor = new(color: new ClearColorValue(0, 0, 0, 1));
	private static readonly ClearValue ClearDepth = new(depthStencil: new ClearDepthStencilValue(1.0f, 0));

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly Swapchain _swapchain;
	private readonly RenderGraph _graph;
	private readonly BarrierPlan _plan;
	private readonly AliasPlan _aliasPlan;
	private readonly HashSet<int> _aliasedHandoffIds; // resources that reuse an earlier alias's memory this frame
	private readonly HashSet<int> _persistentIds;     // resources whose contents survive across frames
	private readonly Dictionary<int, ResourceState> _persistentState = []; // last state, carried across frames
	private readonly HashSet<int> _bufferIds;         // storage/vertex buffer resources (emit buffer barriers, not image)
	private readonly VulkanMemoryAllocator _allocator;
	private readonly TechniqueCache _techniques;
	private readonly TransientResources _transients;
	private readonly MeshStore _meshes;
	private readonly DescriptorAllocator _descriptors;
	private readonly RenderContext _context;
	private ImGuiRenderer? _imgui; // created on demand when the host calls EnableImGui

	private CommandPool _commandPool;
	private CommandBuffer[] _commandBuffers = [];
	private Semaphore[] _imageAvailable = []; // per frame-in-flight
	private Semaphore[] _renderFinished = []; // per swapchain image
	private Fence[] _inFlight = [];           // per frame-in-flight

	// GPU→CPU readback (§7.4 picking): the first g.Readback target is copied to a per-slot host-visible
	// buffer each frame; the result is read back ~2 frames later (when the slot's fence next signals), kept
	// in _readbackCpu as decoded floats. -1 = no readback declared.
	private readonly int _readbackId = -1;
	private Buffer[] _readbackBuffers = [];
	private DeviceMemory[] _readbackMemories = [];
	private bool[] _readbackValid = [];
	private float[] _readbackCpu = [];
	private int _readbackWidth;
	private int _readbackHeight;

	private int _currentFrame;
	private bool _framebufferResized;
	private Extent2D _windowExtent;
	private string? _capturePath;

	public GraphExecutor(
		VulkanContext ctx,
		RenderGraph graph,
		IReadOnlyDictionary<string, TechniqueTemplate> techniques,
		Vector2D<int> framebufferSize,
		bool vsync = true)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_graph = graph;
		_windowExtent = new Extent2D((uint)framebufferSize.X, (uint)framebufferSize.Y);
		_swapchain = new Swapchain(ctx, _windowExtent, vsync);

		// Derive once — the graph is immutable, so order/barriers/load-store/lifetimes are frame-invariant.
		var schedule = GraphSchedule.Derive(graph);
		_plan = BarrierPlan.Derive(schedule);
		_aliasPlan = AliasPlan.Derive(LifetimePlan.Derive(schedule));

		// Each bucket's members share one block; every member after the first reuses an earlier alias's memory
		// this frame, so its Undefined→… birth transition must wait on all prior work to that memory (the
		// aliasing barrier — see EmitTransition). The first member is covered by the per-frame fence instead.
		_aliasedHandoffIds = [];
		foreach (var bucket in _aliasPlan.Buckets)
			for (var i = 1; i < bucket.Members.Length; i++)
				_aliasedHandoffIds.Add(bucket.Members[i].Id);

		// Persistent resources (auto-exposure history) keep their contents across frames, so their first
		// transition each frame must start from the layout the previous frame left them in, not Undefined.
		_persistentIds = [];
		_bufferIds = [];
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Persistent)
				_persistentIds.Add(resource.Id);
			if (resource.Kind == ResourceKind.Buffer)
				_bufferIds.Add(resource.Id);
		}
		if (_aliasPlan.AliasableCount > _aliasPlan.Buckets.Length)
			Console.WriteLine($"[loom] transient aliasing: {_aliasPlan.AliasableCount} transients folded into {_aliasPlan.Buckets.Length} memory buckets.");

		// One VMA allocator backs both transients and meshes. The command pool must exist before the mesh
		// store (geometry uploads use a one-time copy command), so create it up front.
		_allocator = new VulkanMemoryAllocator(new VulkanMemoryAllocatorCreateInfo(
			Vk.Version13, _vk, ctx.Instance, ctx.PhysicalDevice, ctx.Device));
		CreateCommandPool();

		_techniques = new TechniqueCache(ctx);
		_transients = new TransientResources(ctx, graph, _aliasPlan, _swapchain.ImageFormat, _swapchain.Extent, MaxFramesInFlight, _allocator);
		_meshes = new MeshStore(ctx, _allocator, _commandPool);
		_descriptors = new DescriptorAllocator(ctx, MaxFramesInFlight);
		_context = new RenderContext(ctx, _techniques, techniques, _transients, _descriptors, _meshes);

		CreateCommandBuffers();
		CreateSyncObjects();

		// Readback staging (§7.4): one host-visible buffer per frame slot for the first g.Readback target.
		if (graph.ReadbackTargets.Length > 0)
		{
			var target = graph.ReadbackTargets[0];
			_readbackId = target.Id;
			(_readbackWidth, _readbackHeight) = target.Resolve();
			_readbackCpu = new float[_readbackWidth * _readbackHeight];
			var bytes = (ulong)_readbackWidth * (ulong)_readbackHeight * 2; // R16F = 2 bytes/texel
			_readbackBuffers = new Buffer[MaxFramesInFlight];
			_readbackMemories = new DeviceMemory[MaxFramesInFlight];
			_readbackValid = new bool[MaxFramesInFlight];
			for (var i = 0; i < MaxFramesInFlight; i++)
				(_readbackBuffers[i], _readbackMemories[i]) = CreateStagingBuffer(bytes);
		}
	}

	/// <summary>
	/// Sample the picking readback at a world-domain pixel (§7.4). Returns the resource's value there from a
	/// frame or two ago (the readback latency is real). 0 outside the populated region or before warm-up.
	/// </summary>
	public float SamplePickReadback(int x, int y)
	{
		if (_readbackId < 0 || (uint)x >= (uint)_readbackWidth || (uint)y >= (uint)_readbackHeight)
			return 0f;
		return _readbackCpu[y * _readbackWidth + x];
	}

	/// <summary>
	/// Upload geometry once and get a <see cref="Mesh"/> handle for passes to draw via <c>ctx.Draw</c>.
	/// Call after construction but before the render loop; the handle is stable for the executor's lifetime.
	/// </summary>
	public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	public void OnResize(Vector2D<int> size)
	{
		_windowExtent = new Extent2D((uint)size.X, (uint)size.Y);
		_framebufferResized = true;
	}

	public void WaitIdle() => _vk.DeviceWaitIdle(_ctx.Device);

	/// <summary>Capture the next rendered frame to a PNG (debug/validation). Synchronous on that frame.</summary>
	public void RequestCapture(string path) => _capturePath = path;

	/// <summary>
	/// The camera the next frame renders from. Per-frame data the host sets before <see cref="DrawFrame"/>;
	/// passes pull it via <c>ctx.Camera</c>. Defaults to identity (clip space == world space).
	/// </summary>
	public Camera Camera { get; set; } = Camera.Identity;

	/// <summary>
	/// The object id under the cursor for the next frame (0 = nothing), set by the host. Passes pull it via
	/// <c>ctx.HoverId</c> — the JFA outline seeds the hovered object's silhouette. Per-frame, like the camera.
	/// </summary>
	public uint HoverId { get; set; }

	/// <summary>The sun's view + projection for the next frame, set by the host. The shadow pass renders the
	/// scene from it; lighting projects into it for the shadow test. Defaults to identity.</summary>
	public Camera SunView { get; set; } = Camera.Identity;

	/// <summary>Elapsed seconds since startup, set by the host each frame; passes pull it via <c>ctx.Time</c>
	/// for time-driven effects (the GPU particle fountain).</summary>
	public float Time { get; set; }

	/// <summary>
	/// This frame's Dear ImGui draw data — the native <c>ImDrawData*</c> the host produced via
	/// <c>ImGui.Render()</c>, set before <see cref="DrawFrame"/>. The HUD pass's <c>ctx.DrawImGui()</c> hands it
	/// to the backend ImGui renderer. <see cref="nint.Zero"/> (the default) draws no UI. Only meaningful after
	/// <see cref="EnableImGui"/>.
	/// </summary>
	public nint ImGuiDrawData { get; set; }

	/// <summary>
	/// Stand up the backend Dear ImGui renderer (the <c>imgui_impl_vulkan</c> equivalent), baking its pipeline
	/// against the swapchain format and uploading the current ImGui context's font atlas. Call once after
	/// construction, before the render loop, with the path to the compiled <c>imgui.spv</c>. The host must have
	/// created the ImGui context (and configured its fonts) first. After this, a pass that <c>.Blends</c> the
	/// backbuffer can call <c>ctx.DrawImGui()</c>, and the host sets <see cref="ImGuiDrawData"/> each frame.
	/// </summary>
	public void EnableImGui(string spirvPath)
	{
		var spirv = File.ReadAllBytes(spirvPath);
		_imgui = new ImGuiRenderer(_ctx, _allocator, _commandPool, _swapchain.ImageFormat, spirv, MaxFramesInFlight);
		_context.SetImGui(_imgui);
	}

	public void DrawFrame(double deltaSeconds)
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return; // Minimized — nothing to draw.

		_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);

		// The fence just signalled, so this slot's readback buffer holds the copy from when it last ran (~2
		// frames ago). Decode it into _readbackCpu before this frame overwrites it — that's the latency §7.4
		// is upfront about. (Done before recording, so SamplePickReadback always sees the freshest available.)
		DownloadReadback(_currentFrame);

		uint imageIndex = 0;
		var acquire = _ctx.KhrSwapchain.AcquireNextImage(
			_ctx.Device, _swapchain.Handle, ulong.MaxValue,
			_imageAvailable[_currentFrame], default, ref imageIndex);

		if (acquire == Result.ErrorOutOfDateKhr)
		{
			RecreateSwapchain();
			return;
		}
		if (acquire is not (Result.Success or Result.SuboptimalKhr))
			acquire.Check("vkAcquireNextImageKHR");

		_vk.ResetFences(_ctx.Device, 1, in _inFlight[_currentFrame]);

		var capturePath = _capturePath;
		var captureSize = (ulong)_swapchain.Extent.Width * _swapchain.Extent.Height * 4;
		Buffer captureBuffer = default;
		DeviceMemory captureMemory = default;
		if (capturePath is not null)
			(captureBuffer, captureMemory) = CreateStagingBuffer(captureSize);

		// This frame slot's prior work is done (fence waited), so its descriptor sets are idle: recycle them.
		_descriptors.BeginFrame(_currentFrame);
		_transients.SetBackbuffer(_currentFrame, _swapchain.Images[imageIndex], _swapchain.ImageViews[imageIndex], _swapchain.Extent);

		var cmd = _commandBuffers[_currentFrame];
		_vk.ResetCommandBuffer(cmd, 0);
		RecordGraph(cmd, captureBuffer, (float)deltaSeconds);

		SemaphoreSubmitInfo waitInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _imageAvailable[_currentFrame],
			StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
		};
		SemaphoreSubmitInfo signalInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _renderFinished[imageIndex],
			StageMask = PipelineStageFlags2.AllCommandsBit,
		};
		CommandBufferSubmitInfo cmdInfo = new()
		{
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = cmd,
		};
		SubmitInfo2 submit = new()
		{
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = 1,
			PWaitSemaphoreInfos = &waitInfo,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &cmdInfo,
			SignalSemaphoreInfoCount = 1,
			PSignalSemaphoreInfos = &signalInfo,
		};
		_vk.QueueSubmit2(_ctx.GraphicsQueue, 1, in submit, _inFlight[_currentFrame]).Check("vkQueueSubmit2");

		if (capturePath is not null)
		{
			_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);
			WriteCapture(captureMemory, captureSize, _swapchain.Extent.Width, _swapchain.Extent.Height, capturePath);
			_vk.DestroyBuffer(_ctx.Device, captureBuffer, null);
			_vk.FreeMemory(_ctx.Device, captureMemory, null);
			_capturePath = null;
		}

		var swapchain = _swapchain.Handle;
		var renderFinished = _renderFinished[imageIndex];
		var presentIndex = imageIndex;
		PresentInfoKHR present = new()
		{
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1,
			PWaitSemaphores = &renderFinished,
			SwapchainCount = 1,
			PSwapchains = &swapchain,
			PImageIndices = &presentIndex,
		};
		var result = _ctx.KhrSwapchain.QueuePresent(_ctx.PresentQueue, in present);
		if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr || _framebufferResized)
		{
			_framebufferResized = false;
			RecreateSwapchain();
		}
		else if (result != Result.Success)
		{
			result.Check("vkQueuePresentKHR");
		}

		_currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
	}

	private void RecordGraph(CommandBuffer cmd, Buffer captureBuffer, float deltaTime)
	{
		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		_vk.BeginCommandBuffer(cmd, in beginInfo).Check("vkBeginCommandBuffer");

		_context.BeginFrame(cmd, deltaTime, Time, _currentFrame, Camera, SunView, HoverId, ImGuiDrawData, _swapchain.Extent);

		foreach (var pass in _plan.Passes)
		{
			foreach (var transition in pass.Transitions)
				EmitTransition(cmd, transition);
			RecordPass(cmd, pass);
		}

		RecordReadback(cmd);
		FinishFrame(cmd, captureBuffer);

		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
	}

	// Copy the readback target (its last graph use is a .Samples, so it's in SHADER_READ) into this slot's
	// host-visible buffer. The slot's fence guards it; DownloadReadback decodes it ~2 frames later. §7.4.
	private void RecordReadback(CommandBuffer cmd)
	{
		if (_readbackId < 0)
			return;

		ref var image = ref _transients.Get(_currentFrame, _readbackId);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
		TransitionImage(cmd, image.Image, range,
			ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

		BufferImageCopy region = new()
		{
			ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
			ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
		};
		_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, _readbackBuffers[_currentFrame], 1, in region);
		_readbackValid[_currentFrame] = true;
	}

	// Decode this slot's readback buffer (R16F halves → floats) into _readbackCpu. Called after the slot's
	// fence signals, so the data is the copy from when the slot last ran (a frame or two ago).
	private void DownloadReadback(int slot)
	{
		if (_readbackId < 0 || !_readbackValid[slot])
			return;

		var count = _readbackWidth * _readbackHeight;
		void* mapped;
		_vk.MapMemory(_ctx.Device, _readbackMemories[slot], 0, (ulong)count * 2, 0, &mapped).Check("vkMapMemory");
		var halves = new ReadOnlySpan<Half>(mapped, count);
		for (var i = 0; i < count; i++)
			_readbackCpu[i] = (float)halves[i];
		_vk.UnmapMemory(_ctx.Device, _readbackMemories[slot]);
	}

	private void RecordPass(CommandBuffer cmd, PassBarriers pass)
	{
		// Compute passes have no attachments and no dynamic-rendering scope — just bind + dispatch. The
		// resource transitions (storage GENERAL, the consumer's SHADER_READ) were already emitted before this.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_context.BeginPass([], default, false); // no colour/depth targets; keeps the bake key stable
			pass.Pass.Record?.Invoke(_context);
			return;
		}

		var attachments = pass.Attachments;
		var color = stackalloc RenderingAttachmentInfo[attachments.Length];
		var colorCount = 0;
		RenderingAttachmentInfo depth = default;
		var hasDepth = false;
		var depthReadOnly = false;
		Extent2D area = default;
		var colorFormats = stackalloc VkFormat[attachments.Length];
		VkFormat depthFormat = default;

		foreach (var attachment in attachments)
		{
			ref var image = ref _transients.Get(_currentFrame, attachment.Resource.Id);
			if (area.Width == 0)
				area = image.Extent; // all of a pass's attachments share a domain → one render area

			if (attachment.State == ResourceState.ColorAttachment)
			{
				colorFormats[colorCount] = image.Format;
				color[colorCount++] = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = ImageLayout.ColorAttachmentOptimal,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					ClearValue = ClearColor,
				};
			}
			else
			{
				depthFormat = image.Format;
				depthReadOnly = attachment.State == ResourceState.DepthReadOnly;
				depth = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = StateMapping.For(attachment.State).Layout,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					ClearValue = ClearDepth,
				};
				hasDepth = true;
			}
		}

		RenderingInfo info = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), area),
			LayerCount = 1,
			ColorAttachmentCount = (uint)colorCount,
			PColorAttachments = colorCount > 0 ? color : null,
			PDepthAttachment = hasDepth ? &depth : null,
		};
		_vk.CmdBeginRendering(cmd, in info);

		Viewport viewport = new()
		{
			X = 0,
			Y = 0,
			Width = area.Width,
			Height = area.Height,
			MinDepth = 0,
			MaxDepth = 1,
		};
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);
		Rect2D scissor = new(new Offset2D(0, 0), area);
		_vk.CmdSetScissor(cmd, 0, 1, in scissor);

		_context.BeginPass(new ReadOnlySpan<VkFormat>(colorFormats, colorCount), depthFormat, depthReadOnly);
		pass.Pass.Record?.Invoke(_context);

		_vk.CmdEndRendering(cmd);
	}

	// Terminal: hand the present target to the swapchain, copying it out first if a capture was requested.
	private void FinishFrame(CommandBuffer cmd, Buffer captureBuffer)
	{
		if (_graph.PresentTarget is not { } present)
			return; // Headless graph: nothing to present.

		ref var image = ref _transients.Get(_currentFrame, present.Id);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

		if (captureBuffer.Handle != 0)
		{
			TransitionImage(cmd, image.Image, range,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, captureBuffer, 1, in region);

			TransitionImage(cmd, image.Image, range,
				ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
		}
		else
		{
			foreach (var transition in _plan.FinalTransitions)
				EmitTransition(cmd, transition);
		}
	}

	private void EmitTransition(CommandBuffer cmd, ResourceTransition transition)
	{
		var id = transition.Resource.Id;

		// Buffers have no layout — emit a buffer memory barrier scoped by the from/to stage+access. This is the
		// compute(StorageWrite) → vertex-input(VertexAttributeRead) hand-off for GPU particles (§7.6).
		if (_bufferIds.Contains(id))
		{
			EmitBufferBarrier(cmd, _transients.GetBuffer(_currentFrame, id), StateMapping.For(transition.From), StateMapping.For(transition.To));
			return;
		}

		ref var image = ref _transients.Get(_currentFrame, id);

		// Persistent resources survive across frames. The barrier plan, derived per-frame, thinks every
		// resource starts Undefined; for a persistent one that would discard the value the next frame's compute
		// needs to read. Substitute the real layout/state the previous frame left it in (tracked here), so the
		// transition preserves contents. First frame ever: no entry yet, so it stays Undefined (garbage, which
		// the exposure adaptation converges away from). The single shared image + in-order submission make the
		// resulting SHADER_READ→GENERAL barrier the cross-frame read/write hazard guard.
		var fromState = transition.From;
		if (_persistentIds.Contains(id) && fromState == ResourceState.Undefined && _persistentState.TryGetValue(id, out var carried))
			fromState = carried;
		if (_persistentIds.Contains(id))
			_persistentState[id] = transition.To;

		var src = StateMapping.For(fromState);
		var dst = StateMapping.For(transition.To);
		var srcStage = src.Stage;
		var srcAccess = src.Access;

		// Aliasing barrier: when a resource is born onto memory an earlier alias used this frame (an
		// Undefined→… transition for a non-first bucket member), the fresh-memory TopOfPipe source scope is
		// unsafe — the predecessor's GPU work on that memory may still be running. Widen the source scope to
		// "all prior commands / all memory" so this image's first write waits for the predecessor to finish.
		// (Conservative but correct; hand-offs are few. The Undefined old layout already discards the alias's
		// contents, which is exactly the aliasing semantic.)
		if (transition.From == ResourceState.Undefined && _aliasedHandoffIds.Contains(transition.Resource.Id))
		{
			srcStage = PipelineStageFlags2.AllCommandsBit;
			srcAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
		}

		var range = new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1);
		TransitionImage(cmd, image.Image, range, src.Layout, dst.Layout, srcStage, srcAccess, dst.Stage, dst.Access);
	}

	private void TransitionImage(
		CommandBuffer cmd, Image image, ImageSubresourceRange range,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		ImageMemoryBarrier2 barrier = new()
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = range,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	// A buffer memory barrier (no layout) for the storage→vertex hand-off of a GPU-particle buffer.
	private void EmitBufferBarrier(CommandBuffer cmd, Buffer buffer, StateMapping.Scope from, StateMapping.Scope to)
	{
		BufferMemoryBarrier2 barrier = new()
		{
			SType = StructureType.BufferMemoryBarrier2,
			SrcStageMask = from.Stage,
			SrcAccessMask = from.Access,
			DstStageMask = to.Stage,
			DstAccessMask = to.Access,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Buffer = buffer,
			Offset = 0,
			Size = Vk.WholeSize,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			BufferMemoryBarrierCount = 1,
			PBufferMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	private static AttachmentLoadOp ToLoadOp(LoadAction load) => load switch
	{
		LoadAction.Clear => AttachmentLoadOp.Clear,
		LoadAction.Load => AttachmentLoadOp.Load,
		_ => AttachmentLoadOp.DontCare,
	};

	private static AttachmentStoreOp ToStoreOp(StoreAction store) =>
		store == StoreAction.Store ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

	private (Buffer Buffer, DeviceMemory Memory) CreateStagingBuffer(ulong size)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};
		_vk.CreateBuffer(_ctx.Device, in info, null, out var buffer).Check("vkCreateBuffer");

		_vk.GetBufferMemoryRequirements(_ctx.Device, buffer, out var requirements);
		MemoryAllocateInfo alloc = new()
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = _ctx.FindMemoryType(
				requirements.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
		};
		_vk.AllocateMemory(_ctx.Device, in alloc, null, out var memory).Check("vkAllocateMemory");
		_vk.BindBufferMemory(_ctx.Device, buffer, memory, 0).Check("vkBindBufferMemory");
		return (buffer, memory);
	}

	private void WriteCapture(DeviceMemory memory, ulong size, uint width, uint height, string path)
	{
		void* mapped;
		_vk.MapMemory(_ctx.Device, memory, 0, size, 0, &mapped).Check("vkMapMemory");
		var src = new ReadOnlySpan<byte>(mapped, (int)size);

		// Swapchain is typically B8G8R8A8; PNG wants RGBA, so swap R/B for those formats.
		var swizzle = _swapchain.ImageFormat is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm;
		var rgba = new byte[(int)size];
		for (var i = 0; i < (int)(width * height); i++)
		{
			var o = i * 4;
			rgba[o] = swizzle ? src[o + 2] : src[o];
			rgba[o + 1] = src[o + 1];
			rgba[o + 2] = swizzle ? src[o] : src[o + 2];
			rgba[o + 3] = src[o + 3];
		}
		_vk.UnmapMemory(_ctx.Device, memory);

		PngWriter.WriteRgba(path, (int)width, (int)height, rgba);
	}

	private void CreateCommandPool()
	{
		CommandPoolCreateInfo info = new()
		{
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
			QueueFamilyIndex = _ctx.GraphicsFamily,
		};
		_vk.CreateCommandPool(_ctx.Device, in info, null, out _commandPool).Check("vkCreateCommandPool");
	}

	private void CreateCommandBuffers()
	{
		_commandBuffers = new CommandBuffer[MaxFramesInFlight];
		CommandBufferAllocateInfo info = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = _commandPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = MaxFramesInFlight,
		};
		fixed (CommandBuffer* p = _commandBuffers)
			_vk.AllocateCommandBuffers(_ctx.Device, in info, p).Check("vkAllocateCommandBuffers");
	}

	private void CreateSyncObjects()
	{
		_imageAvailable = new Semaphore[MaxFramesInFlight];
		_inFlight = new Fence[MaxFramesInFlight];

		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _imageAvailable[i]).Check("vkCreateSemaphore");
			_vk.CreateFence(_ctx.Device, in fenceInfo, null, out _inFlight[i]).Check("vkCreateFence");
		}

		CreateRenderFinishedSemaphores();
	}

	private void CreateRenderFinishedSemaphores()
	{
		_renderFinished = new Semaphore[_swapchain.ImageCount];
		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		for (var i = 0; i < _renderFinished.Length; i++)
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _renderFinished[i]).Check("vkCreateSemaphore");
	}

	private void RecreateSwapchain()
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return;

		_vk.DeviceWaitIdle(_ctx.Device);
		var oldImageCount = _swapchain.ImageCount;
		_swapchain.Recreate(_windowExtent);
		_transients.Recreate(_swapchain.Extent);

		if (_swapchain.ImageCount != oldImageCount)
		{
			DestroyRenderFinishedSemaphores();
			CreateRenderFinishedSemaphores();
		}
	}

	private void DestroyRenderFinishedSemaphores()
	{
		foreach (var semaphore in _renderFinished)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		_renderFinished = [];
	}

	public void Dispose()
	{
		_vk.DeviceWaitIdle(_ctx.Device);

		_context.Dispose();
		_imgui?.Dispose(); // before the allocator — it holds VMA font/vertex/index allocations
		_descriptors.Dispose();
		_meshes.Dispose();
		_transients.Dispose();
		_allocator.Dispose(); // after every image/buffer it backed is freed
		_techniques.Dispose();

		for (var i = 0; i < _readbackBuffers.Length; i++)
		{
			if (_readbackBuffers[i].Handle != 0)
				_vk.DestroyBuffer(_ctx.Device, _readbackBuffers[i], null);
			if (_readbackMemories[i].Handle != 0)
				_vk.FreeMemory(_ctx.Device, _readbackMemories[i], null);
		}

		DestroyRenderFinishedSemaphores();
		foreach (var semaphore in _imageAvailable)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		foreach (var fence in _inFlight)
			if (fence.Handle != 0)
				_vk.DestroyFence(_ctx.Device, fence, null);

		if (_commandPool.Handle != 0)
			_vk.DestroyCommandPool(_ctx.Device, _commandPool, null);

		_swapchain.Dispose();
	}
}
