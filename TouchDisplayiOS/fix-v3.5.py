from pathlib import Path

# v3.5: Windows Host rotates the physical primary display to portrait when an
# iPhone/iPad connects vertically. Keep the whole portrait desktop undistorted
# on iOS. Any leftover aspect-ratio area is filled with a blurred copy of the
# same desktop instead of black bars.

view_path = Path('TouchDisplayiOS/Sources/RemoteDisplayView.swift')
view = view_path.read_text(encoding='utf-8')

old_image_set = '''        didSet {
            imageView.image = image
            setNeedsLayout()
        }
'''
new_image_set = '''        didSet {
            backgroundImageView.image = image
            imageView.image = image
            setNeedsLayout()
        }
'''
if old_image_set not in view:
    raise SystemExit('v3.5 image update target not found')
view = view.replace(old_image_set, new_image_set, 1)

old_props = '''    private let imageView = UIImageView()
    private var touchIds: [ObjectIdentifier: Int] = [:]
'''
new_props = '''    private let backgroundImageView = UIImageView()
    private let blurView = UIVisualEffectView(effect: UIBlurEffect(style: .systemUltraThinMaterialDark))
    private let imageView = UIImageView()
    private var touchIds: [ObjectIdentifier: Int] = [:]
'''
if old_props not in view:
    raise SystemExit('v3.5 image view properties target not found')
view = view.replace(old_props, new_props, 1)

old_setup = '''        backgroundColor = .black
        isMultipleTouchEnabled = true
        imageView.contentMode = .scaleToFill
        imageView.clipsToBounds = true
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
        addSubview(imageView)
'''
new_setup = '''        backgroundColor = .black
        isMultipleTouchEnabled = true

        backgroundImageView.contentMode = .scaleAspectFill
        backgroundImageView.clipsToBounds = true
        backgroundImageView.isUserInteractionEnabled = false
        addSubview(backgroundImageView)

        blurView.isUserInteractionEnabled = false
        addSubview(blurView)

        imageView.contentMode = .scaleAspectFit
        imageView.clipsToBounds = true
        imageView.backgroundColor = .clear
        imageView.isUserInteractionEnabled = false
        addSubview(imageView)
'''
if old_setup not in view:
    raise SystemExit('v3.5 undistorted display setup target not found')
view = view.replace(old_setup, new_setup, 1)

old_layout = '''    override func layoutSubviews() {
        super.layoutSubviews()
        imageView.frame = bounds
    }
'''
new_layout = '''    override func layoutSubviews() {
        super.layoutSubviews()
        backgroundImageView.frame = bounds
        blurView.frame = bounds
        imageView.frame = bounds
    }
'''
if old_layout not in view:
    raise SystemExit('v3.5 layout target not found')
view = view.replace(old_layout, new_layout, 1)

old_normalized = '''    private func normalizedPoint(_ point: CGPoint) -> (x: Float, y: Float) {
        guard bounds.width > 0, bounds.height > 0 else { return (0, 0) }
        let x = point.x / bounds.width
        let y = point.y / bounds.height
        return (Float(max(0, min(1, x))), Float(max(0, min(1, y))))
    }
'''
new_normalized = '''    private func normalizedPoint(_ point: CGPoint) -> (x: Float, y: Float) {
        guard let image, image.size.width > 0, image.size.height > 0, bounds.width > 0, bounds.height > 0 else {
            return (Float(max(0, min(1, point.x / max(1, bounds.width)))),
                    Float(max(0, min(1, point.y / max(1, bounds.height)))))
        }

        let scale = min(bounds.width / image.size.width, bounds.height / image.size.height)
        let displayWidth = image.size.width * scale
        let displayHeight = image.size.height * scale
        let left = (bounds.width - displayWidth) / 2
        let top = (bounds.height - displayHeight) / 2
        let x = (point.x - left) / max(1, displayWidth)
        let y = (point.y - top) / max(1, displayHeight)
        return (Float(max(0, min(1, x))), Float(max(0, min(1, y))))
    }
'''
if old_normalized not in view:
    raise SystemExit('v3.5 aspect-fit touch target not found')
view = view.replace(old_normalized, new_normalized, 1)
view_path.write_text(view, encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v3.4', 'TouchDisplay v3.5')
app = app.replace('Full Desktop • Touch • Audio • LAN / Tailscale', 'Auto Portrait PC • Touch • Audio • LAN / Tailscale')
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v3.5 undistorted portrait display patch applied')
