package kz.pult.touchdisplay

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.Bundle
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
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors
import kotlin.math.min

class MainActivity : Activity() {
    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    private var remoteView: RemoteView? = null
    private val touchExecutor = Executors.newSingleThreadExecutor()
    @Volatile private var connected = false

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
        enterImmersive()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(48, 32, 48, 32)
            setBackgroundColor(Color.rgb(15, 18, 24))
        }

        val title = TextView(this).apply {
            text = "TouchDisplay v1"
            textSize = 30f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        root.addView(title, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = 20 })

        val subtitle = TextView(this).apply {
            text = "Планшет как сенсорный экран Windows\nLAN / Wi‑Fi / Tailscale"
            textSize = 16f
            setTextColor(Color.LTGRAY)
            gravity = Gravity.CENTER
        }
        root.addView(subtitle, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = 24 })

        val host = field("IP или Tailscale-имя ПК", "")
        val port = field("Порт", "59432")
        val pin = field("PIN из TouchDisplay Host", "")
        root.addView(host, fieldParams())
        root.addView(port, fieldParams())
        root.addView(pin, fieldParams())

        val connect = Button(this).apply {
            text = "ПОДКЛЮЧИТЬСЯ"
            textSize = 17f
            setOnClickListener {
                val h = host.text.toString().trim()
                val p = port.text.toString().toIntOrNull() ?: 59432
                val code = pin.text.toString().trim()
                if (h.isBlank() || code.isBlank()) {
                    Toast.makeText(this@MainActivity, "Введите адрес ПК и PIN", Toast.LENGTH_SHORT).show()
                } else {
                    isEnabled = false
                    text = "Подключение…"
                    connect(h, p, code)
                }
            }
        }
        root.addView(connect, LinearLayout.LayoutParams(520, -2).apply { topMargin = 16 })

        val status = TextView(this).apply {
            text = message ?: "Для удалённого доступа укажите адрес Tailscale 100.x.x.x или MagicDNS-имя."
            textSize = 14f
            setTextColor(if (message == null) Color.GRAY else Color.rgb(255, 180, 100))
            gravity = Gravity.CENTER
        }
        root.addView(status, LinearLayout.LayoutParams(-1, -2).apply { topMargin = 18 })
        setContentView(root)
    }

    private fun field(hint: String, value: String): EditText = EditText(this).apply {
        this.hint = hint
        setHintTextColor(Color.GRAY)
        setTextColor(Color.WHITE)
        setText(value)
        textSize = 17f
        setSingleLine(true)
        setBackgroundColor(Color.rgb(35, 40, 50))
        setPadding(22, 8, 22, 8)
    }

    private fun fieldParams() = LinearLayout.LayoutParams(620, 58).apply { bottomMargin = 10 }

    private fun connect(host: String, port: Int, pin: String) {
        Thread {
            try {
                closeConnection()
                val s = Socket()
                s.tcpNoDelay = true
                s.keepAlive = true
                s.connect(InetSocketAddress(host, port), 7000)
                val din = DataInputStream(BufferedInputStream(s.getInputStream(), 512 * 1024))
                val dout = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 64 * 1024))

                val pinBytes = pin.toByteArray(Charsets.UTF_8)
                dout.write("TD01".toByteArray(Charsets.US_ASCII))
                dout.writeInt(pinBytes.size)
                dout.write(pinBytes)
                dout.flush()

                val accepted = din.readUnsignedByte()
                if (accepted != 1) throw IOException("Неверный PIN")

                socket = s
                input = din
                output = dout
                connected = true

                runOnUiThread {
                    val view = RemoteView()
                    remoteView = view
                    setContentView(view)
                    enterImmersive()
                }

                readFrames(din)
            } catch (e: Exception) {
                val msg = e.message ?: e.javaClass.simpleName
                closeConnection()
                runOnUiThread { showConnectScreen("Ошибка подключения: $msg") }
            }
        }.start()
    }

    private fun readFrames(din: DataInputStream) {
        while (connected) {
            val length = din.readInt()
            if (length <= 0 || length > 20_000_000) throw IOException("Некорректный кадр")
            val bytes = ByteArray(length)
            din.readFully(bytes)
            val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                ?: throw IOException("Не удалось декодировать кадр")
            remoteView?.setFrame(bitmap)
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
                if (connected) {
                    closeConnection()
                    runOnUiThread { showConnectScreen("Соединение потеряно") }
                }
            }
        }
    }

    @Synchronized
    private fun closeConnection() {
        connected = false
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
            frame = bitmap
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
