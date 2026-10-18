# Cortado

A macOS Logi Actions SDK plugin with **one assignable coffee-cup action** for MX Creative Keypad. It runs Apple's `/usr/bin/caffeinate` directly, without a shell, administrator access, or another background application.

## Install

Open `dist/Cortado_<version>.lplug4` to install it through Logi Plugin Service. In Options+, open your MX Creative Keypad customization, find **Cortado** under installed plugins, and assign its **Cortado** action to a slot. Use the default action icon to retain the active/inactive cup; a custom icon can override it.

The plugin will be distributed through the official Logitech marketplace, pending approval. Until then, packages are generated locally; `dist/` is not included in the source checkout.

## Use

Press the cup to choose **Indefinitely**, **Duration**, **Time**, or **Presets**. When active, open it again to stop or adjust the session. **Settings** chooses whether to keep just the Mac awake or also its display. Bindings share one session; existing `caffeinate` processes are also detected.

The caption shows live status. If an old custom icon hides it, reset the action icon to default in Options+. Sessions survive plugin reloads and Logi Plugin Service restarts: Cortado saves the process identity, mode, and timing in `state.json`, then restores controls and the countdown only after verifying the same caffeinate process is still running. Caffeinate runs in an independent process session without inheriting the service's lock files, control sockets, or standard streams, so keeping the Mac awake does not keep service resources open. Missing or replaced processes clear the saved session without launching another one; unrelated caffeinate processes remain external.

**Indefinite sessions keep the Mac awake even while Logi Plugin Service is closed.** Use **Stop** to end them; timed sessions still expire through caffeinate itself while the plugin is absent. A Mac restart ends the process rather than starting a new session. Storage or process-inspection failures are reported in plugin status; recovery cannot be guaranteed if session state cannot be saved. Cortado cannot override lid-close sleep, forced sleep, or macOS power policy.

## Build and develop

Install the **.NET 10 SDK** and the current Options+ service. Install `just` with Homebrew for the command shortcuts below. Commands run from the repository root:

```sh
brew install just
```

### Build and install

```sh
just build
just test
just test-core
```

`just test` runs the full solution tests and requires the locally installed Logi Plugin Service. `just test-core` runs the SDK-independent tests used by CI.

Ordinary builds do not install or reload the plugin. To link the Release build for local development:

```sh
dotnet build src/CortadoPlugin.csproj -c Release -p:InstallDevelopmentLink=true
open -g 'loupedeck:plugin/Cortado/reload'
```

If Cortado does not appear, use Options+ **Restart Logi Plugin Service**. Before switching to packaged installation, remove only `CortadoPlugin.link` from `~/Library/Application Support/Logi/LogiPluginService/Plugins/`.

After moving or renaming the checkout, rerun the development-link build command above to update its absolute path.

The SDK is referenced from `/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle`. For another location, pass `-p:PluginApiDir=/path/to/MonoBundle` and set `CORTADO_SDK_DIR` to that same directory for SDK tests. Logitech SDK and native libraries are not bundled.

### Hot Reloading

```sh
dotnet watch --project src/CortadoPlugin.csproj build -c Release
```

This watches and rebuilds the linked Release output; it does **not** hot-reload the running plugin. After a successful rebuild, run the reload URL above. Reloading preserves the current keep-awake process and restores its session after verifying its identity. Sessions started by older versions remain tied to their original service process and cannot survive that process exiting.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the source layout and testing guidance.

## License

Cortado's source code and original artwork are available under the [MIT license](LICENSE). Logitech software is separately licensed and must be obtained from Logitech.

Built with the [Logi Actions SDK](https://logitech.github.io/actions-sdk-docs/).
