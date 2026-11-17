namespace FrameLine.Capture;

// OWNER: agent "capture". Public surface of the module:
//   CaptureManager (CaptureManager.cs), ScreenCapturer (ScreenCapturer.cs), CaptureSound (CaptureSound.cs),
//   RegionSelectOverlay (RegionSelectOverlay.cs, also used by region recording), Monitors / MonitorInfo,
//   WindowCapture / CaptureWindowInfo (window list for MCP).
public enum CaptureMode { Region, Window, FullScreen }
