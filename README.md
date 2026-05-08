# NetUM

NetUM is a lightweight WinUI 3 desktop app for monitoring network usage on Windows. It shows live download and upload speed, tracks daily and monthly totals, keeps historical usage data, and can stay in the notification area when the main window is closed.

## Features

- Live download and upload speed display
- Daily and monthly usage tracking
- History and adapter breakdown views
- Duplicate adapter filtering for common Windows lightweight filter drivers
- Notification area icon with restore and exit actions
- Local history persistence in `%APPDATA%\\NetUM`

## Requirements

- Windows 10 version 22H2 or newer, or Windows 11
- .NET 10 SDK
- Windows App SDK dependencies restored through NuGet

## Build

From the repository root:

```powershell
dotnet build NetUM.csproj -p:Platform=x64
```

If NetUM is already running and locking the output executable, use:

```powershell
dotnet build NetUM.csproj -p:Platform=x64 -p:UseAppHost=false
```

## Run

```powershell
dotnet run --project NetUM.csproj -p:Platform=x64
```

## Data Storage

Usage history is stored per user at:

```text
%APPDATA%\NetUM\usage-history.json
```

This file is not part of the repository.

## Project Notes

- Target framework: `net10.0-windows10.0.22621.0`
- Packaging: unpackaged WinUI 3 app (`WindowsPackageType=None`)
- Supported platforms: x64 and ARM64

## Publishing Notes

Before pushing to a public GitHub repository, you should still decide whether to add:

- A `LICENSE` file
- A screenshot or animated demo in the README
- Issue templates or contribution guidelines
