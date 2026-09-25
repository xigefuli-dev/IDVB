import importlib.util
from pathlib import Path
from types import SimpleNamespace
import json
import unittest

spec = importlib.util.spec_from_file_location("adapter", Path(__file__).parents[1] / "Plugins/IdvLogin/adapter_bootstrap.py")
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)


class AdapterTests(unittest.TestCase):
    def test_official_mode_bypasses_both_hooks_and_pins_inflight_routing(self):
        calls = []
        class Addon:
            def request(self, flow): calls.append("request")
            def response(self, flow): calls.append("response")
        module = SimpleNamespace(IDVLoginAddon=Addon, getShortGameId=lambda value: value,
                                 _request_values=lambda request: request.query)
        adapter.extend_module("mitm_addon", module)
        flow = SimpleNamespace(request=SimpleNamespace(path="/mpay/api/qrcode/create_login", query={"game_id":"h55"}), metadata={})
        adapter.LOGIN_MODE = "official"
        Addon().request(flow)
        adapter.LOGIN_MODE = "channel"
        Addon().response(flow)
        self.assertEqual(calls, [])
        Addon().request(flow)
        adapter.LOGIN_MODE = "official"
        Addon().response(flow)
        self.assertEqual(calls, ["request", "response"])
        calls.clear()
        flow.request.query["game_id"] = "another-game"
        Addon().request(flow)
        Addon().response(flow)
        self.assertEqual(calls, ["request", "response"])

    def test_official_mode_clears_channel_autologin_without_deleting_accounts(self):
        values = {"auto-h55":"saved-channel", "CHANNEL_ACCOUNT_SELECTED":"saved-channel"}
        class Handler:
            def _route(self, *args): return args
        adapter.extend_module("local_handler", SimpleNamespace(LocalRequestHandler=Handler,
            genv=SimpleNamespace(set=lambda key, value, *args: values.__setitem__(key, value))))
        adapter.CAPABILITIES["login_mode"] = True
        result = Handler()._route("/_idv-login/idvb/login-mode", "POST", {}, {"mode":"official"})
        self.assertTrue(json.loads(result[2])["success"])
        self.assertEqual(values, {"auto-h55":"", "CHANNEL_ACCOUNT_SELECTED":""})

    def test_source_metadata_does_not_leak_credentials(self):
        class Manager:
            channels = [SimpleNamespace(uuid="a", channel_name="huawei", login_info={"token": "secret"})]
            def list_channels(self, game):
                return [{"uuid": "a", "name": "player"}]
        adapter.extend_module("channelmgr", SimpleNamespace(ChannelManager=Manager))
        self.assertEqual(Manager().list_channels("h55"), [{"uuid": "a", "name": "player", "login_channel": "huawei"}])

    def test_only_automatic_popup_is_suppressed(self):
        values = {"auto-h55": "fixture"}
        class Addon:
            genv = SimpleNamespace(set=lambda key, value: values.__setitem__(key, value), get=lambda key, default=None: values.get(key, default))
            def _modify_create_login_response(self, flow, hosted_target_game_id=""):
                if not values.get("has_opened_admin"):
                    values["popup"] = True
                data = json.loads(flow.response.content)
                data["qrcode_scanners"][0]["url"] = "https://localhost/_idv-login/index?game_id=h55&view=accounts"
                data["auto_login_scheduled"] = True
                flow.response.content = json.dumps(data).encode()
                return flow
        adapter.extend_module("mitm_addon", SimpleNamespace(IDVLoginAddon=Addon, getShortGameId=lambda value: value))
        flow = SimpleNamespace(request=SimpleNamespace(query={"game_id": "h55"}), response=SimpleNamespace(content=json.dumps({
            "uuid": "fresh-game-qr", "qrcode_scanners": [{"url": "https://id5.163.com/"}],
            "qrcode_extern_links": [{"url": "https://game.example/download"}]
        }).encode()))
        self.assertIs(Addon()._modify_create_login_response(flow), flow)
        self.assertNotIn("popup", values)
        data = json.loads(flow.response.content)
        self.assertEqual(data["qrcode_scanners"], [])
        self.assertEqual(data["qrcode_extern_links"], [])
        self.assertEqual(data["uuid"], "fresh-game-qr")
        self.assertTrue(data["auto_login_scheduled"])
        # An unrelated game / ordinary manual login retains its native behavior.
        flow.request.query["game_id"] = "other"
        flow.response.content = json.dumps({"qrcode_scanners": [{"url": "https://game.example/"}]}).encode()
        Addon()._modify_create_login_response(flow)
        self.assertEqual(json.loads(flow.response.content)["qrcode_scanners"][0]["url"],
                         "https://localhost/_idv-login/index?game_id=h55&view=accounts")

    def test_changed_response_schema_is_preserved_and_reported(self):
        class Addon:
            genv = SimpleNamespace(set=lambda *args: None, get=lambda *args: "fixture")
            def _modify_create_login_response(self, flow):
                self.genv.set("has_opened_admin", True)
        adapter.extend_module("mitm_addon", SimpleNamespace(IDVLoginAddon=Addon, getShortGameId=lambda value: value))
        payload = b'{"qrcode_scanners":{"unexpected":true},"uuid":"keep"}'
        flow = SimpleNamespace(request=SimpleNamespace(query={"game_id": "h55"}), response=SimpleNamespace(content=payload))
        Addon()._modify_create_login_response(flow)
        self.assertEqual(flow.response.content, payload)
        self.assertFalse(adapter.CAPABILITIES["suppress_auto_accounts"])

    def test_incompatible_update_is_not_patched(self):
        adapter.CAPABILITIES["suppress_auto_accounts"] = False
        class Addon:
            def _modify_create_login_response(self, flow):
                return flow
        original = Addon._modify_create_login_response
        adapter.extend_module("mitm_addon", SimpleNamespace(IDVLoginAddon=Addon))
        self.assertIs(original, Addon._modify_create_login_response)
        self.assertFalse(adapter.CAPABILITIES["suppress_auto_accounts"])

    def test_status_is_versioned_and_other_routes_unchanged(self):
        class Handler:
            def _route(self, *args):
                return args
        adapter.extend_module("local_handler", SimpleNamespace(LocalRequestHandler=Handler))
        handler = Handler()
        result = handler._route("/_idv-login/idvb/status", "GET", {})
        payload = json.loads(result[2])
        self.assertEqual(payload["adapter_version"], 5)
        self.assertTrue(payload["stop"])
        self.assertEqual(handler._route("/original", "POST", {}, {"x": True}), ("/original", "POST", {}, {"x": True}))

    def test_stop_route_triggers_shutdown_handler(self):
        called = []
        adapter.EXIT_HANDLER = lambda: called.append(True)
        try:
            class Handler:
                def _route(self, *args):
                    return args
            adapter.extend_module("local_handler", SimpleNamespace(LocalRequestHandler=Handler))
            status, headers, body = Handler()._route("/_idv-login/idvb/stop", "POST", {})
            self.assertEqual(status, 200)
            self.assertTrue(json.loads(body)["success"])
            import time
            time.sleep(0.05)
            self.assertEqual(called, [True])
        finally:
            adapter.EXIT_HANDLER = None


if __name__ == "__main__":
    unittest.main()
