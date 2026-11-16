import fcntl
import os
from pathlib import Path
import select
import signal
import struct
import sys
import threading
import time


WIDTH, HEIGHT = 320, 170
FRAME_SIZE = WIDTH * HEIGHT * 2
COMMAND = struct.Struct("<HB")
EVENT = struct.Struct("<qqHHi")
SYNC = EVENT.pack(0, 0, 0, 0, 0)
KEY_TIMEOUT = 2.5


class Framebuffer:
    def __init__(self, fd):
        self.fd = fd
        self.info = bytearray(160)
        self.offset()
        virtual_width = struct.unpack_from("<I", self.info, 8)[0]
        stride = Path("/sys/class/graphics/fb0/stride")
        self.stride = int(stride.read_text()) if stride.is_file() else virtual_width * 2
        if self.stride < WIDTH * 2:
            raise RuntimeError("Invalid framebuffer stride")

    def offset(self):
        fcntl.ioctl(self.fd, 0x4600, self.info, True)
        width, height, _, _, x, y, bpp = struct.unpack_from("<7I", self.info)
        colors = struct.unpack_from("<8I", self.info, 32)
        if (width, height, bpp) != (WIDTH, HEIGHT, 16) or colors[:2] != (11, 5) or colors[3:5] != (5, 6) or colors[6:] != (0, 5):
            raise RuntimeError("Expected a 320x170 RGB565 framebuffer")
        return x, y

    def frame(self):
        x, y = self.offset()
        start = y * self.stride + x * 2
        if self.stride == WIDTH * 2:
            os.lseek(self.fd, start, os.SEEK_SET)
            data = os.read(self.fd, FRAME_SIZE)
        else:
            data = bytearray()
            for row in range(HEIGHT):
                os.lseek(self.fd, start + row * self.stride, os.SEEK_SET)
                data.extend(os.read(self.fd, WIDTH * 2))
        if len(data) != FRAME_SIZE:
            raise RuntimeError("Short framebuffer read")
        return data


class Keyboard:
    def __init__(self, fd, supported):
        self.fd = fd
        self.supported = supported
        self.pressed = {}

    def send(self, code, down):
        packet = EVENT.pack(0, 0, 1, code, int(down)) + SYNC
        if os.write(self.fd, packet) != len(packet):
            raise OSError("Short keypad write")

    def handle(self, code, value):
        if code == 0 and value == 0:
            self.release_all()
        elif value == 1 and code not in self.pressed:
            if code == 52 and not (self.supported >> 52) & 1:
                actual_code = 91  # Dot on the Cardputer's symbol layer.
            else:
                actual_code = code
            if 0 < actual_code < 768 and (self.supported >> actual_code) & 1:
                self.pressed[code] = actual_code
                self.send(actual_code, True)
        elif value == 0 and code in self.pressed:
            self.send(self.pressed[code], False)
            del self.pressed[code]

    def release_all(self):
        for code in reversed(tuple(self.pressed)):
            self.handle(code, 0)


def receive_keys(source, keyboard, stop):
    pending = bytearray()
    last_command = time.monotonic()
    try:
        while not stop.is_set():
            readable, _, _ = select.select([source], [], [], 0.25)
            if readable:
                incoming = os.read(source, 512)
                if not incoming:
                    break
                pending.extend(incoming)
                while len(pending) >= COMMAND.size:
                    code, value = COMMAND.unpack_from(pending)
                    del pending[:COMMAND.size]
                    keyboard.handle(code, value)
                    last_command = time.monotonic()
            if keyboard.pressed and time.monotonic() - last_command >= KEY_TIMEOUT:
                keyboard.release_all()
    except OSError as error:
        print(f"Zero mirror: {error}", file=sys.stderr)
    finally:
        try:
            keyboard.release_all()
        finally:
            stop.set()


def find_keypad():
    keyboards = [
        event for event in Path("/sys/class/input").glob("event*")
        if (event / "device/name").read_text().strip() == "tca8418c"
    ]
    if len(keyboards) != 1:
        raise RuntimeError("Expected exactly one Cardputer keypad")
    event = keyboards[0]
    bitmap = (event / "device/capabilities/key").read_text()
    # Linux lists native unsigned-long words from most to least significant.
    supported = int("".join(word.zfill(16) for word in bitmap.split()), 16)
    return Path("/dev/input") / event.name, supported


def main():
    stop = threading.Event()
    signal.signal(signal.SIGHUP, lambda *_: stop.set())
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    keypad_path, supported = find_keypad()
    with open(keypad_path, "wb", buffering=0) as evdev, open("/dev/fb0", "rb", buffering=0) as display:
        framebuffer = Framebuffer(display.fileno())
        keyboard = Keyboard(evdev.fileno(), supported)
        worker = threading.Thread(target=receive_keys, args=(sys.stdin.fileno(), keyboard, stop))
        worker.start()
        try:
            output = sys.stdout.buffer
            output.write(b"CZM1")
            while not stop.is_set():
                output.write(framebuffer.frame())
                output.flush()
                stop.wait(0.1)
        finally:
            stop.set()
            worker.join()


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:
        pass
    except (OSError, RuntimeError) as error:
        sys.exit(f"Zero mirror: {error}")
