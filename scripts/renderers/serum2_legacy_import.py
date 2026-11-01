"""Windows Serum 2 FXP importer, isolated from the audio renderer.

Uses the public VST3 interfaces and the plugin's own file-drop importer. The
editor lives in a transparent, non-activating tool window. No input is injected,
no cursor is moved, and no preset is installed in the user's preset folders.
Only a validated native state is saved in the caller's private temporary folder.
"""
import argparse
import ctypes as ct
from ctypes import wintypes as wt
import hashlib
import json
import os
from pathlib import Path
import struct
import sys
import threading
import time
import uuid


LIMIT = 128 * 1024 * 1024
P, I, U = ct.c_void_p, ct.c_int32, ct.c_uint32
CALL = getattr(ct, "WINFUNCTYPE", ct.CFUNCTYPE)
_native_lifetime = []


def _iid(value):
    return uuid.UUID(value).bytes_le


def _call(obj, index, result, types, *args):
    table = ct.cast(obj, ct.POINTER(ct.POINTER(P))).contents
    return CALL(result, P, *types)(table[index])(obj, *args)


def _check(result, operation):
    if result != 0:
        raise ValueError(f"Serum 2 legacy import: {operation} failed ({result})")


def validate_fxp(raw, content_hash):
    if not 60 < len(raw) <= 64 * 1024 * 1024 or raw[:4] != b"CcnK" or \
            raw[8:12] != b"FPCh" or raw[16:20] != b"XfsX" or \
            struct.unpack_from(">I", raw, 4)[0] not in (len(raw), len(raw) - 8) or \
            struct.unpack_from(">I", raw, 56)[0] != len(raw) - 60:
        raise ValueError("Serum 2 fallback requires a complete, identified Serum 1 FPCh FXP")
    if not isinstance(content_hash, str) or len(content_hash) != 64 or \
            hashlib.sha256(raw).hexdigest() != content_hash.lower():
        raise ValueError("Legacy preset changed after scanning; scan it again before rendering")


class _Stream:
    def __init__(self, interface, initial=b""):
        self.data, self.position = bytearray(initial), 0
        self.pointer = interface("C3BF6EA2-3099-4752-9B6B-F9901EE33E9B", [
            CALL(I, P, P, I, ct.POINTER(I))(self.read),
            CALL(I, P, P, I, ct.POINTER(I))(self.write),
            CALL(I, P, ct.c_int64, I, ct.POINTER(ct.c_int64))(self.seek),
            CALL(I, P, ct.POINTER(ct.c_int64))(self.tell)])

    def read(self, _, buffer, count, actual):
        if count < 0 or count > LIMIT:
            return -1
        data = bytes(self.data[self.position:self.position + count])
        ct.memmove(buffer, data, len(data))
        self.position += len(data)
        if actual:
            actual[0] = len(data)
        return 0 if len(data) == count else 1

    def write(self, _, buffer, count, actual):
        if count < 0 or self.position + count > LIMIT:
            return -1
        end = self.position + count
        if end > len(self.data):
            self.data.extend(bytes(end - len(self.data)))
        self.data[self.position:end] = ct.string_at(buffer, count)
        self.position = end
        if actual:
            actual[0] = count
        return 0

    def seek(self, _, offset, mode, result):
        position = offset + (self.position if mode == 1 else len(self.data) if mode == 2 else 0)
        if mode not in (0, 1, 2) or not 0 <= position <= LIMIT:
            return -1
        self.position = position
        if result:
            result[0] = position
        return 0

    def tell(self, _, result):
        result[0] = self.position
        return 0


