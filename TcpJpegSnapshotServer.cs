using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/*
 * TcpJpegSnapshotServer
 * ---------------------
 * Versión TCP del servidor de snapshots JPEG.
 *
 * Diferencias clave respecto a la versión UDP:
 *  - TCP es orientado a conexión: el cliente se conecta una vez y mantiene
 *    la sesión abierta durante toda la comunicación.
 *  - TCP es un protocolo de STREAM (no de datagramas): los mensajes no tienen
 *    límite de tamaño ni se fragmentan a nivel de aplicación. Se usa un
 *    prefijo de 4 bytes (big-endian) para indicar la longitud de cada mensaje.
 *  - Garantiza entrega y orden: no hay pérdida de paquetes como en UDP.
 *  - Soporta múltiples clientes simultáneos (un Task por cliente).
 *
 * Protocolo de mensajes (framing):
 *   REQUEST  (cliente → servidor): [4 bytes: longitud][string UTF-8]
 *   RESPONSE (servidor → cliente): [4 bytes: longitud][bytes JPEG]
 *                                   longitud = 0 si no hay frame disponible.
 *
 * Formato del string de request:
 *   w=360;h=240;q=70;fps=10;move=W
 */

public class TcpJpegSnapshotServer : MonoBehaviour
{
    [Header("Dependency (same Camera GameObject)")]
    public JPEGCameraCapturerImproved capturer;

    [Header("Capture update rate")]
    [Range(1, 60)]
    public int fps = 10;

    [Header("TCP Server")]
    public int listenPort = 5000;

    [Header("Movement settings")]
    public float moveSpeed = 5f;

    // ── Shared JPEG buffer ───────────────────────────────────────────────────
    private readonly object _jpegLock  = new object();
    private byte[]          _latestJpeg;
    private bool            _requestInFlight = false;

    // ── Pending capture parameters ───────────────────────────────────────────
    private readonly object _paramLock   = new object();
    private int  _pendingWidth   = -1;
    private int  _pendingHeight  = -1;
    private int  _pendingQuality = -1;
    private int  _pendingFps     = -1;
    private bool _pendingParams  = false;

    // ── Movement ─────────────────────────────────────────────────────────────
    private readonly object _moveLock    = new object();
    private string          _currentMove = "0";

    private TcpListener             _listener;
    private CancellationTokenSource _cts;

    // ── Unity lifecycle ──────────────────────────────────────────────────────
    private void Start()
    {
        if (capturer == null)
            capturer = GetComponent<JPEGCameraCapturerImproved>();

        if (capturer == null)
        {
            Debug.LogError("JPEGCameraCapturerImproved not found.");
            enabled = false;
            return;
        }

        StartCoroutine(FrameProducerLoop());
        StartTcpServer();

        Debug.Log($"TCP JPEG snapshot server listening on 0.0.0.0:{listenPort} (localhost + LAN)");
    }

    // ── WASD movement (main thread) ──────────────────────────────────────────
    private void Update()
    {
        string move;
        lock (_moveLock) { move = _currentMove; _currentMove = "0"; }

        if (move == "0") return;

        Vector3 dir = Vector3.zero;
        switch (move)
        {
            case "W": dir =  transform.forward; break;
            case "S": dir = -transform.forward; break;
            case "A": dir = -transform.right;   break;
            case "D": dir =  transform.right;   break;
        }
        transform.position += dir * moveSpeed * Time.deltaTime;
    }

    // ── Frame producer coroutine (main thread) ───────────────────────────────
    private IEnumerator FrameProducerLoop()
    {
        while (true)
        {
            lock (_paramLock)
            {
                if (_pendingParams)
                {
                    if (_pendingWidth   > 0) capturer.resWidth   = _pendingWidth;
                    if (_pendingHeight  > 0) capturer.resHeight  = _pendingHeight;
                    if (_pendingQuality > 0) capturer.jpgQuality = _pendingQuality;
                    if (_pendingFps     > 0) fps                 = _pendingFps;
                    _pendingParams  = false;
                    _pendingWidth = _pendingHeight = _pendingQuality = _pendingFps = -1;
                }
            }

            var wait = new WaitForSeconds(1f / Mathf.Max(1, fps));

            if (!_requestInFlight)
            {
                _requestInFlight         = true;
                capturer.IsCaptureEnable = true;

                float t0 = Time.realtimeSinceStartup;
                while (capturer.IsCaptureEnable &&
                       (Time.realtimeSinceStartup - t0) < 1f)
                    yield return null;

                if (capturer.IsCaptureEnable)
                {
                    capturer.IsCaptureEnable = false;
                    _requestInFlight         = false;
                    Debug.LogWarning("Capture timed out.");
                    yield return wait;
                    continue;
                }

                _requestInFlight = false;
                var bytes = capturer.jpg;
                if (bytes != null && bytes.Length > 0)
                    lock (_jpegLock) _latestJpeg = bytes;
            }

            yield return wait;
        }
    }

