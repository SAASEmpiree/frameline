using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ScreenRecorderLib;

namespace FrameLine.Recording;

/// Builds ScreenRecorderLib options for "full screen of the monitor under the cursor", or for a
/// rectangle (physical virtual-screen pixels) cropped out of the monitor that holds its centre.
internal static class RecorderSetup
{
    // Hardware H.264 encoders (and H.264 level 5.1/5.2) stop at 4096 x 2304: larger displays are
    // scaled down to fit, otherwise Media Foundation refuses to start.
    private const int MaxEncodeWidth = 4096, MaxEncodeHeight = 2304;

    public static RecorderOptions Build(int fps, bool systemAudio, bool microphone, bool hardwareEncoding, Int32Rect? region = null)
    {
        RecNative.RECT monitor;
        bool known;
        if (region is Int32Rect r)
        {
            var center = new RecNative.POINT { X = r.X + r.Width / 2, Y = r.Y + r.Height / 2 };
            known = RecNative.TryGetMonitorAt(center, out var name, out monitor, out _);
            var display = MakeSource(known ? name : null, ref monitor, ref known);
            // A region that cannot be located must fail, not silently record the whole main monitor.
            var crop = (known ? Crop(r, monitor) : null) ?? throw new ArgumentException("The region is not on a display.");
            return Finish(display, fps, systemAudio, microphone, hardwareEncoding, crop, monitor);
        }
        known = RecNative.TryGetMonitorUnderCursor(out var device, out monitor, out _);
        return Finish(MakeSource(known ? device : null, ref monitor, ref known), fps, systemAudio, microphone, hardwareEncoding, null, monitor);
    }

    /// The region clipped to its monitor, relative to the monitor's top-left, with even sides
    /// (H.264 needs them). Null when nothing of it is on that monitor.
    private static ScreenRect? Crop(Int32Rect r, RecNative.RECT m)
    {
        int left = Math.Max(r.X, m.Left), top = Math.Max(r.Y, m.Top);
        int right = Math.Min(r.X + r.Width, m.Right), bottom = Math.Min(r.Y + r.Height, m.Bottom);
        int w = (right - left) & ~1, h = (bottom - top) & ~1;
        if (w < 2 || h < 2) return null;
        return new ScreenRect(left - m.Left, top - m.Top, w, h);
    }

    private static RecorderOptions Finish(DisplayRecordingSource display, int fps, bool systemAudio, bool microphone,
        bool hardwareEncoding, ScreenRect? crop, RecNative.RECT monitor)
    {
        int pixelW = crop != null ? (int)crop.Width : monitor.Width;
        int pixelH = crop != null ? (int)crop.Height : monitor.Height;

        var output = new OutputOptions { RecorderMode = RecorderMode.Video, Stretch = StretchMode.Uniform };
        if (crop != null) output.SourceRect = crop;
        if (pixelW > MaxEncodeWidth || pixelH > MaxEncodeHeight)
        {
            double k = Math.Min((double)MaxEncodeWidth / pixelW, (double)MaxEncodeHeight / pixelH);
            int w = (int)Math.Floor(pixelW * k) & ~1, h = (int)Math.Floor(pixelH * k) & ~1;
            output.OutputFrameSize = new ScreenSize(w, h);
        }

        var audioSources = new List<AudioSourceBase>();
        if (systemAudio && LoopbackAudioSource.Default is { } loop) audioSources.Add(loop);
        if (microphone && CaptureAudioSource.Default is { } mic) audioSources.Add(mic);

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { display } },
            OutputOptions = output,
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High },
                Quality = 80,
                Framerate = Math.Clamp(fps, 1, 120),
                // Variable frame rate like ScreenCaptureKit: frames are written when the screen changes.
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = hardwareEncoding,
                // Fast start rewrites the whole file at stop; on long recordings that outlasts the
                // stop safety net. Plain MP4 finalises in constant time.
                IsMp4FastStartEnabled = false,
                IsFragmentedMp4Enabled = false,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo,
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true, IsMouseClicksDetected = false },
        };
    }

    /// The ScreenRecorderLib display matching the GDI device name; the main monitor otherwise, whose
    /// size is then unknown (`known` false, no downscale, no crop).
    private static DisplayRecordingSource MakeSource(string? device, ref RecNative.RECT bounds, ref bool known)
    {
        string? match = null;
        try
        {
            match = Recorder.GetDisplays()
                .Select(d => d.DeviceName)
                .FirstOrDefault(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* enumeration failure: fall back to the main monitor below */ }
        if (match == null) { known = false; bounds = default; }

        var source = match != null ? new DisplayRecordingSource(match) : new DisplayRecordingSource(DisplayRecordingSource.MainMonitor);
        source.RecorderApi = RecorderApi.DesktopDuplication;
        source.IsCursorCaptureEnabled = true;
        source.IsBorderRequired = false;
        return source;
    }
}
