"""Persistent JSONL worker. stdout is protocol; original module output is stderr."""
from __future__ import annotations

import argparse
import json
import math
import os
import queue
import sys
import threading
import time
import traceback

sys.dont_write_bytecode = True


def serializable(value, depth=0):
    if value is None or isinstance(value, (str, bool, int)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else None
    if hasattr(value, "shape") and hasattr(value, "tolist"):
        return serializable(value.tolist(), depth) if value.size <= 256 else dict(arrayShape=list(value.shape))
    if hasattr(value, "item"):
        return serializable(value.item(), depth)
    if depth > 16:
        return dict(omittedType=type(value).__name__)
    if isinstance(value, dict):
        return {str(k): serializable(v, depth + 1) for k, v in value.items() if not str(k).startswith("_v2Worker")}
    if isinstance(value, (list, tuple, set, frozenset)):
        values = list(value)
        result = [serializable(v, depth + 1) for v in values[:96]]
        if len(values) > 96:
            result.append(dict(omittedItems=len(values) - 96))
        return result
    return str(value)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    parser.add_argument("--runtime-root", required=True)
    parser.add_argument("--catalog-path", required=True)
    args = parser.parse_args()
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    # Preserve the pipe separately, then redirect even native fd-1 writes away
    # from JSONL. This never starts the reference app's GUI entry point.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout = sys.stderr
    from reference_runtime import ReferenceRuntime
    from reference_resources import UnsupportedInput
    runtime = ReferenceRuntime(args.source_root, args.runtime_root, args.catalog_path)
    write_lock = threading.Lock()
    active_lock = threading.Lock()
    active = {}
    pending = queue.Queue()

    def emit(value):
        with write_lock:
            protocol.write(json.dumps(serializable(value), ensure_ascii=False, allow_nan=False) + "\n")
            protocol.flush()

    def read():
        for line in sys.stdin:
            received = time.perf_counter()
            try:
                if len(line) > 1024 * 1024:
                    raise ValueError("request-too-large")
                request = json.loads(line)
                if not isinstance(request.get("id"), (str, int)):
                    raise ValueError("request-id-required")
                if request.get("operation") == "cancel":
                    with active_lock:
                        event = active.get(request.get("targetId"))
                        if event is not None:
                            event.set()
                    emit(dict(id=request["id"], operation="cancel", cancelled=event is not None))
                    continue
                event = threading.Event()
                with active_lock:
                    if request["id"] in active:
                        raise ValueError("duplicate-active-request-id")
                    active[request["id"]] = event
                pending.put((request, received, event))
            except Exception as error:
                emit(dict(id=None, operation="error", reason="invalid-request", error=str(error)))
        pending.put(None)

    threading.Thread(target=read, name="reference-jsonl-reader", daemon=True).start()
    try:
        while (item := pending.get()) is not None:
            request, received, event = item
            try:
                operation = request.get("operation")
                if operation == "ping":
                    emit(dict(id=request["id"], operation="ping", ready=True, **runtime.status()))
                elif operation == "shutdown":
                    emit(dict(id=request["id"], operation="shutdown", stopped=True))
                    break
                elif operation in ("identify", "align"):
                    emit(runtime.compute(request, received, event))
                else:
                    raise UnsupportedInput("unsupported-operation")
            except Exception as error:
                traceback.print_exc(file=sys.stderr)
                emit(dict(id=request["id"], operation=request.get("operation"), identityVerified=False,
                          poseVerified=False, reason="unsupported" if isinstance(error, UnsupportedInput) else "worker-error",
                          error=str(error)))
            finally:
                with active_lock:
                    active.pop(request["id"], None)
    finally:
        runtime.close()
        protocol.close()


if __name__ == "__main__":
    main()
