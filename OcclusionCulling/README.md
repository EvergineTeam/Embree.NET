# OcclusionCulling

What a CPU occlusion culling pass costs over a thousand objects, with Embree answering the
visibility queries. No shading, no lighting, no shadows: this measures visibility and nothing
else.

![The scene](docs/scene.png)

A thousand boxes scattered at random through a cube, each with its own size on each axis, 12,000
triangles, one Embree geometry per box so every one has its own `geomID`. The layout comes from a
fixed seed, so the scene is the same scene on every run — the benchmark compares medians between
runs and a clock-seeded layout would move the numbers with no way to tell that from a real
change.

Scattered rather than gridded on purpose. A regular grid flatters occlusion culling: every
occluder is the same size and sits exactly behind the one in front, which is the easiest case
there is. Random sizes and positions give irregular gaps, and those are what the technique
actually has to cope with.

## The two passes

Both run after a frustum pass, which is the order a real engine uses. They answer different
questions and their cost scales with different things.

**Per-object rays.** Nine sample points per box (eight AABB corners and the centre), one ray
each, stopping at the first sample that reaches the box. Cost scales with the object count. This
is what an engine runs to decide which draw calls to submit.

**Visibility buffer.** A 320×180 grid of primary rays; whatever geometry they land on is
visible. Cost scales with resolution and does not care how many objects there are.

Each comes in a single-ray and an 8-wide packet form.

## Results

24 logical cores, Embree 4.4.1 built with `EMBREE_MAX_ISA=AVX2`:

```
Method                                        median      mean       rays   visible  culled   miss  screen
frustum only                                 0.004ms   0.004ms          0     1,000    0.0%      0   0.00%
per-object, single ray                       0.205ms   0.204ms      7,861       229   77.1%    119   7.45%
per-object, 8-wide packets                   0.349ms   0.373ms      9,000       231   76.9%    117   7.14%
visibility buffer 320x180, single ray        0.644ms   0.652ms     57,600       316   68.4%     30   0.15%
visibility buffer 320x180, 8-wide packets    0.661ms   0.680ms     57,600       316   68.4%     30   0.15%
```

`miss` counts visible boxes the pass discarded — the ones that would pop. `screen` is how much of
the covered screen area those boxes actually held, which is the number that matters: much of what
is technically visible is visible through a gap a pixel or two wide.

**The per-object pass costs 0.21 ms and removes three quarters of the draw calls.** Against a
16.6 ms frame that is a bit over 1%, so it pays for itself as soon as the objects it removes cost
more than that to draw.

The two passes trade against each other rather than one being better. Per-object is three times
cheaper and culls more (77% against 68%), but discards boxes holding 7.5% of the screen. The
visibility buffer misses almost nothing — 0.15% — and is the one to reach for if popping matters
more than the milliseconds.

![Which boxes survived](docs/verdict.png)

Green is a box the per-object pass kept, red one it discarded. Every red patch is a fragment
showing through a gap between nearer boxes.

![A slab through the middle of the cloud](docs/slice.png)

The middle slab seen from above, camera at the cross. Bright boxes survived, dark ones did not:
the culling keeps the side facing the camera and throws away what is behind it.

## Things worth knowing before copying this

**Ask the device how wide a packet it supports.** `rtcIntersect16`/`rtcOccluded16` may only be
called when `RTC_DEVICE_PROPERTY_NATIVE_RAY16_SUPPORTED` says so; calling them anyway is
undefined behaviour, and in practice it corrupts the heap and takes the process down somewhere
unrelated with no hint of the real cause. The binaries this package ships are built with
`EMBREE_MAX_ISA=AVX2`, which tops out at 8 — 16 needs AVX-512. The sample prints the width it
found.

**Packets are not automatically faster.** Here they are 70% *slower* for the per-object pass,
because filling eight lanes means giving up the early exit: the moment one sample proves a box
visible the rest are pointless, but the lanes are already committed. 9,000 rays instead of 7,861,
and the SIMD saving does not cover the difference. For the visibility buffer, where all the rays
are needed anyway, the two come out level. Neither result was obvious in advance.

**The two traversal paths do not agree exactly.** Single-ray finds 229 boxes visible, 8-wide
finds 231, reproducibly. They are separate kernels inside Embree and they disagree on grazing
hits. Two boxes in a thousand does not matter for culling, but it does mean the packet path is
not a drop-in replacement anywhere the answer has to be bit-identical.

**The obvious formulation of the per-object test is wrong.** Aiming an occlusion ray at a sample
point on a box and stopping just short of it has the box occlude itself: the point is on its
surface, so its own front face is in the way for all but a few silhouette corners. This asks what
the ray hits *first* instead, which costs more per ray and answers the question actually being
asked.

**Ray structures must be aligned.** `RTCRay8` wants 32 bytes, `RTCRay16` wants 64, and a C# local
guarantees neither. Every buffer here comes from `NativeMemory.AlignedAlloc`.

## Running it

```bash
dotnet run --project OcclusionCulling -c Release
```

`--images` also writes `scene.png`, `verdict.png` and `slice.png` to the working directory. The
PNG writer is about sixty lines in `Png.cs`, which keeps the sample free of dependencies and able
to run on every RID the binding ships; `System.Drawing` would have pinned it to Windows.
