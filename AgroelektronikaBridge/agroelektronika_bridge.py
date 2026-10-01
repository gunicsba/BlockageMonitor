#!/usr/bin/env python3
"""
Agroelektronika seed monitor -> BlockageMonitor bridge.

Reads the Agroelektronika controller's RS232 stream (9600 8N1, 2-byte frames,
~250 frames/s) and forwards per-row activity to BlockageMonitor as PGN 32100
UDP packets.

Serial frame (see agroelektronika-seed-monitor-protocol.md):
    byte 1: 1 S12 S11 S10 S9 S8 S7 x     (bit 7 = 1 -> first byte)
    byte 2: 0 S6  S5  S4  S3 S2 S1 x     (bit 7 = 0 -> second byte)
    sensors = ((b1 >> 1) & 0x3F) << 6 | ((b2 >> 1) & 0x3F)

PGN 32100 (BlockageMonitor, UDP port 25600):
    [0x64, 0x7D, (row << 4) | module, rate, crc]   crc = sum(bytes 0..3) & 0xFF

BlockageMonitor marks a row as blocked when no PGN 32100 packet (of any rate)
arrives for that row within its "BlockSeconds" setting, and it only redraws a
bar when a packet arrives. So the bridge sends a row's rate when it changes,
repeats an unchanged non-zero rate every --keepalive seconds so a steadily
flowing row does not time out, and sends a single 0 when a sensor goes idle
(no seed pulses in the last --window seconds, or stuck active) to drop the
bar. After that 0 the row stays silent, so it times out and the alarm fires.
Rates are measured over that sliding --window (default 2 s, updated every
--interval) so a single quiet interval does not drop a row to 0.
When the controller stops sending, nothing is forwarded and every row times out.
"""

import argparse
import collections
import random
import socket
import sys
import threading
import time

SENSOR_COUNT = 12
PGN32100_HEADER = (0x64, 0x7D)
DEFAULT_PORT = 25600


# ---------------------------------------------------------------- decoding

class FrameDecoder:
    """Byte-by-byte decoder with resync on bit 7 (mirrors seedmon_feed in the spec)."""

    def __init__(self):
        self.first = None
        self.frames = 0
        self.dropped = 0
        self.bytes = 0
        self.recent = b""

    def feed(self, data):
        """Feed received bytes, yield the 12-bit sensor mask of each complete frame."""
        self.bytes += len(data)
        self.recent = (self.recent + bytes(data))[-16:]
        for b in data:
            if b & 0x80:
                if self.first is not None:
                    self.dropped += 1      # first byte without its second byte
                self.first = b
            elif self.first is None:
                self.dropped += 1          # second byte without a first byte
            else:
                self.frames += 1
                yield (((self.first >> 1) & 0x3F) << 6) | ((b >> 1) & 0x3F)
                self.first = None


def encode_frame(mask):
    """Inverse of the decoder, used by the simulator and self test."""
    return bytes((0x81 | (((mask >> 6) & 0x3F) << 1), ((mask & 0x3F) << 1)))


def pgn32100(row, module, rate):
    data = [PGN32100_HEADER[0], PGN32100_HEADER[1], ((row & 0x0F) << 4) | (module & 0x0F), rate & 0xFF]
    data.append(sum(data) & 0xFF)
    return bytes(data)


# ---------------------------------------------------------------- statistics

class SensorStats:
    """Counts rising edges (pulses) and active frames per sensor over a window."""

    def __init__(self):
        self.lock = threading.Lock()
        self.prev = 0
        self.reset()

    def reset(self):
        self.edges = [0] * SENSOR_COUNT
        self.active = [0] * SENSOR_COUNT
        self.frames = 0

    def add(self, mask):
        rising = mask & ~self.prev
        self.prev = mask
        with self.lock:
            self.frames += 1
            for i in range(SENSOR_COUNT):
                bit = 1 << i
                if rising & bit:
                    self.edges[i] += 1
                if mask & bit:
                    self.active[i] += 1

    def take(self):
        with self.lock:
            snap = (self.edges, self.active, self.frames)
            self.reset()
        return snap


class RateWindow:
    """Sums the last few report intervals, so one quiet interval does not drop a row to 0."""

    def __init__(self, intervals):
        self.history = collections.deque(maxlen=intervals)

    def add(self, edges, active, frames, seconds):
        self.history.append((edges, active, frames, seconds))

    def clear(self):
        self.history.clear()

    def totals(self, i):
        """Edges, active frames, frames and seconds of sensor i over the window."""
        return (sum(h[0][i] for h in self.history), sum(h[1][i] for h in self.history),
                sum(h[2] for h in self.history), sum(h[3] for h in self.history))


# ---------------------------------------------------------------- sources

