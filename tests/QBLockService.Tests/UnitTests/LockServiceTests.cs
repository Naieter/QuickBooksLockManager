using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QBLockService.Models;
using QBLockService.Services;
using Xunit;

namespace QBLockService.Tests.UnitTests;

public class LockServiceTests
{
    // ─── Acquire ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Acquire_NewFile_ReturnsAcquired()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();

        var result = await svc.AcquireAsync(req);

        result.Status.Should().Be("Acquired");
        result.Success.Should().BeTrue();
        result.Lock.Should().NotBeNull();
        result.Lock!.FileKey.Should().Be(req.FileKey);
        result.Lock.UserName.Should().Be(req.UserName);
    }

    [Fact]
    public async Task Acquire_WhenAlreadyLockedByOtherUser_ReturnsDenied()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var user1 = TestHelpers.MakeRequest(userName: "user1");
        await svc.AcquireAsync(user1);

        var user2 = TestHelpers.MakeRequest(userName: "user2", machine: "PC-002");
        var result = await svc.AcquireAsync(user2);

        result.Status.Should().Be("Denied");
        result.Success.Should().BeFalse();
        result.CurrentHolder.Should().NotBeNull();
        result.CurrentHolder!.UserName.Should().Be("user1");
    }

    [Fact]
    public async Task Acquire_SameSession_ReturnsAlreadyOwned()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "same-session-id";
        var req = TestHelpers.MakeRequest(appInstanceId: appId);
        await svc.AcquireAsync(req);

        var req2 = TestHelpers.MakeRequest(appInstanceId: appId);
        var result = await svc.AcquireAsync(req2);

        result.Status.Should().Be("AlreadyOwned");
        result.Success.Should().BeTrue();
    }

    // ─── Heartbeat ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_ActiveLock_ReturnsSuccess()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var hb = await svc.HeartbeatAsync(new HeartbeatRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = req.AppInstanceId
        });

        hb.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_WrongAppInstance_ReturnsFalse()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var hb = await svc.HeartbeatAsync(new HeartbeatRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = "wrong-instance"
        });

        hb.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_NonexistentLock_ReturnsFalse()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var hb = await svc.HeartbeatAsync(new HeartbeatRequest
        {
            LockId = "does-not-exist",
            AppInstanceId = "any"
        });
        hb.Success.Should().BeFalse();
    }

    // ─── Release ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_OwnedLock_Succeeds()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var release = await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = req.AppInstanceId
        });

        release.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Release_AfterRelease_FileBecomesAvailable()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = req.AppInstanceId
        });

        // Another user should now be able to acquire
        var req2 = TestHelpers.MakeRequest(userName: "user2");
        var result2 = await svc.AcquireAsync(req2);
        result2.Status.Should().Be("Acquired");
    }

    [Fact]
    public async Task Release_WrongAppInstance_Fails()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var release = await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = "wrong-app"
        });

        release.Success.Should().BeFalse();
    }

    // ─── Stale Expiration ───────────────────────────────────────────────────

    [Fact]
    public async Task Acquire_StaleLock_ExpiresAndGrantsNewLock()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync(timeoutMinutes: 5);
        var req1 = TestHelpers.MakeRequest(userName: "user1");
        var acquired = await svc.AcquireAsync(req1);

        // Backdate the heartbeat to simulate staleness
        var lock_ = db.ActiveLocks.First(l => l.LockId == acquired.Lock!.LockId);
        lock_.LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        var req2 = TestHelpers.MakeRequest(userName: "user2");
        var result = await svc.AcquireAsync(req2);

        result.Status.Should().Be("Acquired");
        result.Success.Should().BeTrue();

        // Old lock should be expired — AsNoTracking bypasses the stale cached entity
        db.ActiveLocks.AsNoTracking().First(l => l.LockId == acquired.Lock!.LockId).Status
            .Should().Be(LockStatus.Expired);
    }

    [Fact]
    public async Task ExpireStale_BackgroundJob_ExpiresOldLocks()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync(timeoutMinutes: 5);
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var lock_ = db.ActiveLocks.First(l => l.LockId == acquired.Lock!.LockId);
        lock_.LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        await svc.ExpireStaleLocksAsync();

        db.ActiveLocks.AsNoTracking().First(l => l.LockId == acquired.Lock!.LockId).Status
            .Should().Be(LockStatus.Expired);
    }

    // ─── Force Unlock ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForceUnlock_ActiveLock_Succeeds()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        var result = await svc.ForceReleaseAsync(new ForceReleaseRequest
        {
            LockId = acquired.Lock!.LockId,
            AdminUserName = "admin",
            Reason = "User confirmed file closed."
        });

        result.Success.Should().BeTrue();
        db.ActiveLocks.First(l => l.LockId == acquired.Lock!.LockId).Status
            .Should().Be(LockStatus.ForceReleased);
    }

    [Fact]
    public async Task ForceUnlock_WritesAuditRecord()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);

        await svc.ForceReleaseAsync(new ForceReleaseRequest
        {
            LockId = acquired.Lock!.LockId,
            AdminUserName = "admin",
            Reason = "Test."
        });

        var audit = db.AuditLogs.FirstOrDefault(a =>
            a.EventType == AuditEventType.ForceUnlock &&
            a.LockId == acquired.Lock!.LockId);

        audit.Should().NotBeNull();
        audit!.Details.Should().Contain("admin");
    }

    // ─── Multiple Locks (same user/session) ─────────────────────────────────

    [Fact]
    public async Task MultipleFiles_SameUser_CanHoldTwoActiveLocks()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "multi-file-session";

        var r1 = await svc.AcquireAsync(TestHelpers.MakeRequest("file1.qbw", appInstanceId: appId));
        var r2 = await svc.AcquireAsync(TestHelpers.MakeRequest("file2.qbw", appInstanceId: appId));

        r1.Status.Should().Be("Acquired");
        r2.Status.Should().Be("Acquired");

        var myLocks = await svc.GetMyLocksAsync(appId);
        myLocks.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReleasingOnelock_DoesNotRelease_OtherLock()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "multi-file-session";

        var r1 = await svc.AcquireAsync(TestHelpers.MakeRequest("file1.qbw", appInstanceId: appId));
        var r2 = await svc.AcquireAsync(TestHelpers.MakeRequest("file2.qbw", appInstanceId: appId));

        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = r1.Lock!.LockId,
            AppInstanceId = appId
        });

        var myLocks = await svc.GetMyLocksAsync(appId);
        myLocks.Should().HaveCount(1);
        myLocks[0].LockId.Should().Be(r2.Lock!.LockId);
    }

    [Fact]
    public async Task OtherUser_BlockedFromBothFiles_WhenUserHoldsTwoLocks()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "multi-file-session";

        await svc.AcquireAsync(TestHelpers.MakeRequest("file1.qbw", appInstanceId: appId));
        await svc.AcquireAsync(TestHelpers.MakeRequest("file2.qbw", appInstanceId: appId));

        var other1 = await svc.AcquireAsync(TestHelpers.MakeRequest("file1.qbw", userName: "other", machine: "PC-X"));
        var other2 = await svc.AcquireAsync(TestHelpers.MakeRequest("file2.qbw", userName: "other", machine: "PC-X"));

        other1.Status.Should().Be("Denied");
        other2.Status.Should().Be("Denied");
    }

    // ─── File Switching ─────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_AcquireNewBeforeReleasingOld_BothLocksActive()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "switch-session";

        // User acquires file A
        var rA = await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", appInstanceId: appId));
        rA.Status.Should().Be("Acquired");

        // User switches to file B — acquires B first
        var rB = await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", appInstanceId: appId));
        rB.Status.Should().Be("Acquired");

        // Both locks held while user closes old file
        var myLocks = await svc.GetMyLocksAsync(appId);
        myLocks.Should().HaveCount(2);

        // User confirms old file closed — release A
        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = rA.Lock!.LockId,
            AppInstanceId = appId
        });

        myLocks = await svc.GetMyLocksAsync(appId);
        myLocks.Should().HaveCount(1);
        myLocks[0].FileKey.Should().Be("companyB.qbw");
    }

    [Fact]
    public async Task Switch_WhenTargetFileLocked_OldLockRemains()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appIdA = "user-a-session";
        var appIdB = "user-b-session";

        // User B holds companyB
        await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", userName: "userB", appInstanceId: appIdB));

        // User A holds companyA
        await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", userName: "userA", appInstanceId: appIdA));

        // User A tries to switch to companyB — must be denied
        var switchResult = await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", userName: "userA", appInstanceId: appIdA));
        switchResult.Status.Should().Be("Denied");

        // User A's lock on companyA should still be active
        var aLocks = await svc.GetMyLocksAsync(appIdA);
        aLocks.Should().HaveCount(1);
        aLocks[0].FileKey.Should().Be("companyA.qbw");
    }

    // ─── GetAllLocks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllLocks_ReturnsOnlyActiveLocks()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var appId = "list-test";

        var r = await svc.AcquireAsync(TestHelpers.MakeRequest("file1.qbw", appInstanceId: appId));
        await svc.AcquireAsync(TestHelpers.MakeRequest("file2.qbw", appInstanceId: appId));
        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = r.Lock!.LockId,
            AppInstanceId = appId
        });

        var all = await svc.GetAllLocksAsync();
        all.Should().HaveCount(1);
        all[0].FileKey.Should().Be("file2.qbw");
    }

    // ─── Audit log ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditLog_Contains_AcquireAndRelease()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var req = TestHelpers.MakeRequest();
        var acquired = await svc.AcquireAsync(req);
        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = acquired.Lock!.LockId,
            AppInstanceId = req.AppInstanceId
        });

        var log = await svc.GetAuditLogAsync(100, req.FileKey);
        log.Should().Contain(e => e.EventType == AuditEventType.LockAcquired);
        log.Should().Contain(e => e.EventType == AuditEventType.LockReleased);
    }
}
