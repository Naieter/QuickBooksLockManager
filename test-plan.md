# QuickBooks Lock Manager — Test Plan

## 1. Unit Tests (automated, `QBLockService.Tests`)

| Test | Scenario | Expected |
|------|----------|----------|
| Acquire_NewFile | Fresh file, no lock | Status=Acquired, Lock non-null |
| Acquire_LockedByOther | File already locked by user2 | Status=Denied, CurrentHolder=user1 |
| Acquire_SameSession | Same AppInstanceId re-acquires | Status=AlreadyOwned, Success=true |
| Heartbeat_ActiveLock | Valid lock + correct AppInstanceId | Success=true |
| Heartbeat_WrongAppInstance | Correct LockId, wrong AppInstanceId | Success=false |
| Heartbeat_NonexistentLock | Random LockId | Success=false |
| Release_OwnedLock | Release own lock | Success=true |
| Release_AfterRelease_FileAvailable | Release then other user acquires | Success=Acquired |
| Release_WrongAppInstance | Release someone else's lock | Success=false |
| Acquire_StaleLock | Heartbeat > timeout | Old lock Expired, new Acquired |
| ExpireStale_BackgroundJob | Background runner with stale lock | Lock.Status=Expired |
| ForceUnlock_ActiveLock | Admin force-releases | Success, Status=ForceReleased |
| ForceUnlock_WritesAudit | Force unlock | AuditLog contains ForceUnlock event |
| MultipleFiles_SameUser | Two files, same appInstanceId | Two Active locks |
| ReleasingOneLock_NotOther | Release one of two | Remaining lock still active |
| OtherUser_BlockedFromBoth | Two locks by alice | bob denied both |
| Switch_AcquireNewBeforeOld | Alice switches files | Both locks active, then A released |
| Switch_WhenTargetLocked | Target locked by bob | Switch denied, old lock preserved |
| GetAllLocks_OnlyActive | One released, one active | Returns 1 |
| AuditLog_AcquireRelease | Acquire then release | Both events in log |

## 2. Integration Tests (automated, `QBLockService.Tests.IntegrationTests`)

| Test | Scenario |
|------|----------|
| TwoUsers_SimultaneousAcquire | Concurrent requests, exactly one wins |
| TwoUsers_SequentialAcquire | Sequential, second denied |
| UserA_Releases_ThenUserB_CanAcquire | Full lifecycle |
| FileSwitching_AcquireNewBeforeReleasingOld | Switch with both locks temporarily held |
| FileSwitching_WhenTargetIsLocked | Switch blocked, old lock preserved |
| MultiFileModeUser_HoldsTwoLocks | Alice holds two, bob blocked from both, alice releases one |
| StaleRecovery_BackgroundExpiry | Stale → expired → bob acquires |
| AdminForceUnlock | Force unlock + audit record |

## 3. Manual Smoke Tests (on real workstations)

### 3.1 Basic Launch
- [ ] Start QBLockService on server, verify `/health` returns 200
- [ ] Start QBLockManager on workstation, verify "Connected" status
- [ ] Configure watched folder with at least one .QBW file
- [ ] Verify file appears in the list with status "Available"

### 3.2 Lock Acquisition
- [ ] Click Open on an available file
- [ ] Verify QuickBooks launches with the correct file
- [ ] Verify file status changes to "Locked by me"
- [ ] Verify "My Active Locks" section shows the file

### 3.3 Lock Denial (two workstations)
- [ ] User A opens Company.qbw
- [ ] On User B's workstation, verify Company.qbw shows "In use by [User A] on [PC-A]"
- [ ] User B clicks Open — verify QuickBooks does NOT launch
- [ ] Verify message shows User A's name, machine, and lock time

### 3.4 Lock Release
- [ ] User A uses "Release Lock" in My Active Locks
- [ ] Verify lock is released on User A's screen
- [ ] Verify User B's screen shows "Available" after refresh
- [ ] User B opens the file successfully

### 3.5 File Switching
- [ ] User A opens Company A
- [ ] User A clicks "Switch File" → selects Company B
- [ ] Verify lock acquired for B while A still locked
- [ ] User A confirms closing A → releases A
- [ ] Verify only Company B lock remains

### 3.6 Lock Service Unavailable
- [ ] Stop the lock service
- [ ] On workstation, click Refresh
- [ ] Verify status shows "OFFLINE"
- [ ] Click Open on a file — verify blocked with correct message

### 3.7 Stale Lock Recovery
- [ ] User A opens a file, then disconnect their network or kill QBLockManager
- [ ] Wait past the timeout (default 5 minutes)
- [ ] On another workstation, verify file shows "Stale lock"
- [ ] Wait another minute for background expiration, then verify file shows "Available"

### 3.8 Admin Force Unlock
- [ ] Enable Admin Mode in settings with correct admin API key
- [ ] Select a file locked by another user
- [ ] Click "Admin Force Unlock"
- [ ] Confirm warning dialog
- [ ] Verify lock released
- [ ] Open Audit Log and verify ForceUnlock event appears

### 3.9 Multi-File Mode
- [ ] Enable MultiFileMode in settings
- [ ] Open File A
- [ ] Click "Open Another Company File" → select File B
- [ ] Verify both files appear in "My Active Locks"
- [ ] Verify another user is blocked from both
- [ ] Release File A, verify only File B remains locked

## 4. Edge Case Tests

### 4.1 File Not Synced
- [ ] Reference a .QBW path that doesn't exist locally
- [ ] Verify message: "This file is not currently available on this workstation"

### 4.2 QuickBooks Not Found
- [ ] Set QBPath to a non-existent path
- [ ] Click Open
- [ ] Verify message: "QuickBooks could not be found..."

### 4.3 Race Condition (manual approximation)
- [ ] Have two users click Open on the same file within 1 second of each other
- [ ] Verify only one succeeds (the other sees "In use by...")

### 4.4 Machine Restart / Crash Recovery
- [ ] User A opens a file, then hard-restart their machine
- [ ] Wait for heartbeat timeout
- [ ] User B should be able to acquire after timeout expires

## 5. Security Tests

- [ ] Attempt API call without X-Api-Key — verify 401
- [ ] Attempt force-release without X-Admin-Key — verify 403
- [ ] Verify no .QBW file content is ever stored in the database
- [ ] Verify API only returns metadata (paths, usernames), not file contents
