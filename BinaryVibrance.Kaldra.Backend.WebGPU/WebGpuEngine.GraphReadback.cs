using System.Runtime.Versioning;

using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// The general <c>g.Readback</c> pipeline (<see cref="BinaryVibrance.Kaldra.Core.Hosting.IEngine.Read"/>) — e.g. an object-id buffer
/// a demo's cursor-picking system reads back to the CPU each frame. Mirrors the frame-capture readback (the
/// sibling <c>WebGpuEngine.Readback.cs</c> partial: same async-map-then-poll shape, its own buffer per target and
/// its own slot range so the two never collide) but drives every <c>graph.ReadbackTargets</c> entry every frame
/// rather than a one-shot request.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuEngine
{
	private enum GraphReadbackState { Idle, CopyRecorded, Mapping }

	private sealed class GraphReadbackTarget
	{
		public required int Id;
		public required int Width;
		public required int Height;
		public required int BytesPerTexel;
		public required int BytesPerRow; // 256-byte aligned (WebGPU copy requirement)
		public int Buffer = -1;
		public GraphReadbackState State;
	}

	// Slot numbers for beginReadbackMap/tryFetchReadback are a flat namespace shared with the capture path (slot 0);
	// starting at 1 keeps every graph-readback target on its own slot with no collision.
	private const int GraphReadbackSlotBase = 1;

	private readonly List<GraphReadbackTarget> _graphReadbacks = [];

	// Rebuild the readback target list from the freshly-created transients — resource ids are graph-local (a swap
	// re-mints them), so the previous graph's buffers are released and every target's dimensions/format re-resolved
	// against its actual transient texture. Called from RebuildGraphResources, after _transients is assigned.
	private void RebuildGraphReadbacks(RenderGraph authored)
	{
		ReleaseGraphReadbacks();
		foreach (var target in authored.ReadbackTargets)
		{
			ref var tex = ref _transients!.Texture(target.Id);
			var bpt = tex.Format.BytesPerTexel;
			_graphReadbacks.Add(new GraphReadbackTarget
			{
				Id = target.Id,
				Width = tex.Width,
				Height = tex.Height,
				BytesPerTexel = bpt,
				BytesPerRow = Align256(tex.Width * bpt),
			});
		}
	}

	private void ReleaseGraphReadbacks()
	{
		foreach (var t in _graphReadbacks)
			if (t.Buffer >= 0)
				_handles.Release(WebGpuHandleAllocator.Buffers, t.Buffer);
		_graphReadbacks.Clear();
	}

	// Copy each idle target's current texture into its map-read buffer at the tail of this frame's stream. A target
	// still mapping from a previous cycle is skipped this frame — its own state machine catches up once its map
	// lands (a frame or two of picking latency, same as the desktop backends; invisible for hover).
	private void RecordGraphReadbacks()
	{
		foreach (var t in _graphReadbacks)
		{
			if (t.State != GraphReadbackState.Idle)
				continue;
			if (t.Buffer < 0)
			{
				t.Buffer = _handles.Allocate(WebGpuHandleAllocator.Buffers);
				WebGpuInterop.CreateBuffer(t.Buffer, t.BytesPerRow * t.Height, WebGpuStateMapping.BufferMapRead | WebGpuStateMapping.BufferCopyDst, "graph-readback");
			}
			_writer.CopyTextureToBuffer(_transients!.TextureHandle(t.Id), t.Width, t.Height, t.Buffer, 0, t.BytesPerRow);
			t.State = GraphReadbackState.CopyRecorded;
		}
	}

	// Drive each target's async map after the frame is submitted: start it the frame the copy was recorded, then
	// poll until it lands and hand the de-padded bytes to StoreReadback (IEngine.Read then returns them).
	private void ServiceGraphReadbacks()
	{
		for (var i = 0; i < _graphReadbacks.Count; i++)
		{
			var t = _graphReadbacks[i];
			switch (t.State)
			{
				case GraphReadbackState.CopyRecorded:
					WebGpuInterop.BeginReadbackMap(GraphReadbackSlotBase + i, t.Buffer);
					t.State = GraphReadbackState.Mapping;
					break;

				case GraphReadbackState.Mapping:
					var padded = new byte[t.BytesPerRow * t.Height];
					if (!WebGpuInterop.TryFetchReadback(GraphReadbackSlotBase + i, padded))
						continue;
					DeliverGraphReadback(t, padded);
					t.State = GraphReadbackState.Idle;
					break;
			}
		}
	}

	private void DeliverGraphReadback(GraphReadbackTarget t, byte[] padded)
	{
		var tightRowBytes = t.Width * t.BytesPerTexel;
		if (t.BytesPerRow == tightRowBytes)
		{
			StoreReadback(t.Id, padded.AsSpan(0, tightRowBytes * t.Height));
			return;
		}
		var tight = new byte[tightRowBytes * t.Height];
		for (var y = 0; y < t.Height; y++)
			padded.AsSpan(y * t.BytesPerRow, tightRowBytes).CopyTo(tight.AsSpan(y * tightRowBytes, tightRowBytes));
		StoreReadback(t.Id, tight);
	}
}
