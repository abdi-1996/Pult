from pathlib import Path

network_path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = network_path.read_text(encoding='utf-8')

# TD05 opts into TDU5 video packets with XOR FEC. Host keeps TD04 compatibility.
if 'Data("TD04".utf8)' not in text:
    raise SystemExit('v4.1 TD05 handshake target not found')
text = text.replace('Data("TD04".utf8)', 'Data("TD05".utf8)', 1)

# Ask for maximum source quality. Smart Bitrate / AI Priority on Host reduces it
# before latency grows, so the client does not need manual quality controls.
text = text.replace('packet.append(90) // v4 high-quality target; Host AI Priority may reduce it',
                    'packet.append(92) // v4.1 max-quality target; Host Smart Bitrate may reduce it')
text = text.replace('''        packet.append(90)\n        packet.append(60)\n''',
                    '''        packet.append(92)\n        packet.append(60)\n''')

old_pub = '''    @Published var connectedHost = ""\n'''
new_pub = '''    @Published var connectedHost = ""\n    @Published var streamStats = ""\n'''
if old_pub not in text:
    raise SystemExit('v4.1 streamStats property target not found')
text = text.replace(old_pub, new_pub, 1)

text = text.replace('status = "Подключено • Low Latency v4 • \\(host)"',
                    'status = "Подключено • Low Latency v4.1 + FEC • \\(host)"')

old_stats = '''                    guard let self, self.videoSocket === socket, self.connected else { return }
                    self.sendVideoFeedback(socket: socket, fps: fps, lossPermille: lossPermille, decodeMs: decodeMs)
'''
new_stats = '''                    guard let self, self.videoSocket === socket, self.connected else { return }
                    self.streamStats = "\\(fps) FPS • loss \\(lossPermille / 10)% • decode \\(decodeMs) ms"
                    self.sendVideoFeedback(socket: socket, fps: fps, lossPermille: lossPermille, decodeMs: decodeMs)
'''
if old_stats not in text:
    raise SystemExit('v4.1 stats callback target not found')
text = text.replace(old_stats, new_stats, 1)

# Reset diagnostics whenever a session ends.
text = text.replace('''        frame = nil
        status = "Соединение потеряно: \\(error.localizedDescription)"
''', '''        frame = nil
        streamStats = ""
        status = "Соединение потеряно: \\(error.localizedDescription)"
''', 1)
text = text.replace('''        frame = nil
        status = "Отключено"
''', '''        frame = nil
        streamStats = ""
        status = "Отключено"
''', 1)

network_path.write_text(text, encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v4.0', 'TouchDisplay v4.1')
app = app.replace('Low Latency • AI Priority • Audio • Tailscale',
                  'FEC • Smart Bitrate • AI Priority • 10ms Audio • Tailscale')

old_host = '''                        Text(model.connectedHost)
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .lineLimit(1)
                            .padding(.horizontal, 11)
                            .frame(height: 34)
                            .background(.black.opacity(0.52), in: Capsule())
'''
new_host = '''                        VStack(alignment: .leading, spacing: 1) {
                            Text(model.connectedHost)
                                .font(.system(size: 11, weight: .semibold, design: .monospaced))
                                .lineLimit(1)
                            if !model.streamStats.isEmpty {
                                Text(model.streamStats)
                                    .font(.system(size: 9, weight: .medium, design: .monospaced))
                                    .foregroundStyle(.white.opacity(0.72))
                                    .lineLimit(1)
                            }
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(.black.opacity(0.52), in: RoundedRectangle(cornerRadius: 11, style: .continuous))
'''
if old_host not in app:
    raise SystemExit('v4.1 remote stats UI target not found')
app = app.replace(old_host, new_host, 1)
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v4.1 FEC + Smart Bitrate client patch applied')
