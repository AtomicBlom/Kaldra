// The WebGPU side of the Loom browser backend — the JS half of the marshaling seam.
//
// C# (compiled to WASM) drives all rendering: object creation arrives as individual `createX(handle, …)`
// calls that store into handle tables C# indexes, and per-frame work arrives as ONE binary command stream
// (see WebGpuCommandWriter.cs) replayed here into a single command encoder. So the WASM↔JS boundary is
// crossed once per frame, not once per draw.

const PROTOCOL_VERSION = 1;
const MAGIC = 0x4C574742; // 'LWGB'

// ── Handle tables (order must match WebGpuHandleAllocator's table ids) ──
const T_BUFFERS = 0, T_TEXTURES = 1, T_VIEWS = 2, T_SAMPLERS = 3, T_SHADER_MODULES = 4,
	T_BIND_GROUP_LAYOUTS = 5, T_PIPELINE_LAYOUTS = 6, T_RENDER_PIPELINES = 7,
	T_COMPUTE_PIPELINES = 8, T_BIND_GROUPS = 9;
const tables = Array.from({ length: 10 }, () => []);

// ── Format id → WGSL format string (must match WebGpuFormatMapping) ──
const FORMATS = {
	1: "rgba8unorm-srgb", 2: "rgba8unorm", 3: "rgb10a2unorm", 4: "rgba16float",
	5: "r16float", 6: "r32uint", 7: "rg16float", 8: "depth32float", 9: "r32float",
	10: "bgra8unorm", 11: "bgra8unorm-srgb",
};

const LOAD_OPS = ["clear", "load", "clear"]; // Clear, Load, DontCare→clear (WebGPU has no don't-care load)
const STORE_OPS = ["store", "discard"];

// Sampler address-mode ids (mirror WebGpuSamplerCache): clamp-to-edge, repeat, mirror-repeat.
const ADDRESS_MODES = ["clamp-to-edge", "repeat", "mirror-repeat"];

// Shader-stage visibility flags (mirror WebGpuBindingModel.Visibility*).
const VIS_VERTEX = 0x1, VIS_FRAGMENT = 0x2, VIS_COMPUTE = 0x4;

// Bind-group-layout entry kinds (mirror WebGpuBindingModel.Layout*).
const BGL_UNIFORM = 0, BGL_SAMPLED_TEXTURE = 1, BGL_FILTERING_SAMPLER = 2, BGL_STORAGE_BUFFER_RO = 3,
	BGL_STORAGE_BUFFER_RW = 4, BGL_STORAGE_TEXTURE = 5, BGL_COMPARISON_SAMPLER = 6, BGL_DEPTH_TEXTURE = 7;

// Command opcodes (must match WebGpuCommandWriter).
const OP_END = 0, OP_BEGIN_RENDER_PASS = 1, OP_END_PASS = 2, OP_SET_RENDER_PIPELINE = 3,
	OP_SET_BIND_GROUP = 4, OP_SET_VERTEX_BUFFER = 5, OP_SET_INDEX_BUFFER = 6, OP_DRAW = 7,
	OP_DRAW_INDEXED = 8, OP_SET_VIEWPORT = 9, OP_SET_SCISSOR = 10, OP_BEGIN_COMPUTE_PASS = 11,
	OP_SET_COMPUTE_PIPELINE = 12, OP_DISPATCH = 13, OP_WRITE_BUFFER = 14, OP_COPY_TEXTURE_TO_BUFFER = 15;

let device = null, queue = null, context = null, canvas = null;
let canvasViewFormat = "bgra8unorm";
const errors = [];

// ── Device / canvas ──

export function isWebGpuAvailable() { return typeof navigator !== "undefined" && !!navigator.gpu; }

