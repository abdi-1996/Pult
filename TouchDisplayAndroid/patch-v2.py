from pathlib import Path

path = Path('TouchDisplayAndroid/app/src/main/java/kz/pult/touchdisplay/MainActivity.kt')
text = path.read_text(encoding='utf-8')

# v2.1: no Display 2. Keep primary-screen streaming, add PC audio and explicit Tailscale fallback.
text = text.replace('import android.os.Bundle\n', 'import android.os.Bundle\nimport android.media.AudioAttributes\nimport android.media.AudioFormat\nimport android.media.AudioTrack\nimport android.util.Log\n')
text = text.replace('private const val DISCOVERY_PORT = 59431', 'private const val DISCOVERY_PORT = 59431\n        private const val AUDIO_PORT = 59434')
text = text.replace('text = "TouchDisplay v1.7"', 'text = "TouchDisplay v2.1"')
text = text.replace('text = "Введите только пароль\\nПК найдётся автоматически"', 'text = "Введите пароль\\nLAN / Tailscale • Touch • Audio"')

# Optional manual Tailscale address for first connection away from home.
password_view = '        root.addView(password, LinearLayout.LayoutParams(620, 62).apply { bottomMargin = 14 })\n'
remote_view = '''        root.addView(password, LinearLayout.LayoutParams(620, 62).apply { bottomMargin = 10 })\n\n        val tailscaleHost = EditText(this).apply {\n            hint = "Tailscale IP/имя (необязательно)"\n            setHintTextColor(Color.GRAY)\n            setTextColor(Color.WHITE)\n            textSize = 16f\n            setSingleLine(true)\n            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI\n            setBackgroundColor(Color.rgb(35, 40, 50))\n            setPadding(22, 8, 22, 8)\n            setText(prefs.getString("last_tail", "") ?: "")\n        }\n        root.addView(tailscaleHost, LinearLayout.LayoutParams(620, 58).apply { bottomMargin = 14 })\n'''
if password_view not in text:
    raise SystemExit('tailscale field insertion marker not found')
text = text.replace(password_view, remote_view)

text = text.replace('connectWithPassword(value)', 'connectWithPassword(value, tailscaleHost.text.toString().trim())')
text = text.replace('private fun connectWithPassword(password: String) {', 'private fun connectWithPassword(password: String, manualHost: String) {')

old_candidates_start = '''                val candidates = LinkedHashSet<Pair<String, Int>>()\n                val discovered = discoverHost(password)\n'''
new_candidates_start = '''                val candidates = LinkedHashSet<Pair<String, Int>>()\n                if (manualHost.isNotBlank()) {\n                    candidates.add(manualHost to TCP_PORT)\n                }\n                val discovered = if (manualHost.isBlank()) discoverHost(password) else null\n'''
if old_candidates_start not in text:
    raise SystemExit('candidate start marker not found')
text = text.replace(old_candidates_start, new_candidates_start)

old_save = '''                if (discovered != null) {\n                    prefs.edit()\n                        .putString("last_lan", discovered.lanAddress)\n                        .putString("last_tail", discovered.tailscaleAddress ?: "")\n                        .putInt("last_port", discovered.port)\n                        .apply()\n                }\n                startSession(connection)\n'''
new_save = '''                if (discovered != null) {\n                    prefs.edit()\n                        .putString("last_lan", discovered.lanAddress)\n                        .putString("last_tail", discovered.tailscaleAddress ?: "")\n                        .putInt("last_port", discovered.port)\n                        .apply()\n                } else if (manualHost.isNotBlank()) {\n                    prefs.edit()\n                        .putString("last_tail", manualHost)\n                        .putInt("last_port", TCP_PORT)\n                        .apply()\n                }\n                startSession(connection, password)\n'''
if old_save not in text:
    raise SystemExit('successful host save marker not found')
text = text.replace(old_save, new_save)

# Audio socket lifecycle.
text = text.replace('    private var socket: Socket? = null\n', '    private var socket: Socket? = null\n    private var audioSocket: Socket? = null\n')
text = text.replace('    private fun startSession(connection: OpenConnection) {', '    private fun startSession(connection: OpenConnection, password: String) {')
text = text.replace('        startDecoder()\n        readFramesFast(connection.input)', '        startDecoder()\n        startAudio(connection.host, password)\n        readFramesFast(connection.input)')

marker = '    private fun startDecoder() {'
audio_method = r'''    private fun startAudio(host: String, password: String) {
        Thread {
            var track: AudioTrack? = null
            var s: Socket? = null
            try {
                s = Socket().apply {
                    tcpNoDelay = true
                    keepAlive = true
                    receiveBufferSize = 128 * 1024
                    connect(InetSocketAddress(host, AUDIO_PORT), 3000)
                }
                audioSocket = s

                val din = DataInputStream(BufferedInputStream(s.getInputStream(), 128 * 1024))
                val dout = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 16 * 1024))
                val pass = password.toByteArray(Charsets.UTF_8)
                dout.write("TDA2".toByteArray(Charsets.US_ASCII))
                dout.writeInt(pass.size)
                dout.write(pass)
                dout.flush()

                if (din.readUnsignedByte() != 1) throw IOException("Audio auth failed")
                val sampleRate = din.readInt()
                val channels = din.readUnsignedByte()
                val bits = din.readUnsignedByte()
                if (bits != 16 || channels !in 1..2) throw IOException("Unsupported audio format")

                val channelMask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
                val encoding = AudioFormat.ENCODING_PCM_16BIT
                val minBuffer = AudioTrack.getMinBufferSize(sampleRate, channelMask, encoding)
                val bufferBytes = maxOf(minBuffer, sampleRate * channels * 2 / 10) // ~100 ms

                track = AudioTrack.Builder()
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                            .build()
                    )
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setEncoding(encoding)
                            .setSampleRate(sampleRate)
                            .setChannelMask(channelMask)
                            .build()
                    )
                    .setBufferSizeInBytes(bufferBytes)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()
                track.play()

                while (connected) {
                    val length = din.readInt()
                    if (length <= 0 || length > 262_144) throw IOException("Bad audio packet")
                    val pcm = ByteArray(length)
                    din.readFully(pcm)
                    track.write(pcm, 0, pcm.size, AudioTrack.WRITE_BLOCKING)
                }
            } catch (e: Exception) {
                if (connected) Log.w("TouchDisplay", "Audio stream unavailable", e)
            } finally {
                try { track?.pause() } catch (_: Exception) { }
                try { track?.flush() } catch (_: Exception) { }
                try { track?.release() } catch (_: Exception) { }
                try { s?.close() } catch (_: Exception) { }
                if (audioSocket === s) audioSocket = null
            }
        }.apply {
            name = "TouchDisplayAudio"
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

'''
if marker not in text:
    raise SystemExit('audio insertion marker not found')
text = text.replace(marker, audio_method + marker)

old_close = '''        try { socket?.close() } catch (_: Exception) { }\n        socket = null\n'''
new_close = '''        try { socket?.close() } catch (_: Exception) { }\n        try { audioSocket?.close() } catch (_: Exception) { }\n        socket = null\n        audioSocket = null\n'''
if old_close not in text:
    raise SystemExit('audio close marker not found')
text = text.replace(old_close, new_close)

path.write_text(text, encoding='utf-8')
print('TouchDisplay Android v2.1 audio + Tailscale patch applied')
