# Contributing

Keep changes focused and include regression tests for behavior changes. For bug reports, include macOS, Options+, Logi Plugin Service, and Cortado versions, reproduction steps, and expected versus actual behavior. Remove personal information from any attached logs.

## Development

Follow the build and development-link instructions in [README.md](README.md). All commands below run from the repository root.

The SDK-independent core can be tested with only the .NET 10 SDK, without installing Logitech software:

```sh
dotnet test tests/Cortado.Core.Tests/Cortado.Core.Tests.csproj -c Release
```

GitHub Actions runs those tests on Linux and macOS. Native macOS process tests are skipped on Linux. Full plugin builds and SDK rendering tests require the locally installed Logi Plugin Service on macOS:

```sh
dotnet test CortadoPlugin.sln -c Release
```

For a non-default service location, pass `-p:PluginApiDir=/path/to/MonoBundle` and set `CORTADO_SDK_DIR` to that same directory for SDK tests.

## Source layout

- `core`: session timing, process observation/control, persisted preferences/session recovery, and menu logic.
- `src`: Logitech integration, dynamic folder, and original cup artwork.
- `tests`: core and SDK contract tests.
- `src/package`: package metadata and source icon; build output goes to `bin`.

Keep native button captions separate from artwork and preserve user icon/background customizations. Device navigation, caption placement, and live refresh also need physical MX Creative Keypad verification; automated image tests do not establish hardware compatibility.

Controller disposal leaves its caffeinate process running so plugin and service restarts can recover it. Native process tests must explicitly stop their own verified processes in `finally` blocks, including on assertion failures. Scope test monitoring to test-owned identities; never stop a developer's existing caffeinate sessions. Recovery tests should cover PID/start-time and boot-identity mismatches as well as elapsed countdown time while unloaded.

Keep native launch isolation intact: surviving caffeinate processes must not inherit host descriptors, even ones opened without `FD_CLOEXEC`. The lock-file and control-socket regression tests simulate a host restart while caffeinate remains alive.

When changing `CupRenderer.Icon()`, run the `Icon_RendersOriginalCupForPackaging` SDK test, copy its generated `Artwork/Icon256x256.png` from the test output into `src/package/metadata/Icon256x256.png`, then rebuild. The build reuses this PNG for action-list icons.

## Licensing

Do not commit local settings, credentials, logs, development `.link` files, or Logitech SDK/native assemblies. The SDK comes from each developer's local service installation and is not redistributed by this project.

Contributions are made under the project's [MIT license](LICENSE). Third-party software remains subject to its own license terms.
