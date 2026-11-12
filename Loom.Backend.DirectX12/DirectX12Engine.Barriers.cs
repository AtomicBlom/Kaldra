using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

public sealed unsafe partial class DirectX12Engine
{
	// One pass's worth of pending barriers, grown on demand and reused. Aliasing barriers are kept apart from
	// transitions because they have to be submitted first — see EmitGraphTransitions.
	private ResourceBarrier[] _aliasingBarriers = new ResourceBarrier[8];
	private ResourceBarrier[] _transitionBarriers = new ResourceBarrier[32];
	private int _aliasingBarrierCount;
	private int _transitionBarrierCount;

	/// <summary>
	/// Emit every transition in <paramref name="transitions"/> in as few submissions as the ordering allows: one
	/// <c>ResourceBarrier</c> call for the aliasing barriers, then one for the state transitions. A G-buffer hand-off
	/// is four or five transitions, and one call expressing all of them lets the driver resolve the dependency once
	/// instead of stalling five times over.
	/// <para>
	/// The two batches cannot merge: barriers within a single submission are unordered with respect to each other, and
	/// a resource's aliasing barrier must be seen before the transition that activates it. Batching the transitions
	/// among themselves is sound because derivation folds a pass's accesses to one use per resource, so no pass
	/// carries two transitions of the same resource.
	/// </para>
	/// </summary>
	private void EmitGraphTransitions(uint index, ReadOnlySpan<ResourceTransition> transitions)
	{
		_aliasingBarrierCount = 0;
		_transitionBarrierCount = 0;
		_resolveCount = 0;
		foreach (var transition in transitions)
			QueueGraphTransition(index, transition);
		FlushBarriers(_aliasingBarriers, ref _aliasingBarrierCount);
		FlushTransitions();
		if (_resolveCount > 0)
			Resolve(index);
	}

	// The multisampled targets this batch resolves for sampling (their ids), between its barriers and the ones after
	private int[] _resolveIds = new int[8];
	private int _resolveCount;

	// Each target queued for resolving resolved into its sampled resource, then that made readable and the target a
	// render target again
	private void Resolve(uint index)
	{
		var transients = _transients!;
		var read = DirectX12StateMapping.For(ResourceState.ShaderRead);
		for (var k = 0; k < _resolveCount; k++)
		{
			var id = _resolveIds[k];
			_commandList.ResolveSubresource(transients.ResolveOf(index, id), 0, transients.Resource(index, id), 0, transients.Format(index, id));
			QueueTransition(transients.ResolveOf(index, id), ResourceStates.ResolveDest, read);
			transients.SetResolveState(index, id, read);
			QueueTransition(transients.Resource(index, id), ResourceStates.ResolveSource, ResourceStates.RenderTarget);
			transients.SetState(index, id, ResourceStates.RenderTarget);
		}
		_resolveCount = 0;
		FlushTransitions();
	}

	/// <summary>Submit the queued state transitions. Public within the engine so a path that queues a barrier outside
	/// a pass — the capture copy — can submit it before the command that depends on it.</summary>
	private void FlushTransitions() => FlushBarriers(_transitionBarriers, ref _transitionBarrierCount);

	// Track the resource's real D3D12 state and queue a transition only when the required state differs (D3D12
	// barriers need the exact current state, unlike Vulkan's "Undefined" old layout — see DirectX12StateMapping).
	private void QueueGraphTransition(uint index, ResourceTransition transition)
	{
		var target = DirectX12StateMapping.For(transition.To);
		var id = transition.Resource.Id;

		if (transition.Resource.IsBackbuffer)
		{
			if (_backbufferState[index] == target)
				return;
			QueueTransition(_presentTarget.BackBuffer(index), _backbufferState[index], target);
			_backbufferState[index] = target;
			return;
		}

		var transients = _transients!; // non-null throughout recording (SetGraph built it)

		// An aliased transient reuses memory another bucket member held — before its first (re)activation this
		// frame, an aliasing barrier orders this use after whatever last touched the shared memory (and marks its
		// contents undefined, which the pass's clear/full-write then initialises). The DirectX12 analogue of the
		// Vulkan backend's widened-source-scope birth barrier; once per resource per frame.
		if (_aliasedHandoffIds.Contains(id) && _aliasBarriered.Add(id))
			QueueAliasingBarrier(transients.Resource(index, id));

		// A multisampled colour target that a pass samples: the graph taking it to ShaderRead is where it is resolved, into
		// the resource samplers read (DirectX12Transients.SampledResource). Its other transitions are its own, as any
		// target's: it is only ever drawn into.
		if (transients.Resolves(index, id))
		{
			if (transition.To == ResourceState.ShaderRead)
			{
				QueueTo(transients.Resource(index, id), transients.State(index, id), ResourceStates.ResolveSource);
				transients.SetState(index, id, ResourceStates.ResolveSource);
				QueueTo(transients.ResolveOf(index, id), transients.ResolveState(index, id), ResourceStates.ResolveDest);
				transients.SetResolveState(index, id, ResourceStates.ResolveDest);
				if (_resolveCount == _resolveIds.Length)
					Array.Resize(ref _resolveIds, _resolveIds.Length * 2);
				_resolveIds[_resolveCount++] = id;
				return;
			}
			if (transition.To != ResourceState.ColorAttachment)
				throw new NotSupportedException($"DirectX12: '{transition.Resource.Name}' is multisampled and is resolved only to be sampled; the graph takes it to {transition.To}.");
			// Drawn into from nothing, after drawing, or after its resolve was sampled: anything else would be a use the
			// derivation refuses, so one slipping past it throws here as it does on Vulkan
			if (transition.From is not (ResourceState.Undefined or ResourceState.ColorAttachment or ResourceState.ShaderRead))
				throw new NotSupportedException($"DirectX12: '{transition.Resource.Name}' is multisampled and is resolved only to be sampled; the graph takes it from {transition.From}.");
		}

		var current = transients.State(index, id);
		if (current == target)
			return;
		QueueTransition(transients.Resource(index, id), current, target);
		transients.SetState(index, id, target);
	}

	// A D3D12 aliasing barrier handing the bucket's shared memory to <paramref name="after"/> (before = null:
	// any resource previously on that memory is deactivated — conservative but correct, and hand-offs are few).
	private void QueueAliasingBarrier(ComPtr<ID3D12Resource> after)
	{
		if (_aliasingBarrierCount == _aliasingBarriers.Length)
			Array.Resize(ref _aliasingBarriers, _aliasingBarriers.Length * 2);

		_aliasingBarriers[_aliasingBarrierCount++] = new ResourceBarrier
		{
			Type = ResourceBarrierType.Aliasing,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Aliasing = new ResourceAliasingBarrier { PResourceBefore = (ID3D12Resource*)null, PResourceAfter = after.Handle }
			}
		};
	}

	private void QueueTo(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		if (before != after)
			QueueTransition(resource, before, after);
	}

	private void QueueTransition(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		if (_transitionBarrierCount == _transitionBarriers.Length)
			Array.Resize(ref _transitionBarriers, _transitionBarriers.Length * 2);

		_transitionBarriers[_transitionBarrierCount++] = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = resource.Handle,
					Subresource = 0xffffffff, // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
					StateBefore = before,
					StateAfter = after
				}
			}
		};
	}

	private void FlushBarriers(ResourceBarrier[] barriers, ref int count)
	{
		if (count == 0)
			return;
		fixed (ResourceBarrier* p = barriers)
			_commandList.ResourceBarrier((uint)count, p);
		count = 0;
	}
}
