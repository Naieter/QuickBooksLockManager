# QuickBooks Lock Manager

A centralized file-lock manager that prevents multiple users from opening the same QuickBooks Desktop Enterprise Platinum company file simultaneously in a shared/synced drive environment.

---

## IMPORTANT: Read This First

**This tool only protects files opened or switched through the Lock Manager.**

Users who open `.QBW` files directly via File Explorer, QuickBooks recent files, QuickBooks internal menus, desktop shortcuts pointing to QuickBooks directly, or any other method outside the Lock Manager will bypass all protection. If a user opens a file outside the Lock Manager while someone else has it locked through the Lock Manager, a file conflict can still occur.

**Required user workflow:** Always open QuickBooks company files through the QuickBooks Lock Manager. Never open them directly.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Why Centralized Atomic Locking Is Required](#why-centralized-atomic-locking-is-required)
3. [Why Synced-Folder Lock Files Are Unsafe](#why-synced-folder-lock-files-are-unsafe)
4. [Recommendation: QuickBooks Native Multi-User / Database Server Manager](#recommendation-quickbooks-native-multi-user--database-server-manager)
5. [Setup Instructions](#setup-instructions)
6. [Deploying the Lock Service (Server)](#deploying-the-lock-service-server)
7. [Installing the Desktop Launcher (Workstations)](#installing-the-desktop-launcher-workstations)
8. [Configuring Watched Folders](#configuring-watched-folders)
9. [Configuring the QuickBooks Executable Path](#configuring-the-quickbooks-executable-path)
10. [Required User Workflow](#required-user-workflow)
11. [How File Switching Works](#how-file-switching-works)
12. [How Multi-File Mode Works](#how-multi-file-mode-works)
13. [How Lock Timeout and Stale Locks Work](#how-lock-timeout-and-stale-locks-work)
14. [How Admin Force Unlock Works](#how-admin-force-unlock-works)
15. [Bypass Limitation and Training Recommendations](#bypass-limitation-and-training-recommendations)
16. [Known Limitations](#known-limitations)
17. [Build and Run Instructions](#build-and-run-instructions)
18. [API Reference](#api-reference)

---

## Architecture Overview

```
┌─────────────────────────────────────────────┐
│            Workstation (each user)           │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │        QBLockManager (WPF .NET 8)      │  │
│  │                                        │  │
│  │  • Scans synced folder for .QBW files  │  │
│  │  • Shows lock status for each file     │  │
│  │  • Acquires lock BEFORE launching QB   │  │
│  │  • Sends heartbeat every 20 seconds    │  │
│  │  • Releases lock when user is done     │  │
│  └──────────────┬─────────────────────────┘  │
│                 │ HTTP + X-Api-Key            │
└─────────────────┼───────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────┐
│     Lock Server (one always-on machine)      │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │    QBLockService (ASP.NET Core 8)      │  │
│  │                                        │  │
│  │  • REST API for lock acquire/release   │  │
│  │  • SQLite with partial unique index    │  │
│  │  • Background job expires stale locks  │  │
│  │  • Writes full audit log               │  │
│  │  • Admin force-unlock endpoint         │  │
│  └──────────────┬─────────────────────────┘  │
│                 │                             │
│  ┌──────────────▼─────────────────────────┐  │
│  │         SQLite (qblocks.db)            │  │
│  │  • ActiveLocks table                   │  │
│  │  • AuditLog table                      │  │
│  └────────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

**Components:**
- **QBLockService** — ASP.NET Core 8 minimal API running on a central server. Holds all lock state in SQLite. Exposed over HTTP on the LAN/VPN.
- **QBLockManager** — WPF .NET 8 desktop app running on each workstation. The user's front door to QuickBooks company files.

**Stack:** C# / .NET 8 · WPF · ASP.NET Core Minimal API · SQLite · Entity Framework Core 8

---

## Why Centralized Atomic Locking Is Required

QuickBooks Desktop stores all company data in a single `.QBW` file. If two users have the file open simultaneously:

1. User A makes changes and saves.
2. User B (who opened the file slightly earlier or later) saves their version.
3. User B's save overwrites User A's changes entirely — QuickBooks has no merge capability.
4. User A's work is silently lost.

In a synced drive environment (OneDrive, Dropbox, ShareFile, mapped network share with offline sync), this risk is compounded because:

- Each workstation has its own local copy.
- Sync propagation can take 1–60+ seconds.
- Both users may load the file before the other's open is reflected.
- There is no inter-machine file locking at the OS level across sync clients.

A **centralized database with an atomic transaction and a unique constraint** is the only reliable approach: before any file is opened, the workstation must first request and receive a lock from a single authoritative source. If the lock is denied, QuickBooks is never launched.

---

## Why Synced-Folder Lock Files Are Unsafe

A common naive approach is to write a `.lock` file next to the `.QBW` file:

1. Machine A checks for `company.lock` — does not exist.
2. Machine A writes `company.lock`.
3. Machine B checks for `company.lock` — does not exist yet (sync delay).
4. Machine B writes `company.lock`, overwriting Machine A's.
5. Both users open the file simultaneously.

Even on a true network share (no sync delay), file creation is not atomic across the network without distributed lock infrastructure. Lock files in the synced folder are **not a safe solution**.

---

## Recommendation: QuickBooks Native Multi-User / Database Server Manager

**If your environment supports it, QuickBooks Database Server Manager (QBDBSM) is the better long-term solution.**

QuickBooks Desktop Enterprise includes a multi-user mode:

- One machine runs **QuickBooks Database Server Manager**, which hosts the `.QBW` file.
- All other workstations connect to the host over the LAN in multi-user mode.
- QuickBooks itself handles record-level locking.
- No sync, no conflict, no lock manager needed.

**When to prefer QBDBSM:**
- You have a dedicated always-on server or workstation on your LAN.
- All users are on the same LAN or reliable VPN.
- You are willing to move the authoritative `.QBW` file to the host machine (not a synced folder).

**When this lock manager is the right tool:**
- Files must live in a cloud-synced folder (OneDrive, Dropbox, ShareFile, etc.).
- Remote workers use local copies that sync to the cloud.
- A dedicated QB host machine is not practical.
- You want an additional guardrail even if you also run QBDBSM.

**Recommendation:** Evaluate whether you can move to QBDBSM. If you can, do it — it is Intuit's supported multi-user solution. Use this lock manager as a transition tool or as a guardrail in sync-only environments where QBDBSM is not feasible.

---

## Setup Instructions

### Prerequisites

- .NET 8 SDK (on the build machine)
- .NET 8 Runtime (on the server and each workstation)
- Windows 10/11
- QuickBooks Desktop Enterprise installed on each workstation

### Build

```powershell
# From the repo root:
dotnet build QuickBooksLockManager.sln -c Release

# Publish the service (for the server):
dotnet publish src\QBLockService -c Release -o publish\QBLockService

# Publish the desktop app (for workstations):
dotnet publish src\QBLockManager -c Release -o publish\QBLockManager
```

### Run Tests

```powershell
dotnet test tests\QBLockService.Tests --verbosity normal
```

---

## Deploying the Lock Service (Server)

The lock service must run on one always-on machine that all workstations can reach over HTTP.

### Option A: PowerShell installer (recommended)

```powershell
# On the server:
.\Deploy-QBLockService.ps1 `
    -ApiKey "your-long-random-api-key" `
    -AdminApiKey "your-long-random-admin-key" `
    -Port 5100
```

This installs QBLockService as a Windows Service that starts automatically.

### Option B: Manual

1. Copy `publish\QBLockService\` to the server (e.g., `C:\QBLockService\`).
2. Copy `server-appsettings.example.json` to `C:\QBLockService\appsettings.json`.
3. Edit `appsettings.json`:
   - Set `ApiKey` to a long random string.
   - Set `AdminApiKey` to a different long random string.
   - Set `Database.Path` to a path the service has write access to.
   - Set `Urls` to `http://0.0.0.0:5100` (or your preferred port).
4. Install as a Windows Service:
   ```powershell
   sc.exe create QBLockService binpath= "C:\QBLockService\QBLockService.exe" start= auto
   sc.exe start QBLockService
   ```
5. Verify: open `http://SERVERNAME:5100/health` from a workstation browser.

### Firewall

Open inbound TCP port 5100 on the server (or your configured port) for the workstation subnet.

---

## Installing the Desktop Launcher (Workstations)

### Option A: Just copy the .exe (recommended for end users)

1. Run `.\Build-Release.ps1` once on your build machine.
2. Copy `release\QBLockManager\QBLockManager.exe` to the workstation.
3. Create a desktop shortcut named **"QuickBooks Lock Manager"**.
4. The user double-clicks the shortcut. A **setup wizard** launches automatically and asks for:
   - The lock server address (e.g., `http://192.168.1.50:5100`)
   - The access key (from you, the admin)
   - The folder where their QuickBooks files are stored (Browse button)
   - Their name
   - Confirms QuickBooks path (auto-detected or Browse)
5. After setup the main window opens. No JSON editing, no IT knowledge required.

### Option B: Scripted install (for IT/admin deployment)

```powershell
.\Install-QBLockManager.ps1 `
    -ServiceUrl "http://LOCK-SERVER:5100" `
    -ApiKey "same-api-key-as-server" `
    -WatchFolder "C:\Users\$env:USERNAME\OneDrive\CompanyFiles"
```

This copies the app, pre-fills `appsettings.json` (skipping the wizard), and creates desktop and Start Menu shortcuts.

**Remove or rename any existing QuickBooks shortcut** on the desktop and Start Menu so users are not tempted to open QB directly.

---

## Configuring Watched Folders

Edit `appsettings.json` on each workstation:

```json
"WatchedFolders": [
  {
    "Path": "C:\\Users\\alice\\OneDrive\\CompanyFiles",
    "Recursive": false,
    "Label": "Main QB Files"
  }
]
```

- `Path`: Full local path to the folder containing `.QBW` files.
- `Recursive`: Set to `true` to scan subfolders.
- `Label`: Optional display label.

Multiple folders are supported. Add more objects to the array.

### Stable File Key

The file key is how the lock service identifies a file across machines where local paths differ.

Set `SharedRootPath` to the common base path segment. For example, if:
- Alice's path: `C:\Users\alice\OneDrive\CompanyFiles\Main Company.qbw`
- Bob's path: `C:\Users\bob\OneDrive\CompanyFiles\Main Company.qbw`

Set `SharedRootPath` on both machines to `C:\Users\alice\OneDrive\CompanyFiles` (Alice's) and `C:\Users\bob\OneDrive\CompanyFiles` (Bob's). The file key becomes the relative path `main company.qbw`, which is the same on both machines.

If paths are identical across machines (mapped drive), set `SharedRootPath` to that mapped drive path.

---

## Configuring the QuickBooks Executable Path

Set `QuickBooksExePath` in `appsettings.json`:

```json
"QuickBooksExePath": "C:\\Program Files\\Intuit\\QuickBooks Enterprise Solutions 24.0\\QBW32.EXE"
```

Common locations:
- `C:\Program Files\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE`
- `C:\Program Files\Intuit\QuickBooks Enterprise Solutions 23.0\QBW32.EXE`
- `C:\Program Files (x86)\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE`

You can also configure this through the Settings window (⚙ button) inside the app, which has a Browse button.

---

## Required User Workflow

**Users must always open QuickBooks company files through the Lock Manager.**

```
1. Click "QuickBooks Lock Manager" shortcut on the desktop.
2. The app displays all available .QBW files and their lock status.
3. Select the company file you want to open.
4. Click "Open."
5. The app acquires a lock and launches QuickBooks.
6. Work in QuickBooks normally.
7. When done, close or switch the file through the Lock Manager.
8. Click "Release Lock" or use "Switch File" to move to another company.
```

**Do not:**
- Open `.QBW` files by double-clicking them in File Explorer.
- Open company files from QuickBooks' "Open Previous Company" menu.
- Open QuickBooks directly from its own shortcut and then open a company file.
- Open a second company file from inside QuickBooks without going through the Lock Manager.

---

## How File Switching Works

When you need to switch from one company file to another without closing QuickBooks:

1. In the Lock Manager, select the new company file.
2. Click **Switch File**.
3. The app acquires a lock on the new file first.
   - If the new file is locked by someone else, the switch is blocked and your current lock remains active.
   - If the new file is available, the lock is granted and QuickBooks is instructed to open it.
4. Close the previous company file inside QuickBooks.
5. In the Lock Manager's "My Active Locks" section, select the old file's lock.
6. Click **Release Selected Lock**.

Both locks are active briefly during the switch. This is intentional — it prevents another user from opening your old file before you have fully switched away.

---

## How Multi-File Mode Works

QuickBooks Desktop Enterprise supports having multiple company files open simultaneously in some configurations.

To enable multi-file mode:

1. Set `"MultiFileMode": true` in `appsettings.json`, or check the option in Settings.
2. The **Open Another Company File** button becomes available.
3. Each open company file gets its own lock with its own heartbeat.
4. Releasing one file's lock does not affect other open files.
5. Other users are blocked from any file you currently have locked.

---

## How Lock Timeout and Stale Locks Work

If a workstation crashes, loses network, or the Lock Manager is closed without releasing locks, the central service will automatically expire those locks.

**Timeout:** Default 5 minutes without a heartbeat. Configurable in `LockSettings.TimeoutMinutes`.

**Heartbeat:** Every 20 seconds (configurable in `HeartbeatIntervalSeconds`), the desktop app sends a heartbeat to the server for each active lock. If heartbeats stop, the server marks the lock stale after the timeout.

**Stale locks** are shown in amber/orange in the file list, with the message: _"This file has a stale lock from [user] on [computer]. Call or message them to verify lock is stale before overriding."_

After the timeout passes, the background job automatically expires the stale lock and makes the file available.

---

## How Admin Force Unlock Works

Admins can force-release a lock in situations where:
- A user's machine crashed and the timeout hasn't expired yet.
- A user is unavailable and the file needs to be accessed urgently.

**To enable admin mode:**
1. Set `AdminApiKey` in `appsettings.json` (must match the server's `AdminApiKey`).
2. Set `IsAdminMode: true` in `appsettings.json`.
3. Restart the Lock Manager — an **Admin Force Unlock** button will appear.

**Force unlock procedure:**
1. Select the locked file in the file list.
2. Click **Admin Force Unlock**.
3. Read the warning: the original user may still have the file open.
4. Confirm only after verifying with the original user that they are done.
5. An audit record is written with the admin's name, reason, and timestamp.

**WARNING:** Force-unlocking a file while another user still has it open defeats the protection. Always confirm with the original user before force-unlocking.

---

## Bypass Limitation and Training Recommendations

This application only protects files that are opened or switched through the Lock Manager.

**Bypasses not covered:**
- User opens a `.QBW` file from File Explorer.
- User opens a company from QuickBooks' recent company list.
- User opens a company from File > Open Company inside QuickBooks.
- User's machine already has QuickBooks open before using the Lock Manager.
- User creates a shortcut directly to a `.QBW` file.

**Recommended controls:**
1. **Replace desktop shortcuts.** Remove or rename QuickBooks direct shortcuts. Replace them with Lock Manager shortcuts.
2. **User training.** All users must be trained: "Do not open QuickBooks company files directly. Always use the Lock Manager."
3. **Communication.** Post a reminder on the shared drive: `READ ME — Always use QuickBooks Lock Manager.txt`.
4. **Verify QB is not running.** The Lock Manager warns if QuickBooks is already running when you try to open a file.

---

## Known Limitations

1. **No enforcement of bypasses.** The Lock Manager cannot prevent a determined or forgetful user from opening a `.QBW` file outside the app.

2. **QuickBooks process visibility.** QuickBooks Enterprise may reuse an existing process instance or open files inside the same window. The Lock Manager cannot reliably detect which company file is currently open inside QuickBooks. This is why locks require explicit release by the user, not auto-detection of file close.

3. **File rename/move.** If a `.QBW` file is renamed or moved, its file key changes and the old lock record becomes orphaned. Coordinate file renames with users and use Admin Force Unlock to clean up stale orphan locks.

4. **Single lock server.** The lock service is a single point of failure. If it goes down, all file opens are blocked (fail-closed by design). Consider running the service on a reliable server and configuring Windows Service auto-restart.

5. **Sync delay.** The Lock Manager prevents opening conflicts for files opened through it, but does not prevent sync conflicts caused by QuickBooks autosave or backup operations while a file is open.

6. **API key security.** The MVP uses a shared API key. This is simpler than Windows authentication but means anyone who obtains the key can interact with the lock service. Keep the key confidential and rotate it if compromised.

7. **No guarantee of QuickBooks compatibility.** This tool launches QuickBooks with a command-line argument pointing to the company file. This is a standard and documented method, but behavior may vary across QuickBooks versions. Test on your specific QuickBooks Enterprise version.

---

## Build and Run Instructions

### Build Everything

```powershell
dotnet restore
dotnet build QuickBooksLockManager.sln -c Release
```

### Run Tests

```powershell
dotnet test tests\QBLockService.Tests -c Release --verbosity normal
```

### Run Lock Service (development)

```powershell
cd src\QBLockService
dotnet run
# API available at http://localhost:5100
# Swagger UI at http://localhost:5100/swagger
```

### Run Desktop App (development)

```powershell
cd src\QBLockManager
dotnet run
```

### Build single-file executables (recommended for distribution)

```powershell
.\Build-Release.ps1
```

This produces:
```
release\
  QBLockService\QBLockService.exe   ← deploy to server
  QBLockManager\QBLockManager.exe   ← copy to each workstation
```

Both executables are fully self-contained — **no .NET installation required** on the server or workstations. `QBLockManager.exe` shows a setup wizard on first launch.

### End-user workstation deployment (no scripts needed)

1. Copy `release\QBLockManager\QBLockManager.exe` to the workstation (e.g., the desktop or `C:\Program Files\QBLockManager\`).
2. Create a shortcut to it on the desktop named **"QuickBooks Lock Manager"**.
3. Tell the user to double-click it. The setup wizard walks them through the rest.

The wizard collects: lock server address, access key, QuickBooks files folder, their name, and QuickBooks path. No JSON editing required.

---

## API Reference

All endpoints require `X-Api-Key` header (matching `ApiKey` in server config).
Force-release additionally requires `X-Admin-Key` header.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/health` | Health check — no auth required |
| GET | `/api/files/locks` | All currently active locks |
| GET | `/api/locks/mine?appInstanceId=X` | Locks held by this app instance |
| POST | `/api/locks/acquire` | Acquire a lock (atomic) |
| POST | `/api/locks/heartbeat` | Update heartbeat for an active lock |
| POST | `/api/locks/release` | Release a lock you own |
| POST | `/api/locks/force-release` | Admin: force-release any lock |
| GET | `/api/audit?limit=100&fileKey=X` | Audit log entries |

Swagger UI available at `/swagger` in development mode.

---

## Security Notes

- API keys are stored in plaintext in `appsettings.json`. Protect this file with NTFS permissions.
- The lock service stores only file metadata (paths, usernames, timestamps). It never reads or writes `.QBW` file contents.
- Do not expose the lock service port to the public internet. It is intended for LAN/VPN use only.
- Rotate API keys if a workstation is compromised or an employee departs.
- For higher security, implement Windows Integrated Authentication (replace `ApiKeyMiddleware` with `AddAuthentication().AddNegotiate()`) — this is beyond the MVP scope but is a straightforward upgrade path.
