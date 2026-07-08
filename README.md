# Config Editor for NDI | Tractus Events

`ndi-config-tui` is a Linux text user interface for editing NDI JSON configuration files over a local terminal or SSH session. It mirrors the `raspi-config` / `whiptail` interaction model: an alternate-screen blue backdrop, fixed-height centered dialogs, numbered tag/item menus, compact `<Select>` / `<Back>` / `<Apply>` / `<Finish>` buttons, checkboxes, and mouse-selectable rows. It uses only `System.Console` and `System.Text.Json`, so the production build can be a single executable without shipping the NDI SDK or a terminal UI framework.

The default target is the current NDI configuration file. If `NDI_CONFIG_DIR` is set, the tool uses `$NDI_CONFIG_DIR/ndi-config.v1.json`; otherwise it uses `$HOME/.ndi/ndi-config.v1.json`. Use `--user` to force the `$HOME/.ndi` path or `--file <path>` to edit an arbitrary file.

## Install on x86-64 Linux

```bash
curl -L -o "install-ndi-config-tui-linux-x64.sh" https://u.tractus.ca/nditoolbelt/config_linux_x64 \
  && chmod +x ./install-ndi-config-tui-linux-x64.sh \
  && sudo ./install-ndi-config-tui-linux-x64.sh
```

When run with `sudo`, the installer writes the application to `/opt/tractus/ndi-config` and installs the executable as `/opt/tractus/ndi-config/ndi-config`. It also creates this command symlink:

```text
/usr/local/bin/ndi-config -> /opt/tractus/ndi-config/ndi-config
```

After installation, run the editor with:

```bash
ndi-config
```

## Run

```bash
dotnet run --project Tractus.Ndi.ConfigTui
dotnet run --project Tractus.Ndi.ConfigTui -- --file /path/to/ndi-config.v1.json
```

## Versioning

The application version is defined in `Tractus.Ndi.ConfigTui.csproj`. `dotnet publish` embeds that version into the binary assembly metadata, the TUI shows it in the top-right corner, and `scripts/build-installer.sh` reads the same `<Version>` value for the generated installer.

## Missing Config Behavior

If the target config file does not exist, the editor opens a new in-memory config rooted at `{ "ndi": {} }`. Nothing is created on disk until you apply/save. On save, the parent directory is created automatically, no `.bak` file is written unless the target file already existed, and the new file is populated with explicit defaults for groups, receive transports, and multicast.

Absent config keys are treated as NDI defaults: Send and Receive are in the `Public` group, multicast is disabled with send prefix `239.255.0.0`, mask `255.255.0.0`, TTL `1`, and receive multicast disabled; there are no discovery servers, external sources, source filter, machine name, or preferred NICs; `Multi-TCP`, `UDP`, and `Reliable UDP` receive are enabled.

## Publish a Single Linux Binary

```bash
./scripts/publish-linux-binaries.sh
```

The project enables Native AOT for publish builds. The script produces self-contained executables for x86-64 and AArch64:

- `publish_accessmgr_x64/ndi-config-tui`
- `publish_accessmgr_aarch64/ndi-config-tui`

The Release configuration suppresses native debug symbol sidecars, so each deployment artifact is a single executable. It only edits JSON and does not load any NDI runtime libraries.

## Build the Self-Extracting Installer

```bash
./scripts/build-installer.sh --rid linux-x64
```

This creates `artifacts/install-ndi-config-tui-linux-x64.sh` with the compressed `ndi-config` payload appended to the end of the script. Use `--rid linux-arm64` to create `artifacts/install-ndi-config-tui-linux-arm64.sh` on an AArch64 builder, or on an x86-64 host with the NativeAOT ARM64 cross-linker prerequisites installed.

Run as root for a system install:

```bash
sudo ./artifacts/install-ndi-config-tui-linux-x64.sh
```

The system install writes to `/opt/tractus/ndi-config` and creates this symlink:

```text
/usr/local/bin/ndi-config -> /opt/tractus/ndi-config/ndi-config
```

Run as a normal user for a local extraction:

```bash
./artifacts/install-ndi-config-tui-linux-x64.sh
```

The user install extracts to `./ndi-config` and prints a warning that running through `sudo` or `su` installs to `/opt/tractus/ndi-config`. The installer displays the EULA before installing; pass `--accept-license` only when automating installs where the EULA has already been accepted.

## Keyboard

- Up/Down: move menu selection
- Left/Right or Tab: move action-button focus
- Mouse click: select menu rows and action buttons in terminals with SGR mouse support
- Enter or Space: activate the focused button or selected row
- `1`-`9`: select the numbered menu item
- `A`: add an external source in the External Sources menu
- `D` or Delete: delete the selected external source
- `B`, Backspace, or Esc: go back; Esc exits from the main menu
- Ctrl+S or F10: apply changes to disk
- F1 or `?`: show key help

Before saving an existing file, the editor writes `<file>.bak` unless `--no-backup` is used. Pass `--advanced` to expose Advanced SDK codec and vendor settings.


## Multicast

The Multicast menu exposes one `Enable Multicast` checkbox. Enabling it sets both `multicast.send.enable` and `multicast.recv.enable` in the NDI configuration, and the editor shows a warning first because multicast delivery requires a network configured for multicast, including IGMP Snooping and Querying. Disabling it clears both send and receive multicast flags.

## Reliable UDP Check

The Receive menu exposes `Multi-TCP`, `UDP`, and `Reliable UDP` as independent checkboxes. It also shows a `Reliable UDP System` status row. On Linux, NDI calls out UDP GSO / `UDP_SEGMENT` support as the optimization path for Reliable UDP, and that support arrived in Linux 4.18. If Reliable UDP is enabled on an older kernel, the menu marks the row with `!` and the status detail explains that CPU overhead may be higher.
