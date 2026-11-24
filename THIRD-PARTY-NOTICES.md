# Third-party notices

FlowGuard includes or depends on the following components.

## WinDivert 1.4 (`WinDivert.dll`, `WinDivert64.sys`)

- Project: https://reqrypt.org/windivert.html, https://github.com/basil00/Divert
- License: dual-licensed under the **GNU Lesser General Public License (LGPL) version 3** or the
  **GNU General Public License (GPL) version 2**. FlowGuard uses it under the LGPL-3.0.
- Shipped unmodified as separate files next to `FlowGuard.exe`. You may replace them with another
  compatible build.

## WinDivertSharp 1.4.3.2 (`WinDivertSharp.dll`)

- Project: https://github.com/TechnikEmpire/WinDivertSharp
- License: dual-licensed under the **LGPL version 3** or the **GPL version 2**. FlowGuard uses it
  under the LGPL-3.0.
- Shipped unmodified as a separate file next to `FlowGuard.exe` (not inside the single-file
  bundle), so it can be replaced.

Full license texts are in the `licenses` folder: `lgpl-3.0.txt`, `gpl-3.0.txt` (the LGPL-3.0
builds on it) and `gpl-2.0.txt`. They are unmodified copies from https://www.gnu.org/licenses/.

## WPF-UI 4.3.0 and WPF-UI.Tray 4.3.0

- Project: https://github.com/lepoco/wpfui
- License: **MIT**. Copyright (c) Leszek Pomianowski and WPF UI Contributors.

## CommunityToolkit.Mvvm 8.4.2

- Project: https://github.com/CommunityToolkit/dotnet
- License: **MIT**. Copyright (c) .NET Foundation and Contributors.

## .NET runtime 10.0 (bundled inside `FlowGuard.exe`)

- Project: https://github.com/dotnet/runtime, https://github.com/dotnet/wpf
- License: **MIT**. Copyright (c) .NET Foundation and Contributors. The runtime also carries its
  own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## System.Drawing.Common (via WPF-UI.Tray)

- License: **MIT**. Copyright (c) .NET Foundation and Contributors.
