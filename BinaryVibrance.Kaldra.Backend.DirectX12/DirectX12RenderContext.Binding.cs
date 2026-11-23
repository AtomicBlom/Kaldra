using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using Silk.NET.Direct3D12;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

internal sealed unsafe partial class DirectX12RenderContext
{
	// The persistent SRV table each (material, table width) pair was written into, by descriptor index. Indices rather
	// than GPU handles because growing the heap moves every handle but keeps every index.
	private readonly Dictionary<(int Material, int Inputs), uint> _materialTables = [];

	/// <summary>
	/// Bind a material's loaded-texture SRVs into the technique's reflected SRV table (slots 0..N-1). Shared by
	/// Draw(mesh, material), textured instancing, and textured particles. The caller checks TableParam/InputCount.
	/// <para>
	/// The table is written once and kept. A material's textures are fixed when it is created — only its parameter
	/// block is writable, and that rides a root CBV, not this table — so re-creating N shader-resource views on every
	/// draw produced identical descriptors at the cost of the frame's whole heap budget. Now a repeat draw of the same
	/// material is one root-table set and no descriptor writes at all.
	/// </para>
	/// </summary>
	private void BindMaterialTable(DirectX12Technique baked, Material material, string technique)
	{
		_materials.ValidateAgainst(material, technique);

		var key = (material.Id, baked.InputCount);
		if (!_materialTables.TryGetValue(key, out var index))
		{
			// Safe to keep for the engine's lifetime because the resources behind these views are: a material lives as long
			// as the engine and IEngine.ReleaseTexture refuses any texture a material holds, so a written view can never
			// come to describe something else.
			var textures = _materials.TexturesOf(material.Id);
			index = _descriptors.AllocatePersistent((uint)baked.InputCount);
			for (var i = 0u; i < baked.InputCount; i++)
				WriteTextureSrv(textures[(int)i], _descriptors.PersistentCpu(index + i));
			_descriptors.CommitPersistent(index, (uint)baked.InputCount);
			_materialTables[key] = index;
		}
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, _descriptors.PersistentGpu(index));
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

	// Bind a draw-list texture: a loaded texture (full mip chain) by id, or — for a graph resource (id offset by
	// DebugTextureBase: a TextureRef the pass declared with .Samples, or an inspectable one for the resource viewer) — a
	// single-mip SRV over this frame's image (its resolve, where multisampled), through the resource's sampleable format
	// (R32_FLOAT for depth). The
	// graph has already moved a declared one to a shader-resource state for this pass.
	private void WriteListTextureSrv(int id, CpuDescriptorHandle dest)
	{
		if (id < InspectableResource.DebugTextureBase || _transients is null)
		{
			WriteTextureSrv(id, dest);
			return;
		}
		// A graph resource is sampled as a pass input is, through its resolve where it is multisampled
		WriteSrv(id - InspectableResource.DebugTextureBase, dest);
	}

	// Push a scalar block to the pass's root constants (the per-pass channel: JFA step, exposure rate, a
	// light-space matrix). Padded to the layout's 16 floats so the shader always reads defined memory.
	private void SetPush(DirectX12Technique baked, string technique, ReadOnlySpan<float> push)
	{
		// Reports both "no block to push into" and "the push overflows the block" — the second of which used to be a
		// silent truncation — and reads identically on every backend.
		_validate.PushFits(technique, push.Length * sizeof(float), routesToUniform: false, 0,
			baked.RootConstantParam >= 0 ? DirectX12Technique.ScalarPushSize : 0);
		var block = stackalloc float[16];
		new Span<float>(block, 16).Clear();
		push.CopyTo(new Span<float>(block, push.Length));
		_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
	}

	// Write the sampled inputs' SRVs into a fresh descriptor-ring range and bind it as the technique's SRV table.
	private void BindInputs(DirectX12Technique baked, ReadOnlySpan<ResourceHandle> inputs)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: sampled inputs require an active graph (SetGraph).");
		if (baked.TableParam < 0)
			throw new InvalidOperationException("DirectX12: technique declares no SRV table but sampled inputs were supplied.");

		var (cpu, gpu) = _descriptors.Allocate((uint)inputs.Length);
		for (var i = 0; i < inputs.Length; i++)
		{
			var id = inputs[i].Id;
			if (!_transients.IsTransient(id))
				throw new NotSupportedException("DirectX12: sampling the backbuffer or a buffer resource is not supported.");
			WriteSrv(id, _descriptors.Offset(cpu, (uint)i));
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
		_ctx.Device.CreateShaderResourceView(_transients.SampledResource(_slot, id), &srv, dest);
	}

	/// <summary>
	/// Bind a draw's root CBVs (b1..) in the shader's slot order: the slot reflection identified as the per-material
	/// block takes the material's parameter bytes, and the remaining slots take the pass's <c>.Reads</c> constants in
	/// order, then the per-draw block.
	/// <para>
	/// The material block is staged into the same per-frame upload ring as the per-draw block, so a material needs no
	/// GPU buffer of its own and carries no frames-in-flight hazard — which is also why
	/// <c>IEngine.Write(material, …)</c> takes effect on the next draw with no graph rebuild.
	/// </para>
	/// </summary>
	private void BindUniformSlots(DirectX12Technique baked, in DrawCall call)
	{
		var materialSlot = call.Material is not null ? baked.MaterialUniformSlot : null;
		var drawBlock = call.Uniforms.Length > 0;
		// Counting the slots up front (rather than appending) is what guarantees the material's slot is reached even
		// when it sits past the constants — appending would stop short and leave that CBV unbound.
		var total = Math.Min(_activeConstantsCount + (drawBlock ? 1 : 0) + (materialSlot.HasValue ? 1 : 0), baked.UniformBufferCount);

		var constant = 0;
		for (var slot = 0; slot < total; slot++)
		{
			var param = (uint)(baked.CbvRootParam + slot);
			if (slot == materialSlot)
			{
				_cmd.SetGraphicsRootConstantBufferView(param, _uniformUpload.Upload(_slot, _materials.ParametersOf(call.Material!.Value.Id)));
			}
			else if (constant < _activeConstantsCount)
			{
				_cmd.SetGraphicsRootConstantBufferView(param, _transients!.Resource(_slot, _activeConstantsIds[constant++]).GetGPUVirtualAddress());
			}
			else if (drawBlock)
			{
				_cmd.SetGraphicsRootConstantBufferView(param, _uniformUpload.Upload(_slot, call.Uniforms));
				drawBlock = false;
			}
		}
	}
}
