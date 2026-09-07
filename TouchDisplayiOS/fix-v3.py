from pathlib import Path

path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = path.read_text(encoding='utf-8')
text = text.replace('candidates.append((manual, videoPort))', 'candidates.append((manual, 59432))')
text = text.replace('let p = savedPort > 0 ? savedPort : videoPort', 'let p = savedPort > 0 ? savedPort : 59432')
text = text.replace('let s = try BlockingSocket(host: host, port: self.audioPort)', 'let s = try BlockingSocket(host: host, port: 59434)')
text = text.replace('address.sin_port = discoveryPort.bigEndian', 'address.sin_port = UInt16(59431).bigEndian')

old = '''    nonisolated private func readFrames(socket: BlockingSocket) {
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
'''
new = '''    nonisolated private func readFrames(socket: BlockingSocket) {
        do {
            while true {
                let length = try socket.readInt32BE()
                guard length > 0 && length < 24_000_000 else {
                    throw TouchDisplayError.protocolError("Некорректный видеокадр")
                }
                let data = try socket.readExact(length)
                if let image = UIImage(data: data) {
                    Task { @MainActor [weak self] in self?.frame = image }
                }
            }
        } catch {
            Task { @MainActor [weak self] in self?.handleDisconnect(error) }
        }
    }
'''
if old not in text:
    raise SystemExit('readFrames block not found')
text = text.replace(old, new)
path.write_text(text, encoding='utf-8')
print('iOS v3 fixes applied')
