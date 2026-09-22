"""IDVB-owned adapter. Never edits the independent idv-login installation.

Hooks are capability-checked at import time; the local status endpoint makes
incompatible upstream updates observable instead of claiming integration works.
"""
import importlib.abc
import importlib.machinery
import json
import os
from pathlib import Path
import runpy
import sys
import re

CAPABILITIES = {"account_source": False, "suppress_auto_accounts": False, "login_mode": False}
LOGIN_MODE = "official"
NAVIGATION_DIAGNOSTICS = {"managed_responses": 0, "removed_navigation_entries": 0, "unexpected_schema": 0}


def extend_module(name, module):
    if name == "channelmgr":
        cls = getattr(module, "ChannelManager", None)
        original = getattr(cls, "list_channels", None)
        if not callable(original):
            return

        def list_channels(self, *args, **kwargs):
            result = original(self, *args, **kwargs)
            sources = {}
            for account in getattr(self, "channels", []):
                source = getattr(account, "channel_name", "")
                if not source:
                    source = getattr(account, "login_info", {}).get("login_channel", "")
                account_id = getattr(account, "uuid", "")
                if source == "myapp":
                    if account_id.startswith("wx-"):
                        source = "wechat"
                    elif account_id.startswith("qq-"):
                        source = "qq"
                if isinstance(source, str) and len(source) <= 64 and all(c.isalnum() or c in "_-" for c in source):
                    sources[account_id] = source
            return [dict(item, login_channel=sources.get(item.get("uuid"), "")) for item in result]

        cls.list_channels = list_channels
        CAPABILITIES["account_source"] = True
    elif name == "mitm_addon":
        cls = getattr(module, "IDVLoginAddon", None)
        def identity_v_request(flow):
            path = flow.request.path.split("?")[0]
            if path.startswith("/_idv-login/"):
                return False
            values = module._request_values(flow.request)
            ids = [values.get("game_id", ""), values.get("dst_jf_game_id", "")]
            match = re.match(r"^/mpay/games/([^/]+)/", path)
            if match:
                ids.append(match.group(1))
            return any(module.getShortGameId(value) == "h55" for value in ids if value)

        request = getattr(cls, "request", None)
        response = getattr(cls, "response", None)
        if callable(request) and callable(response) and callable(getattr(module, "_request_values", None)):
            def native_request(self, flow):
                bypass = LOGIN_MODE == "official" and identity_v_request(flow)
                flow.metadata["idvb_native_login"] = bypass
                if not bypass:
                    return request(self, flow)

            def native_response(self, flow):
                # Pin the route on request: switching mode cannot reinterpret
                # an already in-flight login response from another launch.
                if not flow.metadata.get("idvb_native_login", False):
                    return response(self, flow)

            cls.request = native_request
            cls.response = native_response
            CAPABILITIES["login_mode"] = True
        original = getattr(cls, "_modify_create_login_response", None)
        code = getattr(original, "__code__", None)
        # The upstream one-shot flag only gates the unsolicited accounts UI.
        # If upstream removes it, don't patch a different operation by guessing.
        if code is None or "has_opened_admin" not in code.co_consts:
            return

        def create_login(self, flow, *args, **kwargs):
            query = flow.request.query
            target = query.get("dst_jf_game_id") or kwargs.get("hosted_target_game_id") or (args[0] if args else "") or query.get("game_id", "")
            short_id = module.getShortGameId(target)
            managed = short_id == "h55" and bool(self.genv.get("auto-h55", ""))
            if managed:
                self.genv.set("has_opened_admin", True)
            result = original(self, flow, *args, **kwargs)
            if not managed:
                return result
            # These are external navigation entries, not the QR challenge or
            # its query/confirmation endpoints. Restoring the native URL only
            # redirects the unwanted browser launch to the game's home page.
            # Keep the arrays (valid empty collections) but remove their entries.
            try:
                after = json.loads(flow.response.content)
                fields = ("qrcode_scanners", "qrcode_extern_links")
                if not isinstance(after, dict) or any(not isinstance(after.get(key, []), list) for key in fields):
                    raise ValueError("Unexpected navigation schema")
                removed = sum(len(after.get(key, [])) for key in fields)
                for key in fields:
                    if key in after:
                        after[key] = []
                flow.response.content = json.dumps(after).encode("utf-8")
                NAVIGATION_DIAGNOSTICS["managed_responses"] += 1
                NAVIGATION_DIAGNOSTICS["removed_navigation_entries"] += removed
            except (AttributeError, ValueError, TypeError):
                CAPABILITIES["suppress_auto_accounts"] = False
                NAVIGATION_DIAGNOSTICS["unexpected_schema"] += 1
            return result

        cls._modify_create_login_response = create_login
        CAPABILITIES["suppress_auto_accounts"] = True
    elif name == "local_handler":
        cls = getattr(module, "LocalRequestHandler", None)
        original = getattr(cls, "_route", None)
        if not callable(original):
            return

        def route(self, path, method, args, json_body=None):
            global LOGIN_MODE
            if path == "/_idv-login/idvb/status" and method == "GET":
                payload = {"success": True, "adapter_version": 4, **CAPABILITIES, "mode": LOGIN_MODE, "navigation": dict(NAVIGATION_DIAGNOSTICS)}
                return 200, {"Content-Type": "application/json"}, json.dumps(payload).encode("utf-8")
            if path == "/_idv-login/idvb/login-mode" and method == "POST":
                mode = (json_body or {}).get("mode")
                if mode not in ("official", "channel") or not CAPABILITIES["login_mode"]:
                    return 409, {"Content-Type": "application/json"}, b'{"success":false}'
                if mode == "official":
                    module.genv.set("auto-h55", "", True)
                    module.genv.set("CHANNEL_ACCOUNT_SELECTED", "")
                LOGIN_MODE = mode
                return 200, {"Content-Type": "application/json"}, json.dumps({"success": True, "mode": mode}).encode()
            return original(self, path, method, args, json_body)

        cls._route = route


class AdapterFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname not in {"channelmgr", "mitm_addon", "local_handler"}:
            return None
        spec = importlib.machinery.PathFinder.find_spec(fullname, path)
        if spec is None or spec.loader is None:
            return None
        loader = spec.loader

        class AdapterLoader(importlib.abc.Loader):
            def create_module(self, module_spec):
                return loader.create_module(module_spec)

            def exec_module(self, module):
                loader.exec_module(module)
                extend_module(fullname, module)

        spec.loader = AdapterLoader()
        return spec


def main():
    root = Path(sys.argv[1]).resolve(strict=True)
    entry = root / "src" / "main.pyc"
    if not entry.is_file():
        entry = root / "src" / "main.py"
    if not entry.is_file():
        raise RuntimeError("Unsupported idv-login installation")
    # Preserve this adapter through the elevation that upstream requires.
    # UAC remains an OS consent prompt; never bypass it.
    if sys.platform == "win32":
        import ctypes
        import subprocess
        if not ctypes.windll.shell32.IsUserAnAdmin():
            arguments = subprocess.list2cmdline([str(Path(__file__).resolve()), str(root)])
            result = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, arguments, str(root), 0)
            if result <= 32:
                raise RuntimeError("idv-login elevation was not approved")
            return
    os.chdir(root)
    sys.path.insert(0, str(root / "src"))
    sys.meta_path.insert(0, AdapterFinder())
    sys.argv = [str(entry)]
    runpy.run_path(str(entry), run_name="__main__")


if __name__ == "__main__":
    main()
