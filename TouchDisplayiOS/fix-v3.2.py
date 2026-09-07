from pathlib import Path

# v3.2: never force landscape. Use the iPhone/iPad's current orientation and
# update the Host profile whenever the SwiftUI viewport changes.

network_path = Path('TouchDisplayiOS/Sources/NetworkClient.swift')
text = network_path.read_text(encoding='utf-8')

old_dims = '''        let native = UIScreen.main.nativeBounds.size
        let adaptiveWidth = Int(max(native.width, native.height))
        let adaptiveHeight = Int(min(native.width, native.height))
'''
new_dims = '''        let native = UIScreen.main.nativeBounds.size
        let interfaceOrientation = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .first?.interfaceOrientation
        let isLandscape = interfaceOrientation?.isLandscape ?? false
        let adaptiveWidth = Int(isLandscape ? max(native.width, native.height) : min(native.width, native.height))
        let adaptiveHeight = Int(isLandscape ? min(native.width, native.height) : max(native.width, native.height))
'''
if old_dims not in text:
    raise SystemExit('v3.2 current-orientation display target not found')
text = text.replace(old_dims, new_dims, 1)

marker = '''    private func startAudio(host: String, password: String) {
'''
update_method = '''    func updateDisplayProfile(pixelWidth: Int, pixelHeight: Int) {
        guard let socket = videoSocket, connected else { return }
        let width = max(1, pixelWidth)
        let height = max(1, pixelHeight)
        var packet = Data([0x12])
        packet.append(int32BE(max(960, min(3072, width))))
        packet.append(int32BE(max(540, min(2048, height))))
        packet.append(84)
        packet.append(40)
        Task.detached(priority: .userInitiated) {
            try? socket.write(packet)
        }
    }

'''
if marker not in text:
    raise SystemExit('v3.2 update profile method marker not found')
text = text.replace(marker, update_method + marker, 1)
network_path.write_text(text, encoding='utf-8')

app_path = Path('TouchDisplayiOS/Sources/TouchDisplayApp.swift')
app = app_path.read_text(encoding='utf-8')
app = app.replace('TouchDisplay v3.1', 'TouchDisplay v3.2')
app = app.replace('Adaptive Display • Touch • Audio • LAN / Tailscale', 'Portrait / Landscape • Touch • Audio • LAN / Tailscale')

start = app.find('struct RemoteScreen: View {')
if start < 0:
    raise SystemExit('v3.2 RemoteScreen start not found')

new_remote = r'''struct RemoteScreen: View {
    @ObservedObject var model: TouchDisplayModel
    @State private var controlsVisible = true

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .topLeading) {
                Color.black.ignoresSafeArea()
                RemoteDisplayRepresentable(model: model)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture(count: 3) {
                        withAnimation(.easeInOut(duration: 0.15)) { controlsVisible.toggle() }
                    }

                if controlsVisible {
                    HStack(spacing: 8) {
                        Button {
                            model.disconnect()
                        } label: {
                            Image(systemName: "xmark")
                                .font(.system(size: 14, weight: .bold))
                                .frame(width: 36, height: 36)
                                .background(.black.opacity(0.58), in: Circle())
                        }
                        .buttonStyle(.plain)

                        Text(model.connectedHost)
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .lineLimit(1)
                            .padding(.horizontal, 11)
                            .frame(height: 34)
                            .background(.black.opacity(0.52), in: Capsule())
                    }
                    .foregroundStyle(.white)
                    .padding(.top, 8)
                    .padding(.leading, 8)
                }
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
'''
app = app[:start] + new_remote
app_path.write_text(app, encoding='utf-8')

print('TouchDisplay iOS v3.2 portrait/current-orientation fix applied')
