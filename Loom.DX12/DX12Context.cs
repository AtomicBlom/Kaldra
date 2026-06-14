using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.DX12;

/// <summary>
/// DirectX 12 device bring-up — the DX12 counterpart of <c>VulkanContext</c> (Planning.md §2). Owns the
/// long-lived API handles: the DXGI factory, the D3D12 device, the direct (graphics) command queue, and the
/// debug layer. The Vulkan/Silk.NET stack is quarantined in this assembly; nothing here reaches Core/Graph.
/// <para>
/// This first slice creates the device on the system default adapter (this dev box is single-GPU — an
/// Adreno on Windows-on-ARM); explicit high-performance/discrete adapter selection honouring
/// <see cref="DX12EngineSettings.PreferDiscreteGpu"/> is a later refinement.
/// </para>
/// </summary>
internal sealed unsafe class DX12Context : IDisposable
{
	public D3D12 D3D12 { get; }
	public DXGI DXGI { get; }

	public ComPtr<IDXGIFactory4> Factory;
	public ComPtr<ID3D12Device> Device;
	public ComPtr<ID3D12CommandQueue> GraphicsQueue;

	/// <summary>The debug-layer message queue (null when validation is off/unavailable) — the DX12 analogue of the Vulkan validation messenger; drained each frame by the engine.</summary>
	public ComPtr<ID3D12InfoQueue> InfoQueue;

	/// <summary>Bytes between consecutive descriptors in an RTV heap — needed to index hand-built RTV handles.</summary>
	public uint RtvDescriptorSize { get; }

	/// <summary>Human-readable adapter name for HUDs/logs (the neutral <c>IEngine.DeviceName</c>).</summary>
	public string DeviceName { get; }

	private ComPtr<ID3D12Debug> _debug;

	public DX12Context(DX12EngineSettings settings)
	{
		D3D12 = D3D12.GetApi();
		DXGI = DXGI.GetApi(default(INativeWindowSource)); // null window: load the system dxgi.dll directly (no DXVK)

		// Debug layer (D3D12's validation, the counterpart of the Vulkan validation layers). Best-effort: if the
		// layer isn't installed the device still comes up, exactly as Vulkan degrades when layers are absent.
		uint factoryFlags = 0;
		if (settings.EnableValidation)
		{
			if (D3D12.GetDebugInterface(out ComPtr<ID3D12Debug> debug) >= 0 && debug.Handle is not null)
			{
				debug.EnableDebugLayer();
				_debug = debug;
				factoryFlags = 0x1; // DXGI_CREATE_FACTORY_DEBUG — surfaces DXGI-side messages too
			}
		}

		SilkMarshal.ThrowHResult(DXGI.CreateDXGIFactory2(factoryFlags, out Factory));

		// Default adapter (single-GPU box). The device requires at least feature level 11_0; the Adreno X1-85
		// reports 12_1. Tightening the floor to 12_0 (Vulkan-1.3-baseline parity) lands with real techniques.
		SilkMarshal.ThrowHResult(D3D12.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DFeatureLevel.Level110, out Device));

		// The info queue exists only when the debug layer is active; draining it each frame is how this backend
		// reports "validation-clean" (the DX12 equivalent of the Vulkan validation-layer messages).
		if (settings.EnableValidation && Device.QueryInterface(out ComPtr<ID3D12InfoQueue> infoQueue) >= 0 && infoQueue.Handle is not null)
			InfoQueue = infoQueue;

		var queueDesc = new CommandQueueDesc
		{
			Type = CommandListType.Direct,
			Flags = CommandQueueFlags.None,
			Priority = 0,
			NodeMask = 0,
		};
		SilkMarshal.ThrowHResult(Device.CreateCommandQueue(&queueDesc, out GraphicsQueue));

		RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
		DeviceName = ReadDefaultAdapterName();
	}

	/// <summary>Read adapter 0's description (the default adapter on a single-GPU machine) for the device name.</summary>
	private string ReadDefaultAdapterName()
	{
		ComPtr<IDXGIAdapter1> adapter = default;
		if (Factory.EnumAdapters1(0, ref adapter) >= 0 && adapter.Handle is not null)
		{
			AdapterDesc1 desc = default;
			adapter.GetDesc1(ref desc);
			adapter.Dispose();
			// DXGI_ADAPTER_DESC1::Description is a 128-wchar null-terminated buffer; desc is a local, so the
			// fixed-buffer access yields a pointer directly (no fixed statement needed).
			var name = new string(desc.Description).Trim();
			if (!string.IsNullOrWhiteSpace(name))
				return name;
		}
		return "DirectX 12 device";
	}

	/// <summary>
	/// Print any debug-layer messages accumulated since the last drain and return how many were errors or
	/// corruption (0 = clean). The smoke run's "validation-clean" claim rests on this printing nothing severe.
	/// </summary>
	public int DrainDebugMessages()
	{
		if (InfoQueue.Handle is null)
			return 0;

		var errors = 0;
		var count = InfoQueue.GetNumStoredMessages();
		for (ulong i = 0; i < count; i++)
		{
			nuint length = 0;
			InfoQueue.GetMessageA(i, (Message*)null, ref length); // first call: ask for the message size
			if (length == 0)
				continue;
			var buffer = new byte[length];
			fixed (byte* p = buffer)
			{
				var message = (Message*)p;
				if (InfoQueue.GetMessageA(i, message, ref length) < 0)
					continue;
				var text = message->PDescription is null ? "" : new string((sbyte*)message->PDescription);
				Console.WriteLine($"[d3d12:{message->Severity}] {text}");
				if (message->Severity is MessageSeverity.Error or MessageSeverity.Corruption)
					errors++;
			}
		}
		InfoQueue.ClearStoredMessages();
		return errors;
	}

	public void Dispose()
	{
		if (InfoQueue.Handle is not null)
			InfoQueue.Dispose();
		GraphicsQueue.Dispose();
		Device.Dispose();
		Factory.Dispose();
		if (_debug.Handle is not null)
			_debug.Dispose();
	}
}