export async function initDevice(canvasSelector, protocolVersion) {
	if (protocolVersion !== PROTOCOL_VERSION)
		throw new Error(`Loom WebGPU protocol mismatch: host ${protocolVersion}, shim ${PROTOCOL_VERSION} (stale cached loom-webgpu.js?).`);
	if (!navigator.gpu)
		throw new Error("WebGPU is not available (navigator.gpu is undefined).");

	canvas = document.querySelector(canvasSelector);
	if (!canvas) throw new Error(`Canvas not found: ${canvasSelector}`);

	const adapter = await navigator.gpu.requestAdapter();
	if (!adapter) throw new Error("No WebGPU adapter available.");

	const wanted = [];
	if (adapter.features.has("float32-filterable")) wanted.push("float32-filterable");
	device = await adapter.requestDevice({ requiredFeatures: wanted });
	device.addEventListener("uncapturederror", e => errors.push(String(e.error?.message ?? e.error)));
	queue = device.queue;

	context = canvas.getContext("webgpu");
	const preferred = navigator.gpu.getPreferredCanvasFormat();
	// Render through an sRGB view of the canvas so shader/clear values are sRGB-encoded on write, matching the
	// other backends' sRGB swapchains (their shared shaders assume it).
	canvasViewFormat = preferred === "rgba8unorm" ? "rgba8unorm-srgb" : "bgra8unorm-srgb";
	context.configure({ device, format: preferred, viewFormats: [canvasViewFormat], alphaMode: "opaque" });

	const info = adapter.info ?? {};
	const adapterName = [info.vendor, info.architecture, info.description].filter(Boolean).join(" ") || "WebGPU adapter";
	return JSON.stringify({
		adapter: adapterName,
		format: preferred,
		minUniformBufferOffsetAlignment: device.limits.minUniformBufferOffsetAlignment,
		float32Filterable: wanted.includes("float32-filterable"),
	});
}

export function configureCanvas(width, height) {
	if (!canvas || !context) return;
	const dpr = Math.min(window.devicePixelRatio || 1, 2);
	canvas.width = Math.max(1, Math.floor(width * dpr));
	canvas.height = Math.max(1, Math.floor(height * dpr));
	context.configure({ device, format: navigator.gpu.getPreferredCanvasFormat(), viewFormats: [canvasViewFormat], alphaMode: "opaque" });
}

// ── Object creation (C#-allocated handles) ──

export function createBuffer(handle, byteSize, usage, label) {
	tables[T_BUFFERS][handle] = device.createBuffer({ size: align4(byteSize), usage, label });
}

export function createTexture(handle, width, height, mipCount, formatId, usage, sampleCount, label) {
	tables[T_TEXTURES][handle] = device.createTexture({
		size: { width, height, depthOrArrayLayers: 1 },
		mipLevelCount: mipCount,
		sampleCount,
		format: FORMATS[formatId],
		usage,
		label,
	});
}

export function createTextureView(handle, textureHandle, formatId, baseMip, mipCount, label) {
	tables[T_VIEWS][handle] = tables[T_TEXTURES][textureHandle].createView({
		format: FORMATS[formatId],
		baseMipLevel: baseMip,
		mipLevelCount: mipCount,
		label,
	});
}

export function writeBuffer(bufferHandle, dstOffset, bytes) {
	const u8 = bytes.slice();
	queue.writeBuffer(tables[T_BUFFERS][bufferHandle], dstOffset, u8, 0, u8.byteLength);
}

export function releaseHandle(table, handle) {
	const obj = tables[table][handle];
	if (obj && (table === T_BUFFERS || table === T_TEXTURES) && typeof obj.destroy === "function")
		obj.destroy();
	tables[table][handle] = undefined;
}

export function createSampler(handle, linear, addressMode, compare) {
	const filter = linear ? "linear" : "nearest";
	const mode = ADDRESS_MODES[addressMode] ?? "clamp-to-edge";
	const desc = {
		magFilter: filter, minFilter: filter, mipmapFilter: filter,
		addressModeU: mode, addressModeV: mode, addressModeW: mode,
	};
	if (compare) desc.compare = "less-equal";
	tables[T_SAMPLERS][handle] = device.createSampler(desc);
}