def serial_source(args):
    """Opens the port here (not inside the generator) so errors surface in the main thread."""
    try:
        import serial
    except ImportError:
        if sys.platform == "win32":
            bits = "" if sys.maxsize > 2**32 else "-32"
            cmd = f"py -{sys.version_info.major}.{sys.version_info.minor}{bits} -m pip install pyserial"
        else:
            cmd = f"\"{sys.executable}\" -m pip install pyserial"
        sys.exit(f"pyserial is not installed for this Python ({sys.executable}).\n"
                 f"Install it with: {cmd}")
    port = args.port or auto_port()
    try:
        ser = serial.Serial(port, args.baud, bytesize=8, parity="N", stopbits=1, timeout=0.1)
    except serial.SerialException as e:
        sys.exit(f"Cannot open {port}: {e}")
    print(f"Reading {port} @ {args.baud} 8N1")

    def read():
        while True:
            chunk = ser.read(256)
            if chunk:
                yield chunk
    return read()


def auto_port():
    """Pick the single USB serial adapter, so built-in ports such as Intel AMT SOL are skipped."""
    from serial.tools import list_ports as lp
    ports = lp.comports()
    usb = [p for p in ports if p.vid is not None]
    if len(usb) == 1:
        return usb[0].device
    found = ", ".join(f"{p.device} ({p.description})" for p in ports) or "none"
    sys.exit(f"Cannot pick a serial port automatically, use --port. Ports found: {found}")


def simulated_source(args):
    """Generates a controller-like byte stream: pulses on all sensors except the blocked ones."""
    blocked = {s - 1 for s in args.sim_blocked}
    period = [max(1, int(250 / random.uniform(8, 15))) for _ in range(SENSOR_COUNT)]
    phase = [random.randrange(p) for p in period]
    print(f"Simulating controller, blocked sensors: {sorted(s + 1 for s in blocked) or 'none'}")
    tick = 0
    while True:
        mask = 0
        for i in range(SENSOR_COUNT):
            # pulse is 2 frames (~8 ms) wide
            if i not in blocked and (tick + phase[i]) % period[i] < 2:
                mask |= 1 << i
        yield encode_frame(mask)
        tick += 1
        time.sleep(0.004)


# ---------------------------------------------------------------- main loop

def reader(source, decoder, stats, verbose, failure):
    last = None
    try:
        for chunk in source:
            for mask in decoder.feed(chunk):
                stats.add(mask)
                if verbose >= 2 and mask != last:
                    print(f"{time.monotonic():.3f} frame {mask:012b}", flush=True)
                last = mask
    except Exception as e:  # e.g. USB adapter unplugged
        failure.append(e)


def compute_rate(args, edges, active, frames, seconds):
    if args.rate_mode == "duty":
        if active == 0:
            return 0
        return max(1, min(255, round(255 * active / max(frames, 1))))
    if edges == 0:
        return 0
    return max(1, min(255, round(edges / seconds * args.scale)))


def run(args):
    source = simulated_source(args) if args.simulate else serial_source(args)
    decoder = FrameDecoder()
    stats = SensorStats()
    failure = []
    thread = threading.Thread(target=reader, args=(source, decoder, stats, args.verbose, failure), daemon=True)
    thread.start()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    target = (args.host, args.udp_port)
    print(f"Sending PGN 32100 to {target[0]}:{target[1]}, module {args.module}, "
          f"sensor 1 -> row {args.row_offset}, rate mode '{args.rate_mode}'")

    last = time.monotonic()
    last_bytes = 0
    log = None
    if args.log:
        log = open(args.log, "w", buffering=1)
        log.write("time,frames,sensor,edges,active,rate,sent\n")
    window = RateWindow(max(1, round(args.window / args.interval)))
    sent_rate = [None] * SENSOR_COUNT
    sent_time = [0.0] * SENSOR_COUNT
    while True:
        time.sleep(args.interval)
        if not thread.is_alive():
            sys.exit(f"Serial reader stopped: {failure[0] if failure else 'source ended'}")
        now = time.monotonic()
        seconds, last = now - last, now
        edges, active, frames = stats.take()

        if frames == 0:
            received, last_bytes = decoder.bytes - last_bytes, decoder.bytes
            if received == 0:
                print("WARNING: no data on the serial port, check --port (see --list-ports) and the cable",
                      file=sys.stderr, flush=True)
            else:
                print(f"WARNING: {received} bytes received but no valid frames, check wiring/polarity. "
                      f"Last bytes: {decoder.recent.hex(' ')}", file=sys.stderr, flush=True)
            window.clear()
            continue
        last_bytes = decoder.bytes
        window.add(edges, active, frames, seconds)

        report = []
        for i in range(SENSOR_COUNT):
            row = args.row_offset + i
            if row > 15:
                break
            rate = compute_rate(args, *window.totals(i))
            changed = rate != sent_rate[i]
            # an unchanged 0 is never repeated, so an idle row times out as blocked in BlockageMonitor
            keepalive = rate > 0 and now - sent_time[i] >= args.keepalive
            if log:
                log.write(f"{now:.3f},{frames},{i + 1},{edges[i]},{active[i]},{rate},{int(changed or keepalive)}\n")
            if not (changed or keepalive):
                report.append(f"S{i + 1}:({rate})")
                continue
            sock.sendto(pgn32100(row, args.module, rate), target)
            sent_rate[i], sent_time[i] = rate, now
            report.append(f"S{i + 1}:{rate}")

        if args.verbose:
            print(f"{frames:4d} frames, {decoder.dropped} dropped | " + " ".join(report), flush=True)


