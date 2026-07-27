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
	BGL_STORAGE_BUFFER_RW = 4, BGL_STORAGE_TEXTURE = 5, BGL_COMPARISON_SAMPLER = 6, BGL_DEPTH_TEXTURE = 7,
	BGL_UNFILTERABLE_TEXTURE = 8, BGL_NON_FILTERING_SAMPLER = 9;

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

// A demo asset (model/texture bytes), fetched over HTTP from a static file under wwwroot — the browser
// counterpart to a desktop File.ReadAllBytesAsync. `url` is relative to the served root (e.g.
// "assets/kenney/castle/tree-large.glb"), matching the desktop host's on-disk relative path. Returns base64: the
// JSImport source generator doesn't support byte[]/MemoryView as an async (Promise-returning) return type, only
// as an argument — base64 text rides the same Task<string> channel initDevice already proves works.
export async function fetchAsset(url) {
	const res = await fetch(url);
	if (!res.ok)
		throw new Error(`Fetching asset '${url}' failed: ${res.status} ${res.statusText}`);
	const bytes = new Uint8Array(await res.arrayBuffer());
	// Chunked (not one String.fromCharCode.apply over the whole array) so a large asset doesn't blow the call stack.
	let binary = "";
	const chunkSize = 0x8000;
	for (let i = 0; i < bytes.length; i += chunkSize)
		binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
	return btoa(binary);
}

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
	// COPY_SRC lets the capture path read the backbuffer straight off the swapchain texture (compositor-independent).
	context.configure({ device, format: preferred, viewFormats: [canvasViewFormat], alphaMode: "opaque",
		usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC });

	const info = adapter.info ?? {};
	const adapterName = [info.vendor, info.architecture, info.description].filter(Boolean).join(" ") || "WebGPU adapter";
	return JSON.stringify({
		adapter: adapterName,
		format: preferred,
		minUniformBufferOffsetAlignment: device.limits.minUniformBufferOffsetAlignment,
		float32Filterable: wanted.includes("float32-filterable"),
	});
}

// The backing-store size IS the render resolution the engine drives (its transients and viewport match it), so
// set it verbatim — the host derives it from the canvas's CSS display size × devicePixelRatio (getCanvasSize), so
// the content fills the canvas crisply rather than underfilling it by the DPR factor.
export function configureCanvas(width, height) {
	if (!canvas || !context) return;
	canvas.width = Math.max(1, width);
	canvas.height = Math.max(1, height);
	context.configure({ device, format: navigator.gpu.getPreferredCanvasFormat(), viewFormats: [canvasViewFormat], alphaMode: "opaque",
		usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC });
}