export function createShaderModule(handle, wgsl, label) {
	tables[T_SHADER_MODULES][handle] = device.createShaderModule({ code: wgsl, label });
}

// Entries arrive as flat (binding, visibility, kind, extra) int quads.
export function createBindGroupLayout(handle, entries) {
	const a = entries.slice();
	const list = [];
	for (let i = 0; i < a.length; i += 4) {
		const binding = a[i], visibility = a[i + 1], kind = a[i + 2], extra = a[i + 3];
		const entry = { binding, visibility };
		switch (kind) {
			case BGL_UNIFORM: entry.buffer = { type: "uniform" }; break;
			case BGL_SAMPLED_TEXTURE: entry.texture = { sampleType: "float" }; break;
			case BGL_FILTERING_SAMPLER: entry.sampler = { type: "filtering" }; break;
			case BGL_STORAGE_BUFFER_RO: entry.buffer = { type: "read-only-storage" }; break;
			case BGL_STORAGE_BUFFER_RW: entry.buffer = { type: "storage" }; break;
			case BGL_STORAGE_TEXTURE: entry.storageTexture = { access: "read-write", format: FORMATS[extra] }; break;
			case BGL_COMPARISON_SAMPLER: entry.sampler = { type: "comparison" }; break;
			case BGL_DEPTH_TEXTURE: entry.texture = { sampleType: "depth" }; break;
			default: throw new Error(`createBindGroupLayout: unknown kind ${kind}`);
		}
		list.push(entry);
	}
	tables[T_BIND_GROUP_LAYOUTS][handle] = device.createBindGroupLayout({ entries: list });
}

export function createPipelineLayout(handle, bindGroupLayoutHandles) {
	const a = bindGroupLayoutHandles.slice();
	const layouts = Array.from(a, h => tables[T_BIND_GROUP_LAYOUTS][h]);
	tables[T_PIPELINE_LAYOUTS][handle] = device.createPipelineLayout({ bindGroupLayouts: layouts });
}

// The render-pipeline descriptor arrives as JSON (bake-time only): all module/layout references are int handles,
// format references are format ids, and fixed-function state is WebGPU string literals built on the C# side.
export function createRenderPipeline(handle, descriptorJson) {
	const d = JSON.parse(descriptorJson);
	const vertexBuffers = (d.vertexBuffers ?? []).map(b => ({
		arrayStride: b.arrayStride,
		stepMode: b.stepMode,
		attributes: b.attributes.map(at => ({ shaderLocation: at.shaderLocation, offset: at.offset, format: at.format })),
	}));
	const targets = d.colorTargets.map(t => {
		const target = { format: FORMATS[t.format], writeMask: GPUColorWrite.ALL };
		if (t.blend) target.blend = t.blend;
		return target;
	});
	const desc = {
		label: d.label,
		layout: tables[T_PIPELINE_LAYOUTS][d.layout],
		vertex: { module: tables[T_SHADER_MODULES][d.module], entryPoint: d.vertexEntry, buffers: vertexBuffers },
		fragment: { module: tables[T_SHADER_MODULES][d.module], entryPoint: d.fragmentEntry, targets },
		primitive: { topology: d.topology, cullMode: d.cullMode, frontFace: d.frontFace },
	};
	if (d.depth) desc.depthStencil = { format: FORMATS[d.depth.format], depthWriteEnabled: d.depth.write, depthCompare: d.depth.compare };
	tables[T_RENDER_PIPELINES][handle] = device.createRenderPipeline(desc);
}

export function createComputePipeline(handle, layoutHandle, moduleHandle, entryPoint, label) {
	tables[T_COMPUTE_PIPELINES][handle] = device.createComputePipeline({
		label,
		layout: tables[T_PIPELINE_LAYOUTS][layoutHandle],
		compute: { module: tables[T_SHADER_MODULES][moduleHandle], entryPoint },
	});
}

