"""
tcp_client_viewer.py
====================
Cliente TCP equivalente a udp_client_viewer.py.

Diferencias clave respecto a la versión UDP:
  - TCP es orientado a conexión: el cliente conecta una vez y mantiene
    la sesión abierta durante todo el streaming.
  - No hay límite de 65 KB por mensaje: soporta imágenes de alta resolución.
  - Entrega garantizada y ordenada: no hay pérdida de frames.
  - Se usa un prefijo de 4 bytes (big-endian) para delimitar cada mensaje
    (framing), ya que TCP es un protocolo de stream sin fronteras de mensaje.

Protocolo:
  REQUEST  (cliente → servidor): [4 bytes: longitud][string UTF-8]
  RESPONSE (servidor → cliente): [4 bytes: longitud][bytes JPEG]
                                  longitud = 0 → no hay frame todavía.

Uso:
    python tcp_client_viewer.py -w 360 -ht 240 -q 70 -fps 10
    python tcp_client_viewer.py -ip 10.50.33.6 -w 640 -ht 480 -q 85 -fps 15
"""

import argparse
import socket
import struct
import time

import cv2
import numpy as np


# ── Argumentos ───────────────────────────────────────────────────────────────
def parse_args():
    parser = argparse.ArgumentParser(
        description="TCP JPEG snapshot client para Unity"
    )
    parser.add_argument("-ip",   default="127.0.0.1", help="IP del servidor (default: 127.0.0.1)")
    parser.add_argument("-port", type=int, default=5000, help="Puerto TCP (default: 5000)")
    parser.add_argument("-w",    type=int, default=1280, help="Ancho en píxeles (default: 1280)")
    parser.add_argument("-ht",   type=int, default=720,  help="Alto en píxeles (default: 720)")
    parser.add_argument("-q",    type=int, default=70,   help="Calidad JPEG 1-100 (default: 70)")
    parser.add_argument("-fps",  type=int, default=10,   help="FPS objetivo (default: 10)")
    return parser.parse_args()


# ── Framing: enviar mensaje con prefijo de longitud ──────────────────────────
def send_framed(sock: socket.socket, data: bytes):
    """Envía [4 bytes big-endian longitud][datos]."""
    header = struct.pack(">I", len(data))  # big-endian unsigned int
    sock.sendall(header + data)


# ── Framing: recibir mensaje con prefijo de longitud ─────────────────────────
def recv_exact(sock: socket.socket, n: int) -> bytes:
    """Lee exactamente n bytes del socket TCP."""
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("Conexión cerrada por el servidor.")
        buf += chunk
    return buf


def recv_framed(sock: socket.socket) -> bytes:
    """Recibe [4 bytes longitud][datos] y devuelve solo los datos."""
    header = recv_exact(sock, 4)
    length = struct.unpack(">I", header)[0]
    if length == 0:
        return b""  # no frame disponible
    return recv_exact(sock, length)


# ── Construir el string de request ───────────────────────────────────────────
def build_request(w: int, ht: int, q: int, fps: int, move: str = "0") -> bytes:
    return f"w={w};h={ht};q={q};fps={fps};move={move}".encode("utf-8")


# ── Detectar tecla WASD ───────────────────────────────────────────────────────
def key_to_move(key: int) -> str:
    return {ord('w'): "W", ord('a'): "A",
            ord('s'): "S", ord('d'): "D"}.get(key, "0")


# ── Overlay con estadísticas ──────────────────────────────────────────────────
def draw_overlay(img: np.ndarray, size_bytes: int,
                 display_fps: float, move: str) -> np.ndarray:
    overlay   = img.copy()
    font      = cv2.FONT_HERSHEY_SIMPLEX
    scale     = 0.55
    color     = (0, 255, 0)
    shadow    = (0, 0, 0)
    move_lbl  = move if move != "0" else "–"
    lines = [
        f"Size: {size_bytes} bytes",
        f"FPS:  {display_fps:.1f}",
        f"Move: {move_lbl}  (WASD para mover)",
        "Protocolo: TCP",
    ]
    for i, line in enumerate(lines):
        y = 22 + i * 22
        cv2.putText(overlay, line, (9, y+1), font, scale, shadow, 2, cv2.LINE_AA)
        cv2.putText(overlay, line, (8, y),   font, scale, color,  1, cv2.LINE_AA)
    return overlay


# ── Bucle principal ───────────────────────────────────────────────────────────
def main():
    args       = parse_args()
    TARGET_FPS = max(1, args.fps)
    SLEEP_S    = 1.0 / TARGET_FPS

    print(f"Conectando (TCP) a {args.ip}:{args.port} ...")
    print(f"Parámetros → w={args.w} h={args.ht} q={args.q} fps={args.fps}")
    print("Teclas: W/A/S/D para mover la cámara, Q para salir.")

    current_move = "0"
    fps_count    = 0
    fps_timer    = time.time()
    disp_fps     = 0.0

    while True:
        # ── Conectar al servidor ──────────────────────────────────────────────
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(5.0)
        try:
            sock.connect((args.ip, args.port))
            print(f"Conectado a {args.ip}:{args.port}")
        except (ConnectionRefusedError, socket.timeout) as e:
            print(f"No se pudo conectar ({e}). Reintentando en 2 s...")
            sock.close()
            time.sleep(2)
            continue

        sock.settimeout(None)  # sin timeout tras conectar

        # ── Bucle de streaming ────────────────────────────────────────────────
        try:
            while True:
                frame_start = time.time()

                # 1) Enviar request con framing
                payload = build_request(args.w, args.ht, args.q, args.fps, current_move)
                send_framed(sock, payload)

                # 2) Recibir respuesta con framing
                data = recv_framed(sock)

                if not data:
                    print("Servidor: no hay frame todavía.")
                    time.sleep(SLEEP_S)
                    continue

                # 3) Decodificar JPEG
                arr = np.frombuffer(data, dtype=np.uint8)
                img = cv2.imdecode(arr, cv2.IMREAD_COLOR)

                if img is None:
                    print(f"Decodificación fallida ({len(data)} bytes).")
                    time.sleep(SLEEP_S)
                    continue

                # 4) Actualizar FPS counter
                fps_count += 1
                elapsed = time.time() - fps_timer
                if elapsed >= 1.0:
                    disp_fps  = fps_count / elapsed
                    fps_count = 0
                    fps_timer = time.time()

                # 5) Mostrar frame con overlay
                frame = draw_overlay(img, len(data), disp_fps, current_move)
                cv2.imshow("TCP JPEG Snapshot", frame)

                # 6) Leer teclas
                key = cv2.waitKey(1) & 0xFF
                if key == ord('q'):
                    raise KeyboardInterrupt
                elif key in (ord('w'), ord('a'), ord('s'), ord('d')):
                    current_move = key_to_move(key)
                elif key != 255:
                    current_move = "0"

                # 7) Controlar cadencia
                sleep_time = SLEEP_S - (time.time() - frame_start)
                if sleep_time > 0:
                    time.sleep(sleep_time)

        except KeyboardInterrupt:
            print("Saliendo...")
            break
        except (ConnectionError, OSError) as e:
            print(f"Conexión perdida: {e}. Reconectando en 2 s...")
            time.sleep(2)
        finally:
            sock.close()

    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
