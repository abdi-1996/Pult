from pathlib import Path

path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = path.read_text(encoding='utf-8')

# Keep a dedicated UDP receiver. The authenticated TCP socket is now control/touch only.
old_props = '''    private var videoSocket: BlockingSocket?\n    private var audioSocket: BlockingSocket?\n'''
new_props = '''    private var videoSocket: BlockingSocket?\n    private var audioSocket: BlockingSocket?\n    private var udpReceiver: UdpVideoReceiver?\n'''
if old_props not in text:
    raise SystemExit('v4 socket properties target not found')
text = text.replace(old_props, new_props, 1)

old_task = '''        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            do {
                let result = try await self.findAndOpen(password: normalized, manual: manualTailscale.trimmingCharacters(in: .whitespacesAndNewlines), displayWidth: adaptiveWidth, displayHeight: adaptiveHeight)
                await self.sessionOpened(socket: result.socket, host: result.host, password: normalized, discovered: result.discovered)
                self.readFrames(socket: result.socket)
            } catch {
                await self.sessionFailed(error)
            }
        }
'''
new_task = '''        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            var receiver: UdpVideoReceiver?
            do {
                let lowLatencyReceiver = try UdpVideoReceiver()
                receiver = lowLatencyReceiver
                let result = try await self.findAndOpen(
                    password: normalized,
                    manual: manualTailscale.trimmingCharacters(in: .whitespacesAndNewlines),
                    displayWidth: adaptiveWidth,
                    displayHeight: adaptiveHeight,
                    udpPort: lowLatencyReceiver.localPort)
                await self.sessionOpened(
                    socket: result.socket,
                    host: result.host,
                    password: normalized,
                    discovered: result.discovered,
                    udpReceiver: lowLatencyReceiver)
            } catch {
                receiver?.close()
                await self.sessionFailed(error)
            }
        }
'''
if old_task not in text:
    raise SystemExit('v4 connect task target not found')
text = text.replace(old_task, new_task, 1)

old_sig = 'nonisolated private func findAndOpen(password: String, manual: String, displayWidth: Int, displayHeight: Int) async throws -> (socket: BlockingSocket, host: String, discovered: DiscoveredPC?) {'
new_sig = 'nonisolated private func findAndOpen(password: String, manual: String, displayWidth: Int, displayHeight: Int, udpPort: Int) async throws -> (socket: BlockingSocket, host: String, discovered: DiscoveredPC?) {'
if old_sig not in text:
    raise SystemExit('v4 findAndOpen signature target not found')
text = text.replace(old_sig, new_sig, 1)

old_open = '''                let socket = try BlockingSocket(host: host, port: port)
                try authenticateVideo(socket: socket, password: password)
                try sendDisplayProfile(socket: socket, width: displayWidth, height: displayHeight)
                return (socket, host, discovered)
'''
new_open = '''                let socket = try BlockingSocket(host: host, port: port)
                try authenticateVideo(socket: socket, password: password, udpPort: udpPort)
                try sendDisplayProfile(socket: socket, width: displayWidth, height: displayHeight)
                return (socket, host, discovered)
'''
if old_open not in text:
    raise SystemExit('v4 low latency authentication target not found')
text = text.replace(old_open, new_open, 1)

old_auth = '''    nonisolated private func authenticateVideo(socket: BlockingSocket, password: String) throws {
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
'''
new_auth = '''    nonisolated private func authenticateVideo(socket: BlockingSocket, password: String, udpPort: Int) throws {
        var packet = Data("TD04".utf8)
        let p = Data(password.utf8)
        packet.append(int32BE(p.count))
        packet.append(p)
        packet.append(int32BE(udpPort))
        try socket.write(packet)
        let accepted = try socket.readByte()
        if accepted != 1 {
            socket.close()
            throw TouchDisplayError.authentication
        }
    }
'''
if old_auth not in text:
    raise SystemExit('v4 TD04 handshake target not found')
text = text.replace(old_auth, new_auth, 1)

# Retina/full quality request. Host still backs off automatically when AI/network is busy.
text = text.replace('packet.append(int32BE(max(960, min(3072, width))))', 'packet.append(int32BE(max(640, min(3200, width))))')
text = text.replace('packet.append(int32BE(max(540, min(2048, height))))', 'packet.append(int32BE(max(640, min(3200, height))))')
text = text.replace('packet.append(84) // JPEG quality optimized for Retina-sized displays', 'packet.append(90) // v4 high-quality target; Host AI Priority may reduce it')
text = text.replace('packet.append(40) // target FPS; Host dynamically backs off if capture is slower', 'packet.append(60) // v4 target FPS')
# v3.2 updateDisplayProfile has the same values without comments.
text = text.replace('''        packet.append(84)\n        packet.append(40)\n''', '''        packet.append(90)\n        packet.append(60)\n''')

