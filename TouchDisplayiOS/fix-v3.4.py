from pathlib import Path

# v3.4: show the entire Windows desktop edge-to-edge on portrait iPhone/iPad.
# Unlike v3.3 aspect-fill, this deliberately stretches the source to the client
# viewport so nothing is cropped and there are no letterbox bars.

view_path = Path('TouchDisplayiOS/Sources/RemoteDisplayView.swift')
view = view_path.read_text(encoding='utf-8')

old_mode = '''        imageView.contentMode = .scaleAspectFill
        imageView.clipsToBounds = true
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
'''
new_mode = '''        imageView.contentMode = .scaleToFill
        imageView.clipsToBounds = true
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
'''
if old_mode not in view:
    raise SystemExit('v3.4 scaleToFill target not found')
view = view.replace(old_mode, new_mode, 1)

old_normalized = '''    private func normalizedPoint(_ point: CGPoint) -> (x: Float, y: Float) {
        guard let image, image.size.width > 0, image.size.height > 0, bounds.width > 0, bounds.height > 0 else {
            return (Float(max(0, min(1, point.x / max(1, bounds.width)))),
                    Float(max(0, min(1, point.y / max(1, bounds.height)))))
        }

        let scale = max(bounds.width / image.size.width, bounds.height / image.size.height)
        let displayWidth = image.size.width * scale
        let displayHeight = image.size.height * scale
        let left = (bounds.width - displayWidth) / 2
        let top = (bounds.height - displayHeight) / 2
        let x = (point.x - left) / max(1, displayWidth)
        let y = (point.y - top) / max(1, displayHeight)
        return (Float(max(0, min(1, x))), Float(max(0, min(1, y))))
    }
'''
new_normalized = '''    private func normalizedPoint(_ point: CGPoint) -> (x: Float, y: Float) {
        guard bounds.width > 0, bounds.height > 0 else { return (0, 0) }
        let x = point.x / bounds.width
        let y = point.y / bounds.height
        return (Float(max(0, min(1, x))), Float(max(0, min(1, y))))
    }
'''
if old_normalized not in view:
    raise SystemExit('v3.4 touch mapping target not found')
view = view.replace(old_normalized, new_normalized, 1)
view_path.write_text(view, encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v3.3', 'TouchDisplay v3.4')
app = app.replace('Fullscreen Fill • Touch • Audio • LAN / Tailscale', 'Full Desktop • Touch • Audio • LAN / Tailscale')
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v3.4 full-desktop stretch patch applied')
