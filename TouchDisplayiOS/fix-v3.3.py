from pathlib import Path

# v3.3: fill the entire iPhone/iPad display (no letterbox bars), keep touch
# coordinates aligned with the cropped image, and fix the video reader so it
# stays alive for the whole connected session instead of stopping after one frame.

# --- Video session stability ---
network_path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = network_path.read_text(encoding='utf-8')

old_reader = '''    nonisolated private func readFrames(socket: BlockingSocket) {
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

new_reader = '''    nonisolated private func readFrames(socket: BlockingSocket) {
        do {
            while true {
                let image: UIImage? = try autoreleasepool {
                    let length = try socket.readInt32BE()
                    guard length > 0 && length < 24_000_000 else {
                        throw TouchDisplayError.protocolError("Некорректный видеокадр")
                    }
                    let data = try socket.readExact(length)
                    return UIImage(data: data)
                }

                guard let image else { continue }
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    guard self.videoSocket === socket, self.connected else { return }
                    self.frame = image
                }
            }
        } catch {
            Task { @MainActor [weak self] in
                guard let self else { return }
                guard self.videoSocket === socket else { return }
                self.handleDisconnect(error)
            }
        }
    }
'''

if old_reader not in text:
    raise SystemExit('v3.3 stable frame reader target not found')
text = text.replace(old_reader, new_reader, 1)
network_path.write_text(text, encoding='utf-8')

# --- Edge-to-edge fill + correct touch mapping for aspect-fill crop ---
view_path = Path('TouchDisplayiOS/Sources/RemoteDisplayView.swift')
view = view_path.read_text(encoding='utf-8')

old_mode = '''        imageView.contentMode = .scaleAspectFit
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
'''
new_mode = '''        imageView.contentMode = .scaleAspectFill
        imageView.clipsToBounds = true
        imageView.backgroundColor = .black
        imageView.isUserInteractionEnabled = false
'''
if old_mode not in view:
    raise SystemExit('v3.3 image content mode target not found')
view = view.replace(old_mode, new_mode, 1)

old_scale = '        let scale = min(bounds.width / image.size.width, bounds.height / image.size.height)\n'
new_scale = '        let scale = max(bounds.width / image.size.width, bounds.height / image.size.height)\n'
if old_scale not in view:
    raise SystemExit('v3.3 touch aspect-fill mapping target not found')
view = view.replace(old_scale, new_scale, 1)

view_path.write_text(view, encoding='utf-8')

# UI version label only; Host remains v3.1 and is protocol-compatible.
app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v3.2', 'TouchDisplay v3.3')
app = app.replace('Portrait / Landscape • Touch • Audio • LAN / Tailscale', 'Fullscreen Fill • Touch • Audio • LAN / Tailscale')
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v3.3 fullscreen fill + stable session patch applied')
