# HelloEmbree

CPU ray tracing with `Evergine.Bindings.Embree`, presented through the **Evergine low-level
graphics API** — no Evergine Framework, no scene graph, just `GraphicsContext`, `SwapChain`,
`CommandQueue` and a fullscreen triangle — inside a plain **Windows Forms** window.

![The sample running](docs/window.png)

## Hosting inside a WinForms window

`MainForm` is an ordinary `Form` with a toolbar and a status bar. Evergine renders into an
[`EvergineControl`](https://github.com/EvergineTeam/Evergine.Public) docked in the middle of it,
so the ray traced image participates in a normal WinForms layout:

```csharp
form.CreateControl();                                  // realize the HWND first
var surfaceInfo = new SurfaceInfo(form.RenderControl.Handle, SurfaceInfo.SurfaceTypes.Forms);
// ... swapChainDescription.SurfaceInfo = surfaceInfo ...

var windowSystem = new FormsWindowsSystem { AutoRegisterWindow = false };
windowSystem.RegisterLoopThreadControl(form);          // loop ends when the form closes
windowSystem.Run(Load, Draw);
```

Two details matter here:

- The HWND has to be read **after** the control is parented. WinForms recreates a control's
  handle when it is added to a container, which would leave the swapchain bound to a dead window.
- `AutoRegisterWindow = false` stops `FormsWindowsSystem` from creating its own window;
  `RegisterLoopThreadControl(form)` points the render loop at our form instead.

The status bar shows the live per-stage cost, the toolbar can freeze the camera and write a PNG.
Resizing the window only resizes the swapchain — the ray traced image keeps its own fixed
resolution and is stretched by the fullscreen triangle, so resizing costs nothing on the CPU.

## What it does

Each frame:

1. **`Raytracer.cs`** traces the scene on the CPU with Embree. Primary rays go through
   `rtcIntersect1`, hard shadows through `rtcOccluded1`, and the result is Lambert-shaded into
   an RGBA8 buffer. The work is spread over all cores with `Parallel.For` over image rows.
2. **`Program.cs`** uploads that buffer to a `Texture2D` with `GraphicsContext.UpdateTextureData`
   and draws it to the DX11 swapchain with a fullscreen triangle (HLSL compiled at runtime via
   `GraphicsContext.ShaderCompile`, no vertex buffer — positions come from `SV_VertexID`).

The scene is a checkered ground plane, a rotated cube, and two icospheres — 2,574 triangles in
four separate geometries so each gets its own `geomID` and albedo.

Around frame 10 the swapchain color target is copied into a staging texture and saved as
`screenshot.png` (the same staging + `MapMemory` pattern as Evergine's `SnapShoter`).

## Running it

The sample needs the native Embree library. It is **not** committed to this repository — see the
[root README](../README.md); build it with the
[Build Embree Libraries](../.github/workflows/embree-cmake.yml) workflow, or drop a
`embree4.dll` into `Evergine.Bindings.Embree/runtimes/win-x64/native/`.

> If you use the official Intel release binaries instead of the workflow output, `embree4.dll`
> depends on TBB. Windows resolves a DLL's dependencies next to the DLL being loaded, not next
> to the executable, so `tbb12.dll` and `tbbmalloc.dll` have to sit in the same
> `runtimes/win-x64/native/` folder. The workflow builds Embree with
> `EMBREE_TASKING_SYSTEM=INTERNAL`, which removes the dependency entirely.

```bash
dotnet run --project HelloEmbree -c Release
```

Options:

| Flag | Effect |
|---|---|
| *(none)* | Opens the window, camera orbits the scene, writes `screenshot.png` at frame 10 and on demand from the toolbar |
| `--exit` | Same, but closes right after the screenshot |
| `--bench` | Disables VSync, discards 20 warm-up frames, times 100 frames and prints a breakdown |

## Measured cost

`--bench` on a 24-thread CPU, 800x600:

```
Stage                    min       median       mean        max
Embree CPU trace       3.83 ms     4.44 ms     4.50 ms     5.63 ms
Texture upload         0.05 ms     0.05 ms     0.06 ms     0.10 ms
GPU draw + sync        0.01 ms     0.03 ms     0.04 ms     0.13 ms
```

Practically all of the frame is CPU tracing; uploading the 1.9 MB texture and blitting it costs
under 2% of the frame. Note the reported rays/s covers the whole per-pixel loop — ray setup,
`rtcIntersect1`, shading, `rtcOccluded1` and the byte writes — not `rtcIntersect1` alone.

## Note on ray alignment

`RTCRay` and `RTCRayHit` must be 16-byte aligned; Embree's kernels use aligned SIMD loads and a
C# local carries no such guarantee. `Raytracer.Render` gives each `Parallel.For` worker its own
`NativeMemory.AlignedAlloc` block for the ray and shadow ray. Taking the address of a stack local
instead crashes with an access violation on some code paths and silently works on others.
