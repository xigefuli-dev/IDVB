"""Headless calls into unmodified 9491 identity and current-frame pose solvers."""
from __future__ import annotations

import math
import mmap
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
from types import SimpleNamespace

from reference_resources import CatalogBindings, PROFILE, SOURCE_COMMIT, UnsupportedInput


class ReferenceRuntime:
    def __init__(self, source_root, runtime_root, catalog_path):
        started = time.perf_counter()
        source = Path(source_root).resolve()
        runtime = Path(runtime_root).resolve()
        import json
        release = json.loads((runtime.parent / "release-report.json").read_text(encoding="utf-8-sig"))
        if release.get("fullSourceCommit") != SOURCE_COMMIT:
            raise UnsupportedInput("reference-runtime-source-mismatch")
        if not (source / "app/single_map_structure.py").is_file():
            raise UnsupportedInput("reference-python-source-unavailable")
        sys.dont_write_bytecode = True
        sys.path[:0] = [str(source / "app"), str(runtime)]
        sys._MEIPASS = str(runtime)
        self._temporary = tempfile.TemporaryDirectory(prefix="idvb-reference-python-")
        os.environ["IDENTITYV_NIGHTMARE_MEMORY_ROOT"] = str(Path(self._temporary.name) / "memory")
        os.environ["IDENTITYV_NIGHTMARE_LIVE_ROOT"] = str(Path(self._temporary.name) / "live")
        import cv2
        import numpy as np
        import identityv_daily_assistant as daily
        self.cv2, self.np, self.daily = cv2, np, daily
        if cv2.__version__ != "4.12.0":
            raise UnsupportedInput("reference-opencv-version-mismatch")
        host = daily.DailyAssistant.__new__(daily.DailyAssistant)
        host.route_profile_id = host.preferred_route_profile_id = PROFILE
        host.aligned_author_route_lock = threading.RLock()
        host.runtime_timeline_enabled = False
        host._install_map_mode("difficult", asset_root=runtime / "assets/difficult-maps",
                               memory_root=Path(self._temporary.name) / "memory")
        self.host = host
        self.bindings = CatalogBindings(catalog_path, host.pack)
        preload_started = time.perf_counter()
        # Match the reference application's primary/secondary resource preload.
        # Each floor loads its own static product; no frame or pose is supplied.
        self.preloaded_floors = {str(floor): host.engine.preload(str(floor)) for floor in host.pack.floors}
        self.preload_ms = (time.perf_counter() - preload_started) * 1000
        self.startup_ms = (time.perf_counter() - started) * 1000

    def status(self):
        return dict(sourceCommit=SOURCE_COMMIT, supportedMaps=len(self.bindings.maps),
                    mapClasses=list(self.bindings.classes), rejectedBindings=self.bindings.rejected,
                    startupMs=self.startup_ms, python=sys.version.split()[0], opencv=self.cv2.__version__,
                    preloadedFloors=self.preloaded_floors, preloadMs=self.preload_ms,
                    coordinateSpace="author-source-to-client", sessionWrites=False)

    def _read_frame(self, spec):
        if spec.get("coordinateSpace") != "client":
            raise UnsupportedInput("full-client-frame-required")
        width, height, stride = (int(spec[k]) for k in ("width", "height", "stride"))
        if not (16 <= width <= 8192 and 16 <= height <= 8192 and width * 3 <= stride <= width * 3 + 4096):
            raise UnsupportedInput("invalid-bgr-frame-layout")
        length = stride * height
        if length > 256 * 1024 * 1024:
            raise UnsupportedInput("frame-too-large")
        # Copy before releasing the mapping. Static feature caches must never
        # retain a view onto a capture buffer that C# can subsequently reuse.
        with mmap.mmap(-1, length, tagname=str(spec["memoryName"]), access=mmap.ACCESS_READ) as shared:
            rows = self.np.ndarray((height, stride), dtype=self.np.uint8, buffer=shared)
            image = rows[:, :width * 3].reshape(height, width, 3).copy()
            del rows
        return image

    def _matrix(self, pose):
        scale, x, y = (float(pose[k]) for k in ("scale", "tx", "ty"))
        if scale <= 0 or not all(math.isfinite(v) for v in (scale, x, y)):
            raise UnsupportedInput("invalid-prior-pose")
        return self.np.array([[scale, 0., x], [0., scale, y], [0., 0., 1.]])

    def _session(self, request, source_id, floor, prior):
        daily, host, np = self.daily, self.host, self.np

        class RequestSession(daily.DailyAssistant):
            def __init__(self):
                # Resources and immutable-image caches are shared. No group,
                # gate, verified frame, UI or committed pose survives a request.
                self.__dict__.update(host.__dict__)
                self.gate = SimpleNamespace(confirmed_id=source_id)
                self.current_floor = floor
                self.active_map_cycle_id = str(request["id"])
                self.surface_visibility_generation = 0
                self.runtime_timeline_enabled = False
                self.current_alignment_candidate = {}
                self.settings = {}
                self.events = []
                self.group = None
                self.view = None
                if prior is not None:
                    canonical = self.author_coordinate_adapters.matrix_profile_to_canonical(PROFILE, source_id, floor)
                    self.group = SimpleNamespace(verified_floors={floor}, route_profile_id=PROFILE,
                        canonical_geometry_verified_floors={floor}, relations={floor: np.eye(3)},
                        cycle_id=None, generation=0)
                    self.view = dict(matrixSourceToClient=prior.copy(),
                        canonicalMatrixSourceToClient=prior @ np.linalg.inv(canonical))

            def _active_round_layer_group(self, map_id):
                return self.group if map_id == source_id else None

            def _round_group_floor_view(self, map_id, requested_floor, **_):
                return self.view if map_id == source_id and str(requested_floor) == floor else None

            def _open_cycle_transient_motion_active(self, _):
                return False  # The caller provides a stable, current client capture.

            def _log(self, event, **details):
                self.events.append(dict(event=event, **details))

        return RequestSession()

    def compute(self, request, received_at, cancelled):
        started = time.perf_counter()
        budget_ms = float(request.get("budgetMs", 1000))
        if not math.isfinite(budget_ms) or not 0 < budget_ms <= 1000:
            raise UnsupportedInput("invalid-request-budget")
        deadline = received_at + budget_ms / 1000
        expired = lambda: cancelled.is_set() or time.perf_counter() >= deadline
        timings = dict(queueMs=(started - received_at) * 1000, budgetMs=budget_ms)
        result = dict(id=request["id"], operation=request["operation"], floor=request.get("floor"),
            identityVerified=False, poseVerified=False, competitionComplete=False, timings=timings,
            coordinateSpace="author-source-to-client")
        if expired():
            return dict(result, reason="request-budget-expired")
        self.bindings = self.bindings.refreshed(self.host.pack)
        floor_key = str(request.get("floor", ""))
        if request["operation"] == "identify":
            pool, floor = self.bindings.identity_pool(request.get("mapClass"), floor_key)
        else:
            source_id, floor = self.bindings.selected(request.get("mapId"), floor_key,
                request.get("sourceMapId"), request.get("mapClass"))
            pool = {source_id: str(request["mapId"])}
        frame = self._read_frame(request["frame"])
        timings["frameCopyMs"] = (time.perf_counter() - started) * 1000
        # IDVB has already observed the current frame's floor. The adapter only
        # maps that declared floor through registered resources; it never uses
        # a filename, prior floor, or a second independently timed UI capture.
        result["floorSource"] = "caller-current-frame-observation"
        if expired():
            return dict(result, reason="request-budget-expired")
        compute_at = time.perf_counter()
        matrix = None
        if request["operation"] == "identify":
            rows = self.host.engine.rank(frame, floor=floor, limit=len(self.host.pack.maps),
                                         budget_seconds=max(0., deadline - time.perf_counter()))
            top = rows[0] if rows else None
            authorized = bool(self.daily.should_auto_lock_candidate(top, False, None))
            candidate = (top or {}).get("difficultEntry") or {}
            verification = candidate.get("verification") or {}
            evidence = self.host.engine.last_difficult_evidence or {}
            competition = evidence.get("tilemapSide" if candidate.get("locatorKind") == "side" else "tilemapEntry") or {}
            result.update(reason=(top or {}).get("reason", "no-identity-candidates"),
                identityVerified=authorized, evidence=evidence,
                competitionComplete=bool(candidate.get("competitionComplete", competition.get("complete", False))))
            if authorized and top["mapId"] in pool:
                source_id = top["mapId"]
                result.update(mapId=pool[source_id], sourceMapId=source_id)
                if (candidate.get("poseVerified") is True and verification.get("success") is True
                        and (verification.get("residual") or {}).get("verified") is True):
                    matrix = verification.get("matrixSourceToClient")
                    result["confidence"] = verification["residual"].get("withinSixPixels")
        else:
            prior = request.get("priorPose")
            if prior is not None:
                if (prior.get("trusted") is not True or prior.get("mapId") != request.get("mapId")
                        or str(prior.get("floor")) != floor_key):
                    raise UnsupportedInput("prior-pose-map-floor-binding-mismatch")
                prior = self._matrix(prior)
            session = self._session(request, source_id, floor, prior)
            route = session._aligned_author_route_image(PROFILE, source_id, floor)
            fitted = session._alignment_v2_structure_fit(frame, route, source_id, floor, PROFILE, cancelled=expired)
            residual = fitted.get("selectedRouteCurrentFrameResidual")
            result.update(mapId=request["mapId"], sourceMapId=source_id,
                reason=fitted.get("reason", "structure-current-frame-candidate"),
                evidence=dict(fit=fitted, structure=getattr(session, "_single_map_structure_diagnostics", None), events=session.events),
                poseRoute="same-floor-original-solve" if prior is not None else "original-first-pose-structure-fit")
            if fitted.get("success") and self.daily.rendered_profile_residual_is_verified(residual):
                matrix = fitted.get("matrixSourceToClient")
                result["confidence"] = (residual or {}).get("withinSixPixels")
        timings.update(computeMs=(time.perf_counter() - compute_at) * 1000,
                       elapsedMs=(time.perf_counter() - received_at) * 1000)
        if matrix is not None:
            matrix = self.np.asarray(matrix, dtype=float)
            if (matrix.shape == (3, 3) and self.np.isfinite(matrix).all() and matrix[0, 0] > 0
                    and self.np.allclose(matrix[:2, :2], self.np.eye(2) * matrix[0, 0], atol=1e-8, rtol=0)):
                result.update(poseVerified=True, scale=float(matrix[0, 0]), tx=float(matrix[0, 2]), ty=float(matrix[1, 2]))
        if expired():
            result.update(identityVerified=False, poseVerified=False,
                          reason="request-cancelled" if cancelled.is_set() else "request-budget-expired")
        return result

    def close(self):
        self._temporary.cleanup()
