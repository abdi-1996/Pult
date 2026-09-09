import Foundation
import UIKit
import Darwin

/// TouchDisplay v4.1 low-latency receiver.
/// TDU4 frames remain supported. TDU5 adds XOR FEC: one lost UDP packet per
/// 8-packet group can be reconstructed without retransmission.
final class UdpVideoReceiver {
    static let headerSize = 20
    static let payloadSize = 1160
    static let fecGroupSize = 8

    private(set) var localPort: Int = 0

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

        var receiveBuffer: Int32 = 8 * 1024 * 1024
        _ = withUnsafePointer(to: &receiveBuffer) {
            setsockopt(socketFd, SOL_SOCKET, SO_RCVBUF, $0, socklen_t(MemoryLayout<Int32>.size))
        }

        var timeout = timeval(tv_sec: 0, tv_usec: 100_000)
        _ = withUnsafePointer(to: &timeout) {
            setsockopt(socketFd, SOL_SOCKET, SO_RCVTIMEO, $0, socklen_t(MemoryLayout<timeval>.size))
        }

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr = in_addr(s_addr: INADDR_ANY)

        let bindResult = withUnsafePointer(to: &address) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                Darwin.bind(socketFd, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else {
            Darwin.close(socketFd)
            throw TouchDisplayError.connection("Не удалось открыть UDP порт")
        }

        var bound = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let nameResult = withUnsafeMutablePointer(to: &bound) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                Darwin.getsockname(socketFd, sa, &length)
            }
        }
        guard nameResult == 0 else {
            Darwin.close(socketFd)
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

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
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
        let createdAt = CFAbsoluteTimeGetCurrent()
        var bytes: [UInt8]
        var received: [Bool]
        var receivedCount = 0
        var parityByGroup: [Int: [UInt8]] = [:]

        init(frameId: UInt32, frameLength: Int, chunkCount: Int) {
            self.frameId = frameId
            self.frameLength = frameLength
            self.chunkCount = chunkCount
            self.bytes = [UInt8](repeating: 0, count: frameLength)
            self.received = [Bool](repeating: false, count: chunkCount)
        }

        func acceptData(chunkIndex: Int, payload: ArraySlice<UInt8>) {
            guard chunkIndex >= 0, chunkIndex < chunkCount, !received[chunkIndex] else { return }
            let destinationOffset = chunkIndex * UdpVideoReceiver.payloadSize
            let copyCount = min(payload.count, frameLength - destinationOffset)
            guard copyCount > 0 else { return }

            var i = 0
            for value in payload.prefix(copyCount) {
                bytes[destinationOffset + i] = value
                i += 1
            }
            received[chunkIndex] = true
            receivedCount += 1
        }

        func acceptParity(groupIndex: Int, payload: ArraySlice<UInt8>) {
            guard groupIndex >= 0 else { return }
            var parity = [UInt8](repeating: 0, count: UdpVideoReceiver.payloadSize)
            var i = 0
            for value in payload.prefix(UdpVideoReceiver.payloadSize) {
                parity[i] = value
                i += 1
            }
            parityByGroup[groupIndex] = parity
        }

        @discardableResult
        func recoverIfPossible(groupIndex: Int) -> Bool {
            guard let parity = parityByGroup[groupIndex] else { return false }
            let first = groupIndex * UdpVideoReceiver.fecGroupSize
            guard first < chunkCount else { return false }
            let last = min(chunkCount, first + UdpVideoReceiver.fecGroupSize)

            var missing = -1
            var missingCount = 0
            for chunk in first..<last where !received[chunk] {
                missing = chunk
                missingCount += 1
                if missingCount > 1 { return false }
            }
            guard missingCount == 1, missing >= 0 else { return false }

            var recovered = parity
            for chunk in first..<last where chunk != missing && received[chunk] {
                let sourceOffset = chunk * UdpVideoReceiver.payloadSize
                let sourceCount = min(UdpVideoReceiver.payloadSize, frameLength - sourceOffset)
                if sourceCount <= 0 { continue }
                for i in 0..<sourceCount {
                    recovered[i] ^= bytes[sourceOffset + i]
                }
            }

            let destinationOffset = missing * UdpVideoReceiver.payloadSize
            let copyCount = min(UdpVideoReceiver.payloadSize, frameLength - destinationOffset)
            guard copyCount > 0 else { return false }
            for i in 0..<copyCount {
                bytes[destinationOffset + i] = recovered[i]
            }
            received[missing] = true
            receivedCount += 1
            parityByGroup.removeValue(forKey: groupIndex)
            return true
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
        var fecRecovered = 0
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

            if count >= Self.headerSize,
               buffer[0] == 84, buffer[1] == 68, buffer[2] == 85 {
                let version = buffer[3]
                if version == 52 || version == 53 { // TDU4 / TDU5
                    let frameId = readUInt32BE(buffer, 4)
                    let packetIndex = Int(readUInt16BE(buffer, 8))
                    let chunkCount = Int(readUInt16BE(buffer, 10))
                    let frameLength = Int(readUInt32BE(buffer, 12))
                    let payloadCount = count - Self.headerSize

                    if frameLength > 0,
                       frameLength <= 16 * 1024 * 1024,
                       chunkCount > 0,
                       chunkCount <= 65535,
                       payloadCount > 0,
                       chunkCount == (frameLength + Self.payloadSize - 1) / Self.payloadSize {

                        var assembly = assemblies[frameId]
                        if assembly == nil {
                            assembly = FrameAssembly(frameId: frameId, frameLength: frameLength, chunkCount: chunkCount)
                            assemblies[frameId] = assembly
                        }

                        if let assembly,
                           assembly.frameLength == frameLength,
                           assembly.chunkCount == chunkCount {
                            if version == 52 {
                                if packetIndex >= 0 && packetIndex < chunkCount {
                                    let payload = buffer[Self.headerSize..<(Self.headerSize + payloadCount)]
                                    assembly.acceptData(chunkIndex: packetIndex, payload: payload)
                                }
                            } else {
                                let groupIndex = Int(readUInt16BE(buffer, 16))
                                let flags = buffer[18]
                                let payload = buffer[Self.headerSize..<(Self.headerSize + payloadCount)]
                                if flags == 0, packetIndex >= 0, packetIndex < chunkCount {
                                    assembly.acceptData(chunkIndex: packetIndex, payload: payload)
                                    if assembly.recoverIfPossible(groupIndex: groupIndex) { fecRecovered += 1 }
                                } else if flags == 1 {
                                    assembly.acceptParity(groupIndex: groupIndex, payload: payload)
                                    if assembly.recoverIfPossible(groupIndex: groupIndex) { fecRecovered += 1 }
                                }
                            }

                            if assembly.receivedCount == assembly.chunkCount {
                                assemblies.removeValue(forKey: frameId)
                                completed += 1
                                decoder.submit(Data(assembly.bytes))

                                // Once a newer frame is complete, older incomplete frames are stale.
                                let stale = assemblies.keys.filter { $0 < frameId }
                                for key in stale {
                                    if assemblies.removeValue(forKey: key) != nil { dropped += 1 }
                                }
                            }
                        }
                    }
                }
            }

            let now = CFAbsoluteTimeGetCurrent()

            // Expire incomplete frames quickly. Waiting for old video is worse than
            // dropping it in an interactive remote-desktop stream.
            let expired = assemblies.filter { now - $0.value.createdAt > 0.12 }.map(\.key)
            for key in expired {
                if assemblies.removeValue(forKey: key) != nil { dropped += 1 }
            }

            if assemblies.count > 4 {
                let keys = assemblies.keys.sorted()
                for key in keys.prefix(assemblies.count - 4) {
                    if assemblies.removeValue(forKey: key) != nil { dropped += 1 }
                }
            }

            let elapsed = now - intervalStart
            if elapsed >= 1.0 {
                let total = completed + dropped
                let fps = Int((Double(completed) / elapsed).rounded())
                // FEC-recovered frames do not count as loss. The Host only needs to
                // react to frames that were actually dropped after recovery.
                let loss = total > 0 ? min(1000, Int((Double(dropped) * 1000.0 / Double(total)).rounded())) : 0

                statsLock.lock()
                let avgDecode = decodeSamples > 0 ? Int((decodeMsTotal / Double(decodeSamples)).rounded()) : 0
                decodeMsTotal = 0
                decodeSamples = 0
                statsLock.unlock()

                _ = fecRecovered // retained for diagnostics / future UI packet
                onStats(max(0, fps), max(0, loss), max(0, avgDecode))
                completed = 0
                dropped = 0
                fecRecovered = 0
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
    private let queue = DispatchQueue(label: "TouchDisplay.LatestJPEG", qos: .userInitiated)
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