old_session_sig = '    private func sessionOpened(socket: BlockingSocket, host: String, password: String, discovered: DiscoveredPC?) {'
new_session_sig = '    private func sessionOpened(socket: BlockingSocket, host: String, password: String, discovered: DiscoveredPC?, udpReceiver receiver: UdpVideoReceiver) {'
if old_session_sig not in text:
    raise SystemExit('v4 sessionOpened signature target not found')
text = text.replace(old_session_sig, new_session_sig, 1)

old_session_start = '''        videoSocket?.close()
        videoSocket = socket
        connected = true
'''
new_session_start = '''        videoSocket?.close()
        udpReceiver?.close()
        videoSocket = socket
        udpReceiver = receiver
        connected = true
'''
if old_session_start not in text:
    raise SystemExit('v4 session start target not found')
text = text.replace(old_session_start, new_session_start, 1)

old_audio_start = '''        startAudio(host: host, password: password)
    }
'''
new_audio_start = '''        receiver.start(
            onFrame: { [weak self] image in
                Task { @MainActor [weak self] in
                    guard let self, self.videoSocket === socket, self.connected else { return }
                    self.frame = image
                }
            },
            onStats: { [weak self] fps, lossPermille, decodeMs in
                Task { @MainActor [weak self] in
                    guard let self, self.videoSocket === socket, self.connected else { return }
                    self.sendVideoFeedback(socket: socket, fps: fps, lossPermille: lossPermille, decodeMs: decodeMs)
                }
            })

        startAudio(host: host, password: password)
    }
'''
if old_audio_start not in text:
    raise SystemExit('v4 receiver start target not found')
text = text.replace(old_audio_start, new_audio_start, 1)

# Feedback lets the Host reduce video bytes before latency grows.
marker = '    func updateDisplayProfile(pixelWidth: Int, pixelHeight: Int) {'
feedback_method = '''    private func sendVideoFeedback(socket: BlockingSocket, fps: Int, lossPermille: Int, decodeMs: Int) {
        func appendUInt16(_ value: Int, to data: inout Data) {
            let v = max(0, min(65535, value))
            data.append(UInt8((v >> 8) & 0xff))
            data.append(UInt8(v & 0xff))
        }

        var packet = Data([0x21])
        appendUInt16(fps, to: &packet)
        appendUInt16(lossPermille, to: &packet)
        appendUInt16(decodeMs, to: &packet)
        Task.detached(priority: .userInteractive) {
            try? socket.write(packet)
        }
    }

'''
if marker not in text:
    raise SystemExit('v4 feedback method marker not found')
text = text.replace(marker, feedback_method + marker, 1)

# The live profile update method also gets v4 limits/targets.
text = text.replace('packet.append(int32BE(max(960, min(3072, width))))', 'packet.append(int32BE(max(640, min(3200, width))))')
text = text.replace('packet.append(int32BE(max(540, min(2048, height))))', 'packet.append(int32BE(max(640, min(3200, height))))')

# Close the UDP receiver whenever the user or session disconnects.
old_disconnect_cleanup = '''        videoSocket?.close()
        audioSocket?.close()
        videoSocket = nil
        audioSocket = nil
'''
new_disconnect_cleanup = '''        videoSocket?.close()
        audioSocket?.close()
        udpReceiver?.close()
        videoSocket = nil
        audioSocket = nil
        udpReceiver = nil
'''
count = text.count(old_disconnect_cleanup)
if count < 2:
    raise SystemExit(f'v4 disconnect cleanup expected >=2, got {count}')
text = text.replace(old_disconnect_cleanup, new_disconnect_cleanup)

# Lower local audio device buffering on iPhone/iPad. Audio remains independent of video.
old_session_audio = '''                try session.setCategory(.playback, mode: .moviePlayback, options: [.mixWithOthers])
                try session.setActive(true)
'''
new_session_audio = '''                try session.setCategory(.playback, mode: .moviePlayback, options: [.mixWithOthers])
                try? session.setPreferredIOBufferDuration(0.01)
                try session.setActive(true)
'''
if old_session_audio not in text:
    raise SystemExit('v4 audio latency target not found')
text = text.replace(old_session_audio, new_session_audio, 1)

text = text.replace('status = "Подключено • Adaptive Display • \\(host)"', 'status = "Подключено • Low Latency v4 • \\(host)"')

path.write_text(text, encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v3.5', 'TouchDisplay v4.0')
app = app.replace('Auto Portrait PC • Touch • Audio • LAN / Tailscale', 'Low Latency • AI Priority • Audio • Tailscale')
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v4.0 UDP latest-frame + AI Priority client patch applied')
