# Native components

CLAP, VST2.4 and LV2 are hosted entirely in managed code. The only native
format host is the vendored OwnVST3 library used for VST3 (and AU on macOS).
It is stored per RID under `Bridge/Process/Native/` and copied beside the
helper executable by the bridge project:

| Format | Windows | Linux | macOS |
| --- | --- | --- | --- |
| VST3/AU | `ownvst3.tlvsti.dll` | `libownvst3.tlvsti.so` | `libownvst3.tlvsti.dylib` |
| CLAP | none (managed C ABI) | none | none |
| VST2 | none (managed AEffect ABI) | none | none |
| LV2 | none (managed C ABI) | none | none |

The `.tlvsti` suffix is mandatory. Windows resolves DLLs by file name; loading
the same `ownvst3.dll` in both this extension and epmixer (which uses
`.tlhost`) would make one extension use the other's native build. The vendored
library is loaded by absolute path from the helper directory, never through
the default search order.

Rules for a native shim:

- resolve it by absolute path from the helper's directory, never by the default
  search order;
- return only unmanaged, blittable values across the boundary;
- expose an event-capable process call (notes/CCs/transport), not just audio;
- report instrument metadata (category, buses, latency, tail);
- never throw across the C boundary;
- add its license to `THIRD-PARTY-NOTICES.txt` before shipping.

The helper advertises a format in `HelloAck` only when the corresponding host
is available for that RID.
