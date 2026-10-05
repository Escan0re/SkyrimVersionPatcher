# External components and technical references

## Project license

Copyright (C) 2026 Escan0re. Original Skyrim Version Patcher code is licensed under
the GNU General Public License, version 3 (GPL-3.0-only); see [LICENSE](LICENSE).
It is provided without warranty. Independent third-party components retain their
respective licenses and notices listed below.

## Steam

The application uses the user's installed Steam client and existing session without shutting down or restarting the client. Steam is not bundled.

A full Skyrim installation is not bundled. Steam credentials, depot keys and an independently authenticated Steam session are not bundled. Locally prepared binary transition packages contain metadata and changed byte ranges; they are applicable only to the exact source file hashes described by each package. They do not replace access to the original game.

- Steam browser protocol: [Valve Developer Community](https://developer.valvesoftware.com/wiki/Steam_browser_protocol)
- Steam Console accessibility: [Microsoft UI Automation overview](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview)
- Accessibility activation: [AccessibleObjectFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow)
- Standard MSAA fallback: [Microsoft Active Accessibility](https://learn.microsoft.com/en-us/windows/win32/winauto/microsoft-active-accessibility), [Windows IAccessible declaration](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/oleacc.h) and [IAccessible2 interface declaration](https://github.com/LinuxA11y/IAccessible2/blob/master/api/Accessible2.idl). COM declarations are used for interoperability; no separate accessibility library is bundled.
- Chromium 126 runtime accessibility activation: [legacy renderer window](https://github.com/chromium/chromium/blob/126.0.6478.183/content/browser/renderer_host/legacy_render_widget_host_win.cc) and [Windows accessibility state](https://github.com/chromium/chromium/blob/126.0.6478.183/content/browser/accessibility/browser_accessibility_state_impl_win.cc)
- Chromium's Windows accessibility properties: [AXPlatformNodeWin source](https://github.com/chromium/chromium/blob/main/ui/accessibility/platform/ax_platform_node_win.cc)
- Asynchronous accessibility action acknowledgements: [Chromium Windows accessibility tests](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/browser/accessibility/accessibility_win_browsertest.cc). The application waits for confirmed focus and value before submitting console input; no Chromium code is bundled.
- Scoped console input uses accessibility patterns and guarded Windows input: [SendInput reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- Steam's built-in console JavaScript API: [Valve Steamworks, Steam Cloud](https://partner.steamgames.com/doc/features/cloud)
- Local Chromium debugging protocol: [Chrome DevTools Runtime](https://chromedevtools.github.io/devtools-protocol/tot/Runtime/)
- Steam CEF transport reference: [steam-debug source](https://github.com/kmturley/steam-debug/blob/main/steam-debug.mjs). The project is not linked or bundled; this application implements its own restricted console transport with .NET.

The Steam Console DOM class identifiers are determined from public static UI files of the user's installed client. The application does not inspect Steam process memory, saved credentials, or authentication JavaScript APIs. CDP is accepted only through an already available owner-verified loopback listener. No debug startup flags, client shutdown commands or client restart fallback are used.

The optional file and chunk integrity readers follow the publicly documented wire structure in [SteamKit's manifest source](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/Types/DepotManifest.cs) and [SteamKit's generated content manifest schema](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/Base/Generated/ContentManifest.cs). They are local format readers; SteamKit binaries are not linked. Reused installed ranges are verified against public target SHA1 records and assembled into a separate cache. This implementation does not independently authenticate to Steam or request individual missing CDN chunks: an incomplete locally recoverable depot falls back to Steam's full `download_depot` command. Hashes and local receipts check content integrity and are not a replacement for Steam authorization or an independent cryptographic proof of manifest origin.

## Catalog, executable hashes and transitions

Only Skyrim 1.6.1170 and 1.5.97 are supported installation targets. The application uses its embedded metadata catalog; no remote catalog service or remote catalog cache is included.

The embedded executable reference database is currently empty. References for the two supported targets can be learned in memory from a verified Steam depot cache and its matching public manifest. Windows VersionInfo alone is not proof of an official executable.

The runtime metadata reader prefers numeric FileVersion/ProductVersion strings over fixed Windows version fields. These are independent resource values: [Microsoft explanation](https://devblogs.microsoft.com/oldnewthing/20180529-00/?p=98855) and [.NET Windows FileVersionInfo implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.FileVersionInfo/src/System/Diagnostics/FileVersionInfo.Windows.cs). Exact hashes and manifest checks remain separate from the metadata reader.

The `.svpt` binary transition format, its builder, and its staging implementation are original project code using .NET's ZIP and hashing APIs. Reliquary and the Nexus downgrade patchers are design references, not linked libraries. This project does not incorporate their binaries or patch format. No prepared binary transition packages are bundled. A user-provided `RuntimeOnly` package must target one of the two supported versions; a mixed runtime/data installation must not be represented as a complete target release.

## .NET

The single executable includes the .NET 10 and WPF runtimes, under Microsoft's licenses and notices. The silent launcher extracts them under the current user's LocalAppData directory.

- [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET runtime third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)
- [WPF third-party notices](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT)

## NSIS

The silent launcher is built with unmodified [NSIS 3](https://nsis.sourceforge.io/Download) and its non-solid LZMA compression module. NSIS licenses are documented in [the NSIS manual](https://nsis.sourceforge.io/Docs/AppendixI.html); the corresponding source distribution is available from the official download page. NSIS plug-ins are not bundled.

Skyrim and Steam belong to their respective owners. This project is not an official Bethesda or Valve product.
