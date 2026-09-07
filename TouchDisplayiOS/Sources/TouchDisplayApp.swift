import SwiftUI

@main
struct TouchDisplayApp: App {
    @StateObject private var model = TouchDisplayModel()

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
                .preferredColorScheme(.dark)
        }
    }
}

struct RootView: View {
    @ObservedObject var model: TouchDisplayModel

    var body: some View {
        Group {
            if model.connected {
                RemoteScreen(model: model)
            } else {
                LoginView(model: model)
            }
        }
    }
}

struct LoginView: View {
    @ObservedObject var model: TouchDisplayModel
    @State private var password = ""
    @State private var tailscale = UserDefaults.standard.string(forKey: "td.tail") ?? ""
    @State private var showRemote = false

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color(red: 0.045, green: 0.055, blue: 0.075).ignoresSafeArea()

                VStack(spacing: 18) {
                    Spacer()
                    Image(systemName: UIDevice.current.userInterfaceIdiom == .pad ? "ipad.landscape" : "iphone.landscape")
                        .font(.system(size: proxy.size.width > 700 ? 60 : 46, weight: .light))
                        .foregroundStyle(.white.opacity(0.92))

                    Text("TouchDisplay v3")
                        .font(.system(size: proxy.size.width > 700 ? 38 : 31, weight: .medium))

                    Text(UIDevice.current.userInterfaceIdiom == .pad ? "iPad как сенсорный экран Windows" : "iPhone как сенсорный экран Windows")
                        .font(.system(size: 16))
                        .foregroundStyle(.secondary)

                    Text("Screen • Touch • Audio • LAN / Tailscale")
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)

                    VStack(spacing: 12) {
                        TextField("6-значный пароль", text: $password)
                            .keyboardType(.numberPad)
                            .textContentType(.oneTimeCode)
                            .multilineTextAlignment(.center)
                            .font(.system(size: 22, weight: .medium, design: .monospaced))
                            .padding(.horizontal, 16)
                            .frame(height: 52)
                            .background(.white.opacity(0.09), in: RoundedRectangle(cornerRadius: 12))
                            .onChange(of: password) { value in
                                let digits = value.compactMap { $0.wholeNumberValue }.map(String.init).joined()
                                password = String(digits.prefix(6))
                            }

                        Button {
                            withAnimation(.easeInOut(duration: 0.2)) { showRemote.toggle() }
                        } label: {
                            HStack {
                                Image(systemName: "network")
                                Text("Tailscale")
                                Spacer()
                                Image(systemName: showRemote ? "chevron.up" : "chevron.down")
                            }
                            .font(.system(size: 14, weight: .medium))
                            .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)

                        if showRemote {
                            TextField("Tailscale IP, например 100.x.x.x", text: $tailscale)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .keyboardType(.numbersAndPunctuation)
                                .padding(.horizontal, 14)
                                .frame(height: 46)
                                .background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
                        }

                        Button {
                            hideKeyboard()
                            model.connect(password: password, manualTailscale: tailscale)
                        } label: {
                            HStack(spacing: 10) {
                                if model.connecting { ProgressView().tint(.white) }
                                Text(model.connecting ? "ПОДКЛЮЧЕНИЕ…" : "ВОЙТИ")
                                    .fontWeight(.semibold)
                            }
                            .frame(maxWidth: .infinity)
                            .frame(height: 52)
                            .background(password.count == 6 ? Color.white.opacity(0.20) : Color.white.opacity(0.09), in: RoundedRectangle(cornerRadius: 12))
                        }
                        .buttonStyle(.plain)
                        .disabled(password.count != 6 || model.connecting)
                    }
                    .frame(maxWidth: 430)
                    .padding(.horizontal, 28)

                    Text(model.status)
                        .font(.system(size: 14))
                        .foregroundStyle(model.status.lowercased().contains("ошиб") || model.status.lowercased().contains("невер") ? .orange : .secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 520)
                        .padding(.horizontal, 24)

                    Spacer()
                    Text("Один TouchDisplayHost.exe работает с Android, iPhone и iPad")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary.opacity(0.75))
                        .padding(.bottom, 14)
                }
            }
        }
    }

    private func hideKeyboard() {
        UIApplication.shared.sendAction(#selector(UIResponder.resignFirstResponder), to: nil, from: nil, for: nil)
    }
}

struct RemoteScreen: View {
    @ObservedObject var model: TouchDisplayModel
    @State private var controlsVisible = true

    var body: some View {
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
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
    }
}
