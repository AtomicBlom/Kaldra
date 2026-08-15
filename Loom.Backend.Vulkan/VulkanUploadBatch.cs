using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Backend.Vulkan;

/// <summary>
/// One command buffer that every resource upload records into, submitted once and never waited on.
/// <para>
/// Each <c>CreateTexture</c> / <c>CreateMesh</c> used to open its own one-shot command buffer, submit it, and block
/// on <c>vkQueueWaitIdle</c> — a full GPU drain per resource, and two per mesh, because vertices and indices were
/// copied separately. Loading a scene's worth of assets meant dozens of drains; streaming one texture mid-gameplay
/// meant stalling the frame that asked for it.
/// </para>
/// <para>
/// <b>Why no wait is needed.</b> Uploads are drained at the top of a frame, so this batch is submitted to the
/// graphics queue before that frame's own command buffer. Each upload ends with a barrier whose second scope covers
/// the reads that will consume it, and a barrier's synchronization scopes extend to commands in <em>later
/// submissions on the same queue</em> — so the copy is ordered before any draw that samples the result without the
/// CPU waiting for anything.
/// </para>
/// <para>
/// The fence exists only to know when the staging buffers may be freed, and it is checked one batch late — by the
/// time a frame's uploads are flushed, the previous frame's copies have long finished, so the wait costs nothing in
/// the steady state while remaining correct if they somehow have not.
/// </para>
/// </summary>
internal sealed unsafe class VulkanUploadBatch : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly CommandPool _pool;
	private readonly Fence _fence;

	private CommandBuffer _cmd;
	private bool _recording;
	private bool _submitted;

	// Staging allocations the open batch will need until its copies have run. They move to _inFlight on submit and
	// are freed once the fence for that submit is known signalled.
	private readonly List<(Buffer Buffer, Allocation Allocation)> _staging = [];
	private readonly List<(Buffer Buffer, Allocation Allocation)> _inFlight = [];

	public VulkanUploadBatch(VulkanContext ctx, CommandPool pool)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_pool = pool;
		FenceCreateInfo info = new() { SType = StructureType.FenceCreateInfo };
		_vk.CreateFence(_ctx.Device, in info, null, out _fence).Check("vkCreateFence");
	}

	/// <summary>The command buffer to record copies and layout transitions into, opening the batch if it is not
	/// already open. Every upload in one drain shares it.</summary>
	public CommandBuffer Recorder()
	{
		if (_recording)
			return _cmd;

		ReclaimStaging();
		if (_cmd.Handle == 0)
		{
			CommandBufferAllocateInfo allocInfo = new()
			{
				SType = StructureType.CommandBufferAllocateInfo,
				CommandPool = _pool,
				Level = CommandBufferLevel.Primary,
				CommandBufferCount = 1,
			};
			_vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out _cmd).Check("vkAllocateCommandBuffers");
		}
		CommandBufferBeginInfo begin = new()
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};
		_vk.BeginCommandBuffer(_cmd, in begin).Check("vkBeginCommandBuffer");
		_recording = true;
		return _cmd;
	}

	/// <summary>Hand the batch a staging buffer to destroy once its copies have run.</summary>
	public void Retire(Buffer buffer, Allocation allocation) => _staging.Add((buffer, allocation));

	/// <summary>Submit whatever has been recorded. No-op when nothing has. Does not wait.</summary>
	public void Flush()
	{
		if (!_recording)
			return;

		_vk.EndCommandBuffer(_cmd).Check("vkEndCommandBuffer");
		_recording = false;

		// The previous batch's staging is freed here rather than at its own submit, which is the whole point: the
		// fence it waits on was signalled a frame ago, so this is a check rather than a stall.
		ReclaimStaging();

		var cmd = _cmd;
		SubmitInfo submit = new()
		{
			SType = StructureType.SubmitInfo,
			CommandBufferCount = 1,
			PCommandBuffers = &cmd,
		};
		_vk.ResetFences(_ctx.Device, 1, in _fence).Check("vkResetFences");
		_vk.QueueSubmit(_ctx.GraphicsQueue, 1, in submit, _fence).Check("vkQueueSubmit");
		_submitted = true;

		_inFlight.AddRange(_staging);
		_staging.Clear();
	}

	// Free the staging of the last submitted batch, waiting for its fence if it somehow has not signalled yet. Also
	// what makes the single command buffer reusable — it cannot be re-recorded while a submission still reads it.
	private void ReclaimStaging()
	{
		if (!_submitted)
			return;

		_vk.WaitForFences(_ctx.Device, 1, in _fence, true, ulong.MaxValue).Check("vkWaitForFences");
		_submitted = false;

		foreach (var (buffer, allocation) in _inFlight)
		{
			_vk.DestroyBuffer(_ctx.Device, buffer, null);
			allocation.Dispose();
		}
		_inFlight.Clear();
	}

	public void Dispose()
	{
		// Anything still recorded but unsubmitted has no GPU work behind it; drop it and free what it staged.
		if (_recording)
		{
			_vk.EndCommandBuffer(_cmd);
			_recording = false;
			foreach (var (buffer, allocation) in _staging)
			{
				_vk.DestroyBuffer(_ctx.Device, buffer, null);
				allocation.Dispose();
			}
			_staging.Clear();
		}
		ReclaimStaging();
		if (_cmd.Handle != 0)
			_vk.FreeCommandBuffers(_ctx.Device, _pool, 1, in _cmd);
		_vk.DestroyFence(_ctx.Device, _fence, null);
	}
}