# ---------------------------------------------------------------- self test

def selftest():
    # verified / predicted mappings from the protocol document
    table = {
        0: "81 00", 1: "81 02", 2: "81 04", 3: "81 08", 4: "81 10", 5: "81 20", 6: "81 40",
        7: "83 00", 8: "85 00", 9: "89 00", 10: "91 00", 11: "A1 00", 12: "C1 00",
    }
    for sensor, hexstr in table.items():
        mask = 0 if sensor == 0 else 1 << (sensor - 1)
        raw = bytes.fromhex(hexstr)
        assert list(FrameDecoder().feed(raw)) == [mask], f"decode sensor {sensor}"
        assert encode_frame(mask) == raw, f"encode sensor {sensor}"

    assert list(FrameDecoder().feed(bytes.fromhex("81 0C"))) == [0b110]  # sensors 2 + 3

    # resync: stray second byte, then a first byte without its second byte
    dec = FrameDecoder()
    assert list(dec.feed(bytes.fromhex("02 81 83 00 81 02"))) == [1 << 6, 1]
    assert dec.dropped == 2

    # README example: row 1, module 0, rate 80
    assert pgn32100(1, 0, 0x50) == bytes((0x64, 0x7D, 0x10, 0x50, 0x41))  # 0x141 truncated to a byte
    print("selftest OK")


def list_ports():
    try:
        from serial.tools import list_ports as lp
    except ImportError:
        sys.exit("pyserial is required: pip install pyserial")
    for p in lp.comports():
        print(f"{p.device:8s} {p.description}")


def main():
    ap = argparse.ArgumentParser(description="Agroelektronika seed monitor -> BlockageMonitor (PGN 32100) bridge")
    ap.add_argument("--port", help="serial port, e.g. COM18 (default: the only USB serial adapter)")
    ap.add_argument("--baud", type=int, default=9600)
    ap.add_argument("--host", default="127.0.0.1",
                    help="BlockageMonitor address, e.g. 127.0.0.1 or 192.168.1.255 (default 127.0.0.1)")
    ap.add_argument("--udp-port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--module", type=int, default=0, help="module ID 0-15 (default 0)")
    ap.add_argument("--row-offset", type=int, default=0,
                    help="row ID within the module for sensor 1, 0-15 (default 0)")
    ap.add_argument("--interval", type=float, default=0.5, help="report interval in seconds (default 0.5)")
    ap.add_argument("--window", type=float, default=2.0,
                    help="rates are measured over the last N seconds; a row drops to 0 after N s without pulses (default 2)")
    ap.add_argument("--keepalive", type=float, default=2.0,
                    help="resend an unchanged non-zero rate every N seconds, keep below BlockSeconds (default 2)")
    ap.add_argument("--rate-mode", choices=("edges", "duty"), default="edges",
                    help="edges: pulses/s * scale (default); duty: share of time active, 0-255")
    ap.add_argument("--scale", type=float, default=5.0,
                    help="edges mode: rate = pulses per second * scale, clamped 1-255 (default 5)")
    ap.add_argument("--log", help="write per-window, per-sensor stats to this CSV file")
    ap.add_argument("--simulate", action="store_true", help="generate a fake controller stream instead of serial")
    ap.add_argument("--sim-blocked", type=int, nargs="*", default=[5, 9],
                    help="sensors (1-12) left blocked in --simulate (default 5 9)")
    ap.add_argument("--list-ports", action="store_true", help="list serial ports and exit")
    ap.add_argument("--selftest", action="store_true", help="run decoder/encoder self test and exit")
    ap.add_argument("-v", "--verbose", action="count", default=0,
                    help="-v: per-report summary, (n) = not sent because unchanged; -vv: every frame change")
    args = ap.parse_args()

    if args.selftest:
        return selftest()
    if args.list_ports:
        return list_ports()
    if args.keepalive <= 0 or args.window <= 0:
        ap.error("--keepalive and --window must be above 0")
    if not 0 <= args.module <= 15 or not 0 <= args.row_offset <= 15:
        ap.error("--module and --row-offset must be 0-15")
    try:
        run(args)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
