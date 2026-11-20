# Storage and privacy

## Where NoorVox keeps things

| Class | Default (installed mode) | Default (portable mode) | What's in it |
|---|---|---|---|
| Home (settings, worker registry) | `%APPDATA%\NoorVox` | `<app>\data` | `settings.json`, `runtime\workers\*.json` |
| Models & voices | `%LOCALAPPDATA%\NoorVox\Models` | `<app>\data\Models` | model packages (hundreds of MB to 4.5 GB each), `engines\` (Qwen engine ~5.2 GB, ONNX Runtime CUDA ~330 MB), `voice-profiles\` (cloned voices) |
| Download cache | `%LOCALAPPDATA%\NoorVox\Cache` | `<app>\data\Cache` | `staging\` during downloads; `pip\` (engine package cache, several GB, safe to delete); `hf\`, `torch\`, `numba\` (Qwen engine caches; it runs offline) |
| Temporary audio | `%LOCALAPPDATA%\NoorVox\Temp` | `<app>\data\Temp` | `synth\`, studio previews (auto-deleted after 3 days), `logs\` |
| Projects | `Documents\NoorVox\Projects` | `<app>\data\Projects` | default parent for new projects (a project can live anywhere) |
| Exports | `Documents\NoorVox\Exports` | `<app>\data\Exports` | quick-narration exports, support bundles |
| Interface data | `%LOCALAPPDATA%\NoorVox\WebView2` | `<app>\data\WebView2` | WebView2 profile (set before the browser starts) |
| Recordings | `Documents\NoorVox\Recordings` | `<app>\data\Recordings` | your Voice lab recordings (`<name>.wav` + `.json` with transcript and notes) |

- **Every class is independently configurable** (Settings → Storage, first-run setup, or `noorvox storage set`). **C: is allowed.**
  Any drive works, and the exact resolved folder is always shown.
- The home is chosen in this order: the `NOORVOX_HOME` environment variable, then a `noorvox.portable` file next to the exe (portable mode), then `%APPDATA%\NoorVox`.
- Windows and the Edge WebView2 Runtime may still write small OS-managed files outside these folders (crash reports, runtime updates).
  NoorVox doesn't claim zero writes to C:.

### Storage audit

`noorvox storage audit`, or Settings → **Run storage audit**, lists every location with its size, file count and free space, and marks those on the
system drive (allowed, just informative). It also **finds NoorVox data where your settings don't point**: default folders left by a first run
with default paths, or by another launcher that saw a different AppData. It only reads; it never deletes or moves anything.

> Packaged apps (MSIX, for example) can see a private, virtualised copy of `%APPDATA%`. If NoorVox is started from such an app and from
> Explorer, the two may use different settings. Portable mode avoids this, because the settings live next to the exe.

## Offline and network behaviour

| Action | Network |
|---|---|
| Synthesis, preview, editing, jobs, assembly, export | **none**: models load from disk by path |
| `models install/update`, `voices install` | HTTPS downloads from the catalog URLs (GitHub releases, Hugging Face at pinned revisions), on request only |
| `engines install` | HTTPS from api.nuget.org (Python, ONNX Runtime), pypi.org / files.pythonhosted.org and download.pytorch.org (pip packages, each hash-checked), on request only |
| Qwen synthesis | **none**: the engine starts with `HF_HUB_OFFLINE=1` / `TRANSFORMERS_OFFLINE=1` and loads the model folder by path |
| Update check | none automatic in 0.1.0 (`models update --check` uses the catalog shipped in the app) |
| Telemetry | **none** |

## Privacy

- Scripts, audio and projects never leave the PC. There are no accounts, no cloud TTS and no usage reporting.
- The studio UI is local-only: bundled files, a strict Content-Security-Policy, navigation outside the app blocked, no remote fonts or scripts.
  Only https links to model-source hosts (GitHub, Hugging Face) can be opened, in your browser, when you click them.
- Worker IPC uses named pipes restricted to the current Windows user, plus a per-launch token. Nothing listens on the network.
- **Support bundles** (`noorvox diagnostics bundle` / Settings → Create support bundle…) contain: versions, OS, settings (recent project paths
  reduced to folder names), the storage audit, installed models and hashes, running workers, and the last 1 MB of NoorVox logs. They **never**
  contain scripts, segment text, audio, takes or project files. Logs and paths can still contain folder names from your PC, so review a bundle before sharing it.
- Logs (`<temp>\logs`) record lifecycle events, timings, character counts, voice ids and style directions. They don't record script text.
  `engine-<id>.log` holds pip's output from engine installs.
- **Microphone:** only the studio's own page may use it (every other permission request is denied), only while you record in the Voice
  lab, and Windows shows its microphone indicator meanwhile. Recordings are saved only when you press Save, as plain WAV files in your
  Recordings folder; they are never uploaded and never deleted by NoorVox (Delete sends them to the Recycle Bin).
- **Cloned voices** (voice profiles) hold a copy of the reference recording, its transcript, its sha256, the original file name (not the
  path), and the consent statement you confirmed, with a timestamp. They stay on this PC in `<models>\voice-profiles\`, are never
  uploaded, and are deleted with `noorvox voices remove-profile`. Only clone voices you own or have permission to use.

## Removing NoorVox

- `Uninstall.ps1` in the install folder removes the program and its shortcuts, and lists your data folders without deleting them.
- To remove everything: delete those folders (run `noorvox storage audit` first to see them all) and the home (`%APPDATA%\NoorVox` or `<app>\data`).