// Entries arrive as flat (binding, kind, resourceHandle, offset, size) int quints; kind 0 = buffer, 1 = view, 2 = sampler.
export function createBindGroup(handle, layoutHandle, entries) {
	const a = entries.slice();
	const list = [];
	for (let i = 0; i < a.length; i += 5) {
		const binding = a[i], kind = a[i + 1], res = a[i + 2], offset = a[i + 3], size = a[i + 4];
		let resource;
		if (kind === 0) resource = { buffer: tables[T_BUFFERS][res], offset, size: size > 0 ? size : undefined };
		else if (kind === 1) resource = tables[T_VIEWS][res];
		else resource = tables[T_SAMPLERS][res];
		list.push({ binding, resource });
	}
	tables[T_BIND_GROUPS][handle] = device.createBindGroup({ layout: tables[T_BIND_GROUP_LAYOUTS][layoutHandle], entries: list });
}

export function writeTexture() { throw new Error("writeTexture: not yet implemented."); }
export function beginReadbackMap() { throw new Error("beginReadbackMap: not yet implemented."); }
export function tryFetchReadback() { throw new Error("tryFetchReadback: not yet implemented."); }

// ── Error plumbing ──

export function pushErrorScope() { device.pushErrorScope("validation"); }
export function popErrorScope(context) {
	device.popErrorScope().then(err => { if (err) errors.push(`${context}: ${err.message}`); });
}
export function takeErrors() {
	if (errors.length === 0) return "";
	const joined = errors.join(" | ");
	errors.length = 0;
	return joined;
}

// ── The per-frame crossing: replay the command stream into one encoder, then submit ──

