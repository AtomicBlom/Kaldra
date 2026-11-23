# Subresource state: per-mip, per-layer resource timelines

A design note, not a specification. It exists because four separate roadmap features all stop at the same wall, and the wall is cheap to describe and expensive to hit four times.

## The wall

Kaldra's barrier derivation tracks **one state per resource**. `BarrierPlan.Derive` walks each resource's `ResourceState` timeline through a `Dictionary<int, ResourceState>` keyed by resource id, and emits a transition when consecutive uses disagree. Below it, the Vulkan backend hardcodes the range it transitions — `new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1)` — and copies with `LayerCount = 1`. Above it, `ResourceHandle` and `Domain` carry no mip or layer axis at all, so there is nothing for a user to declare and nothing for derivation to read.

Those three facts are consistent with each other, which is why nothing is broken today. They stop being consistent the moment one resource needs two states at once.

## What actually needs it

The four consumers are usually named together, and conflating them is the trap. They want different things.

**Hi-Z occlusion** needs per-**mip** state, and needs it in the hardest form: a pass that reads mip *k−1* while writing mip *k*, on the same image, in the same submission. One state per resource cannot express that at all — it is not that the barrier is too coarse, it is that the resource is simultaneously `ShaderRead` and `ColorAttachment` and the plan has one slot to say so.

**Single-pass CSM** needs per-**layer** state. Each cascade renders into one slice of a depth array; the lighting pass then reads the whole array. The read is uniform across layers, so this is the easy case — the granularity is needed to *build* the array, not to consume it.

**Froxel volumetrics is not a subresource problem.** It wants a `Texture3D`, which is a **dimension** change: a new resource kind, a depth extent on `ResourceHandle`, 3D views, 3D dispatch. Modelling its depth slices as array layers would work for exactly as long as nobody sampled trilinearly across them, and would then be wrong permanently. It belongs with roadmap item ② (renderable cube / array / 3D targets), not here. It appears in this note only to be excluded from it.

**Multi-view / XR** transitions the whole layer range *together*, always. It constrains the pass and domain surface — how a pass says "render this twice, once per eye" — rather than the barrier granularity. It is listed because a subresource design that made per-layer state mandatory would make multi-view more awkward, not less.

So: two consumers genuinely need finer state (Hi-Z per mip, CSM per layer), one needs a different axis entirely, and one needs a different layer of the API. Design for the first two; make sure the design does not obstruct the other two.

## The shape

Key the timeline on `(resource, mipRange, layerRange)` rather than on `resource`, with **whole-resource as the fast path**. A resource declaring one mip and one layer produces exactly one key, one timeline, and the same transitions it produces today.

That property is the reason to prefer this shape over the alternatives, and it is worth stating as a requirement rather than a hope: **the derived plan for every existing graph must come out byte-identical.** Every Kaldra resource today is single-mip single-layer, so if the plan changes for any of them, the change is wrong. It also means the golden images provably cannot move, which makes the whole change verifiable by a test suite that already exists.

## The five decisions

These are the choices that are hard to reverse once code depends on them. Making them explicitly is most of the value of this note.

**1. A view is `(id, range)`, not a distinct id.** Minting a fresh resource id per mip or per layer would be the smaller change, and it would be wrong: aliasing, lifetime and readback all key on the id, and splitting it makes one resource look like several to every one of them. A subresource view is the same resource narrowed.

**2. Per-subresource state lives in `GraphSchedule`/`BarrierPlan`, not in the backends.** The temptation is to let each backend widen its own ranges, because each API spells subresources differently. That is the cardinal violation in slow motion: the *decision* about what state a subresource is in is derivation's, and only the *spelling* is the backend's. Backends receive a range on the transition and translate it.

**3. Aliasing stays whole-resource.** `AliasPlan` partitions transients by lifetime interval; sub-allocating a mip chain across buckets buys little and complicates every lifetime question. A resource is aliased or not, as a whole.

**4. Multi-view is a domain property.** "Render this pass to N layers at once" is a statement about the pass's target, not about barriers. It should land on `Domain` and never require the user to name subresource ranges.

**5. Mips are author-declared; generation is a pass.** A resource declares how many mips it has. Filling them is an ordinary graph pass reading level *k−1* and writing level *k* — which is precisely the Hi-Z case, so getting mip generation expressible gets Hi-Z expressible. There is no implicit "generate mips" verb, because it would need ordering the user did not state, and ordering is derived here, never declared.

## What moves

- `BinaryVibrance.Kaldra.Core` — `ResourceHandle` gains a mip/layer axis; a way to name a subrange in a pass's reads and writes; `Domain` gains the layer count for multi-view.
- `BinaryVibrance.Kaldra.Graph` — `BarrierPlan`'s state map keys on the range; `ResourceTransition` carries one; `GraphSchedule`'s read/write edges carry one. `AliasPlan` and `LifetimePlan` are untouched by decision 3.
- The backends — `ImageSubresourceRange` from the transition rather than `(0, 1, 0, 1)` on Vulkan; `Subresource` index arithmetic on DirectX 12, which numbers subresources as `mip + layer * mipCount` and has no range form, so a multi-mip transition becomes several barriers there; WebGPU's `baseMipLevel`/`mipLevelCount` on the view.

The DirectX 12 asymmetry is the one worth knowing in advance: a neutral range is a loop on that backend, so a design that emits many narrow transitions where one wide one would do costs more there than the Vulkan spelling suggests.

## Status

Not scheduled. This note is the prerequisite thinking, written while the four consumers were being surveyed for [Milestone 8](https://github.com/AtomicBlom/Kaldra/milestone/8) so that whichever of them is picked up first does not have to rediscover the other three.