def import_preset(binary, source, content_hash, version, output):
    if sys.platform != "win32" or ct.sizeof(P) != 8:
        raise ValueError("Automatic Serum 1 to Serum 2 import currently requires Windows x64")
    raw = source.read_bytes()
    validate_fxp(raw, content_hash)
    from serum2_state import _dependencies, prepare_legacy_serum2_state
    _, _, _, wrappers = _dependencies()
    keep = []

    def interface(sid, methods):
        obj = (P * 1)()

        @CALL(I, P, P, ct.POINTER(P))
        def query(_, key, out):
            if ct.string_at(key, 16) in (_iid(sid), _iid("00000000-0000-0000-C000-000000000046")):
                out[0] = ct.addressof(obj)
                return 0
            out[0] = None
            return -2147467262

        functions = [query, CALL(U, P)(lambda _: 2), CALL(U, P)(lambda _: 1)] + methods
        table = (P * len(functions))(*[ct.cast(fn, P).value for fn in functions])
        obj[0] = ct.addressof(table)
        keep.extend([obj, table, functions])
        return P(ct.addressof(obj))

    def attributes():
        values = {}

        def put(_, key, value):
            values[ct.string_at(key)] = value
            return 0

        def get(_, key, value):
            stored = values.get(ct.string_at(key))
            if stored is None:
                return 1
            value[0] = stored
            return 0

        def put_string(_, key, value):
            values[ct.string_at(key)] = ct.wstring_at(value)
            return 0

        def get_string(_, key, value, size):
            stored = values.get(ct.string_at(key))
            if not isinstance(stored, str):
                return 1
            encoded = stored.encode("utf-16-le") + bytes(2)
            if len(encoded) > size:
                return 1
            ct.memmove(value, encoded, len(encoded))
            return 0

        def put_binary(_, key, value, size):
            if size > LIMIT:
                return -1
            values[ct.string_at(key)] = ct.create_string_buffer(ct.string_at(value, size))
            return 0

        def get_binary(_, key, value, size):
            stored = values.get(ct.string_at(key))
            if not isinstance(stored, ct.Array):
                return 1
            value[0], size[0] = ct.addressof(stored), len(stored) - 1
            return 0

        return interface("1E5F0AEB-CC7F-4533-A254-401138AD5EE4", [
            CALL(I, P, P, ct.c_int64)(put), CALL(I, P, P, ct.POINTER(ct.c_int64))(get),
            CALL(I, P, P, ct.c_double)(put), CALL(I, P, P, ct.POINTER(ct.c_double))(get),
            CALL(I, P, P, P)(put_string), CALL(I, P, P, P, U)(get_string),
            CALL(I, P, P, P, U)(put_binary), CALL(I, P, P, ct.POINTER(P), ct.POINTER(U))(get_binary)])

    def message():
        attrs, name = attributes(), ct.create_string_buffer(1024)

        def set_id(_, key):
            name.value = ct.string_at(key)[:1023]

        return interface("936F033B-C6C0-47DB-BB08-82F813C1E613", [
            CALL(P, P)(lambda _: ct.addressof(name)), CALL(None, P, P)(set_id),
            CALL(P, P)(lambda _: attrs.value)])

    def get_name(_, out):
        value = "SoundPreviewer FXP import".encode("utf-16-le") + bytes(2)
        ct.memmove(out, value, len(value))
        return 0

    def create(_, cid, key, out):
        requested = ct.string_at(key, 16)
        if requested == _iid("936F033B-C6C0-47DB-BB08-82F813C1E613"):
            out[0] = message()
        elif requested == _iid("1E5F0AEB-CC7F-4533-A254-401138AD5EE4"):
            out[0] = attributes()
        else:
            out[0] = None
            return -2147467262
        return 0

    host = interface("58E595CC-DB2D-4969-8B6A-AF8C36A664E5", [
        CALL(I, P, P)(get_name), CALL(I, P, P, P, ct.POINTER(P))(create)])
    handler = interface("93A0BEA3-0BD0-45DB-8E89-0B0CC1E46AC6", [
        CALL(I, P, U)(lambda t, p: 0), CALL(I, P, U, ct.c_double)(lambda t, p, v: 0),
        CALL(I, P, U)(lambda t, p: 0), CALL(I, P, I)(lambda t, f: 0)])
    frame = interface("367FAF01-AFA9-4693-8D4D-A2A0ED0882A3", [CALL(I, P, P, P)(lambda t, v, r: 0)])
    ole, user, shell = ct.OleDLL("ole32"), ct.WinDLL("user32"), ct.WinDLL("shell32")
    _check(ole.OleInitialize(None), "initialize OLE")
    # All top-level windows created by this UI thread must stay invisible and
    # non-activating, including native error dialogs from the plugin. A blocked
    # importer is terminated by its parent; it never asks the user for input.
    class CreateStruct(ct.Structure):
        _fields_ = [("params", P), ("instance", P), ("menu", P), ("parent", P),
                    ("cy", I), ("cx", I), ("y", I), ("x", I), ("style", I),
                    ("name", P), ("class_name", P), ("extended", U)]

    class CreateWindow(ct.Structure):
        _fields_ = [("structure", ct.POINTER(CreateStruct)), ("after", P)]

    user.CallNextHookEx.argtypes = [P, I, P, P]
    user.CallNextHookEx.restype = ct.c_ssize_t

    @CALL(ct.c_ssize_t, I, P, P)
    def suppress(code, window, data):
        if code == 3:  # HCBT_CREATEWND
            creation = ct.cast(data, ct.POINTER(CreateWindow)).contents.structure.contents
            if not creation.style & 0x40000000:  # WS_CHILD
                creation.extended |= 0x080800A0  # NOACTIVATE, LAYERED, TRANSPARENT, TOOLWINDOW
        if code == 5:  # HCBT_ACTIVATE
            return 1
        return user.CallNextHookEx(None, code, window, data)

    kernel = ct.WinDLL("kernel32")
    user.SetWindowsHookExW.argtypes = [I, P, P, U]
    user.SetWindowsHookExW.restype = P
    hook = user.SetWindowsHookExW(5, suppress, None, kernel.GetCurrentThreadId())
    if not hook:
        raise ValueError("Cannot protect the foreground while importing Serum FXP")
    component, controller, processor, view, factory = P(), P(), P(), P(), P()
    connections, pidls = [], []
    window, data_object = None, P()
    stop, audio_thread = threading.Event(), None
    initialized_component = initialized_controller = active = processing = attached = False
    try:
        library = ct.WinDLL(str(binary))
        library.InitDll.restype = ct.c_bool
        if not library.InitDll():
            raise ValueError("Serum 2 VST3 module initialization failed")
        library.GetPluginFactory.restype = P
        factory = P(library.GetPluginFactory())
        _check(_call(factory, 6, I, [ct.c_char_p, ct.c_char_p, ct.POINTER(P)],
                     _iid("56534558-6673-5073-6572-756d20320000"),
                     _iid("E831FF31-F2D5-4301-928E-BBEE25697802"), ct.byref(component)), "create Serum 2 instrument")
        _check(_call(component, 3, I, [P], host), "initialize processor")
        initialized_component = True
        cid = ct.create_string_buffer(16)
        _check(_call(component, 5, I, [P], cid), "get controller identity")
        _check(_call(factory, 6, I, [P, ct.c_char_p, ct.POINTER(P)], cid,
                     _iid("DCD7BBE3-7742-448D-A874-AACC979C759E"), ct.byref(controller)), "create controller")
        _check(_call(controller, 3, I, [P], host), "initialize controller")
        initialized_controller = True

        def query(obj, key):
            value = P()
            _check(_call(obj, 0, I, [ct.c_char_p, ct.POINTER(P)], _iid(key), ct.byref(value)), "query interface")
            return value

        cp = query(component, "70A4156F-6E6E-4026-9891-48BFAA60D8D1")
        cc = query(controller, "70A4156F-6E6E-4026-9891-48BFAA60D8D1")
        connections = [cp, cc]
        _check(_call(cp, 3, I, [P], cc), "connect processor")
        _check(_call(cc, 3, I, [P], cp), "connect controller")

        def snapshot(obj, index):
            stream = _Stream(interface)
            _check(_call(obj, index, I, [P], stream.pointer), "save native state")
            if not stream.data:
                raise ValueError("Serum 2 returned an empty state")
            return bytes(stream.data)

        fresh = snapshot(component, 13)
        stream = _Stream(interface, fresh)
        _check(_call(controller, 5, I, [P], stream.pointer), "initialize controller state")
        processor = query(component, "42043F99-B7DA-453C-A569-E79D9AAEC33D")

        class Setup(ct.Structure):
            _fields_ = [("mode", I), ("size", I), ("maximum", I), ("rate", ct.c_double)]

        setup = Setup(1, 0, 128, 44100)
        _check(_call(processor, 7, I, [P], ct.byref(setup)), "set up processing")
        _check(_call(component, 10, I, [I, I, I, ct.c_ubyte], 0, 1, 0, 1), "activate output")
        _check(_call(component, 11, I, [ct.c_ubyte], 1), "activate processor")
        active = True
        _check(_call(processor, 8, I, [ct.c_ubyte], 1), "start processing")
        processing = True

        class Bus(ct.Structure):
            _fields_ = [("channels", I), ("silence", ct.c_uint64), ("buffers", P)]

        class Data(ct.Structure):
            _fields_ = [("mode", I), ("size", I), ("samples", I), ("num_inputs", I), ("num_outputs", I),
                        ("inputs", P), ("outputs", P), ("params_in", P), ("params_out", P),
                        ("events_in", P), ("events_out", P), ("context", P)]

        left, right = (ct.c_float * 128)(), (ct.c_float * 128)()
        buffers = (P * 2)(ct.addressof(left), ct.addressof(right))
        bus = Bus(2, 0, ct.addressof(buffers))
        data = Data(1, 0, 128, 0, 1, None, ct.addressof(bus), None, None, None, None, None)
        audio_errors = []

        def process_audio():
            try:
                while not stop.is_set():
                    _check(_call(processor, 9, I, [P], ct.byref(data)), "process import messages")
                    stop.wait(128 / 44100)
            except Exception as error:
                audio_errors.append(str(error))

        audio_thread = threading.Thread(target=process_audio, daemon=True)
        audio_thread.start()
        _check(_call(controller, 16, I, [P], handler), "set component handler")
        view = P(_call(controller, 17, P, [ct.c_char_p], b"editor"))
        if not view:
            raise ValueError("Serum 2 does not expose its native importer editor")
        user.CreateWindowExW.argtypes = [U, wt.LPCWSTR, wt.LPCWSTR, U, I, I, I, I, P, P, P, P]
        user.CreateWindowExW.restype = P
        window = user.CreateWindowExW(0x080800A0, "STATIC", "SoundPreviewer private Serum import", 0,
                                      0, 0, 1200, 800, None, None, None, None)
        user.SetLayeredWindowAttributes.argtypes = [P, U, ct.c_ubyte, U]
        if not window or not user.SetLayeredWindowAttributes(window, 0, 0, 2):
            raise ValueError("Cannot create an invisible Serum import container")
        user.ShowWindow.argtypes = [P, I]
        user.ShowWindow(window, 4)  # SW_SHOWNOACTIVATE; zero alpha was set before this call.
        _check(_call(view, 12, I, [P], frame), "set editor frame")
        _check(_call(view, 4, I, [P, ct.c_char_p], window, b"HWND"), "attach editor")
        attached = True
        rect = (I * 4)()
        _check(_call(view, 9, I, [P], rect), "get editor dimensions")
        _check(_call(view, 10, I, [P], rect), "size editor")

        def pump(seconds):
            until = time.monotonic() + seconds
            while time.monotonic() < until:
                msg = wt.MSG()
                while user.PeekMessageW(ct.byref(msg), None, 0, 0, 1):
                    user.TranslateMessage(ct.byref(msg))
                    user.DispatchMessageW(ct.byref(msg))
                if audio_errors:
                    raise ValueError(audio_errors[0])
                time.sleep(0.01)

        pump(0.25)
        targets = []
        user.GetPropW.argtypes, user.GetPropW.restype = [P, wt.LPCWSTR], P
        enum_callback = CALL(wt.BOOL, P, P)

        @enum_callback
        def find(child, _):
            name = ct.create_unicode_buffer(256)
            user.GetClassNameW(P(child), name, 256)
            target = user.GetPropW(child, "OleDropTargetInterface")
            if target and name.value.startswith("VSTGUI"):
                targets.append((child, target))
            return True

        user.EnumChildWindows.argtypes = [P, enum_callback, P]
        user.EnumChildWindows(window, find, None)
        if len(targets) != 1:
            raise ValueError("Serum 2 has no unambiguous native file import target")
        shell.SHParseDisplayName.argtypes = [wt.LPCWSTR, P, ct.POINTER(P), U, P]
        file_id = P()
        _check(shell.SHParseDisplayName(str(source), None, ct.byref(file_id), 0, None), "resolve private FXP snapshot")
        pidls.append(file_id)
        shell.ILClone.argtypes, shell.ILClone.restype = [P], P
        shell.ILRemoveLastID.argtypes = [P]
        shell.ILFindLastID.argtypes, shell.ILFindLastID.restype = [P], P
        folder_id = P(shell.ILClone(file_id))
        pidls.append(folder_id)
        shell.ILRemoveLastID(folder_id)
        child_id = shell.ILFindLastID(file_id)
        shell.SHCreateDataObject.argtypes = [P, U, P, P, P, ct.POINTER(P)]
        _check(shell.SHCreateDataObject(folder_id, 1, (P * 1)(child_id), None,
                                       _iid("0000010e-0000-0000-c000-000000000046"), ct.byref(data_object)), "create file data")

        class Format(ct.Structure):
            _fields_ = [("format", ct.c_ushort), ("device", P), ("aspect", U), ("index", I), ("medium", U)]

        def query_file(_, fmt):
            if ct.cast(fmt, ct.POINTER(Format)).contents.format != 15:  # CF_HDROP only; never text.
                return -2147221404
            return _call(data_object, 5, I, [P], fmt)

        files = interface("0000010e-0000-0000-c000-000000000046", [
            CALL(I, P, P, P)(lambda t, f, m: _call(data_object, 3, I, [P, P], f, m)),
            CALL(I, P, P, P)(lambda t, f, m: -2147467263), CALL(I, P, P)(query_file),
            CALL(I, P, P, P)(lambda t, f, m: -2147467263), CALL(I, P, P, P, I)(lambda t, f, m, r: -2147467263),
            CALL(I, P, U, P)(lambda t, d, o: _call(data_object, 8, I, [U, P], d, o)),
            CALL(I, P, P, U, P, P)(lambda t, f, a, s, c: -2147221501),
            CALL(I, P, U)(lambda t, c: -2147221501), CALL(I, P, P)(lambda t, c: -2147221501)])

        class Point(ct.Structure):
            _fields_ = [("x", I), ("y", I)]

        user.SetWindowPos.argtypes = [P, P, I, I, I, I, U]
        child, target = targets[0]

        def align():
            point = Point()
            user.GetCursorPos(ct.byref(point))
            # VSTGUI reads GetCursorPos instead of the IDropTarget point argument.
            # Align our invisible container, never the user's cursor. No activation.
            if not user.SetWindowPos(window, None, point.x - 250, point.y - 200, 1200, 800, 0x0014):
                raise ValueError("Cannot position private import container")
            return point

        point, effect = align(), U(1)
        _check(_call(target, 3, I, [P, U, Point, ct.POINTER(U)], files, 1, point, ct.byref(effect)), "enter native importer")
        point, effect.value = align(), 1
        _check(_call(target, 4, I, [U, Point, ct.POINTER(U)], 1, point, ct.byref(effect)), "check native importer")
        if effect.value != 1:
            _call(target, 5, I, [])
            raise ValueError("Serum 2 did not accept the legacy FXP drop; no foreground import was attempted")
        point, effect.value = align(), 1
        _check(_call(target, 6, I, [P, U, Point, ct.POINTER(U)], files, 0, point, ct.byref(effect)), "import legacy FXP")
        pump(0.5)
        processor_state, controller_state = snapshot(component, 13), snapshot(controller, 7)
        names = {source.stem, raw[28:56].split(b"\0", 1)[0].decode("utf-8", errors="replace")}
        state, _ = prepare_legacy_serum2_state(processor_state, controller_state,
                                              wrappers.build_juce_vst3_state(fresh), version, names)
        output.write_bytes(state)
    finally:
        # Native libraries may retain host callbacks until process detach. Keep
        # their Python trampolines alive through the isolated process's exit.
        _native_lifetime.extend(keep)
        stop.set()
        if audio_thread:
            audio_thread.join(2)
            if audio_thread.is_alive():
                # Parent owns the bounded process lifetime; do not tear down a
                # component while its processing call is still in progress.
                raise ValueError("Serum 2 processing did not stop after import")
        if attached:
            _call(view, 5, I, [])
        if processing:
            _call(processor, 8, I, [ct.c_ubyte], 0)
        if active:
            _call(component, 11, I, [ct.c_ubyte], 0)
        if len(connections) == 2:
            _call(connections[0], 4, I, [P], connections[1])
            _call(connections[1], 4, I, [P], connections[0])
        if initialized_controller:
            _call(controller, 4, I, [])
        if initialized_component:
            _call(component, 4, I, [])
        for obj in [view, processor, *connections, controller, component, factory, data_object]:
            if obj:
                _call(obj, 2, U, [])
        if window:
            user.DestroyWindow.argtypes = [P]
            user.DestroyWindow(window)
        ole.CoTaskMemFree.argtypes = [P]
        for allocated in pidls:
            ole.CoTaskMemFree(allocated)
        user.UnhookWindowsHookEx.argtypes = [P]
        user.UnhookWindowsHookEx(hook)
        ole.OleUninitialize()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--result", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.request.stat().st_size > 1024 * 1024:
            raise ValueError("Legacy import request exceeds 1 MiB")
        request = json.loads(args.request.read_text(encoding="utf-8"))
        import_preset(Path(request["Binary"]), Path(request["Source"]), request["ContentHash"],
                      request["Version"], Path(request["Output"]))
        result = {"Success": True}
    except Exception as error:
        result = {"Success": False, "Message": str(error)}
    args.result.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    return 0 if result["Success"] else 1


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    status = main()
    # This disposable native host has closed its result/state files and stopped
    # processing. Bypass DLL detach: Serum retains GUI callbacks until detach,
    # which can run after Python/COM have torn down. The OS reclaims this one
    # process's native resources without touching the parent or another plugin.
    if sys.platform == "win32":
        kernel = ct.WinDLL("kernel32")
        kernel.TerminateProcess.argtypes = [P, U]
        kernel.TerminateProcess(P(-1), status)
    os._exit(status)
