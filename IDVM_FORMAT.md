# IDVM（Identity Vision Model）多 Class 地图数据包格式

状态：Portable v1.3 + Subscription Secure Transport v2
文件扩展名：`.idvm`  
媒体类型：`application/vnd.idv.map`  
字符集：UTF-8（JSON 不带 BOM）

## 1. 目标与范围

IDVM（Identity Vision Model）用于在不同 IDVB 安装之间交换地图资源。一个数据包包含一个或多个非空 Class，每个 Class 包含一张或多张地图，每张地图可包含任意数量的楼层。

包中只保存权威地图数据：未经修改的原始楼层图、楼层定义、门、识别锚点、忽略区、标注、Class 属性和识别配置。人工遮瑕层属于识别配置的一部分，使用 `background` 语义保存。识别裁剪图、透明 Overlay、缩略图、玩家位置、当前对局、窗口状态、日志或其他可重建数据均不是权威源，不写入包；导入后由本地统一背景处理器重建。

发布时 `.idvm` 是 ZIP 容器；开发调试时读取器也接受相同结构的未压缩目录。产品 UI 只选择 `.idvm` 文件。

## 2. 包布局

```text
example.idvm/
├── header
├── manifest.json
└── maps/
    └── 61ac...e902/
        ├── maps/
        │   ├── floor-001.png
        │   └── floor-002.jpg
        └── data/
            ├── metadata.json
            ├── gates.json
            └── anchors.json
```

- `header`：必需，固定 80 字节，ZIP 中必须使用 STORE。
- `manifest.json`：必需，声明包、Class、地图、楼层和全部负载文件摘要。
- `maps/{mapId}/maps/*`：每个楼层的原始参考图。
- `maps/{mapId}/data/*`：该地图的权威结构数据。
- v1 不写入 `cache/`；读取器不得依赖派生缓存完成导入。

逻辑路径始终使用 `/`，按 ASCII 大小写敏感比较。不得包含绝对路径、空段、`.`、`..`、反斜杠、空字节、重复路径、符号链接或设备文件。

## 3. `header`

所有整数使用 little-endian。GUID 使用 RFC 4122 网络字节序，不使用 `.NET Guid.ToByteArray()` 的混合字节序。

| 偏移 | 长度 | 类型 | 含义 |
|---:|---:|---|---|
| 0 | 4 | bytes | ASCII `IDVM` |
| 4 | 2 | uint16 | `formatMajor`，v1 为 `1` |
| 6 | 2 | uint16 | `formatMinor`，当前为 `1`；读取器继续接受 `0` |
| 8 | 2 | uint16 | `headerSize`，必须为 `80` |
| 10 | 2 | uint16 | flags，v1 必须为 `0` |
| 12 | 16 | bytes | `packageId` |
| 28 | 8 | int64 | 创建时间，Unix milliseconds UTC |
| 36 | 32 | bytes | `manifest.json` 原始字节的 SHA-256 |
| 68 | 12 | bytes | 保留，必须全为 `0` |

读取 ZIP 中的其他负载前，应先读取并验证 `header` 与 `manifest.json`。header 中的 `packageId` 和创建时间必须与 manifest 一致。

## 4. `manifest.json`

```json
{
  "format": "idvm",
  "formatVersion": "1.1",
  "packageType": "class-set",
  "packageId": "6f9f8b9a-5f6c-4ef1-9d9e-2a5f4f5a1c10",
  "createdAt": "2026-08-02T03:00:00.0000000+00:00",
  "minimumReader": "1.1",
  "classes": [
    {
      "classId": "1a1e8f54-cfa8-4ead-a03c-7760675cf830",
      "name": "S1",
      "mapIds": ["61ac15d7-78e9-4454-a659-aeeed373e902"],
      "properties": { "removeBackground": false }
    }
  ],
  "maps": [
    {
      "mapId": "61ac15d7-78e9-4454-a659-aeeed373e902",
      "mapVersion": 1,
      "name": "军工厂",
      "classId": "1a1e8f54-cfa8-4ead-a03c-7760675cf830",
      "createdAt": "2026-08-02T03:00:00Z",
      "updatedAt": "2026-08-02T03:00:00Z",
      "root": "maps/61ac15d778e94454a659aeeed373e902",
      "floors": [
        {
          "key": "1f",
          "displayName": "1F",
          "sortOrder": 1,
          "image": "maps/61ac15d778e94454a659aeeed373e902/maps/floor-001.png"
        }
      ]
    }
  ],
  "variantGroups": [
    {
      "groupId": "4bf18ef7-317b-44a2-b84d-4b1db50f6597",
      "classId": "1a1e8f54-cfa8-4ead-a03c-7760675cf830",
      "paletteSlot": 0,
      "mapIds": ["61ac15d7-78e9-4454-a659-aeeed373e902", "4da3bdb4-b586-42bd-9e3c-da22ec8fab16"]
    }
  ],
  "files": [
    {
      "path": "maps/61ac15d778e94454a659aeeed373e902/maps/floor-001.png",
      "size": 123456,
      "sha256": "..."
    }
  ],
  "capabilities": {
    "multiClass": true,
    "multiFloor": true,
    "recognitionAnchors": true,
    "derivedCache": false,
    "backgroundLayers": true,
    "classBackgroundRemoval": true,
    "variantGroups": true
  }
}
```

