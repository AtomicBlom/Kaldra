using System.Runtime.Versioning;

using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>
/// Frame capture (and, later, graph readback) over WebGPU's asynchronous map. A capture copies the backbuffer into
/// a MAP_READ buffer within the frame's command stream, then — because <c>mapAsync</c> resolves on a later task —
/// starts the map after submit and polls it on subsequent frames. When it lands, the padded rows are de-padded (and
/// BGRA→RGBA swapped for the usual BGRA canvas) and PNG-encoded on the CPU, bypassing the flaky headless compositor.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuEngine
{
	private enum CaptureState { Idle, CopyRecorded, Mapping }

	private const int CaptureSlot = 0;

	private int _captureBuffer = -1;
	private int _captureBufferSize;
	private CaptureState _captureState;
	private string _capturePath = "";
	private int _captureWidth, _captureHeight, _captureBytesPerRow;

	// Record the backbuffer→buffer copy into this frame's command stream if a capture was requested and none is in
	// flight. Called after the passes are recorded, before the stream is finished.
	private void RecordCaptureIfRequested()
	{
		var path = CapturePath;
		if (_captureState != CaptureState.Idle || path is null)
			return;

		_captureWidth = _width;
		_captureHeight = _height;
		_captureBytesPerRow = Align256(_width * 4);
		var size = _captureBytesPerRow * _height;
		if (_captureBuffer < 0 || size > _captureBufferSize)
		{
			if (_captureBuffer >= 0)
				_handles.Release(WebGpuHandleAllocator.Buffers, _captureBuffer);
			_captureBuffer = _handles.Allocate(WebGpuHandleAllocator.Buffers);
			_captureBufferSize = size;
			WebGpuInterop.CreateBuffer(_captureBuffer, size, WebGpuStateMapping.BufferMapRead | WebGpuStateMapping.BufferCopyDst, "capture");
		}

		_writer.CopyTextureToBuffer(WebGpuHandleAllocator.BackbufferView, _width, _height, _captureBuffer, 0, _captureBytesPerRow);
		_capturePath = path;
		_captureState = CaptureState.CopyRecorded;
		CapturePath = null;
	}

	// Drive the async map after the frame is submitted: start it the frame the copy was recorded, then poll until it
	// lands and deliver the PNG.
	private void ServiceCapture()
	{
		switch (_captureState)
		{
			case CaptureState.CopyRecorded:
				WebGpuInterop.BeginReadbackMap(CaptureSlot, _captureBuffer);
				_captureState = CaptureState.Mapping;
				break;

			case CaptureState.Mapping:
				var padded = new byte[_captureBufferSize];
				if (!WebGpuInterop.TryFetchReadback(CaptureSlot, padded))
					return;
				DeliverCapture(padded);
				_captureState = CaptureState.Idle;
				break;
		}
	}

	private void DeliverCapture(byte[] padded)
	{
		var stride = _captureWidth * 4;
		var rgba = new byte[stride * _captureHeight];
		var swapRedBlue = _surface.BackbufferFormat == Format.Bgra8Srgb;
		for (var y = 0; y < _captureHeight; y++)
		{
			var src = y * _captureBytesPerRow;
			var dst = y * stride;
			for (var x = 0; x < _captureWidth; x++)
			{
				var s = src + x * 4;
				var d = dst + x * 4;
				if (swapRedBlue)
				{
					rgba[d] = padded[s + 2];
					rgba[d + 1] = padded[s + 1];
					rgba[d + 2] = padded[s];
				}
				else
				{
					rgba[d] = padded[s];
					rgba[d + 1] = padded[s + 1];
					rgba[d + 2] = padded[s + 2];
				}
				rgba[d + 3] = 255; // opaque — the alpha channel isn't meaningful for a screen capture
			}
		}

		using var png = new MemoryStream();
		PngWriter.WriteRgba(png, _captureWidth, _captureHeight, rgba);
		var bytes = png.ToArray();
		if (_settings.CaptureViaPost)
			WebGpuInterop.PostCapture(_capturePath, bytes);
		else
			WebGpuInterop.DownloadCapture(_capturePath, bytes);
	}

	private static int Align256(int value) => (value + 255) & ~255;
}
