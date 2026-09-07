import SwiftUI
import UIKit

struct RemoteDisplayRepresentable: UIViewRepresentable {
    @ObservedObject var model: TouchDisplayModel

    func makeUIView(context: Context) -> RemoteDisplayUIView {
        let view = RemoteDisplayUIView()
        view.model = model
        return view
    }

    func updateUIView(_ uiView: RemoteDisplayUIView, context: Context) {
        uiView.model = model
        uiView.image = model.frame
    }
}

final class RemoteDisplayUIView: UIView, UIGestureRecognizerDelegate {
    weak var model: TouchDisplayModel?
    var image: UIImage? {
        didSet {
            imageView.image = image
            setNeedsLayout()
        }
    }

    private let imageView = UIImageView()
    private var touchIds: [ObjectIdentifier: Int] = [:]
    private var freeIds = Array(0..<32)
    private var suppressTouches = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        isMultipleTouchEnabled = true
        imageView.contentMode = .scaleAspectFit
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
        addSubview(imageView)

        let longPress = UILongPressGestureRecognizer(target: self, action: #selector(handleLongPress(_:)))
        longPress.minimumPressDuration = 0.55
        longPress.allowableMovement = 18
        longPress.cancelsTouchesInView = false
        longPress.delegate = self
        addGestureRecognizer(longPress)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        imageView.frame = bounds
    }

    @objc private func handleLongPress(_ recognizer: UILongPressGestureRecognizer) {
        if recognizer.state == .began {
            let point = recognizer.location(in: self)
            let normalized = normalizedPoint(point)
            for (_, id) in touchIds {
                model?.sendTouch(action: 3, pointerId: id, x: normalized.x, y: normalized.y)
            }
            touchIds.removeAll()
            freeIds = Array(0..<32)
            suppressTouches = true
            model?.sendRightClick(x: normalized.x, y: normalized.y)
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
        }
        if recognizer.state == .ended || recognizer.state == .cancelled || recognizer.state == .failed {
            if allTouchesFinished { suppressTouches = false }
        }
    }

    private var allTouchesFinished: Bool { touchIds.isEmpty }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if suppressTouches { return }
        for touch in touches {
            guard let id = allocateId(for: touch) else { continue }
            let p = normalizedPoint(touch.location(in: self))
            model?.sendTouch(action: 0, pointerId: id, x: p.x, y: p.y)
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        if suppressTouches { return }
        for touch in touches {
            guard let id = touchIds[ObjectIdentifier(touch)] else { continue }
            let p = normalizedPoint(touch.location(in: self))
            model?.sendTouch(action: 1, pointerId: id, x: p.x, y: p.y)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if suppressTouches {
            for touch in touches { releaseId(for: touch) }
            if touchIds.isEmpty { suppressTouches = false }
            return
        }
        for touch in touches {
            let key = ObjectIdentifier(touch)
            guard let id = touchIds[key] else { continue }
            let p = normalizedPoint(touch.location(in: self))
            model?.sendTouch(action: 2, pointerId: id, x: p.x, y: p.y)
            releaseId(for: touch)
        }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            let key = ObjectIdentifier(touch)
            if let id = touchIds[key] {
                let p = normalizedPoint(touch.location(in: self))
                model?.sendTouch(action: 3, pointerId: id, x: p.x, y: p.y)
            }
            releaseId(for: touch)
        }
        if touchIds.isEmpty { suppressTouches = false }
    }

    private func allocateId(for touch: UITouch) -> Int? {
        let key = ObjectIdentifier(touch)
        if let id = touchIds[key] { return id }
        guard !freeIds.isEmpty else { return nil }
        let id = freeIds.removeFirst()
        touchIds[key] = id
        return id
    }

    private func releaseId(for touch: UITouch) {
        let key = ObjectIdentifier(touch)
        if let id = touchIds.removeValue(forKey: key) {
            freeIds.append(id)
            freeIds.sort()
        }
    }

    private func normalizedPoint(_ point: CGPoint) -> (x: Float, y: Float) {
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

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith otherGestureRecognizer: UIGestureRecognizer) -> Bool {
        true
    }
}
