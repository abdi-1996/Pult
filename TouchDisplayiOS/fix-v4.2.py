from pathlib import Path

network_path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = network_path.read_text(encoding='utf-8')

# Coalesce pointer motion: if iPhone produces input faster than the network can
# send it, combine the deltas rather than queue stale cursor positions.
marker = '@MainActor\nfinal class TouchDisplayModel: ObservableObject {'
mouse_sender = r'''final class LatestMouseSender {
    private let lock = NSLock()
    private let queue = DispatchQueue(label: "TouchDisplay.MouseMove", qos: .userInteractive)
    private var pendingX: Float = 0
    private var pendingY: Float = 0
    private var scheduled = false

    func submit(socket: BlockingSocket, dx: Float, dy: Float) {
        guard dx.isFinite, dy.isFinite else { return }
        lock.lock()
        pendingX += dx
        pendingY += dy
        if scheduled {
            lock.unlock()
            return
        }
        scheduled = true
        lock.unlock()

        queue.async { [weak self] in
            self?.drain(socket: socket)
        }
    }

    private func drain(socket: BlockingSocket) {
        while true {
            lock.lock()
            let dx = pendingX
            let dy = pendingY
            pendingX = 0
            pendingY = 0
            if abs(dx) < 0.00001 && abs(dy) < 0.00001 {
                scheduled = false
                lock.unlock()
                return
            }
            lock.unlock()

            var packet = Data([0x30])
            packet.append(floatBE(dx))
            packet.append(floatBE(dy))
            do { try socket.write(packet) }
            catch {
                lock.lock()
                pendingX = 0
                pendingY = 0
                scheduled = false
                lock.unlock()
                return
            }
        }
    }
}

'''
if marker not in text:
    raise SystemExit('v4.2 model marker not found')
text = text.replace(marker, mouse_sender + marker, 1)

prop_marker = '    private let defaults = UserDefaults.standard\n'
if prop_marker not in text:
    raise SystemExit('v4.2 mouse sender property target not found')
text = text.replace(prop_marker, prop_marker + '    private let mouseSender = LatestMouseSender()\n', 1)

method_marker = '    private func startAudio(host: String, password: String) {\n'
mouse_methods = r'''    func sendMouseMove(dx: Float, dy: Float) {
        guard let socket = videoSocket, connected else { return }
        mouseSender.submit(socket: socket, dx: dx, dy: dy)
    }

    func sendMouseButton(button: UInt8, action: UInt8 = 2) {
        guard let socket = videoSocket, connected else { return }
        let packet = Data([0x31, button, action])
        Task.detached(priority: .userInitiated) {
            try? socket.write(packet)
        }
    }

    func sendMouseWheel(delta: Int) {
        guard let socket = videoSocket, connected else { return }
        var packet = Data([0x32])
        packet.append(int32BE(max(-960, min(960, delta))))
        Task.detached(priority: .userInitiated) {
            try? socket.write(packet)
        }
    }

'''
if method_marker not in text:
    raise SystemExit('v4.2 mouse protocol method target not found')
text = text.replace(method_marker, mouse_methods + method_marker, 1)

text = text.replace('Подключено • Low Latency v4.1 + FEC •', 'Подключено • v4.2 Mouse + FEC •')
network_path.write_text(text, encoding='utf-8')

