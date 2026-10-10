import AppKit

/// Text attributes for the capture overlays (loupe pill, dimension badge), built ONCE.
///
/// Why: the loupe redraws on every mouse move, and it used to create a fresh
/// `NSFont.monospacedSystemFont` on each frame. On macOS 26 the previous font's typeface
/// can be torn down on CoreText's private `InitShapingGlyphs` queue while the main thread
/// resolves the new one; CoreText then copies an attribute dictionary holding nil and
/// throws `attempt to insert nil object from objects[0]` → uncaught → abort().
/// 7/7 FrameLine crash reports (Oct 3–8 2026) are that exact stack (`LoupeView.draw` →
/// `NSAttributedString.size()`). Holding the fonts for the app's lifetime means they are
/// never deallocated, so that race cannot happen.
enum OverlayText {
    static let loupeAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.monospacedSystemFont(ofSize: 11, weight: .medium),
        .foregroundColor: NSColor.white,
    ]
    static let badgeAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
        .foregroundColor: NSColor.white,
    ]
}