// The canvas's current display resolution in device pixels — its CSS size (100vw×100vh) times the (capped) device
// pixel ratio. The host reads this to size the engine so one rendered pixel maps to one device pixel.
export function getCanvasSize(canvasSelector) {
	const c = document.querySelector(canvasSelector);
	if (!c) return "1280,720";
	const dpr = Math.min(window.devicePixelRatio || 1, 2);
	const w = Math.max(1, Math.round((c.clientWidth || 1280) * dpr));
	const h = Math.max(1, Math.round((c.clientHeight || 720) * dpr));
	return `${w},${h}`;
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
			// Every uniform binding is dynamic-offset: graph constants bind at offset 0, per-draw/push blocks ride
			// the uniform ring, so a bind group stays valid across draws while only the offset changes.
			case BGL_UNIFORM: entry.buffer = { type: "uniform", hasDynamicOffset: true }; break;
			case BGL_SAMPLED_TEXTURE: entry.texture = { sampleType: "float" }; break;
			case BGL_FILTERING_SAMPLER: entry.sampler = { type: "filtering" }; break;
			case BGL_STORAGE_BUFFER_RO: entry.buffer = { type: "read-only-storage" }; break;
			case BGL_STORAGE_BUFFER_RW: entry.buffer = { type: "storage" }; break;
			case BGL_STORAGE_TEXTURE: entry.storageTexture = { access: "read-write", format: FORMATS[extra] }; break;
			case BGL_COMPARISON_SAMPLER: entry.sampler = { type: "comparison" }; break;
			case BGL_DEPTH_TEXTURE: entry.texture = { sampleType: "depth" }; break;
			// A non-filtering technique's textures bind as unfilterable-float (accepts depth AND colour formats — a
			// depth32float shadow map has no filterable-float sample type) paired with a non-filtering sampler.
			case BGL_UNFILTERABLE_TEXTURE: entry.texture = { sampleType: "unfilterable-float" }; break;
			case BGL_NON_FILTERING_SAMPLER: entry.sampler = { type: "non-filtering" }; break;
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
	if (d.depth) desc.depthStencil = {
		format: FORMATS[d.depth.format], depthWriteEnabled: d.depth.write, depthCompare: d.depth.compare,
		depthBias: d.depth.bias, depthBiasSlopeScale: d.depth.biasSlopeScale, depthBiasClamp: d.depth.biasClamp,
	};
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

export function writeTexture(textureHandle, mip, width, height, bytesPerRow, bytes) {
	const u8 = bytes.slice();
	queue.writeTexture(
		{ texture: tables[T_TEXTURES][textureHandle], mipLevel: mip },
		u8,
		{ offset: 0, bytesPerRow, rowsPerImage: height },
		{ width, height, depthOrArrayLayers: 1 });
}

// Async GPU→CPU readback: mapAsync resolves on a later task, so C# starts a map then polls until it lands. Each
// slot tracks its buffer and state (pending → mapped) independently.
const readbackSlots = [];
export function beginReadbackMap(slot, bufferHandle) {
	const buffer = tables[T_BUFFERS][bufferHandle];
	const s = readbackSlots[slot] = { buffer, mapped: false };
	buffer.mapAsync(GPUMapMode.READ).then(() => { s.mapped = true; }).catch(e => { errors.push(`mapAsync: ${e.message}`); });
}

export function tryFetchReadback(slot, destination) {
	const s = readbackSlots[slot];
	if (!s || !s.mapped) return false;
	const src = new Uint8Array(s.buffer.getMappedRange());
	destination.set(src.subarray(0, Math.min(src.length, destination.length)));
	s.buffer.unmap();
	readbackSlots[slot] = null;
	return true;
}

// Save a captured frame's PNG to a file via a transient object-URL anchor (manual verification).
export function downloadCapture(fileName, png) {
	const blob = new Blob([png.slice()], { type: "image/png" });
	const url = URL.createObjectURL(blob);
	const a = document.createElement("a");
	a.href = url; a.download = fileName;
	document.body.appendChild(a); a.click(); a.remove();
	URL.revokeObjectURL(url);
}

// POST a captured frame's PNG to the harness (fire-and-forget); the diff loop's local server saves it.
export function postCapture(name, png) {
	fetch(`/capture?name=${encodeURIComponent(name)}`, { method: "POST", body: png.slice(), headers: { "Content-Type": "image/png" } })
		.catch(e => errors.push(`postCapture: ${e.message}`));
}

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
	let currentTexture = null, backbufferView = null;
	let renderPasses = 0, firstClear = null;
	const getCurrentTexture = () => currentTexture ??= context.getCurrentTexture();
	const resolveView = h => h === -1 ? (backbufferView ??= getCurrentTexture().createView({ format: canvasViewFormat })) : tables[T_VIEWS][h];

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
				// Sentinel -1 = the swapchain's current texture (the capture path reads the backbuffer directly).
				const srcTexture = tex === -1 ? getCurrentTexture() : tables[T_TEXTURES][tex];
				encoder.copyTextureToBuffer(
					{ texture: srcTexture },
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

// The successful-boot message fades the on-page status line to near-invisible: the in-canvas SUIsei shell's top/
// bottom bars now show the device name and status, so the fixed bottom-left HTML div would otherwise sit right on
// top of the shell's bottom bar. Its text stays in the DOM (unfaded) for the verification harness's CDP reads —
// only its opacity changes. Loading/error text (anything else) stays fully visible.
export function setStatus(text) {
	const el = document.querySelector("#status");
	if (!el) return;
	el.textContent = text;
	// Error text carries a full stack trace and is otherwise hard to read against the canvas — give it a
	// background box and full opacity instead of the normal near-invisible status line.
	const isError = text.includes("ERROR");
	el.classList.toggle("status-error", isError);
	el.style.opacity = isError ? "1" : (text.startsWith("Loom running on") ? "0" : "0.65");
}
export function getLocationSearch() { return globalThis.location?.search ?? ""; }

// Mirrors an error's full formatted text (message + stack trace + inner exceptions) to devtools, for interactive
// debugging — the on-page #status line carries the same text so headless captures see it too.
export function consoleError(text) { console.error(text); }

let frameCallback = null;
export function startFrameLoop(dotnetFrame) {
	frameCallback = dotnetFrame;
	const tick = ts => { frameCallback(ts); requestAnimationFrame(tick); };
	requestAnimationFrame(tick);
}

// Notify C# on window resize, coalesced to one call per animation frame so a drag-resize doesn't thrash the
// engine's transient reallocation. C# re-reads getCanvasSize and drives engine.Resize.
export function startResizeHandler(dotnetResize) {
	let pending = false;
	const onResize = () => {
		if (pending) return;
		pending = true;
		requestAnimationFrame(() => { pending = false; dotnetResize(); });
	};
	window.addEventListener("resize", onResize);
}

// Forward canvas pointer events to C# in device pixels (CSS position × the capped device-pixel ratio, matching
// getCanvasSize), so a hit-test lands on the same geometry the engine rendered. phase: 0 = move, 1 = down, 2 = up.
export function startPointerHandler(dotnetPointer) {
	if (!canvas) return;
	const send = (e, phase) => {
		const rect = canvas.getBoundingClientRect();
		const dpr = Math.min(window.devicePixelRatio || 1, 2);
		dotnetPointer((e.clientX - rect.left) * dpr, (e.clientY - rect.top) * dpr, phase);
	};
	canvas.addEventListener("pointermove", e => send(e, 0));
	canvas.addEventListener("pointerdown", e => send(e, 1));
	canvas.addEventListener("pointerup", e => send(e, 2));
}

// Forward DOM pointer/keyboard/text + Gamepad-API events to the host as neutral input. Each callback carries at
// most three values (the ceiling the .NET source-generated JS interop marshals for a function): the three-value
// events get a dedicated callback (onPointerMove, onGamepadButton, onGamepadAxis), the rest share the
// discriminated onEvent(kind, a, b) channel, and onKey(code, down) carries the physical KeyboardEvent.code string
// the host maps to a neutral key. Pointer moves are coalesced and the poll-only Gamepad API is diffed once per
// animation frame, so a high-rate device can't flood the frame's event capacity. Pointer coordinates are device
// pixels (CSS position × the capped device-pixel ratio, matching getCanvasSize) so a hit-test lands on the
// geometry the engine rendered.
export function startInputHandlers(onPointerMove, onGamepadButton, onGamepadAxis, onEvent, onKey) {
	// onEvent kind codes — must match WebInputPump's Kind* constants.
	const POINTER_BUTTON = 1, WHEEL = 2, TEXT = 3, GAMEPAD_CONNECTION = 4, FOCUS = 5;

	// Pointer/wheel target the canvas (coordinates are canvas-relative); keyboard/focus/gamepad target the window
	// so they work regardless of which element holds DOM focus in a full-screen canvas app.
	const target = canvas ?? document;
	const pointerKind = t => t === "pen" ? 1 : t === "touch" ? 2 : 0;
	const toDevice = e => {
		const rect = (canvas ?? document.documentElement).getBoundingClientRect();
		const dpr = Math.min(window.devicePixelRatio || 1, 2);
		return [(e.clientX - rect.left) * dpr, (e.clientY - rect.top) * dpr];
	};

	// Pointer move: keep only the latest per frame, flushed by the rAF loop below (or eagerly before a button so
	// a click lands where the cursor is).
	let pendingMove = null;
	const flushMove = () => {
		if (!pendingMove) return;
		onPointerMove(pendingMove[0], pendingMove[1], pendingMove[2]);
		pendingMove = null;
	};
	target.addEventListener("pointermove", e => {
		const [x, y] = toDevice(e);
		pendingMove = [x, y, pointerKind(e.pointerType)];
	});
	const button = (e, down) => {
		const [x, y] = toDevice(e);
		pendingMove = [x, y, pointerKind(e.pointerType)];
		flushMove();
		onEvent(POINTER_BUTTON, e.button, down ? 1 : 0);
	};
	target.addEventListener("pointerdown", e => button(e, true));
	target.addEventListener("pointerup", e => button(e, false));
	// Vertical scroll rides deltaY, a shift-scroll reports on deltaX; passive since the host doesn't cancel it.
	target.addEventListener("wheel", e => onEvent(WHEEL, e.deltaX, e.deltaY), { passive: true });

	// Keyboard state via the physical (layout-independent) code; drop auto-repeat — the host's held state already
	// covers a key held across frames, and typed characters come through the text channel below.
	window.addEventListener("keydown", e => { if (!e.repeat) onKey(e.code, true); });
	window.addEventListener("keyup", e => onKey(e.code, false));
	// Typed text via keypress: it delivers a layout/shift-resolved character that raw key codes can't. Emit only
	// genuine single-character values (skip named keys like "Enter"/"ArrowUp" that also fire keypress), one event
	// per code point so an astral character (e.g. an emoji) arrives as a single UTF-32 code point.
	window.addEventListener("keypress", e => {
		const cps = [...(e.key ?? "")];
		if (cps.length === 1 && cps[0].codePointAt(0) >= 0x20)
			onEvent(TEXT, cps[0].codePointAt(0), 0);
	});

	// Focus/visibility: the browser drops keyup for keys held across a blur or tab-hide, so notify the host (it
	// synthesizes the releases). focused = 1 on regain, 0 on loss.
	window.addEventListener("blur", () => onEvent(FOCUS, 0, 0));
	window.addEventListener("focus", () => onEvent(FOCUS, 1, 0));
	document.addEventListener("visibilitychange", () => onEvent(FOCUS, document.hidden ? 0 : 1, 0));

	// Gamepads: connect/disconnect via the DOM events, button/axis changes by diffing the poll-only API each frame.
	const padStates = [];
	window.addEventListener("gamepadconnected", e => { padStates[e.gamepad.index] = { buttons: [], axes: [] }; onEvent(GAMEPAD_CONNECTION, e.gamepad.index, 1); });
	window.addEventListener("gamepaddisconnected", e => { padStates[e.gamepad.index] = null; onEvent(GAMEPAD_CONNECTION, e.gamepad.index, 0); });

	const AXIS_EPS = 1e-3;
	const pollGamepads = () => {
		const pads = navigator.getGamepads ? navigator.getGamepads() : [];
		for (let i = 0; i < pads.length; i++) {
			const gp = pads[i];
			if (!gp) continue;
			const st = padStates[i] ??= { buttons: [], axes: [] };
			for (let b = 0; b < gp.buttons.length; b++) {
				const btn = gp.buttons[b];
				// Buttons 6/7 are the analog triggers — forward their value as axes 4/5, not as digital buttons.
				if (b === 6 || b === 7) {
					const axis = b === 6 ? 4 : 5;
					if (st.axes[axis] === undefined || Math.abs(st.axes[axis] - btn.value) > AXIS_EPS) {
						st.axes[axis] = btn.value;
						onGamepadAxis(i, axis, btn.value);
					}
				} else if (st.buttons[b] !== btn.pressed) {
					st.buttons[b] = btn.pressed;
					onGamepadButton(i, b, btn.pressed ? 1 : 0);
				}
			}
			for (let a = 0; a < gp.axes.length && a < 4; a++) {
				if (st.axes[a] === undefined || Math.abs(st.axes[a] - gp.axes[a]) > AXIS_EPS) {
					st.axes[a] = gp.axes[a];
					onGamepadAxis(i, a, gp.axes[a]);
				}
			}
		}
	};

	// One maintenance tick per animation frame: flush the coalesced pointer move, then diff the gamepads.
	const tick = () => { flushMove(); pollGamepads(); requestAnimationFrame(tick); };
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
