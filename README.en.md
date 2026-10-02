<p align="center"><img src="desktop/iconassets/product/brand.png" width="96" alt="Mansur logo"></p>

# Mansur IME

**Turn everyday typing into English practice.**

A Windows input method with an independently developed Chinese input core.
Compose a sentence, select the words, then press Space three times quickly to
commit it, see its English expression and hear it read aloud. English input stays
in its original form. Select unfamiliar words in the English popup to request a
contextual explanation or slower playback.

**Alpha source · MIT · Copyright (c) 2026 Mansur**

[中文首页](README.md) · [Quick start (Chinese)](docs/QUICK_START.md) ·
[Illustrated manual (Chinese)](docs/USER_GUIDE.md) · [FAQ (Chinese)](docs/FAQ.md)

## Features

- Full pinyin, abbreviated/mixed pinyin, optional fuzzy pronunciation, paged
  candidates and local personal word memory.
- Tap Shift to switch Chinese/English mode without changing input methods.
- Triple Space confirms and learns the current sentence. The Space used to
  select a candidate does not count. Enter commits without learning; pending
  letters are first accepted literally into the draft.
- Select and copy English, replay the sentence, or explicitly request a word
  explanation (Ctrl+D) or selected-text speech (Ctrl+R). Selection alone makes
  no model request.
- Configure translation and speech separately with local models or supported
  API services. Credentials are encrypted for the current Windows user.
- Light/dark/system appearance, horizontal/vertical candidates, font size,
  hideable status bar and personal dictionary import/export.

![English popup](docs/media/english-popup.png)

![Selected-word learning](docs/media/word-learning.png)

Screenshots use production controls and fixed examples, including offscreen
renders. They are not recordings of live API calls. See [media provenance](docs/media/README.md).

## Project poster

<p align="center"><a href="docs/media/mansur-poster.png"><img src="docs/media/mansur-poster.png" width="420" alt="Mansur promotional poster: typing, English translation, speech and word learning"></a></p>

The poster is a promotional illustration; the manual describes current controls.

## Build and configure

**Download ZIP contains source, not an installer.** There is no independently
validated binary release yet. Target: Windows 10/11 x64, with a separate input
component for 32-bit applications. macOS, Linux and Windows ARM64 are not validated.

- [Build instructions](docs/BUILDING.md): Visual Studio 2022, Windows SDK,
  .NET Framework 4.8 and Python. Python 3.12 is the local speech baseline.
- [Local model downloads and checksums](docs/LOCAL_MODELS.md): Qwen GGUF,
  llama.cpp, Kokoro and voice files. Weights and runtimes are not bundled.
- [Provider configuration](docs/DOMESTIC_PROVIDERS.md): independent translation
  and speech services, including Chinese provider presets and compatible APIs.
- [Known limitations and verification scope](docs/KNOWN_ISSUES.md).

Local checks cover 427 desktop checks and 97 Python tests. CI checks public
documentation/assets, Python tests, native x64/Win32 builds and desktop builds.
It does not install the IME, download large models or call real provider accounts.
Clean-machine installation, upgrades, uninstall and cross-application testing
remain separate acceptance work. See [changelog](CHANGELOG.md).

## Privacy and license

Only the confirmed sentence owned by this IME, or explicitly selected text in its
English popup, is sent to the configured service. No host document or clipboard
history is read. Personal word memory stays local. API usage may incur provider
fees. See [privacy](docs/PRIVACY.md).

English is copied for you to paste manually. Automatic editor writeback, message
sending, webpage selection, microphone scoring and vocabulary notebooks are not
implemented.

Original project material uses the [MIT License](LICENSE), allowing commercial
use, modification and redistribution with the copyright and license retained.
[Third-party material](THIRD_PARTY_NOTICES.md), models and dependencies retain
their own licenses. The public author name is Mansur; historical internal
`MansurNext` filenames remain for installation compatibility.

[Report a problem](https://github.com/mabin8121828-crypto/Mansur-IME/issues/new/choose)
using fixed examples and redacted screenshots. Never attach API keys or personal
configuration. [Contributing](CONTRIBUTING.md).