export function submitFrame(commandsView, arenaView) {
	const cmd = commandsView.slice();
	const i32 = new Int32Array(cmd.buffer);
	const f32 = new Float32Array(cmd.buffer);
	const arena = arenaView.byteLength > 0 ? arenaView.slice() : new Uint8Array(0);

	let p = 0;
	if (i32[p++] !== MAGIC) { errors.push("submitFrame: bad stream magic"); return; }
	p++; // version (validated at init)
	const totalWords = i32[p++];

	const encoder = device.createCommandEncoder();
	let pass = null;
	let backbufferView = null;
	let renderPasses = 0, firstClear = null;
	const resolveView = h => h === -1 ? (backbufferView ??= context.getCurrentTexture().createView({ format: canvasViewFormat })) : tables[T_VIEWS][h];

	while (p < totalWords) {
		const op = i32[p++];
		switch (op) {
			case OP_END: p = totalWords; break;
			case OP_BEGIN_RENDER_PASS: {
				const colorCount = i32[p++], hasDepth = i32[p++];
				const colorAttachments = [];
				for (let c = 0; c < colorCount; c++) {
					const view = i32[p++], loadOp = i32[p++], storeOp = i32[p++];
					const r = f32[p++], g = f32[p++], b = f32[p++], a = f32[p++];
					colorAttachments.push({ view: resolveView(view), loadOp: LOAD_OPS[loadOp], storeOp: STORE_OPS[storeOp], clearValue: { r, g, b, a } });
				}
				let depthStencilAttachment;
				if (hasDepth) {
					const view = i32[p++], loadOp = i32[p++], storeOp = i32[p++], clearDepth = f32[p++], readOnly = i32[p++];
					depthStencilAttachment = readOnly
						? { view: resolveView(view), depthReadOnly: true }
						: { view: resolveView(view), depthClearValue: clearDepth, depthLoadOp: LOAD_OPS[loadOp], depthStoreOp: STORE_OPS[storeOp] };
				}
				pass = encoder.beginRenderPass({ colorAttachments, depthStencilAttachment });
				renderPasses++;
				if (firstClear === null && colorAttachments.length > 0) firstClear = colorAttachments[0].clearValue;
				break;
			}
			case OP_END_PASS: pass.end(); pass = null; break;
			case OP_SET_RENDER_PIPELINE: pass.setPipeline(tables[T_RENDER_PIPELINES][i32[p++]]); break;
			case OP_SET_BIND_GROUP: {
				const group = i32[p++], bg = i32[p++], dynCount = i32[p++];
				const dyn = dynCount > 0 ? Array.from(i32.subarray(p, p + dynCount)) : [];
				p += dynCount;
				pass.setBindGroup(group, tables[T_BIND_GROUPS][bg], dyn);
				break;
			}
			case OP_SET_VERTEX_BUFFER: { const slot = i32[p++], buf = i32[p++], off = i32[p++]; pass.setVertexBuffer(slot, tables[T_BUFFERS][buf], off); break; }
			case OP_SET_INDEX_BUFFER: { const buf = i32[p++], fmt = i32[p++], off = i32[p++]; pass.setIndexBuffer(tables[T_BUFFERS][buf], fmt === 0 ? "uint16" : "uint32", off); break; }
			case OP_DRAW: { const vc = i32[p++], ic = i32[p++], fv = i32[p++], fi = i32[p++]; pass.draw(vc, ic, fv, fi); break; }
			case OP_DRAW_INDEXED: { const ic = i32[p++], inst = i32[p++], fi = i32[p++], bv = i32[p++], finst = i32[p++]; pass.drawIndexed(ic, inst, fi, bv, finst); break; }
			case OP_SET_VIEWPORT: { const x = f32[p++], y = f32[p++], w = f32[p++], h = f32[p++]; pass.setViewport(x, y, w, h, 0, 1); break; }
			case OP_SET_SCISSOR: { const x = i32[p++], y = i32[p++], w = i32[p++], h = i32[p++]; pass.setScissorRect(x, y, w, h); break; }
			case OP_BEGIN_COMPUTE_PASS: pass = encoder.beginComputePass(); break;
			case OP_SET_COMPUTE_PIPELINE: pass.setPipeline(tables[T_COMPUTE_PIPELINES][i32[p++]]); break;
			case OP_DISPATCH: { const x = i32[p++], y = i32[p++], z = i32[p++]; pass.dispatchWorkgroups(x, y, z); break; }
			case OP_WRITE_BUFFER: {
				const dst = i32[p++], dstOff = i32[p++], arenaOff = i32[p++], size = i32[p++];
				queue.writeBuffer(tables[T_BUFFERS][dst], dstOff, arena, arenaOff, size);
				break;
			}
			case OP_COPY_TEXTURE_TO_BUFFER: {
				const tex = i32[p++], w = i32[p++], h = i32[p++], buf = i32[p++], bufOff = i32[p++], bpr = i32[p++];
				encoder.copyTextureToBuffer(
					{ texture: tables[T_TEXTURES][tex] },
					{ buffer: tables[T_BUFFERS][buf], offset: bufOff, bytesPerRow: bpr },
					{ width: w, height: h, depthOrArrayLayers: 1 });
				break;
			}
			default: errors.push(`submitFrame: unknown opcode ${op} at word ${p - 1}`); p = totalWords; break;
		}
	}
	queue.submit([encoder.finish()]);
	lastFrameSummary = `words=${totalWords} renderPasses=${renderPasses} bbView=${backbufferView ? "yes" : "no"} firstClear=${firstClear ? JSON.stringify(firstClear) : "none"}`;
}

// ── Host helpers ──

export function setStatus(text) { const el = document.querySelector("#status"); if (el) el.textContent = text; }
export function getLocationSearch() { return globalThis.location?.search ?? ""; }

let frameCallback = null;
export function startFrameLoop(dotnetFrame) {
	frameCallback = dotnetFrame;
	const tick = ts => { frameCallback(ts); requestAnimationFrame(tick); };
	requestAnimationFrame(tick);
}

function align4(n) { return (n + 3) & ~3; }

// Diagnostic hook (read via the CDP verification harness): the pending error queue plus device/canvas state.
let lastFrameSummary = "none";
globalThis.__loomDebug = () => JSON.stringify({
	errors: errors.slice(),
	hasDevice: !!device,
	hasContext: !!context,
	canvasWH: canvas ? [canvas.width, canvas.height] : null,
	viewFormat: canvasViewFormat,
	lastFrame: lastFrameSummary,
});
