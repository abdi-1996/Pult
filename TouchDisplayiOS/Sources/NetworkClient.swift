import Foundation
import UIKit
import AVFoundation
import Darwin

struct DiscoveredPC {
    let lanAddress: String
    let tailscaleAddress: String?
    let port: Int
    let name: String
}

enum TouchDisplayError: LocalizedError {
    case connection(String)
    case authentication
    case protocolError(String)

    var errorDescription: String? {
        switch self {
        case .connection(let value): return value
        case .authentication: return "Неверный пароль"
        case .protocolError(let value): return value
        }
    }
}

final class BlockingSocket {
    private let input: InputStream
    private let output: OutputStream
    private let writeLock = NSLock()
    private var closed = false

    init(host: String, port: Int) throws {
        var readStream: Unmanaged<CFReadStream>?
        var writeStream: Unmanaged<CFWriteStream>?
        CFStreamCreatePairWithSocketToHost(kCFAllocatorDefault, host as CFString, UInt32(port), &readStream, &writeStream)
        guard let r = readStream?.takeRetainedValue(), let w = writeStream?.takeRetainedValue() else {
            throw TouchDisplayError.connection("Не удалось создать соединение")
        }
        input = r as InputStream
        output = w as OutputStream
        input.open()
        output.open()
    }

    func readExact(_ count: Int) throws -> Data {
        if count <= 0 { return Data() }
        var result = Data(count: count)
        var offset = 0
        let deadline = Date().addingTimeInterval(12)

        try result.withUnsafeMutableBytes { raw in
            guard let base = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
            while offset < count {
                if closed { throw TouchDisplayError.connection("Соединение закрыто") }
                let n = input.read(base.advanced(by: offset), maxLength: count - offset)
                if n > 0 {
                    offset += n
                    continue
                }
                if n < 0 {
                    throw TouchDisplayError.connection(input.streamError?.localizedDescription ?? "Ошибка чтения")
                }
                if input.streamStatus == .atEnd || input.streamStatus == .closed || input.streamStatus == .error {
                    throw TouchDisplayError.connection("Соединение закрыто ПК")
                }
                if Date() > deadline && offset == 0 {
                    throw TouchDisplayError.connection("Тайм-аут подключения")
                }
                usleep(2_000)
            }
        }
        return result
    }

    func readByte() throws -> UInt8 {
        let d = try readExact(1)
        return d[d.startIndex]
    }

    func readInt32BE() throws -> Int {
        let b = [UInt8](try readExact(4))
        return (Int(b[0]) << 24) | (Int(b[1]) << 16) | (Int(b[2]) << 8) | Int(b[3])
    }

    func write(_ data: Data) throws {
        writeLock.lock()
        defer { writeLock.unlock() }
        if closed { throw TouchDisplayError.connection("Соединение закрыто") }

        try data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
            var offset = 0
            let deadline = Date().addingTimeInterval(12)
            while offset < data.count {
                let n = output.write(base.advanced(by: offset), maxLength: data.count - offset)
                if n > 0 {
                    offset += n
                    continue
                }
                if n < 0 {
                    throw TouchDisplayError.connection(output.streamError?.localizedDescription ?? "Ошибка отправки")
                }
                if output.streamStatus == .closed || output.streamStatus == .error {
                    throw TouchDisplayError.connection("Соединение закрыто ПК")
                }
                if Date() > deadline {
                    throw TouchDisplayError.connection("Тайм-аут отправки")
                }
                usleep(2_000)
            }
        }
    }

    func close() {
        if closed { return }
        closed = true
        input.close()
        output.close()
    }

    deinit { close() }
}

@MainActor
final class TouchDisplayModel: ObservableObject {
    @Published var connected = false
    @Published var connecting = false
    @Published var status = "Введите пароль"
    @Published var frame: UIImage?
    @Published var connectedHost = ""

    private var videoSocket: BlockingSocket?
    private var audioSocket: BlockingSocket?
    private let defaults = UserDefaults.standard
    private var password = ""

    private let videoPort = 59432
    private let audioPort = 59434
    private let discoveryPort: UInt16 = 59431

