// make_icon.swift — icône d'app ORIGINALE pour FrameLine.
// Logo #1 « coins de cadrage » : squircle graphite (dégradé vertical #2C2C2E→#161618),
// 4 crochets en L blancs (angles d'une sélection de capture) + point rouge d'enregistrement #FF453A.
// Reproduction CoreGraphics fidèle du SVG master (frameline-logo/svg/frameline.svg), grille 120 ×8.5333.
// Sortie : PNG 1024×1024 vers le chemin passé en argument (défaut AppIcon-1024.png).
import CoreGraphics
import ImageIO
import Foundation
import UniformTypeIdentifiers

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon-1024.png"
let S = 1024
let cs = CGColorSpace(name: CGColorSpace.sRGB)!
guard let ctx = CGContext(data: nil, width: S, height: S, bitsPerComponent: 8, bytesPerRow: 0,
                          space: cs, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { fatalError("ctx") }
func col(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
    CGColor(colorSpace: cs, components: [CGFloat(r), CGFloat(g), CGFloat(b), CGFloat(a)])!
}
ctx.clear(CGRect(x: 0, y: 0, width: S, height: S))

let W = CGFloat(S)
// Squircle plein cadre (rayon 22,5 % comme le SVG : rx 230.4 sur 1024)
let radius: CGFloat = 230.4
let squircle = CGPath(roundedRect: CGRect(x: 0, y: 0, width: W, height: W),
                      cornerWidth: radius, cornerHeight: radius, transform: nil)

// Fond : dégradé vertical (haut #2C2C2E clair → bas #161618 sombre). CoreGraphics : y vers le HAUT.
ctx.saveGState()
ctx.addPath(squircle); ctx.clip()
let bg = CGGradient(colorsSpace: cs, colors: [
    col(44.0/255, 44.0/255, 46.0/255),   // #2C2C2E (haut)
    col(22.0/255, 22.0/255, 24.0/255)    // #161618 (bas)
] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: W/2, y: W), end: CGPoint(x: W/2, y: 0), options: [])
ctx.restoreGState()

// 4 crochets en L blancs. Repère grille 120 → ×8.5333 ; symétrie 4 coins (le flip d'axe est neutre).
let s: CGFloat = W / 120.0
let lo = 32 * s, hi = 88 * s            // coins extérieurs (273.07 / 750.93)
let arm = (40 - 32) * s                 // longueur de bras (68.27)
let center = CGPoint(x: W/2, y: W/2)

ctx.saveGState()
ctx.setStrokeColor(col(1, 1, 1))
ctx.setLineWidth(7 * s)                 // 59.73
ctx.setLineCap(.round)
ctx.setLineJoin(.round)
for cx in [lo, hi] {
    for cy in [lo, hi] {
        let sx: CGFloat = center.x > cx ? 1 : -1
        let sy: CGFloat = center.y > cy ? 1 : -1
        let p = CGMutablePath()
        p.move(to: CGPoint(x: cx + sx*arm, y: cy))
        p.addLine(to: CGPoint(x: cx, y: cy))
        p.addLine(to: CGPoint(x: cx, y: cy + sy*arm))
        ctx.addPath(p); ctx.strokePath()
    }
}
ctx.restoreGState()

// Point rouge d'enregistrement centré (#FF453A, r = 9×8.5333 = 76.8)
ctx.setFillColor(col(255.0/255, 69.0/255, 58.0/255))
let dotR: CGFloat = 76.8
ctx.fillEllipse(in: CGRect(x: center.x - dotR, y: center.y - dotR, width: dotR*2, height: dotR*2))

guard let img = ctx.makeImage() else { fatalError("img") }
let url = URL(fileURLWithPath: outPath)
guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else { fatalError("dest") }
CGImageDestinationAddImage(dest, img, nil)
if !CGImageDestinationFinalize(dest) { fatalError("finalize failed") }
print("icon written: \(outPath)")
