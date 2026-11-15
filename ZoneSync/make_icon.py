# -*- coding: utf-8 -*-
# Генератор sync.ico: две круговые стрелки (символ синхронизации).
# Без PIL: рисованные пиксели (суперсэмплинг) + сборка ICO вручную.
import math, struct, zlib, os

OUT_DIR = r"D:\Work\ZoneSync\res"

GREEN = (46, 160, 67)    # верхняя дуга
BLUE  = (47, 114, 214)   # нижняя дуга

def clamp(v, lo, hi):
    return lo if v < lo else (hi if v > hi else v)

def render(size):
    """Возвращает (list of (r,g,b,a) по пикселям, row-major)."""
    buf = [0.0] * (size * size * 4)
    cx = cy = size / 2.0
    r = size * 0.36          # радиус кольца
    t = size * 0.115         # толщина дуги
    ah = size * 0.26         # длина стрелки
    ahw = size * 0.16        # полуширина основания стрелки

    # Две дуги (по часовой, экранные координаты: y вниз):
    # верхняя: от -160° до -20°  (зелёная), стрелка на -20°
    # нижняя:  от  20° до 160°   (синяя), стрелка на 160°
    arcs = [
        (math.radians(-160), math.radians(-20), GREEN),
        (math.radians(20),   math.radians(160), BLUE),
    ]
    heads = []
    for a0, a1, col in arcs:
        heads.append((a1, col))

    ss = 3  # суперсэмплинг 3x3
    for py in range(size):
        for px in range(size):
            cr = cg = cb = ca = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    x = px + (sx + 0.5) / ss
                    y = py + (sy + 0.5) / ss
                    dx = x - cx
                    dy = y - cy
                    dist = math.hypot(dx, dy)
                    # угол: 0° = вправо, растёт по часовой (y вниз)
                    ang = math.atan2(dy, dx)
                    hit = False
                    col = None
                    for a0, a1, c in arcs:
                        # дуга: радиус в кольце и угол в диапазоне
                        if abs(dist - r) <= t / 2.0:
                            # нормализация угла в [a0..a1] (a1 > a0, <= 180°)
                            a = ang
                            if a < a0:
                                a += 2 * math.pi
                            if a0 <= a <= a1:
                                hit = True
                                col = c
                    if not hit:
                        # стрелки: треугольник на конце дуги,
                        # направление касательной (по часовой) = a1 + 90°
                        for a1, c in heads:
                            # конец дуги
                            ex = cx + r * math.cos(a1)
                            ey = cy + r * math.sin(a1)
                            tdir = a1 + math.pi / 2.0
                            # вершины треугольника: основание на конце дуги,
                            # вершина вперёд по касательной
                            nx, ny = math.cos(tdir), math.sin(tdir)
                            pxv, pyv = ex + nx * ah, ey + ny * ah          # остриё
                            perp = tdir + math.pi / 2.0
                            b1x = ex - math.cos(perp) * ahw; b1y = ey - math.sin(perp) * ahw
                            b2x = ex + math.cos(perp) * ahw; b2y = ey + math.sin(perp) * ahw
                            # point-in-triangle
                            d1 = (x - b1x) * (pyv - b1y) - (y - b1y) * (pxv - b1x)
                            d2 = (x - pxv) * (b2y - pyv) - (y - pyv) * (b2x - pxv)
                            d3 = (x - b2x) * (b1y - b2y) - (y - b2y) * (b1x - b2x)
                            neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
                            pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
                            if not (neg and pos):
                                hit = True
                                col = c
                    if hit and col:
                        cr += col[0]; cg += col[1]; cb += col[2]; ca += 255.0
            n = ss * ss
            i = (py * size + px) * 4
            buf[i]   = clamp(cr / n, 0, 255)
            buf[i+1] = clamp(cg / n, 0, 255)
            buf[i+2] = clamp(cb / n, 0, 255)
            buf[i+3] = clamp(ca / n, 0, 255)
    return buf

def write_png(path, size, buf):
    """PNG RGBA из буфера (buf = BGRA-подобный порядок (r,g,b,a))."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter: None
        row = y * size * 4
        for x in range(size):
            i = row + x * 4
            raw.append(int(buf[i]))
            raw.append(int(buf[i+1]))
            raw.append(int(buf[i+2]))
            raw.append(int(buf[i+3]))
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", ihdr)
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)

def bmp_entry(size, buf):
    """32bpp BITMAP (bottom-up) + AND mask для ICO-каталога."""
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            i = (y * size + x) * 4
            xor.append(int(buf[i+2]))  # B
            xor.append(int(buf[i+1]))  # G
            xor.append(int(buf[i]))    # R
            xor.append(int(buf[i+3]))  # A
    row = size * 4
    and_row = ((size + 31) // 32) * 4
    andm = bytearray(and_row * size)
    ih = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                     len(xor), 0, 0, 0, 0)
    return ih + bytes(xor) + bytes(andm)

def write_ico(path, sizes, bufs):
    """ICO: 256 = PNG, остальные = BMP."""
    entries = []
    images = []
    for size, buf in zip(sizes, bufs):
        if size == 256:
            raw = bytearray()
            for y in range(size):
                raw.append(0)
                for x in range(size):
                    i = (y * size + x) * 4
                    raw.append(int(buf[i])); raw.append(int(buf[i+1]))
                    raw.append(int(buf[i+2])); raw.append(int(buf[i+3]))
            def chunk(tag, data):
                c = struct.pack(">I", len(data)) + tag + data
                return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
            ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
            png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + \
                  chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
            entries.append((0, 0, 0, png))
        else:
            entries.append((size, size, 0, bmp_entry(size, buf)))
    out = bytearray()
    out += struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    for w, h, c, data in entries:
        out += struct.pack("<BBBBHHII", w & 0xFF, h & 0xFF, c, 0, 1, 32,
                           len(data), offset)
        offset += len(data)
    for _, _, _, data in entries:
        out += data
    open(path, "wb").write(bytes(out))

os.makedirs(OUT_DIR, exist_ok=True)
sizes = [16, 32, 48, 64, 256]
bufs = [render(s) for s in sizes]
write_ico(os.path.join(OUT_DIR, "sync.ico"), sizes, bufs)
write_png(os.path.join(OUT_DIR, "sync_preview.png"), 256, bufs[-1])
print("icon generated:")
for s in sizes:
    print("  {0}x{0}".format(s))