约束：

- `classes` 和 `maps` 均至少一项；空 Class 不写入包。
- `classId` 是包内关联 ID；Class 名称在包内按不区分大小写规则唯一。
- 每张地图必须且只能属于一个 Class；Class 的 `mapIds` 必须与 `maps[].classId` 完全一致。
- `variantGroups` 在 1.1 中保存组合成员的有序 `mapIds` 与 `paletteSlot`。每组至少两张地图，成员必须存在并属于同一 `classId`；同一地图只能属于一组。
- 同一 Class 最多 12 个变体组，`paletteSlot` 必须位于 `0..11` 且在该 Class 内唯一。`groupId` 在包内唯一。
- `mapId` 标识导出来源中的逻辑地图；`mapVersion` 为正整数。
- `root` 固定为 `maps/{mapId:N}`，不得由名称或 Class 名拼接。
- 楼层 `key` 在地图内唯一，`sortOrder` 必须从 1 连续递增；不得从文件名推断楼层顺序。
- `files` 列出除 `header` 和 `manifest.json` 外的每个普通文件，不能多也不能少；`size` 是未压缩大小，`sha256` 使用小写十六进制。
- `header` 不列入 `files`，避免 header 摘要与 manifest 摘要形成无法生成的循环依赖。
- major 不兼容时必须拒绝；读取器接受严格匹配的 1.0/1.0 与 1.1/1.1 header/manifest。1.0 包按无变体组合读取，且不得夹带 `variantGroups` 关系。
- `classes[].properties.removeBackground` 缺失时按 `false` 读取；导出时始终写出该属性。

## 5. 坐标约定

所有 JSON 几何坐标使用所属楼层图或明确声明的识别图的归一化坐标：

- 原点在左上，`x` 向右，`y` 向下。
- `x`、`y`、`width`、`height` 必须是有限数，矩形必须有正面积并完整落在 `0..1`。
- 最多允许 `1e-6` 的边界浮点误差；读取后 clamp 到 `0..1`。
- `validMapBounds` 相对于生成后的识别图归一化；导入本地后才能转换为识别图像素坐标。

## 6. `metadata.json`

```json
{
  "schemaVersion": 1,
  "map": {
    "id": "61ac15d7-78e9-4454-a659-aeeed373e902",
    "classId": "1a1e8f54-cfa8-4ead-a03c-7760675cf830",
    "title": "军工厂",
    "source": "manual",
    "coordinateSystem": "normalized-top-left-y-down"
  },
  "floors": [
    {
      "key": "1f",
      "displayName": "1F",
      "sortOrder": 1,
      "image": "maps/61ac15d778e94454a659aeeed373e902/maps/floor-001.png",
      "imageWidth": 2048,
      "imageHeight": 1536,
      "orientationDegrees": 0,
      "recognitionRegion": { "x": 0.02, "y": 0.02, "width": 0.96, "height": 0.96 },
      "validMapBounds": { "x": 0, "y": 0, "width": 1, "height": 1 }
    }
  ],
  "recognition": {
    "schemaVersion": 1,
    "wholeImage": {
      "enabled": false,
      "weight": 0.15,
      "annotatedReferencePenalty": 0.55,
      "referenceMayContainAnnotations": true
    }
  }
}
```

metadata 的地图、Class、楼层、图片路径必须与 manifest 完全一致。实际图片格式和尺寸必须与声明一致。`orientationDegrees` 只允许 `0`、`90`、`180`、`270`。

## 7. `gates.json`

```json
{
  "schemaVersion": 1,
  "gates": [
    {
      "id": "1f-main",
      "floorKey": "1f",
      "role": "mainEntrance",
      "bounds": { "x": 0.12, "y": 0.42, "width": 0.025, "height": 0.06 },
      "directionDegrees": 0,
      "enabled": true,
      "confidence": 1.0
    }
  ]
}
```

- `role` 允许 `mainEntrance`、`sideEntrance`、`exit`、`unknown`。
- 每个楼层最多一个 `mainEntrance`。
- `confidence` 范围为 `0..1`。
- `gates.json` 是门几何的唯一 wire-format 权威来源。

