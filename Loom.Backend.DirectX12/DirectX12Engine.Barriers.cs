using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

public sealed unsafe partial class DirectX12Engine
{
	// Track the resource's real D3D12 state and emit a transition only when the required state differs (D3D12
	// barriers need the exact current state, unlike Vulkan's "Undefined" old layout — see DirectX12StateMapping).
	private void EmitGraphTransition(uint index, ResourceTransition transition)
	{
		var target = DirectX12StateMapping.For(transition.To);
		var id = transition.Resource.Id;

		if (transition.Resource.IsBackbuffer)
		{
			if (_backbufferState[index] == target)
				return;
			Transition(_presentTarget.BackBuffer(index), _backbufferState[index], target);
			_backbufferState[index] = target;
			return;
		}

		var transients = _transients!; // non-null throughout recording (SetGraph built it)

		// An aliased transient reuses memory another bucket member held — before its first (re)activation this
		// frame, an aliasing barrier orders this use after whatever last touched the shared memory (and marks its
		// contents undefined, which the pass's clear/full-write then initialises). The DirectX12 analogue of the
		// Vulkan backend's widened-source-scope birth barrier; once per resource per frame.
		if (_aliasedHandoffIds.Contains(id) && _aliasBarriered.Add(id))
			EmitAliasingBarrier(transients.Resource(index, id));

		var current = transients.State(index, id);
		if (current == target)
			return;
		Transition(transients.Resource(index, id), current, target);
		transients.SetState(index, id, target);
	}

	// A D3D12 aliasing barrier handing the bucket's shared memory to <paramref name="after"/> (before = null:
	// any resource previously on that memory is deactivated — conservative but correct, and hand-offs are few).
	private void EmitAliasingBarrier(ComPtr<ID3D12Resource> after)
	{
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Aliasing,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Aliasing = new ResourceAliasingBarrier { PResourceBefore = (ID3D12Resource*)null, PResourceAfter = after.Handle }
			}
		};
		_commandList.ResourceBarrier(1, &barrier);
	}

	private void Transition(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		var barrier = new ResourceBarrier
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
		_commandList.ResourceBarrier(1, &barrier);
	}
}
