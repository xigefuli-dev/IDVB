"""Read the existing EntryIdentityAsset binding; never infer identity from names."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

SOURCE_COMMIT = "9491d0b061bae1ab062c1c75eb0bd1b6c93968dc"
PROFILE = "zhan10-speed"


class UnsupportedInput(ValueError):
    pass


class CatalogBindings:
    def __init__(self, catalog_path, pack, source_hashes=None):
        path = Path(catalog_path).resolve()
        self.path = path
        self.stamp = self.file_stamp(path)
        catalog = json.loads(path.read_text(encoding="utf-8-sig"))
        if catalog.get("StorageSchemaVersion") != 19:
            raise UnsupportedInput("unsupported-catalog-schema")
        self.maps = {}
        self.classes = {}
        self.rejected = []
        self.source_hashes = source_hashes if source_hashes is not None else {}
        source_hashes = self.source_hashes
        for record in catalog.get("Maps", []):
            local_id = str(record.get("Id", ""))
            floors = record.get("Floors", [])
            if not any(f.get("EntryIdentityAsset") for f in floors):
                continue
            try:
                registered = {}
                for floor in floors:
                    profile = (record.get("Recognition", {}).get("Floors", {}).get(floor["Key"]))
                    region = (profile or {}).get("RecognitionRegion")
                    if (not profile or profile.get("OrientationDegrees") != 0
                            or profile.get("FreeCropPoints")
                            or region is not None and any(abs(float(region[key]) - expected) > 1e-6
                                for key, expected in (("X", 0), ("Y", 0), ("Width", 1), ("Height", 1)))):
                        raise UnsupportedInput("entry-author-canvas-transformed")
                    asset = floor.get("EntryIdentityAsset")
                    if not asset or asset.get("SchemaVersion") != 1:
                        raise UnsupportedInput("incomplete-entry-floor-resources")
                    name = asset["PassportFileName"]
                    if Path(name).name != name or any(c in name for c in ("/", "\\", ":")):
                        raise UnsupportedInput("invalid-entry-resource-path")
                    document = json.loads((path.parent / local_id.replace("-", "") / name)
                                          .read_text(encoding="utf-8-sig"))
                    source_id = document["sourceMapId"]
                    source_floor = document["sourceFloor"]
                    item = pack.by_id.get(source_id)
                    if item is None or source_floor not in item.layers:
                        raise UnsupportedInput("unsupported-source-map-floor")
                    route = item.layers[source_floor]
                    if route not in source_hashes:
                        source_hashes[route] = hashlib.sha256(route.read_bytes()).hexdigest()
                    if (document.get("sourceCommit") != SOURCE_COMMIT
                            or document.get("profileId") != PROFILE
                            or document.get("packageMapId") != record.get("SourcePackageMapId", local_id)
                            or document.get("floorKey") != floor["Key"]
                            or document.get("sourceImageSha256") != asset["SourceImageSha256"]
                            or asset["SourceImageSha256"] != floor["ImageSha256"]
                            or asset["SourceImageSha256"] != source_hashes[route]
                            or asset["SourceWidth"] != floor["ImageWidth"]
                            or asset["SourceHeight"] != floor["ImageHeight"]
                            or floor["RecognitionWidth"] != floor["ImageWidth"]
                            or floor["RecognitionHeight"] != floor["ImageHeight"]):
                        raise UnsupportedInput("entry-author-canvas-binding-mismatch")
                    registered[floor["Key"]] = (source_id, source_floor)
                if len({v[0] for v in registered.values()}) != 1:
                    raise UnsupportedInput("entry-map-source-identity-mismatch")
                self.maps[local_id] = dict(mapClass=record["Class"], floors=registered)
                self.classes.setdefault(record["Class"], []).append(local_id)
            except (OSError, KeyError, ValueError, TypeError) as error:
                self.rejected.append(dict(mapId=local_id, reason=str(error)))
        self.expected_sources = frozenset(pack.by_id)
        if self.file_stamp(path) != self.stamp:
            raise UnsupportedInput("entry-catalog-changed-during-read")

    @staticmethod
    def file_stamp(path):
        stat = path.stat()
        return stat.st_mtime_ns, stat.st_size

    def refreshed(self, pack):
        if self.file_stamp(self.path) == self.stamp:
            return self
        # Only immutable frozen reference image hashes survive a catalog edit.
        # Local IDs, class membership and canvas transforms are read again.
        return CatalogBindings(self.path, pack, self.source_hashes)

    def identity_pool(self, map_class, floor):
        if map_class is None and len(self.classes) == 1:
            map_class = next(iter(self.classes))
        rows = {}
        source_floors = set()
        for local_id in self.classes.get(map_class, []):
            binding = self.maps[local_id]["floors"].get(floor)
            if binding is None or binding[0] in rows:
                raise UnsupportedInput("entry-class-floor-binding-incomplete")
            rows[binding[0]] = local_id
            source_floors.add(binding[1])
        if frozenset(rows) != self.expected_sources or len(source_floors) != 1:
            raise UnsupportedInput("unsupported-entry-class-pool")
        return rows, next(iter(source_floors))

    def selected(self, local_id, floor, source_id=None, map_class=None):
        record = self.maps.get(str(local_id))
        binding = record["floors"].get(floor) if record else None
        if (binding is None or (source_id is not None and source_id != binding[0])
                or map_class is not None and record["mapClass"] != map_class):
            raise UnsupportedInput("unsupported-entry-map-floor")
        return binding