# Replace the display surface with two input modes:
# Sensor = absolute Windows touch. Mouse = trackpad style relative pointer +
# two-finger wheel. Three-finger long press disconnects with no permanent X button.
view_path = Path('TouchDisplayiOS/Sources/RemoteDisplayView.swift')
view_path.write_text(r'''import SwiftUI
import UIKit

struct RemoteDisplayRepresentable: UIViewRepresentable {
    @ObservedObject var model: TouchDisplayModel
    let mouseMode: Bool

    func makeUIView(context: Context) -> RemoteDisplayUIView {
        let view = RemoteDisplayUIView()
        view.model = model
        view.mouseMode = mouseMode
        return view
    }

    func updateUIView(_ uiView: RemoteDisplayUIView, context: Context) {
        uiView.model = model
        uiView.mouseMode = mouseMode
        uiView.image = model.frame
    }
}

final class RemoteDisplayUIView: UIView, UIGestureRecognizerDelegate {
    weak var model: TouchDisplayModel?

    var mouseMode = false {
        didSet {
            guard oldValue != mouseMode else { return }
            cancelAllSensorTouches()
            mouseTouches.removeAll()
            mouseMoved = false
        }
    }

    var image: UIImage? {
        didSet {
            backgroundImageView.image = image
            imageView.image = image
            setNeedsLayout()
        }
    }

    private let backgroundImageView = UIImageView()
    private let blurView = UIVisualEffectView(effect: UIBlurEffect(style: .systemUltraThinMaterialDark))
    private let imageView = UIImageView()

    private var touchIds: [ObjectIdentifier: Int] = [:]
    private var freeIds = Array(0..<32)
    private var suppressTouches = false

    private var mouseTouches: [ObjectIdentifier: CGPoint] = [:]
    private var mouseStartPoint: CGPoint = .zero
    private var mouseStartTime: TimeInterval = 0
    private var mouseMoved = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
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

        let longPress = UILongPressGestureRecognizer(target: self, action: #selector(handleSensorLongPress(_:)))
        longPress.minimumPressDuration = 0.55
        longPress.allowableMovement = 18
        longPress.numberOfTouchesRequired = 1
        longPress.cancelsTouchesInView = false
        longPress.delegate = self
        addGestureRecognizer(longPress)

        let exitGesture = UILongPressGestureRecognizer(target: self, action: #selector(handleThreeFingerExit(_:)))
        exitGesture.minimumPressDuration = 1.0
        exitGesture.allowableMovement = 28
        exitGesture.numberOfTouchesRequired = 3
        exitGesture.cancelsTouchesInView = true
        exitGesture.delegate = self
        addGestureRecognizer(exitGesture)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        backgroundImageView.frame = bounds
        blurView.frame = bounds
        imageView.frame = bounds
    }

    @objc private func handleSensorLongPress(_ recognizer: UILongPressGestureRecognizer) {
        guard !mouseMode else { return }
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
            if touchIds.isEmpty { suppressTouches = false }
        }
    }

    @objc private func handleThreeFingerExit(_ recognizer: UILongPressGestureRecognizer) {
        if recognizer.state == .began {
            UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
            model?.disconnect()
        }
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if mouseMode {
            for touch in touches {
                let p = touch.location(in: self)
                mouseTouches[ObjectIdentifier(touch)] = p
            }
            if mouseTouches.count == 1, let touch = touches.first {
                mouseStartPoint = touch.location(in: self)
                mouseStartTime = touch.timestamp
                mouseMoved = false
            } else if mouseTouches.count > 1 {
                mouseMoved = true
            }
            return
        }

        if suppressTouches { return }
        for touch in touches {
            guard let id = allocateId(for: touch) else { continue }
            let p = normalizedPoint(touch.location(in: self))
            model?.sendTouch(action: 0, pointerId: id, x: p.x, y: p.y)
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        if mouseMode {
            let isScroll = mouseTouches.count >= 2
            for touch in touches {
                let key = ObjectIdentifier(touch)
                let now = touch.location(in: self)
                let previous = mouseTouches[key] ?? now
                let dx = now.x - previous.x
                let dy = now.y - previous.y
                mouseTouches[key] = now

                if isScroll {
                    let wheel = Int((-dy * 7.0).rounded())
                    if wheel != 0 { model?.sendMouseWheel(delta: wheel) }
                } else {
                    if hypot(now.x - mouseStartPoint.x, now.y - mouseStartPoint.y) > 4 {
                        mouseMoved = true
                    }
                    if bounds.width > 0, bounds.height > 0 {
                        model?.sendMouseMove(
                            dx: Float(dx / bounds.width),
                            dy: Float(dy / bounds.height)
                        )
                    }
                }
            }
            return
        }

        if suppressTouches { return }
        for touch in touches {
            guard let id = touchIds[ObjectIdentifier(touch)] else { continue }
            let p = normalizedPoint(touch.location(in: self))
            model?.sendTouch(action: 1, pointerId: id, x: p.x, y: p.y)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if mouseMode {
            let wasSingle = mouseTouches.count == 1
            var shouldClick = false
            if wasSingle, let touch = touches.first {
                let elapsed = touch.timestamp - mouseStartTime
                let p = touch.location(in: self)
                let distance = hypot(p.x - mouseStartPoint.x, p.y - mouseStartPoint.y)
                shouldClick = !mouseMoved && elapsed < 0.32 && distance < 7
            }
            for touch in touches { mouseTouches.removeValue(forKey: ObjectIdentifier(touch)) }
            if shouldClick { model?.sendMouseButton(button: 0, action: 2) }
            if mouseTouches.isEmpty { mouseMoved = false }
            return
        }

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
        if mouseMode {
            for touch in touches { mouseTouches.removeValue(forKey: ObjectIdentifier(touch)) }
            if mouseTouches.isEmpty { mouseMoved = false }
            return
        }

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

    private func cancelAllSensorTouches() {
        guard !touchIds.isEmpty else { return }
        for (_, id) in touchIds {
            model?.sendTouch(action: 3, pointerId: id, x: 0, y: 0)
        }
        touchIds.removeAll()
        freeIds = Array(0..<32)
        suppressTouches = false
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
''', encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v4.1', 'TouchDisplay v4.2')
app = app.replace('FEC • Smart Bitrate • AI Priority • 10ms Audio • Tailscale',
                  'Clean UI • Mouse / Touch • FEC • AI Priority • Tailscale')

start = app.find('struct RemoteScreen: View {')
if start < 0:
    raise SystemExit('v4.2 RemoteScreen target not found')

remote = r'''struct RemoteScreen: View {
    @ObservedObject var model: TouchDisplayModel
    @AppStorage("td.mouseMode") private var mouseMode = false

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .bottom) {
                Color.black.ignoresSafeArea()

                RemoteDisplayRepresentable(model: model, mouseMode: mouseMode)
                    .ignoresSafeArea()

                HStack(spacing: 10) {
                    Button {
                        mouseMode.toggle()
                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    } label: {
                        HStack(spacing: 6) {
                            Image(systemName: mouseMode ? "computermouse.fill" : "hand.tap.fill")
                            Text(mouseMode ? "Мышь" : "Сенсор")
                        }
                        .font(.system(size: 13, weight: .semibold))
                        .frame(minWidth: 92, minHeight: 42)
                    }

                    Button {
                        model.sendMouseButton(button: 0, action: 2)
                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    } label: {
                        Text("ЛКМ")
                            .font(.system(size: 13, weight: .bold))
                            .frame(minWidth: 58, minHeight: 42)
                    }

                    Button {
                        model.sendMouseButton(button: 1, action: 2)
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    } label: {
                        Text("ПКМ")
                            .font(.system(size: 13, weight: .bold))
                            .frame(minWidth: 58, minHeight: 42)
                    }
                }
                .buttonStyle(CleanRemoteButtonStyle(active: mouseMode))
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(.black.opacity(0.18), in: Capsule())
                .padding(.bottom, max(3, proxy.safeAreaInsets.bottom > 0 ? 2 : 8))
            }
            .onAppear { updateProfile(for: proxy.size) }
            .onChange(of: proxy.size) { newSize in
                updateProfile(for: newSize)
            }
        }
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
    }

    private func updateProfile(for size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        let scale = UIScreen.main.nativeScale
        model.updateDisplayProfile(
            pixelWidth: Int((size.width * scale).rounded()),
            pixelHeight: Int((size.height * scale).rounded())
        )
    }
}

private struct CleanRemoteButtonStyle: ButtonStyle {
    let active: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .background(
                Color.black.opacity(configuration.isPressed ? 0.72 : (active ? 0.52 : 0.40)),
                in: RoundedRectangle(cornerRadius: 13, style: .continuous)
            )
            .scaleEffect(configuration.isPressed ? 0.96 : 1.0)
    }
}
'''
app = app[:start] + remote
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v4.2 Clean UI + Mouse Mode patch applied')
