using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

internal sealed unsafe partial class DirectX12RenderContext
{
	// Bind a material's loaded-texture SRVs into the technique's reflected SRV table (slots 0..N-1). Shared by
	// Draw(mesh, material), textured instancing, and textured particles. The caller checks TableParam/InputCount.
	private void BindMaterialTable(DirectX12Technique baked, Material material, string technique)
	{
		var textures = _materials.TexturesOf(material.Id);
		if (textures.Length < baked.InputCount)
			throw new InvalidOperationException($"DirectX12: material supplies {textures.Length} textures but technique '{technique}' samples {baked.InputCount}.");
		var (cpu, gpu) = _ring.Allocate((uint)baked.InputCount);
		for (var i = 0; i < baked.InputCount; i++)
			WriteTextureSrv(textures[i], _ring.Offset(cpu, (uint)i));
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
	}

	// Create an SRV over a loaded texture, exposing its full mip chain (unlike the transient WriteSrv, which is
	// single-mip). Material textures sample through this. A cube map is viewed as a TextureCube (its six array
	// slices); everything else as a Texture2D.
	private void WriteTextureSrv(int textureId, CpuDescriptorHandle dest)
	{
		var mips = _textures.MipLevels(textureId);
		var srv = new ShaderResourceViewDesc
		{
			Format = _textures.SrvFormat(textureId),
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
		};
		if (_textures.IsCube(textureId))
		{
			srv.ViewDimension = SrvDimension.Texturecube;
			srv.Anonymous = new ShaderResourceViewDescUnion
			{
				TextureCube = new TexcubeSrv { MostDetailedMip = 0, MipLevels = mips, ResourceMinLODClamp = 0f },
			};
		}
		else
		{
			srv.ViewDimension = SrvDimension.Texture2D;
			srv.Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = mips, PlaneSlice = 0, ResourceMinLODClamp = 0f },
			};
		}
		_ctx.Device.CreateShaderResourceView(_textures.Resource(textureId), &srv, dest);
	}

	// Bind a draw-list texture: a loaded texture (full mip chain) by id, or — for an inspectable graph resource (id
	// offset by DebugTextureBase, the resource viewer) — a single-mip SRV over this frame's transient image, so a
	// debug overlay can preview live graph contents through the resource's sampleable format (R32_FLOAT for depth).
	private void WriteListTextureSrv(int id, CpuDescriptorHandle dest)
	{
		if (id < InspectableResource.DebugTextureBase || _transients is null)
		{
			WriteTextureSrv(id, dest);
			return;
		}
		var rid = id - InspectableResource.DebugTextureBase;
		var srv = new ShaderResourceViewDesc
		{
			Format = _transients.SrvFormat(_slot, rid),
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			ViewDimension = SrvDimension.Texture2D,
			Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f },
			},
		};
		_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, rid), &srv, dest);
	}

	// Push a scalar block to the pass's root constants (the per-pass channel: JFA step, exposure rate, a
	// light-space matrix). Padded to the layout's 16 floats so the shader always reads defined memory.
	private void SetPush(DirectX12Technique baked, ReadOnlySpan<float> push)
	{
		if (baked.RootConstantParam < 0)
			throw new InvalidOperationException("DirectX12: technique has no root-constant block but a push was supplied.");
		var block = stackalloc float[16];
		for (var i = 0; i < push.Length && i < 16; i++)
			block[i] = push[i];
		_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
	}

	// Write the sampled inputs' SRVs into a fresh descriptor-ring range and bind it as the technique's SRV table.
	private void BindInputs(DirectX12Technique baked, ReadOnlySpan<ResourceHandle> inputs)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: sampled inputs require an active graph (SetGraph).");
		if (baked.TableParam < 0)
			throw new InvalidOperationException("DirectX12: technique declares no SRV table but sampled inputs were supplied.");

		var (cpu, gpu) = _ring.Allocate((uint)inputs.Length);
		for (var i = 0; i < inputs.Length; i++)
		{
			var id = inputs[i].Id;
			if (!_transients.IsTransient(id))
				throw new NotSupportedException("DirectX12: sampling the backbuffer or a buffer resource is not supported.");
			WriteSrv(id, _ring.Offset(cpu, (uint)i));
		}
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
	}

	// Create a Texture2D SRV for a transient (or persistent) resource at <paramref name="dest"/>. Samples through
	// the SRV-compatible format: R32_FLOAT for a depth target (a shadow map), whose resource is TYPELESS and whose
	// DSV format (D32_FLOAT) is not SRV-creatable; == the colour format otherwise. Shared by the raster sampling
	// path and the compute-sampling path (the auto-exposure reduction reads the lit scene).
	private void WriteSrv(int id, CpuDescriptorHandle dest)
	{
		var srv = new ShaderResourceViewDesc
		{
			Format = _transients!.SrvFormat(_slot, id),
			ViewDimension = SrvDimension.Texture2D,
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f }
			}
		};
		_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, id), &srv, dest);
	}
}
