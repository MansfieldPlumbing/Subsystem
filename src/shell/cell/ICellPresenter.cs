namespace Subsystem.Shell.Cell;

// The ONE present seam — the only thing that branches per head (CONTRACT invariant 4: "the UI is a
// presenter, holds nothing"). The shared compositor/loop produces a CellBuffer frame and hands it to
// Present(); the head supplies the implementation:
//   Windows desktop/terminal -> VtRenderer (cells -> VT escapes -> conhost/Windows Terminal); a D3D12 cell
//                               presenter is the GPU-window peer.
//   Android                  -> VulkanPresenter (HOLDS an Android.Views.Surface -> ANativeWindow ->
//                               vkCreateAndroidSurfaceKHR -> swapchain; cells -> atlas -> instanced quads).
// Named "presenter" (not "surface") on purpose: "Surface" is the OS concept (Android.Views.Surface) and the
// DirectPort producer (windows/Surface.cs) — the seam must not collide with either. "Present" is the WSI
// verb (vkQueuePresentKHR / IDXGISwapChain::Present), so the name is mechanism, not metaphor. Narrow on
// purpose so a second head adds exactly one file, not a fork. d3d12 <-> vulkan is the expected delta.
public interface ICellPresenter
{
    // Enter the present path (alternate screen / device + swapchain). Idempotent per session.
    void Initialize();

    // Paint `current`, diffing against `previous` so only changed cells are emitted; on return `previous`
    // holds the just-drawn frame as the next baseline.
    void Present(CellBuffer current, CellBuffer previous);

    // Force a full repaint next Present (call on resize / surface-lost).
    void Invalidate();

    // Leave the present path and restore prior state (scrollback / release device).
    void Shutdown();
}