## 8. `anchors.json`

```json
{
  "schemaVersion": 4,
  "floors": {
    "1f": {
      "anchors": [
        {
          "id": "4c98633d-5b6e-43d0-a6bb-bc75d06aa4a5",
          "key": "main-entrance",
          "displayName": "大门",
          "role": "required",
          "weight": 1.0,
          "builtIn": true,
          "gateId": "1f-main"
        },
        {
          "id": "8ac53b9c-eae7-437c-bd43-0919613403a1",
          "key": "custom-anchor",
          "displayName": "辅助锚点",
          "role": "optional",
          "weight": 0.35,
          "builtIn": false,
          "bounds": { "x": 0.3, "y": 0.2, "width": 0.1, "height": 0.1 }
        }
      ],
      "wholeImageIgnoreRegions": [],
      "annotations": [],
      "backgroundLayers": [
        {
          "id": "2e82dd83-7d4b-4dcb-943b-0bd2d24f969f",
          "semantic": "background",
          "shape": "circle",
          "brushSizePixels": 64,
          "points": [
            { "x": 0.32, "y": 0.41 },
            { "x": 0.35, "y": 0.43 }
          ]
        }
      ]
    }
  }
}
```

- 门锚点只写 `gateId`，不得同时写 `bounds`；其 bounds 从 `gates.json` 解析。
- required 非门锚点必须写 `bounds`；optional 锚点允许省略 `bounds`，表示该可选锚点尚未放置。
- `role` 允许 `required`、`optional`；`weight` 必须为有限非负数。
- 标注 `type` 允许 `text`、`outline`，`colorIndex` 范围为 `0..8`。
- `floors` 必须与 metadata 的楼层集合完全一致。
- `anchors.json` schema 1–3 仍可读取，并补充为空的 `backgroundLayers`；schema 4 中的背景层不能静默丢弃。
- `backgroundLayers[].semantic` 必须为 `background`；`shape` 只能为 `circle` 或 `square`；`brushSizePixels` 范围为 `1..1024`；`id` 必须唯一且非空；`points` 至少一个，且全部为楼层原图坐标系中的有效归一化点。
- 原图坐标始终相对于完整楼层图，而不是识别裁剪区域；导出和本地保存时再按同一处理顺序生成识别 PNG 与 Overlay。

## 9. 导出规则

- “当前 Class”只导出当前非空 Class；“全部地图”导出所有非空 Class。
- `packageId` 和包内 `classId` 每次导出重新生成；来源地图 `mapId` 和 `mapVersion` 写入 manifest。
- 导出先在临时目录生成全部负载和摘要，再在目标目录写临时 ZIP，成功后原子替换用户选择的文件。
- 导出过程中任何地图原图或权威配置不可读时，整个导出失败，不留下半成品。

## 10. 导入规则

导入是复制，不是跨设备同步：

- 包内每个 Class 都创建为新的本地 Class，绝不合并到已有 Class，也不使用当前 UI Class。
- 原名未占用时使用原名；否则依次使用 `原名 - 新添加1`、`原名 - 新添加2`……。比较不区分大小写。
- 每张导入地图生成新的本地地图 ID；重复导入同一包会创建下一组 Class 和地图副本。
- 包内 Class 到本地新 Class 的映射必须保持，不能把多 Class 包压成一个 Class。
- 图片与所有 JSON 全部验证通过后才能开始写入仓库。
- 导入先写 staging，仓库使用事务日志记录新 Class 和新地图；1.1 导入维护源 `mapId` 到新本地 `mapId` 的映射，生成新的组 GUID，并在地图全部成功后原子写入组合。失败时回滚新地图、Class 和组合；进程中断后由下一次启动恢复。
- 重复导入同一 1.1 包时，每次产生彼此独立的地图 GUID 与组合 GUID，同时保留成员顺序和颜色槽。
- 导入成功后重新生成缩略图、识别裁剪图和 Overlay，并一次性刷新运行时地图缓存。
- 原始楼层图在导出、导入和后续 Class 属性切换中保持不变。人工遮瑕和 Class 去背景都只生成派生识别图；所有识别、结构对齐、VPSG、指纹和 Overlay 读取处理后的识别路径。

## 11. 安全限制与验证顺序

默认限制：ZIP 条目最多 4096；单文件最大 256 MiB；总展开大小最大 512 MiB；单个 JSON 最大 8 MiB；JSON 最大深度 64。

验证顺序：

