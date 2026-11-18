// Draws the placeholder app icon (an SF Symbol on a plain square; the system applies the icon mask) as a
// 1024 px PNG. Replace with final artwork before a release.
// Usage: swift Scripts/make-icon.swift <out.png>
import AppKit

let size = 1024.0
let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
    NSColor.systemIndigo.setFill()
    rect.fill()
    let configuration = NSImage.SymbolConfiguration(pointSize: size * 0.5, weight: .semibold)
        .applying(NSImage.SymbolConfiguration(paletteColors: [.white]))
    guard let symbol = NSImage(systemSymbolName: "hammer.fill", accessibilityDescription: nil)?
        .withSymbolConfiguration(configuration) else { return false }
    let symbolSize = symbol.size
    symbol.draw(in: NSRect(
        x: (size - symbolSize.width) / 2,
        y: (size - symbolSize.height) / 2,
        width: symbolSize.width,
        height: symbolSize.height
    ))
    return true
}

guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:])
else {
    FileHandle.standardError.write(Data("cannot render the icon\n".utf8))
    exit(1)
}

try png.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
