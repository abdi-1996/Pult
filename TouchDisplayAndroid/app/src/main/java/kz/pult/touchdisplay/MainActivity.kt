package kz.pult.touchdisplay

import android.app.Activity
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.LinkedHashSet
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.min

class MainActivity : Activity() {
    companion object {
        private const val TCP_PORT = 59432
        private const val DISCOVERY_PORT = 59431
        private const val DISCOVERY_REQUEST = "TDISCOVER14"
        private const val DISCOVERY_RESPONSE = "TDHOST14"
    }

    private data class HostInfo(
        val lanAddress: String,
        val port: Int,
        val tailscaleAddress: String?
    )

    private data class OpenConnection(
        val socket: Socket,
        val input: DataInputStream,
        val output: DataOutputStream,
        val host: String
    )

    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    private var remoteView: RemoteView? = null
    private val touchExecutor = Executors.newSingleThreadExecutor()
    private val latestFrame = AtomicReference<ByteArray?>(null)
    private val prefs by lazy { getSharedPreferences("touchdisplay_v14", Context.MODE_PRIVATE) }
    @Volatile private var connected = false
    @Volatile private var decoderRunning = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        enterImmersive()
        showConnectScreen()
    }

    private fun enterImmersive() {
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            )
    }

    private fun showConnectScreen(message: String? = null) {
        connected = false
        decoderRunning = false
        latestFrame.set(null)
        enterImmersive()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(48, 32, 48, 32)
            setBackgroundColor(Color.rgb(15, 18, 24))
        }

        val title = TextView(this).apply {
            text = "TouchDisplay v1.4"
            textSize = 30f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        root.addView(title, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = 16 })

        val subtitle = TextView(this).apply {
            text = "Введите только пароль\nПК найдётся автоматически"
            textSize = 17f
            setTextColor(Color.LTGRAY)
            gravity = Gravity.CENTER
        }
        root.addView(subtitle, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = 24 })

        val password = EditText(this).apply {
            hint = "Пароль из TouchDisplay Host"
            setHintTextColor(Color.GRAY)
            setTextColor(Color.WHITE)
            textSize = 18f
            setSingleLine(true)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setBackgroundColor(Color.rgb(35, 40, 50))
            setPadding(22, 8, 22, 8)
        }
        root.addView(password, LinearLayout.LayoutParams(620, 62).apply { bottomMargin = 14 })

        val connect = Button(this).apply {
            text = "ВОЙТИ"
            textSize = 17f
            setOnClickListener {
                val value = password.text.toString().trim()
                if (value.isBlank()) {
                    Toast.makeText(this@MainActivity, "Введите пароль", Toast.LENGTH_SHORT).show()
                } else {
                    isEnabled = false
                    text = "Поиск ПК…"
                    connectWithPassword(value)
                }
            }
        }
        root.addView(connect, LinearLayout.LayoutParams(520, -2))

        val status = TextView(this).apply {
            text = message ?: if (prefs.contains("last_lan") || prefs.contains("last_tail")) {
                "LAN / Wi‑Fi / Tailscale • сохранённый ПК"
            } else {
                "Первый вход: планшет и ПК должны быть в одной Wi‑Fi сети"
            }
            textSize = 14f
            setTextColor(if (message == null) Color.GRAY else Color.rgb(255, 180, 100))
            gravity = Gravity.CENTER
        }
        root.addView(status, LinearLayout.LayoutParams(-1, -2).apply { topMargin = 18 })
        setContentView(root)
    }

    private fun connectWithPassword(password: String) {
        Thread {
            var lastError: String? = null
            try {
                closeConnection()

                val candidates = LinkedHashSet<Pair<String, Int>>()

                // Discover first when we are at home. This also refreshes a changed DHCP address.
                val discovered = discoverHost()
                if (discovered != null) {
                    candidates.add(discovered.lanAddress to discovered.port)
                    discovered.tailscaleAddress?.takeIf { it.isNotBlank() }?.let {
                        candidates.add(it to discovered.port)
                    }
                    prefs.edit()
                        .putString("last_lan", discovered.lanAddress)
                        .putString("last_tail", discovered.tailscaleAddress ?: "")
                        .putInt("last_port", discovered.port)
                        .apply()
                }

                val savedPort = prefs.getInt("last_port", TCP_PORT)
                prefs.getString("last_lan", null)?.takeIf { !it.isNullOrBlank() }?.let {
                    candidates.add(it to savedPort)
                }
                prefs.getString("last_tail", null)?.takeIf { !it.isNullOrBlank() }?.let {
                    candidates.add(it to savedPort)
                }

                if (candidates.isEmpty()) {
                    throw IOException("ПК не найден. Для первого входа подключите планшет и ПК к одной Wi‑Fi сети.")
                }

                var opened: OpenConnection? = null
                for ((host, port) in candidates) {
                    try {
                        opened = openConnection(host, port, password)
                        break
                    } catch (e: Exception) {
                        lastError = e.message ?: e.javaClass.simpleName
                    }
                }

                val connection = opened ?: throw IOException(lastError ?: "Не удалось подключиться к ПК")
                startSession(connection)
            } catch (e: Exception) {
                val msg = e.message ?: lastError ?: e.javaClass.simpleName
                closeConnection()
                runOnUiThread { showConnectScreen("Ошибка подключения: $msg") }
            }
        }.start()
    }

    private fun openConnection(host: String, port: Int, password: String): OpenConnection {
        val s = Socket()
        try {
            s.tcpNoDelay = true
            s.keepAlive = true
            s.receiveBufferSize = 2 * 1024 * 1024
            s.sendBufferSize = 64 * 1024
            s.connect(InetSocketAddress(host, port), 2200)

            val din = DataInputStream(BufferedInputStream(s.getInputStream(), 2 * 1024 * 1024))
            val dout = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 64 * 1024))

            val passwordBytes = password.toByteArray(Charsets.UTF_8)
            dout.write("TD01".toByteArray(Charsets.US_ASCII))
            dout.writeInt(passwordBytes.size)
            dout.write(passwordBytes)
            dout.flush()

            val accepted = din.readUnsignedByte()
            if (accepted != 1) throw IOException("Неверный пароль")
            return OpenConnection(s, din, dout, host)
        } catch (e: Exception) {
            try { s.close() } catch (_: Exception) { }
            throw e
        }
    }

    private fun startSession(connection: OpenConnection) {
        socket = connection.socket
        input = connection.input
        output = connection.output
        connected = true
        latestFrame.set(null)

        runOnUiThread {
            val view = RemoteView()
            remoteView = view
            setContentView(view)
            enterImmersive()
        }

        startDecoder()
        readFramesFast(connection.input)
    }

    private fun startDecoder() {
        decoderRunning = true
        Thread {
            while (connected && decoderRunning) {
                val bytes = latestFrame.getAndSet(null)
                if (bytes == null) {
                    try { Thread.sleep(2) } catch (_: InterruptedException) { }
                    continue
                }

                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                if (bitmap != null) {
                    remoteView?.setFrame(bitmap)
                }
            }
        }.apply {
            name = "TouchDisplayDecoder"
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    private fun readFramesFast(din: DataInputStream) {
        try {
            while (connected) {
                val length = din.readInt()
                if (length <= 0 || length > 24_000_000) throw IOException("Некорректный кадр")
                val bytes = ByteArray(length)
                din.readFully(bytes)
                // Never let decode/render create a queue. Keep only the newest frame.
                latestFrame.set(bytes)
            }
        } catch (e: Exception) {
            if (connected) throw e
        }
    }

    private fun discoverHost(): HostInfo? {
        var udp: DatagramSocket? = null
        return try {
            udp = DatagramSocket().apply {
                broadcast = true
                soTimeout = 220
            }

            val payload = DISCOVERY_REQUEST.toByteArray(Charsets.US_ASCII)
            val destinations = LinkedHashSet<InetAddress>()
            destinations.add(InetAddress.getByName("255.255.255.255"))

            try {
                val interfaces = NetworkInterface.getNetworkInterfaces()
                while (interfaces.hasMoreElements()) {
                    val networkInterface = interfaces.nextElement()
                    if (!networkInterface.isUp || networkInterface.isLoopback) continue
                    for (address in networkInterface.interfaceAddresses) {
                        address.broadcast?.let { destinations.add(it) }
                    }
                }
            } catch (_: Exception) { }

            for (destination in destinations) {
                try {
                    udp.send(DatagramPacket(payload, payload.size, destination, DISCOVERY_PORT))
                } catch (_: Exception) { }
            }

            val deadline = System.currentTimeMillis() + 850
            val buffer = ByteArray(1024)
            while (System.currentTimeMillis() < deadline) {
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    udp.receive(packet)
                    val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                    val parts = text.split('|')
                    if (parts.size >= 3 && parts[0] == DISCOVERY_RESPONSE) {
                        val port = parts[1].toIntOrNull() ?: TCP_PORT
                        val tailscale = parts[2].takeIf { it.isNotBlank() }
                        return HostInfo(packet.address.hostAddress ?: return null, port, tailscale)
                    }
                } catch (_: SocketTimeoutException) {
                    // Keep listening until the short discovery deadline expires.
                }
            }
            null
        } catch (_: Exception) {
            null
        } finally {
            try { udp?.close() } catch (_: Exception) { }
        }
    }

    private fun sendTouch(action: Int, pointerId: Int, nx: Float, ny: Float) {
        if (!connected) return
        touchExecutor.execute {
            try {
                val dout = output ?: return@execute
                synchronized(dout) {
                    dout.writeByte(0x10)
                    dout.writeByte(action)
                    dout.writeInt(pointerId)
                    dout.writeFloat(nx)
                    dout.writeFloat(ny)
                    dout.flush()
                }
            } catch (_: Exception) {
                // Do not destroy the visible session because a touch write failed once.
                // The video reader will close the session if the connection is actually gone.
            }
        }
    }

    @Synchronized
    private fun closeConnection() {
        connected = false
        decoderRunning = false
        latestFrame.set(null)
        try { socket?.close() } catch (_: Exception) { }
        socket = null
        input = null
        output = null
    }

    override fun onBackPressed() {
        if (connected) {
            closeConnection()
            showConnectScreen()
        } else {
            super.onBackPressed()
        }
    }

    override fun onDestroy() {
        closeConnection()
        touchExecutor.shutdownNow()
        super.onDestroy()
    }

    inner class RemoteView : View(this@MainActivity) {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
        private val frameRect = RectF()
        @Volatile private var frame: Bitmap? = null

        init {
            setBackgroundColor(Color.BLACK)
            isFocusable = true
            isFocusableInTouchMode = true
        }

        fun setFrame(bitmap: Bitmap) {
            val previous = frame
            frame = bitmap
            if (previous != null && previous !== bitmap && !previous.isRecycled) {
                // Let the GC reclaim old native bitmap memory without accumulating many frames.
            }
            postInvalidateOnAnimation()
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            val bmp = frame ?: return
            val scale = min(width.toFloat() / bmp.width.toFloat(), height.toFloat() / bmp.height.toFloat())
            val w = bmp.width * scale
            val h = bmp.height * scale
            val left = (width - w) / 2f
            val top = (height - h) / 2f
            frameRect.set(left, top, left + w, top + h)
            canvas.drawBitmap(bmp, null, frameRect, paint)
        }

        override fun onTouchEvent(event: MotionEvent): Boolean {
            if (frameRect.isEmpty) return true
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                    val i = event.actionIndex
                    sendPointer(0, event, i)
                }
                MotionEvent.ACTION_MOVE -> {
                    for (i in 0 until event.pointerCount) sendPointer(1, event, i)
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                    val i = event.actionIndex
                    sendPointer(2, event, i)
                    if (event.actionMasked == MotionEvent.ACTION_UP) performClick()
                }
                MotionEvent.ACTION_CANCEL -> {
                    for (i in 0 until event.pointerCount) sendPointer(3, event, i)
                }
            }
            return true
        }

        private fun sendPointer(action: Int, event: MotionEvent, index: Int) {
            val nx = ((event.getX(index) - frameRect.left) / frameRect.width()).coerceIn(0f, 1f)
            val ny = ((event.getY(index) - frameRect.top) / frameRect.height()).coerceIn(0f, 1f)
            sendTouch(action, event.getPointerId(index), nx, ny)
        }

        override fun performClick(): Boolean {
            super.performClick()
            return true
        }
    }
}
