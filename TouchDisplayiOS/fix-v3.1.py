from pathlib import Path

path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = path.read_text(encoding='utf-8')

old = '''        connecting = true
        status = "Поиск ПК…"
        password = normalized

        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            do {
                let result = try await self.findAndOpen(password: normalized, manual: manualTailscale.trimmingCharacters(in: .whitespacesAndNewlines))
'''
new = '''        connecting = true
        let native = UIScreen.main.nativeBounds.size
        let adaptiveWidth = Int(max(native.width, native.height))
        let adaptiveHeight = Int(min(native.width, native.height))
        status = "Поиск ПК… • Adaptive \\(adaptiveWidth)×\\(adaptiveHeight)"
        password = normalized

        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else { return }
            do {
                let result = try await self.findAndOpen(password: normalized, manual: manualTailscale.trimmingCharacters(in: .whitespacesAndNewlines), displayWidth: adaptiveWidth, displayHeight: adaptiveHeight)
'''
if old not in text:
    raise SystemExit('connect adaptive insertion target not found')
text = text.replace(old, new)

text = text.replace(
    'nonisolated private func findAndOpen(password: String, manual: String) async throws -> (socket: BlockingSocket, host: String, discovered: DiscoveredPC?) {',
    'nonisolated private func findAndOpen(password: String, manual: String, displayWidth: Int, displayHeight: Int) async throws -> (socket: BlockingSocket, host: String, discovered: DiscoveredPC?) {'
)

old_auth_return = '''                let socket = try BlockingSocket(host: host, port: port)
                try authenticateVideo(socket: socket, password: password)
                return (socket, host, discovered)
'''
new_auth_return = '''                let socket = try BlockingSocket(host: host, port: port)
                try authenticateVideo(socket: socket, password: password)
                try sendDisplayProfile(socket: socket, width: displayWidth, height: displayHeight)
                return (socket, host, discovered)
'''
if old_auth_return not in text:
    raise SystemExit('display profile send target not found')
text = text.replace(old_auth_return, new_auth_return)

marker = '    private func sessionOpened(socket: BlockingSocket, host: String, password: String, discovered: DiscoveredPC?) {'
method = '''    nonisolated private func sendDisplayProfile(socket: BlockingSocket, width: Int, height: Int) throws {
        var packet = Data([0x12])
        packet.append(int32BE(max(960, min(3072, width))))
        packet.append(int32BE(max(540, min(2048, height))))
        packet.append(84) // JPEG quality optimized for Retina-sized displays
        packet.append(40) // target FPS; Host dynamically backs off if capture is slower
        try socket.write(packet)
    }

'''
if marker not in text:
    raise SystemExit('display profile method marker not found')
text = text.replace(marker, method + marker)

text = text.replace('status = "Подключено • \\(host)"', 'status = "Подключено • Adaptive Display • \\(host)"')

path.write_text(text, encoding='utf-8')
print('TouchDisplay iOS v3.1 adaptive display patch applied')
