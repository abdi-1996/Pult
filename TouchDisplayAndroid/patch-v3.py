from pathlib import Path

path = Path('TouchDisplayAndroid/app/src/main/java/kz/pult/touchdisplay/MainActivity.kt')
text = path.read_text(encoding='utf-8')
text = text.replace('TouchDisplay v2.1', 'TouchDisplay v3.0')
text = text.replace('Введите пароль\nLAN / Tailscale • Touch • Audio', 'Введите пароль\nAndroid • LAN / Tailscale • Touch • Audio')
path.write_text(text, encoding='utf-8')
print('TouchDisplay Android v3.0 branding applied')