    // ── Start TCP listener (background thread) ───────────────────────────────
    private void StartTcpServer()
    {
        _cts      = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    // ── Accept incoming clients ──────────────────────────────────────────────
    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch
            {
                break; // listener stopped
            }

            // Handle each client in its own Task (supports multiple clients)
            _ = Task.Run(() => HandleClient(client, token));
        }
    }

    // ── Handle a single client session ──────────────────────────────────────
    private async Task HandleClient(TcpClient client, CancellationToken token)
    {
        string ep = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Debug.Log($"[TCP] Client connected: {ep}");

        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    // ── Read request: [4 bytes length][UTF-8 string] ────────
                    byte[] lenBuf = new byte[4];
                    if (await ReadExactAsync(stream, lenBuf, 4, token) < 4) break;

                    int reqLen = IPAddress.NetworkToHostOrder(
                                     BitConverter.ToInt32(lenBuf, 0));

                    if (reqLen <= 0 || reqLen > 4096) break; // sanity check

                    byte[] reqBuf = new byte[reqLen];
                    if (await ReadExactAsync(stream, reqBuf, reqLen, token) < reqLen) break;

                    string req = Encoding.UTF8.GetString(reqBuf);
                    ParseAndStoreParams(req);
                    Debug.Log($"[TCP] {ep}: \"{req}\"");

                    // ── Send response: [4 bytes length][JPEG bytes] ─────────
                    byte[] jpg;
                    lock (_jpegLock) jpg = _latestJpeg;

                    if (jpg == null || jpg.Length == 0)
                    {
                        // Length = 0 signals "no frame yet"
                        byte[] zero = BitConverter.GetBytes(
                                          IPAddress.HostToNetworkOrder(0));
                        await stream.WriteAsync(zero, 0, 4, token);
                    }
                    else
                    {
                        byte[] lenOut = BitConverter.GetBytes(
                                            IPAddress.HostToNetworkOrder(jpg.Length));
                        await stream.WriteAsync(lenOut, 0, 4, token);
                        await stream.WriteAsync(jpg,    0, jpg.Length, token);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TCP] Client {ep} error: {e.Message}");
                    break;
                }
            }
        }

        Debug.Log($"[TCP] Client disconnected: {ep}");
    }

    // ── Helper: read exactly 'count' bytes from stream ───────────────────────
    private async Task<int> ReadExactAsync(NetworkStream stream,
                                            byte[]        buf,
                                            int           count,
                                            CancellationToken token)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf, total, count - total, token);
            if (n == 0) break; // connection closed
            total += n;
        }
        return total;
    }

    // ── Parse "w=360;h=240;q=70;fps=10;move=W" ──────────────────────────────
    private void ParseAndStoreParams(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;

        int    newW = -1, newH = -1, newQ = -1, newFps = -1;
        string newMove = null;

        foreach (string pair in msg.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string key = pair.Substring(0, eq).Trim().ToLowerInvariant();
            string val = pair.Substring(eq + 1).Trim();

            switch (key)
            {
                case "w":   if (int.TryParse(val, out int w)) newW   = w; break;
                case "h":   if (int.TryParse(val, out int h)) newH   = h; break;
                case "q":   if (int.TryParse(val, out int q)) newQ   = q; break;
                case "fps": if (int.TryParse(val, out int f)) newFps = f; break;
                case "move":
                    string mv = val.ToUpperInvariant();
                    if (mv == "W" || mv == "A" || mv == "S" ||
                        mv == "D" || mv == "0") newMove = mv;
                    break;
            }
        }

        if (newW > 0 || newH > 0 || newQ > 0 || newFps > 0)
        {
            lock (_paramLock)
            {
                if (newW   > 0) _pendingWidth   = newW;
                if (newH   > 0) _pendingHeight  = newH;
                if (newQ   > 0) _pendingQuality = newQ;
                if (newFps > 0) _pendingFps     = newFps;
                _pendingParams = true;
            }
        }

        if (newMove != null)
            lock (_moveLock) { _currentMove = newMove; }
    }

    private void OnDestroy()
    {
        try { _cts?.Cancel();    } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
