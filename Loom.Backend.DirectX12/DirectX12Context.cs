using System.Diagnostics;
using System.Text;
using Loom.Core.Diagnostics;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// DirectX 12 device bring-up — the DirectX12 counterpart of <c>VulkanContext</c>. Owns the
/// long-lived API handles: the DXGI factory, the D3D12 device, the direct (graphics) command queue, and the
/// debug layer. The Vulkan/Silk.NET stack is quarantined in this assembly; nothing here reaches Core/Graph.
/// <para>
/// The device comes up on the system default adapter; <see cref="DirectX12EngineSettings.PreferDiscreteGpu"/>
/// has no effect here (this backend does no explicit high-performance/discrete adapter selection). When
/// <see cref="Loom.Core.Hosting.EngineSettings.PreferSoftwareAdapter"/> is set, the WARP (software rasterizer)
/// adapter is resolved instead via <c>IDXGIFactory4.EnumWarpAdapter</c>, for GPU-less CI and deterministic
/// software rendering.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12Context : IDisposable
{
	public D3D12 D3D12 { get; }
	public DXGI DXGI { get; }

	public ComPtr<IDXGIFactory4> Factory;
	public ComPtr<ID3D12Device> Device;
	public ComPtr<ID3D12CommandQueue> GraphicsQueue;

	/// <summary>The debug-layer message queue (null when validation is off/unavailable) — the DirectX12 analogue of the Vulkan validation messenger; drained each frame by the engine.</summary>
	public ComPtr<ID3D12InfoQueue> InfoQueue;

	/// <summary>Bytes between consecutive descriptors in an RTV heap — needed to index hand-built RTV handles.</summary>
	public uint RtvDescriptorSize { get; }

	/// <summary>Human-readable adapter name for HUDs/logs (the neutral <c>IEngine.DeviceName</c>).</summary>
	public string DeviceName { get; }

	private ComPtr<ID3D12Debug> _debug;

	public DirectX12Context(DirectX12EngineSettings settings)
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

				// GPU-based validation (validates descriptor/resource state on the GPU timeline; far slower) when
				// the host opts in. ID3D12Debug1 carries the toggle; absent it, the basic debug layer still applies.
				if (settings.GpuBasedValidation && debug.QueryInterface(out ComPtr<ID3D12Debug1> debug1) >= 0 && debug1.Handle is not null)
				{
					debug1.SetEnableGPUBasedValidation(true);
					debug1.Dispose();
				}
			}
		}

		SilkMarshal.ThrowHResult(DXGI.CreateDXGIFactory2(factoryFlags, out Factory));

		// A null adapter lets D3D12 pick the system default. When a software adapter is requested instead,
		// resolve WARP explicitly; a failure to enumerate it (WARP is normally always present on Windows) falls
		// back to the default adapter rather than failing engine creation.
		ComPtr<IDXGIAdapter> adapter = default;
		var usingSoftwareAdapter = false;
		if (settings.PreferSoftwareAdapter)
		{
			if (Factory.EnumWarpAdapter(out ComPtr<IDXGIAdapter> warpAdapter) >= 0 && warpAdapter.Handle is not null)
			{
				adapter = warpAdapter;
				usingSoftwareAdapter = true;
			}
			else
			{
				LoomLog.Warning("--software-adapter requested but WARP adapter enumeration failed; falling back to the default adapter.");
			}
		}

		// The device requires at least feature level 11_0; the Adreno X1-85 reports 12_1. Tightening the floor
		// to 12_0 (Vulkan-1.3-baseline parity) lands with real techniques.
		SilkMarshal.ThrowHResult(D3D12.CreateDevice(adapter, D3DFeatureLevel.Level110, out Device));
		if (adapter.Handle is not null)
			adapter.Dispose();

		// The info queue exists only when the debug layer is active; draining it each frame is how this backend
		// reports "validation-clean" (the DirectX12 equivalent of the Vulkan validation-layer messages).
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
		if (usingSoftwareAdapter)
		{
			DeviceName = "WARP (software)";
			LoomLog.Info("adapter=WARP (software)");
		}
		else
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
				// The debug layer's own severity stays in the text — it is D3D12's classification, not Loom's.
				var line = $"[d3d12:{message->Severity}] {text}";
				if (message->Severity is MessageSeverity.Error or MessageSeverity.Corruption)
				{
					LoomLog.Error(line);
					errors++;
				}
				else
				{
					LoomLog.Warning(line);
				}
			}
		}
		InfoQueue.ClearStoredMessages();
		return errors;
	}

	// GPU object names + per-pass debug-label scopes (UseNamedGPUObjects), for PIX/RenderDoc captures — the DirectX12
	// counterpart of VulkanContext's helpers. [Conditional] so the calls (and their string marshalling) vanish
	// entirely without the flag; they no-op safely if the debug layer/capture tooling isn't present.

	/// <summary>Attach a human-readable name to a D3D12 object (pipeline state, resource, command list, …). Pass e.g. <c>(ID3D12Object*)pso.Handle</c>.</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void SetObjectName(ID3D12Object* obj, string name)
	{
		if (obj is null)
			return;
		fixed (char* p = name) // C# strings are null-terminated, so this is a valid LPCWSTR
			obj->SetName(p);
	}

	// PIX legacy string-event metadata (pix_win.h): 0 = UNICODE, 1 = ANSI, 2 = PIX3BLOB (a binary blob — NOT a
	// string). PIX and RenderDoc both decode the ANSI form; metadata 2 with a plain string is a malformed blob the
	// D3D12 debug layer silently drops (the begin goes uncounted while its EndEvent still does — an imbalance).
	private const uint PixEventAnsiVersion = 1;

	/// <summary>Open a named debug-label region on a command list (a PIX/RenderDoc marker scope).</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void BeginDebugLabel(ComPtr<ID3D12GraphicsCommandList> cmd, string label)
	{
		// ANSI string event: a null-terminated UTF-8 payload, Size in bytes including the NUL.
		var bytes = (uint)Encoding.UTF8.GetByteCount(label) + 1;
		var p = (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
		cmd.BeginEvent(PixEventAnsiVersion, p, bytes);
		SilkMarshal.Free((nint)p);
	}

	/// <summary>Close the most recently opened debug-label region on a command list.</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void EndDebugLabel(ComPtr<ID3D12GraphicsCommandList> cmd) => cmd.EndEvent();

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