1. 检查条目数量、路径、文件类型及展开大小，安全复制到隔离目录。
2. 验证 80 字节 header、版本、flags、保留字段和 RFC 4122 packageId。
3. 验证 manifest 原始字节 SHA-256，以及 header/manifest 的 packageId 和创建时间。
4. 验证文件清单的精确集合、大小和每个 SHA-256。
5. 验证 Class、地图、楼层的唯一性和双向引用。
6. 验证每张地图的 metadata、gates、anchors 及坐标。
7. 使用受限图片解码验证实际格式和尺寸。
8. 只有全部通过后才能建立仓库导入事务。

IDVM 不是可执行安装包。便携 IDVM v1.3 不包含脚本、可执行文件、外部实体或符号链接；直接文件导入仍按“不受信任的便携包”处理。数字签名与认证加密由下面的订阅安全传输层提供，不把接收方的订阅状态写回便携包。

## 12. 地图更新订阅安全传输 v2

地图更新订阅不直接把裸 `.idvm` 暴露为更新对象。发布流程先生成完整便携 IDVM，再封装为 `*.idvm.secure`：

```text
subscription endpoint/
├── feed.json                 # ECDSA P-256 签名信封，最后发布
└── maps-<version>.idvm.secure # AES-256-GCM 认证加密的便携 IDVM
```

### 12.1 加密容器

`*.idvm.secure` 的固定头为 92 字节，随后紧接密文。所有整数使用 little-endian。

| 偏移 | 长度 | 含义 |
|---:|---:|---|
| 0 | 8 | ASCII `IDVME2\0\0` |
| 8 | 16 | publicationId（`.NET Guid.TryWriteBytes()` 字节序） |
| 24 | 12 | AES-GCM nonce |
| 36 | 16 | AES-GCM authentication tag |
| 52 | 8 | 解密后 IDVM 字节长度 |
| 60 | 32 | 解密后 IDVM SHA-256 |
| 92 | N | AES-256-GCM ciphertext |

内容密钥为随机 256 位密钥，每个发布目录独立生成。认证附加数据为 UTF-8：

```text
IDVB-IDVM-SECURE-2\n<publicationId:D>\n<PLAINTEXT-SHA256-UPPERHEX>
```

修改容器头、密文、tag、publicationId 或明文摘要都会导致认证解密失败。解密成功后仍必须运行第 11 节的完整 IDVM 验证，不能把 AES-GCM 成功等同于包结构安全。

### 12.2 签名 feed

`feed.json` 保存 `payload` 原始 UTF-8 JSON 的 Base64、ECDSA P-256/SHA-256 签名、发布者 SPKI 公钥。payload 至少固定以下内容：

- publicationId、发布版本、UTC 发布时间、发布范围；
- publisherHandle 与 SPKI SHA-256 publisherKeyId；
- 加密包相对 URI、密文长度与 SHA-256；
- 明文 IDVM 长度与 SHA-256；
- 是否作为 IDVB 官网候选发布。

客户端先验证订阅链接固定的 publisherKeyId，再验 ECDSA 签名，然后检查版本不得回退、下载同源 HTTPS 资产、校验密文摘要、执行 AES-GCM 解密，最后验证便携 IDVM。远程 feed 和包只允许 HTTPS；包 URI 必须与 feed 同主机。本地开发与离线交付允许 `file://`，使用完全相同的验证链。

`@xigefuli` 是保留账号。只有签名公钥与 IDVB 内置官方信任根逐字节对应时才显示“官方”；其他密钥即使提供有效自签名并声明同一 handle，也必须拒绝。普通发布者由订阅链接内固定的公钥指纹认证，显示名不能替代密钥身份。

### 12.3 订阅链接

客户端接受未来官网可直接生成的能力链接：

```text
idvb-sub://v1?feed=<escaped-https-or-file-uri>&key=<base64url-32-bytes>&publisher=<spki-sha256>
```

也可接受带 `#idvb-key=...&idvb-publisher=...` 片段的 HTTPS/file URL。内容密钥只放在能力链接或 URL fragment 中，不写入 feed；正常浏览器请求不会把 fragment 发送给服务器。持有完整订阅链接等同于拥有解密权，界面、日志和网页不得公开回显密钥。

### 12.4 无感更新与来源标记

IDVB 调用独立 `Updater/IDVB.Updater.exe --map-subscriptions` 静默拉取、验签和解密，不请求主程序关闭，也不与程序更新使用同一个互斥实例。Updater 只把已验证便携 IDVM 写入待应用队列；主程序导入到新的不可变地图目录后，原子替换 `maps.json` 中的订阅所有权集合并刷新识别缓存。

当前对局仍可继续读取旧地图目录；旧目录在本进程内不移动、不删除，由下一次进程启动按退休日志回收。地图目录持久化 `Local`、`ImportedPackage`、`Subscription` 来源；订阅地图同时记录 subscriptionId、发布者 handle/公钥指纹和版本。这些字段只用于本机管理和卡片徽标，不进入再次导出的便携 IDVM。
