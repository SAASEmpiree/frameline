// make_icon.swift — génère une icône d'app ORIGINALE pour Easy Screen.
// Dessine un squircle dégradé bleu→violet avec un cadre de sélection (coins) + une pastille obturateur.
// Sortie : PNG 1024×1024 vers le chemin passé en argument.
import CoreGraphics
import ImageIO
import Foundation
import UniformTypeIdentifiers

let outPath = CommandLine.arguments.count > 1
    ? CommandLine.arguments[1]
    : "AppIcon-1024.png"

let S = 1024
let cs = CGColorSpace(name: CGColorSpace.sRGB)!
guard let ctx = CGContext(
    data: nil, width: S, height: S, bitsPerComponent: 8, bytesPerRow: 0,
    space: cs, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else { fatalError("ctx") }

func col(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
    CGColor(colorSpace: cs, components: [CGFloat(r), CGFloat(g), CGFloat(b), CGFloat(a)])!
}

// Fond transparent
ctx.clear(CGRect(x: 0, y: 0, width: S, height: S))

// Squircle (rounded rect façon macOS) avec marge
let inset: CGFloat = 60
let rect = CGRect(x: inset, y: inset, width: CGFloat(S) - 2*inset, height: CGFloat(S) - 2*inset)
let radius: CGFloat = rect.width * 0.235
let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)

// Dégradé diagonal bleu -> violet
ctx.saveGState()
ctx.addPath(path); ctx.clip()
let grad = CGGradient(colorsSpace: cs, colors: [
    col(0.18, 0.49, 0.96),  // bleu #2E7DF5
    col(0.48, 0.25, 0.95)   // violet #7A40F2
] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(grad, start: CGPoint(x: rect.minX, y: rect.maxY),
                       end: CGPoint(x: rect.maxX, y: rect.minY), options: [])
// Voile lumineux en haut
let glow = CGGradient(colorsSpace: cs, colors: [
    col(1, 1, 1, 0.16), col(1, 1, 1, 0.0)
] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(glow, startCenter: CGPoint(x: rect.midX, y: rect.maxY - 120), startRadius: 0,
                       endCenter: CGPoint(x: rect.midX, y: rect.maxY - 120), endRadius: rect.width*0.7, options: [])
ctx.restoreGState()

// Cadre de sélection : 4 coins en L, blancs, coins arrondis
ctx.saveGState()
ctx.setStrokeColor(col(1, 1, 1, 0.97))
let lw: CGFloat = 52
ctx.setLineWidth(lw)
ctx.setLineCap(.round)
ctx.setLineJoin(.round)
// ombre douce sous les traits
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 30, color: col(0,0,0,0.22))

let m: CGFloat = 300          // marge des coins depuis le bord du squircle
let arm: CGFloat = 150        // longueur des bras
let L = rect.minX + m, R = rect.maxX - m
let B = rect.minY + m, T = rect.maxY - m

func corner(_ cx: CGFloat, _ cy: CGFloat, _ dx: CGFloat, _ dy: CGFloat) {
    let p = CGMutablePath()
    p.move(to: CGPoint(x: cx + dx*arm, y: cy))
    p.addLine(to: CGPoint(x: cx, y: cy))
    p.addLine(to: CGPoint(x: cx, y: cy + dy*arm))
    ctx.addPath(p); ctx.strokePath()
}
corner(L, T, 1, -1)   // haut-gauche
corner(R, T, -1, -1)  // haut-droit
corner(L, B, 1, 1)    // bas-gauche
corner(R, B, -1, 1)   // bas-droit
ctx.restoreGState()

// Pastille obturateur au centre
ctx.saveGState()
let dotR: CGFloat = 95
let center = CGPoint(x: rect.midX, y: rect.midY)
ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 24, color: col(0,0,0,0.25))
ctx.setFillColor(col(1, 1, 1, 0.98))
ctx.fillEllipse(in: CGRect(x: center.x - dotR, y: center.y - dotR, width: dotR*2, height: dotR*2))
// anneau intérieur teinté
ctx.setShadow(offset: .zero, blur: 0, color: col(0,0,0,0))
ctx.setStrokeColor(col(0.30, 0.36, 0.95, 0.85))
ctx.setLineWidth(20)
ctx.strokeEllipse(in: CGRect(x: center.x - dotR + 34, y: center.y - dotR + 34, width: (dotR-34)*2, height: (dotR-34)*2))
ctx.restoreGState()

guard let img = ctx.makeImage() else { fatalError("img") }
let url = URL(fileURLWithPath: outPath)
guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
    fatalError("dest")
}
CGImageDestinationAddImage(dest, img, nil)
if CGImageDestinationFinalize(dest) {
    print("icon written: \(outPath)")
} else {
    fatalError("finalize failed")
}
