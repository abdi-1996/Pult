import Foundation
import UIKit
import Darwin

/// TouchDisplay v4 video receiver.
/// Each JPEG frame is split into small UDP datagrams. Missing fragments drop only
/// that frame; the decoder always works on the newest complete frame so latency
/// cannot build up behind an old TCP queue.
final class UdpVideoReceiver {
    static let headerSize = 20
    static let payloadSize = 1160

    let localPort: Int

    private let fd: Int32
    private let stateLock = NSLock()
    private var closed = false
    private var started = false

    init() throws {
        let socketFd = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard socketFd >= 0 else {
            throw TouchDisplayError.connection("Не удалось открыть Low Latency UDP")
        }
        fd = socketFd

        var receiveBuffer: Int32 = 4 * 1024 * 1024
        _ = withUnsafePointer(to: &receiveBuffer) {
            setsockopt(fd, SOL_SOCKET, SO_RCVBUF, $0, socklen_t(MemoryLayout<Int32>.size))
        }

        var timeout = timeval(tv_sec: 0, tv_usec: 250_000)
        _ = withUnsafePointer(to: &timeout) {
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, $0, socklen_t(MemoryLayout<timeval>.size))
        }

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr = in_addr(s_addr: INADDR_ANY)

        let bindResult = withUnsafePointer(to: &address) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                Darwin.bind(fd, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else {
            Darwin.close(fd)
            throw TouchDisplayError.connection("Не удалось открыть UDP порт")
        }

        var bound = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let nameResult = withUnsafeMutablePointer(to: &bound) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                Darwin.getsockname(fd, sa, &length)
            }
        }
        guard nameResult == 0 else {
            Darwin.close(fd)
            throw TouchDisplayError.connection("Не удалось определить UDP порт")
        }
        localPort = Int(UInt16(bigEndian: bound.sin_port))
    }

    func start(
        onFrame: @escaping (UIImage) -> Void,
        onStats: @escaping (_ fps: Int, _ lossPermille: Int, _ decodeMs: Int) -> Void
    ) {
        stateLock.lock()
        if started || closed {
            stateLock.unlock()
            return
        }
        started = true
        stateLock.unlock()

        let decoder = LatestJpegDecoder { image, decodeMs in
            onFrame(image)
            self.statsLock.lock()
            self.decodeMsTotal += decodeMs
            self.decodeSamples += 1
            self.statsLock.unlock()
        }

        DispatchQueue.global(qos: .userInteractive).async { [weak self] in
            self?.receiveLoop(decoder: decoder, onStats: onStats)
        }
    }

    func close() {
        stateLock.lock()
        if closed {
            stateLock.unlock()
            return
        }
        closed = true
        stateLock.unlock()
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }

    deinit { close() }

    private final class FrameAssembly {
        let frameId: UInt32
        let frameLength: Int
        let chunkCount: Int
        var bytes: [UInt8]
        var received: [Bool]
        var receivedCount = 0

        init(frameId: UInt32, frameLength: Int, chunkCount: Int) {
            self.frameId = frameId
            self.frameLength = frameLength
            self.chunkCount = chunkCount
            self.bytes = [UInt8](repeating: 0, count: frameLength)
            self.received = [Bool](repeating: false, count: chunkCount)
        }
    }

    private let statsLock = NSLock()
    private var decodeMsTotal = 0.0
    private var decodeSamples = 0

    private func isClosed() -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return closed
    }

    private func receiveLoop(
        decoder: LatestJpegDecoder,
        onStats: @escaping (Int, Int, Int) -> Void
    ) {
        var assemblies: [UInt32: FrameAssembly] = [:]
        var buffer = [UInt8](repeating: 0, count: 1400)
        var completed = 0
        var dropped = 0
        var intervalStart = CFAbsoluteTimeGetCurrent()

        while !isClosed() {
            var sender = sockaddr_storage()
            var senderLength = socklen_t(MemoryLayout<sockaddr_storage>.size)
            let bufferCount = buffer.count
            let count = buffer.withUnsafeMutableBytes { raw -> Int in
                guard let base = raw.baseAddress else { return -1 }
                return withUnsafeMutablePointer(to: &sender) { ptr in
                    ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                        Darwin.recvfrom(fd, base, bufferCount, 0, sa, &senderLength)
                    }
                }
            }

            if count >= Self.headerSize {
                if buffer[0] == 84, buffer[1] == 68, buffer[2] == 85, buffer[3] == 52 { // TDU4
                    let frameId = readUInt32BE(buffer, 4)
                    let chunkIndex = Int(readUInt16BE(buffer, 8))
                    let chunkCount = Int(readUInt16BE(buffer, 10))
                    let frameLength = Int(readUInt32BE(buffer, 12))
                    let payloadCount = count - Self.headerSize

                    if frameLength > 0,
                       frameLength <= 16 * 1024 * 1024,
                       chunkCount > 0,
                       chunkCount <= 65535,
                       chunkIndex >= 0,
                       chunkIndex < chunkCount,
                       payloadCount > 0,
                       chunkCount == (frameLength + Self.payloadSize - 1) / Self.payloadSize {

                        var assembly = assemblies[frameId]
                        if assembly == nil {
                            assembly = FrameAssembly(frameId: frameId, frameLength: frameLength, chunkCount: chunkCount)
                            assemblies[frameId] = assembly
                        }

                        if let assembly,
                           assembly.frameLength == frameLength,
                           assembly.chunkCount == chunkCount,
                           !assembly.received[chunkIndex] {
                            let destinationOffset = chunkIndex * Self.payloadSize
                            let copyCount = min(payloadCount, frameLength - destinationOffset)
                            if copyCount > 0 {
                                assembly.bytes.withUnsafeMutableBytes { destination in
                                    buffer.withUnsafeBytes { source in
                                        guard let dst = destination.baseAddress,
                                              let src = source.baseAddress else { return }
                                        memcpy(dst.advanced(by: destinationOffset),
                                               src.advanced(by: Self.headerSize),
                                               copyCount)
                                    }
                                }
                                assembly.received[chunkIndex] = true
                                assembly.receivedCount += 1
                            }

                            if assembly.receivedCount == assembly.chunkCount {
                                assemblies.removeValue(forKey: frameId)
                                completed += 1
                                decoder.submit(Data(assembly.bytes))

                                // Any much older incomplete frames are stale now.
                                let stale = assemblies.keys.filter { $0 < frameId }
                                for key in stale {
                                    if assemblies.removeValue(forKey: key) != nil { dropped += 1 }
                                }
                            }
                        }

                        // Bound memory and latency even under severe packet loss.
                        if assemblies.count > 3 {
                            let keys = assemblies.keys.sorted()
                            for key in keys.prefix(assemblies.count - 3) {
                                if assemblies.removeValue(forKey: key) != nil { dropped += 1 }
                            }
                        }
                    }
                }
            }

            let now = CFAbsoluteTimeGetCurrent()
            let elapsed = now - intervalStart
            if elapsed >= 1.0 {
                let total = completed + dropped
                let fps = Int((Double(completed) / elapsed).rounded())
                let loss = total > 0 ? min(1000, Int((Double(dropped) * 1000.0 / Double(total)).rounded())) : 0

                statsLock.lock()
                let avgDecode = decodeSamples > 0 ? Int((decodeMsTotal / Double(decodeSamples)).rounded()) : 0
                decodeMsTotal = 0
                decodeSamples = 0
                statsLock.unlock()

                onStats(max(0, fps), max(0, loss), max(0, avgDecode))
                completed = 0
                dropped = 0
                intervalStart = now
            }
        }
    }

    private func readUInt16BE(_ bytes: [UInt8], _ offset: Int) -> UInt16 {
        (UInt16(bytes[offset]) << 8) | UInt16(bytes[offset + 1])
    }

    private func readUInt32BE(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
        (UInt32(bytes[offset]) << 24) |
        (UInt32(bytes[offset + 1]) << 16) |
        (UInt32(bytes[offset + 2]) << 8) |
        UInt32(bytes[offset + 3])
    }
}

private final class LatestJpegDecoder {
    private let queue = DispatchQueue(label: "TouchDisplay.LatestJPEG", qos: .userInteractive)
    private let lock = NSLock()
    private var latest: Data?
    private var working = false
    private let onDecoded: (UIImage, Double) -> Void

    init(onDecoded: @escaping (UIImage, Double) -> Void) {
        self.onDecoded = onDecoded
    }

    func submit(_ data: Data) {
        lock.lock()
        latest = data // replace stale compressed frame immediately
        if working {
            lock.unlock()
            return
        }
        working = true
        lock.unlock()

        queue.async { [weak self] in self?.drain() }
    }

    private func drain() {
        while true {
            lock.lock()
            guard let data = latest else {
                working = false
                lock.unlock()
                return
            }
            latest = nil
            lock.unlock()

            let start = CFAbsoluteTimeGetCurrent()
            autoreleasepool {
                if let image = UIImage(data: data) {
                    let ms = (CFAbsoluteTimeGetCurrent() - start) * 1000.0
                    onDecoded(image, ms)
                }
            }
        }
    }
}
