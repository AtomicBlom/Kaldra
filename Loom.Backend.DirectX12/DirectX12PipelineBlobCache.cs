using System.Security.Cryptography;
using System.Text;
using Loom.Core.Diagnostics;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

/// <summary>
/// Driver-compiled PSO blobs kept on disk between runs. D3D12 hands back a compiled pipeline's blob
/// (<c>GetCachedBlob</c>) and accepts it again through <c>CachedPSO</c>, which turns most of a second run's pipeline
/// creation into a deserialize instead of a compile. The key is the technique digest the bake cache already
/// computes — a SHA-256 over the DXIL and every PSO-affecting field — plus the exact target formats, so a blob can
/// only ever be offered back for the pipeline it came from.
/// <para>
/// <b>Never fatal.</b> A blob is a hint. A stale one — different driver, different adapter, a D3D12 runtime update —
/// makes <c>CreateGraphicsPipelineState</c> fail, and the caller simply compiles instead; nothing is reported per
/// blob because a driver update legitimately invalidates every one of them at once. A file that cannot be read or
/// written costs the optimisation and nothing else.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12PipelineBlobCache
{
	// Bumped when the file layout changes, so an older Loom's file is rejected wholesale rather than misparsed.
	private const uint FormatVersion = 1;

	private readonly string? _path;
	private readonly Dictionary<string, byte[]> _blobs = [];
	private bool _dirty;

	public DirectX12PipelineBlobCache(DirectX12Context ctx, bool persist)
	{
		if (!persist)
			return;

		// The device name is what distinguishes one adapter's blobs from another's. It does not distinguish driver
		// versions — nothing portable does — but a blob from an older driver is rejected by D3D12 on use, which is
		// the check that actually matters.
		var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ctx.DeviceName)))[..16];
		_path = PipelineCacheFile.PathFor("directx12", key);
		if (_path is not null)
			Load(_path);
	}

	/// <summary>The blob previously compiled for <paramref name="key"/>, or null.</summary>
	public byte[]? TryGet(string key) => _blobs.GetValueOrDefault(key);

	/// <summary>Record the blob D3D12 compiled for <paramref name="key"/>, so the next run can skip compiling it.</summary>
	public void Capture(string key, ComPtr<ID3D12PipelineState> pipeline)
	{
		if (_path is null || _blobs.ContainsKey(key))
			return;

		ComPtr<ID3D10Blob> blob = default;
		if (pipeline.GetCachedBlob(ref blob) < 0 || blob.Handle is null)
			return;

		try
		{
			var size = (int)blob.GetBufferSize();
			if (size <= 0)
				return;
			var bytes = new byte[size];
			new ReadOnlySpan<byte>(blob.GetBufferPointer(), size).CopyTo(bytes);
			_blobs[key] = bytes;
			_dirty = true;
		}
		finally
		{
			blob.Dispose();
		}
	}

	private void Load(string path)
	{
		try
		{
			if (!File.Exists(path))
				return;

			using var stream = File.OpenRead(path);
			using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
			if (reader.ReadUInt32() != FormatVersion)
				return;

			var count = reader.ReadInt32();
			for (var i = 0; i < count; i++)
			{
				var key = reader.ReadString();
				var length = reader.ReadInt32();
				if (length <= 0 || length > stream.Length - stream.Position)
					return; // truncated or hostile: keep whatever parsed cleanly and stop
				_blobs[key] = reader.ReadBytes(length);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
		{
			LoomLog.Info($"DirectX12 pipeline blob cache could not be read ({e.Message}); starting empty.");
			_blobs.Clear();
		}
	}

	public void Save()
	{
		if (_path is null || !_dirty || _blobs.Count == 0)
			return;

		try
		{
			using var memory = new MemoryStream();
			using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
			{
				writer.Write(FormatVersion);
				writer.Write(_blobs.Count);
				foreach (var (key, blob) in _blobs)
				{
					writer.Write(key);
					writer.Write(blob.Length);
					writer.Write(blob);
				}
			}
			PipelineCacheFile.Write(_path, memory.ToArray());
			_dirty = false;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			LoomLog.Info($"DirectX12 pipeline blob cache could not be written ({e.Message}).");
		}
	}
}
