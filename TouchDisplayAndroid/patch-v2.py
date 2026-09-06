from pathlib import Path

path = Path('TouchDisplayAndroid/app/src/main/java/kz/pult/touchdisplay/MainActivity.kt')
text = path.read_text(encoding='utf-8')

text = text.replace('import android.os.Bundle\n', 'import android.os.Bundle\nimport android.media.AudioAttributes\nimport android.media.AudioFormat\nimport android.media.AudioTrack\nimport android.util.Log\n')
text = text.replace('private const val DISCOVERY_PORT = 59431', 'private const val DISCOVERY_PORT = 59431\n        private const val AUDIO_PORT = 59434')
text = text.replace('text = "TouchDisplay v1.7"', 'text = "TouchDisplay v2.0"')
text = text.replace('text = "Введите только пароль\\nПК найдётся автоматически"', 'text = "Введите только пароль\\nDisplay 2 • Touch • Audio"')

text = text.replace('    private var socket: Socket? = null\n', '    private var socket: Socket? = null\n    private var audioSocket: Socket? = null\n')
text = text.replace('                startSession(connection)\n', '                startSession(connection, password)\n')
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
                    receiveBufferSize = 256 * 1024
                    connect(InetSocketAddress(host, AUDIO_PORT), 3000)
                }
                audioSocket = s

                val din = DataInputStream(BufferedInputStream(s.getInputStream(), 256 * 1024))
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
                val bufferBytes = maxOf(minBuffer, sampleRate * channels * 2 / 5)

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

old_close = '''        try { socket?.close() } catch (_: Exception) { }
        socket = null
'''
new_close = '''        try { socket?.close() } catch (_: Exception) { }
        try { audioSocket?.close() } catch (_: Exception) { }
        socket = null
        audioSocket = null
'''
if old_close not in text:
    raise SystemExit('audio close marker not found')
text = text.replace(old_close, new_close)

path.write_text(text, encoding='utf-8')
print('TouchDisplay Android v2.0 audio patch applied')
