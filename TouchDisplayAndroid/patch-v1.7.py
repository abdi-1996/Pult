from pathlib import Path

path = Path('TouchDisplayAndroid/app/src/main/java/kz/pult/touchdisplay/MainActivity.kt')
text = path.read_text(encoding='utf-8')

text = text.replace('private const val DISCOVERY_REQUEST = "TDISCOVER14"', 'private const val DISCOVERY_REQUEST = "TDISCOVER17"')
text = text.replace('private const val DISCOVERY_RESPONSE = "TDHOST14"', 'private const val DISCOVERY_RESPONSE = "TDHOST17"')
text = text.replace('text = "TouchDisplay v1.5"', 'text = "TouchDisplay v1.7"')
text = text.replace('inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD', 'inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD')

old_value = '''                val value = password.text.toString().trim()
                if (value.isBlank()) {
                    Toast.makeText(this@MainActivity, "Введите пароль", Toast.LENGTH_SHORT).show()
                } else {
                    isEnabled = false
                    text = "Поиск ПК…"
                    connectWithPassword(value)
                }
'''
new_value = '''                val value = normalizePassword(password.text.toString())
                if (value.length != 6) {
                    Toast.makeText(this@MainActivity, "Введите 6 цифр пароля", Toast.LENGTH_SHORT).show()
                } else {
                    isEnabled = false
                    text = "Поиск ПК…"
                    connectWithPassword(value)
                }
'''
if old_value not in text:
    raise SystemExit('login value block not found')
text = text.replace(old_value, new_value)

text = text.replace('val discovered = discoverHost()', 'val discovered = discoverHost(password)')
text = text.replace('private fun discoverHost(): HostInfo?', 'private fun discoverHost(password: String): HostInfo?')
text = text.replace('val payload = DISCOVERY_REQUEST.toByteArray(Charsets.US_ASCII)', 'val payload = "$DISCOVERY_REQUEST|${normalizePassword(password)}".toByteArray(Charsets.UTF_8)')

old_candidates = '''                val candidates = LinkedHashSet<Pair<String, Int>>()
                val discovered = discoverHost(password)
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
'''
new_candidates = '''                val candidates = LinkedHashSet<Pair<String, Int>>()
                val discovered = discoverHost(password)
                if (discovered != null) {
                    // On LAN, trust only a v1.7 Host that already proved the entered password.
                    candidates.add(discovered.lanAddress to discovered.port)
                    discovered.tailscaleAddress?.takeIf { it.isNotBlank() }?.let {
                        candidates.add(it to discovered.port)
                    }
                } else {
                    // Outside home UDP discovery is unavailable, so use the last successfully paired PC.
                    val savedPort = prefs.getInt("last_port", TCP_PORT)
                    prefs.getString("last_tail", null)?.takeIf { !it.isNullOrBlank() }?.let {
                        candidates.add(it to savedPort)
                    }
                    prefs.getString("last_lan", null)?.takeIf { !it.isNullOrBlank() }?.let {
                        candidates.add(it to savedPort)
                    }
                }
'''
if old_candidates not in text:
    raise SystemExit('candidate block not found')
text = text.replace(old_candidates, new_candidates)

old_connection = '''                val connection = opened ?: throw IOException(lastError ?: "Не удалось подключиться к ПК")
                startSession(connection)
'''
new_connection = '''                val connection = opened ?: throw IOException(lastError ?: "Не удалось подключиться к ПК")
                if (discovered != null) {
                    prefs.edit()
                        .putString("last_lan", discovered.lanAddress)
                        .putString("last_tail", discovered.tailscaleAddress ?: "")
                        .putInt("last_port", discovered.port)
                        .apply()
                }
                startSession(connection)
'''
if old_connection not in text:
    raise SystemExit('connection block not found')
text = text.replace(old_connection, new_connection)

insert_marker = '    private fun connectWithPassword(password: String) {'
normalize = '''    private fun normalizePassword(value: String): String {
        val out = StringBuilder()
        for (ch in value.trim()) {
            val digit = Character.digit(ch, 10)
            if (digit in 0..9) out.append(('0'.code + digit).toChar())
        }
        return out.toString()
    }

'''
if insert_marker not in text:
    raise SystemExit('normalize insertion marker not found')
text = text.replace(insert_marker, normalize + insert_marker)

path.write_text(text, encoding='utf-8')
print('TouchDisplay Android v1.7 patch applied')
