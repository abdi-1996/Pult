using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;

namespace TouchDisplayHost;

internal sealed class AudioLoopbackServer
{
    private readonly int _port;
    private readonly string _password;
    private readonly Action<string, bool> _status;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public AudioLoopbackServer(int port, string password, Action<string, bool> status)
    {
        _port = port;
        _password = password;
        _status = status;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                client.SendBufferSize = 128 * 1024;
                _ = HandleClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _status("Audio server: " + ex.Message, true);
                try { await Task.Delay(300, token); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var magic = new byte[4];
                await ReadExactAsync(stream, magic, serverToken);
                if (Encoding.ASCII.GetString(magic) != "TDA2") return;

                var passLen = await ReadInt32BigEndianAsync(stream, serverToken);
                if (passLen < 1 || passLen > 64) return;
                var passBytes = new byte[passLen];
                await ReadExactAsync(stream, passBytes, serverToken);
                var supplied = Encoding.UTF8.GetString(passBytes);
                var ok = string.Equals(supplied, _password, StringComparison.Ordinal);
                await stream.WriteAsync(new[] { ok ? (byte)1 : (byte)0 }, serverToken);
                await stream.FlushAsync(serverToken);
                if (!ok) return;

                await WriteInt32BigEndianAsync(stream, 48000, serverToken);
                await stream.WriteAsync(new byte[] { 2, 16 }, serverToken);
                await stream.FlushAsync(serverToken);

                using var capture = new WasapiLoopbackCapture();
                var buffered = new BufferedWaveProvider(capture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                    BufferDuration = TimeSpan.FromMilliseconds(120)
                };
                capture.DataAvailable += (_, e) =>
                {
                    try { buffered.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
                };

                using var resampler = new MediaFoundationResampler(buffered, new WaveFormat(48000, 16, 2))
                {
                    // Lower resampler work/latency than the previous quality-50 path;
                    // still more than enough for remote desktop audio.
                    ResamplerQuality = 30
                };

                capture.StartRecording();
                _status("Audio v4.1: подключено • 48 kHz stereo • 10 ms packets", false);

                var pcm = new byte[1920]; // 10 ms at 48 kHz stereo PCM16
                while (!serverToken.IsCancellationRequested && client.Connected)
                {
                    var read = resampler.Read(pcm, 0, pcm.Length);
                    if (read <= 0)
                    {
                        await Task.Delay(2, serverToken);
                        continue;
                    }

                    await WriteInt32BigEndianAsync(stream, read, serverToken);
                    await stream.WriteAsync(pcm.AsMemory(0, read), serverToken);
                    // NetworkStream itself is unbuffered; no FlushAsync here avoids
                    // an unnecessary await on every 10 ms audio packet.
                }

                try { capture.StopRecording(); } catch { }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _status("Audio: " + ex.Message, true);
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task<int> ReadInt32BigEndianAsync(Stream stream, CancellationToken token)
    {
        var b = new byte[4];
        await ReadExactAsync(stream, b, token);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static async Task WriteInt32BigEndianAsync(Stream stream, int value, CancellationToken token)
    {
        var b = new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        await stream.WriteAsync(b, token);
    }
}
