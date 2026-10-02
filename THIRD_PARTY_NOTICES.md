# Third-party notices

The root MIT license covers original Mansur material only. The following
materials retain their own copyrights and licenses. No third-party input engine
is included in the original input core. This inventory is not a legal approval
of separately downloaded models or an assertion of vendor endorsement.

| Material | Owner / license | Source and retained notice |
| --- | --- | --- |
| AOSP PinyinIME pronunciation and frequency data | Copyright (c) 2009, The Android Open Source Project; Apache-2.0 | [Pinned upstream](https://android.googlesource.com/platform/packages/inputmethods/PinyinIME/+/49aebad1c1cfbbcaa9288ffed5161e79e57c3679/). Complete copyright/license: `licenses/AOSP-NOTICE.txt`. Data is downloaded by `scripts/prepare_lexicon.py`, converted and supplemented; generated corpus provenance records the changes and hashes. It is not relicensed as MIT. |
| jieba dictionary data | Copyright (c) 2013 Sun Junyi; MIT | [Pinned upstream](https://github.com/fxsjy/jieba/tree/67fa2e36e72f69d9134b8a1037b83fbb070b9775). Complete license: `licenses/JIEBA-MIT.txt`. Only word/frequency data is used; the segmentation engine is not imported. |
| Lucide toolbar SVGs and generated PNGs | Lucide Icons and Contributors; ISC, with retained Feather MIT notice / Cole Bemis | `desktop/iconassets/lucide/LICENSE`, `REVISION.txt`, `sources.json` and upstream SVGs. [Upstream](https://github.com/lucide-icons/lucide/tree/5a92b9ba262de5bf10e864219883267672c05db8). PNGs are build-time transformations of these SVGs. The self-authored renderer script has its own MIT header; this does not change the artwork license. |
| LobeHub vendor artwork | Copyright (c) 2023 LobeHub; MIT | `desktop/iconassets/services/LICENSE` and `sources.json`. [Upstream](https://github.com/lobehub/lobe-icons). Fixed `@lobehub/icons-static-png` 1.97.1 package, verified integrity and file hashes. Vendor names and trademarks remain with their owners. |
| Product icon supplied by the project owner | Mansur; project MIT grant for supplied artwork | `desktop/iconassets/product/SOURCE.txt`. Original and size conversions are preserved. This records the supplied image and permitted project use, not an independent verification of its creation history. |

## Optional dependencies, not redistributed by the source export

Local inference may use llama.cpp, Kokoro-ONNX, ONNX Runtime, NumPy and separately
downloaded translation/voice weights. They are not made MIT merely because
Mansur is MIT. Before distributing a model/runtime bundle, inventory every
included dependency and weight, verify its commercial redistribution terms,
retain required notices, and record source versions and hashes. API credentials
and purchased service access are personal configuration, not repository assets.

Pinned download sources, checksums and configuration instructions are in
[LOCAL_MODELS.md](docs/LOCAL_MODELS.md) and
[local-model-downloads.json](docs/local-model-downloads.json). The baseline uses
Qwen3-4B-Instruct-2507 (Apache-2.0), an Unsloth GGUF conversion, llama.cpp b11146
(MIT core with separate bundled component notices), and Kokoro v1.0 model/voice
assets (Apache-2.0). Kokoro-ONNX conversion code is MIT; its phonemizer and eSpeak
NG dependency chain includes GPL components. Separately distributing a speech
runtime requires reviewing those components and applicable source obligations;
the original Mansur MIT grant does not replace them.

Source export intentionally excludes downloaded dictionary bodies and generated
binary dictionaries. Build them from the pinned sources; the generator writes
`NOTICE-AOSP.txt`, `NOTICE-JIEBA.txt` and `lexicon-manifest.json`. Binary application
distributions that include the dictionary must preserve those generated notices
in addition to the original project license. The application build embeds the
project license and icon license resources; the package script also copies legal
notices as readable files.