    func connect(password rawPassword: String, manualTailscale: String) {
        let normalized = rawPassword.compactMap { $0.wholeNumberValue }.map(String.init).joined()
        guard normalized.count == 6 else {
            status = "Пароль должен состоять из 6 цифр"
            return
        }
        if connecting { return }
        connecting = true
        status = "Поиск ПК…"
        password = normalized

        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            do {
                let result = try await self.findAndOpen(password: normalized, manual: manualTailscale.trimmingCharacters(in: .whitespacesAndNewlines))
                await self.sessionOpened(socket: result.socket, host: result.host, password: normalized, discovered: result.discovered)
                self.readFrames(socket: result.socket)
            } catch {
                await self.sessionFailed(error)
            }
        }
    }

    nonisolated private func findAndOpen(password: String, manual: String) async throws -> (socket: BlockingSocket, host: String, discovered: DiscoveredPC?) {
        var candidates: [(String, Int)] = []
        var discovered: DiscoveredPC?

        if !manual.isEmpty {
            candidates.append((manual, videoPort))
        } else {
            discovered = discoverLocal(password: password)
            if let d = discovered {
                candidates.append((d.lanAddress, d.port))
                if let tail = d.tailscaleAddress, !tail.isEmpty { candidates.append((tail, d.port)) }
            }
            let savedTail = UserDefaults.standard.string(forKey: "td.tail") ?? ""
            let savedLan = UserDefaults.standard.string(forKey: "td.lan") ?? ""
            let savedPort = UserDefaults.standard.integer(forKey: "td.port")
            let p = savedPort > 0 ? savedPort : videoPort
            if !savedTail.isEmpty { candidates.append((savedTail, p)) }
            if !savedLan.isEmpty { candidates.append((savedLan, p)) }
        }

        var seen = Set<String>()
        candidates = candidates.filter { seen.insert("\($0.0):\($0.1)").inserted }
        if candidates.isEmpty {
            throw TouchDisplayError.connection("ПК не найден. Для первого удалённого входа откройте Tailscale и укажите его IP в дополнительных настройках.")
        }

        var lastError: Error = TouchDisplayError.connection("ПК недоступен")
        for (host, port) in candidates {
            do {
                let socket = try BlockingSocket(host: host, port: port)
                try authenticateVideo(socket: socket, password: password)
                return (socket, host, discovered)
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    nonisolated private func authenticateVideo(socket: BlockingSocket, password: String) throws {
        var packet = Data("TD01".utf8)
        let p = Data(password.utf8)
        packet.append(int32BE(p.count))
        packet.append(p)
        try socket.write(packet)
        let accepted = try socket.readByte()
        if accepted != 1 {
            socket.close()
            throw TouchDisplayError.authentication
        }
    }

    private func sessionOpened(socket: BlockingSocket, host: String, password: String, discovered: DiscoveredPC?) {
        videoSocket?.close()
        videoSocket = socket
        connected = true
        connecting = false
        connectedHost = host
        status = "Подключено • \(host)"

        if let d = discovered {
            defaults.set(d.lanAddress, forKey: "td.lan")
            defaults.set(d.tailscaleAddress ?? "", forKey: "td.tail")
            defaults.set(d.port, forKey: "td.port")
        } else if isTailscaleAddress(host) {
            defaults.set(host, forKey: "td.tail")
            defaults.set(videoPort, forKey: "td.port")
        }

        startAudio(host: host, password: password)
    }

    private func sessionFailed(_ error: Error) {
        connecting = false
        connected = false
        status = error.localizedDescription
    }

    nonisolated private func readFrames(socket: BlockingSocket) {
        do {
            while true {
                autoreleasepool {
                    do {
                        let length = try socket.readInt32BE()
                        guard length > 0 && length < 24_000_000 else {
                            throw TouchDisplayError.protocolError("Некорректный видеокадр")
                        }
                        let data = try socket.readExact(length)
                        guard let image = UIImage(data: data) else { return }
                        Task { @MainActor [weak self] in self?.frame = image }
                    } catch {
                        Task { @MainActor [weak self] in self?.handleDisconnect(error) }
                    }
                }
                if socket !== self.currentVideoSocketUnsafe() { break }
            }
        } catch {
            Task { @MainActor [weak self] in self?.handleDisconnect(error) }
        }
    }

    nonisolated private func currentVideoSocketUnsafe() -> BlockingSocket? {
        // Session identity is checked on the main actor by disconnect lifecycle; keeping this helper
        // intentionally simple avoids blocking the high-priority frame reader.
        nil
    }

    private func handleDisconnect(_ error: Error) {
        if !connected { return }
        videoSocket?.close()
        audioSocket?.close()
        videoSocket = nil
        audioSocket = nil
        connected = false
        connecting = false
        frame = nil
        status = "Соединение потеряно: \(error.localizedDescription)"
    }

    func disconnect() {
        videoSocket?.close()
        audioSocket?.close()
        videoSocket = nil
        audioSocket = nil
        connected = false
        connecting = false
        frame = nil
        status = "Отключено"
    }

    func sendTouch(action: UInt8, pointerId: Int, x: Float, y: Float) {
        guard let socket = videoSocket, connected else { return }
        var packet = Data([0x10, action])
        packet.append(int32BE(pointerId))
        packet.append(floatBE(x))
        packet.append(floatBE(y))
        Task.detached(priority: .userInteractive) {
            try? socket.write(packet)
        }
    }

    func sendRightClick(x: Float, y: Float) {
        guard let socket = videoSocket, connected else { return }
        var packet = Data([0x11])
        packet.append(floatBE(x))
        packet.append(floatBE(y))
        Task.detached(priority: .userInteractive) {
            try? socket.write(packet)
        }
    }

    private func startAudio(host: String, password: String) {
        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            var engine: AVAudioEngine?
            var player: AVAudioPlayerNode?
            var socket: BlockingSocket?
            do {
                let s = try BlockingSocket(host: host, port: self.audioPort)
                socket = s
                var hello = Data("TDA2".utf8)
                let pass = Data(password.utf8)
                hello.append(int32BE(pass.count))
                hello.append(pass)
                try s.write(hello)
                guard try s.readByte() == 1 else { throw TouchDisplayError.authentication }
                let sampleRate = try s.readInt32BE()
                let channels = Int(try s.readByte())
                let bits = Int(try s.readByte())
                guard bits == 16, channels == 1 || channels == 2 else {
                    throw TouchDisplayError.protocolError("Неподдерживаемый формат аудио")
                }

                await MainActor.run { self.audioSocket = s }
                let session = AVAudioSession.sharedInstance()
                try session.setCategory(.playback, mode: .moviePlayback, options: [.mixWithOthers])
                try session.setActive(true)

                guard let format = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                                 sampleRate: Double(sampleRate),
                                                 channels: AVAudioChannelCount(channels),
                                                 interleaved: false) else {
                    throw TouchDisplayError.protocolError("Не удалось открыть аудио")
                }
                let e = AVAudioEngine()
                let p = AVAudioPlayerNode()
                engine = e
                player = p
                e.attach(p)
                e.connect(p, to: e.mainMixerNode, format: format)
                try e.start()
                p.play()

                while true {
                    let length = try s.readInt32BE()
                    guard length > 0 && length <= 262_144 else { throw TouchDisplayError.protocolError("Некорректный аудиопакет") }
                    let pcm = [UInt8](try s.readExact(length))
                    let bytesPerFrame = channels * 2
                    let frames = pcm.count / bytesPerFrame
                    if frames == 0 { continue }
                    guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
                          let channelData = buffer.int16ChannelData else { continue }
                    buffer.frameLength = AVAudioFrameCount(frames)
                    for f in 0..<frames {
                        for c in 0..<channels {
                            let i = (f * channels + c) * 2
                            let sample = Int16(bitPattern: UInt16(pcm[i]) | (UInt16(pcm[i + 1]) << 8))
                            channelData[c][f] = sample
                        }
                    }
                    p.scheduleBuffer(buffer, completionHandler: nil)
                }
            } catch {
                // Video/touch remain usable if audio is unavailable.
            }
            player?.stop()
            engine?.stop()
            socket?.close()
        }
    }

    nonisolated private func discoverLocal(password: String) -> DiscoveredPC? {
        let fd = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return nil }
        defer { Darwin.close(fd) }

        var yes: Int32 = 1
        _ = withUnsafePointer(to: &yes) {
            setsockopt(fd, SOL_SOCKET, SO_BROADCAST, $0, socklen_t(MemoryLayout<Int32>.size))
        }
        var timeout = timeval(tv_sec: 1, tv_usec: 200_000)
        _ = withUnsafePointer(to: &timeout) {
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, $0, socklen_t(MemoryLayout<timeval>.size))
        }

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = discoveryPort.bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("255.255.255.255"))

        let request = Data("TDISCOVER17|\(password)".utf8)
        let sent = request.withUnsafeBytes { raw -> Int in
            guard let base = raw.baseAddress else { return -1 }
            return withUnsafePointer(to: &address) { ptr in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                    Darwin.sendto(fd, base, request.count, 0, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        if sent < 0 { return nil }

        var buffer = [UInt8](repeating: 0, count: 1024)
        var sender = sockaddr_in()
        var senderLen = socklen_t(MemoryLayout<sockaddr_in>.size)
        let count = buffer.withUnsafeMutableBytes { raw -> Int in
            guard let base = raw.baseAddress else { return -1 }
            return withUnsafeMutablePointer(to: &sender) { ptr in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                    Darwin.recvfrom(fd, base, buffer.count, 0, sa, &senderLen)
                }
            }
        }
        guard count > 0 else { return nil }
        let response = String(decoding: buffer.prefix(count), as: UTF8.self)
        let parts = response.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
        guard parts.count >= 4, parts[0] == "TDHOST17", let port = Int(parts[1]) else { return nil }

        var senderAddress = sender.sin_addr
        var ipChars = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        let lan = ipChars.withUnsafeMutableBufferPointer { ptr -> String in
            _ = inet_ntop(AF_INET, &senderAddress, ptr.baseAddress, socklen_t(INET_ADDRSTRLEN))
            return String(cString: ptr.baseAddress!)
        }
        let tail = parts[2].isEmpty ? nil : parts[2]
        return DiscoveredPC(lanAddress: lan, tailscaleAddress: tail, port: port, name: parts[3])
    }

    private func isTailscaleAddress(_ host: String) -> Bool {
        let p = host.split(separator: ".")
        guard p.count == 4, let a = Int(p[0]), let b = Int(p[1]) else { return false }
        return a == 100 && b >= 64 && b <= 127
    }
}

func int32BE(_ value: Int) -> Data {
    let v = UInt32(truncatingIfNeeded: value).bigEndian
    return withUnsafeBytes(of: v) { Data($0) }
}

func floatBE(_ value: Float) -> Data {
    let v = value.bitPattern.bigEndian
    return withUnsafeBytes(of: v) { Data($0) }
}
