using FluentAssertions;
using QBLockService.Models;
using QBLockService.Services;
using Xunit;
using LockStatusEnum = QBLockService.Models.LockStatus;

namespace QBLockService.Tests.IntegrationTests;

/// <summary>
/// Integration tests simulating two concurrent users and multi-file scenarios.
/// These use the same in-memory SQLite service (shared connection) which exercises
/// the partial unique index constraint for atomicity.
/// </summary>
public class LockIntegrationTests
{
    // ─── Scenario 1: Two users try to lock the same file simultaneously ──────

    [Fact]
    public async Task TwoUsers_SimultaneousAcquire_OnlyOneSucceeds()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();

        var user1 = TestHelpers.MakeRequest("company.qbw", "alice", "PC-001");
        var user2 = TestHelpers.MakeRequest("company.qbw", "bob", "PC-002");

        // Fire both concurrently
        var task1 = svc.AcquireAsync(user1);
        var task2 = svc.AcquireAsync(user2);
        await Task.WhenAll(task1, task2);

        var r1 = task1.Result;
        var r2 = task2.Result;

        // Exactly one should have acquired, the other denied
        var acquired = new[] { r1, r2 }.Count(r => r.Status == "Acquired");
        var denied = new[] { r1, r2 }.Count(r => r.Status == "Denied");

        acquired.Should().Be(1, "exactly one user should win the race");
        denied.Should().Be(1, "exactly one user should be denied");
    }

    [Fact]
    public async Task TwoUsers_SequentialAcquire_SecondIsDenied()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();

        var alice = TestHelpers.MakeRequest("company.qbw", "alice", "PC-001");
        var bob = TestHelpers.MakeRequest("company.qbw", "bob", "PC-002");

        var r1 = await svc.AcquireAsync(alice);
        var r2 = await svc.AcquireAsync(bob);

        r1.Status.Should().Be("Acquired");
        r2.Status.Should().Be("Denied");
        r2.CurrentHolder!.UserName.Should().Be("alice");

        var allLocks = await svc.GetAllLocksAsync();
        allLocks.Should().HaveCount(1);
        allLocks[0].UserName.Should().Be("alice");
    }

    [Fact]
    public async Task UserA_Releases_ThenUserB_CanAcquire()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();

        var alice = TestHelpers.MakeRequest("company.qbw", "alice", "PC-001");
        var bob = TestHelpers.MakeRequest("company.qbw", "bob", "PC-002");

        var r1 = await svc.AcquireAsync(alice);
        var denied = await svc.AcquireAsync(bob);
        denied.Status.Should().Be("Denied");

        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = r1.Lock!.LockId,
            AppInstanceId = alice.AppInstanceId
        });

        var r2 = await svc.AcquireAsync(bob);
        r2.Status.Should().Be("Acquired");
        r2.Lock!.UserName.Should().Be("bob");
    }

    // ─── Scenario 2: User switching files ────────────────────────────────────

    [Fact]
    public async Task FileSwitching_AcquireNewBeforeReleasingOld()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var aliceApp = "alice-session";

        // Alice opens Company A
        var rA = await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", "alice", appInstanceId: aliceApp));
        rA.Status.Should().Be("Acquired");

        // Alice requests switch to Company B — acquires B first
        var rB = await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", "alice", appInstanceId: aliceApp));
        rB.Status.Should().Be("Acquired");

        // Both locks held temporarily
        var myLocks = await svc.GetMyLocksAsync(aliceApp);
        myLocks.Should().HaveCount(2);

        // Bob cannot touch either file
        var bobA = await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", "bob", "PC-BOB"));
        var bobB = await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", "bob", "PC-BOB"));
        bobA.Status.Should().Be("Denied");
        bobB.Status.Should().Be("Denied");

        // Alice confirms Company A is closed — releases A
        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = rA.Lock!.LockId,
            AppInstanceId = aliceApp
        });

        // Now Bob can open Company A
        var bobARetry = await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", "bob", "PC-BOB"));
        bobARetry.Status.Should().Be("Acquired");

        // Bob still cannot open Company B (Alice has it)
        var bobBRetry = await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", "bob", "PC-BOB"));
        bobBRetry.Status.Should().Be("Denied");
    }

    [Fact]
    public async Task FileSwitching_WhenTargetIsLocked_OldLockPreserved()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();

        // Bob has Company B
        var bobApp = "bob-session";
        await svc.AcquireAsync(TestHelpers.MakeRequest("companyB.qbw", "bob", appInstanceId: bobApp));

        // Alice has Company A
        var aliceApp = "alice-session";
        await svc.AcquireAsync(TestHelpers.MakeRequest("companyA.qbw", "alice", appInstanceId: aliceApp));

        // Alice tries to switch to Company B — must be denied
        var switchAttempt = await svc.AcquireAsync(
            TestHelpers.MakeRequest("companyB.qbw", "alice", appInstanceId: aliceApp));
        switchAttempt.Status.Should().Be("Denied");
        switchAttempt.CurrentHolder!.UserName.Should().Be("bob");

        // Alice's Company A lock must still be active
        var aliceLocks = await svc.GetMyLocksAsync(aliceApp);
        aliceLocks.Should().HaveCount(1);
        aliceLocks[0].FileKey.Should().Be("companyA.qbw");
    }

    // ─── Scenario 3: One user holds two files, other user blocked from both ──

    [Fact]
    public async Task MultiFileModeUser_HoldsTwoLocks_OtherBlockedFromBoth()
    {
        var (_, svc) = await TestHelpers.CreateInMemoryAsync();
        var aliceApp = "alice-multi";

        // Alice opens two company files (multi-file mode)
        var rA = await svc.AcquireAsync(TestHelpers.MakeRequest("alpha.qbw", "alice", appInstanceId: aliceApp));
        var rB = await svc.AcquireAsync(TestHelpers.MakeRequest("beta.qbw", "alice", appInstanceId: aliceApp));

        rA.Status.Should().Be("Acquired");
        rB.Status.Should().Be("Acquired");

        // Bob tries both files
        var bobA = await svc.AcquireAsync(TestHelpers.MakeRequest("alpha.qbw", "bob", "PC-BOB"));
        var bobB = await svc.AcquireAsync(TestHelpers.MakeRequest("beta.qbw", "bob", "PC-BOB"));

        bobA.Status.Should().Be("Denied");
        bobA.CurrentHolder!.UserName.Should().Be("alice");

        bobB.Status.Should().Be("Denied");
        bobB.CurrentHolder!.UserName.Should().Be("alice");

        // Alice releases alpha only
        await svc.ReleaseAsync(new ReleaseLockRequest
        {
            LockId = rA.Lock!.LockId,
            AppInstanceId = aliceApp
        });

        // Bob can now get alpha
        var bobARetry = await svc.AcquireAsync(TestHelpers.MakeRequest("alpha.qbw", "bob", "PC-BOB"));
        bobARetry.Status.Should().Be("Acquired");

        // Bob still blocked on beta
        var bobBRetry = await svc.AcquireAsync(TestHelpers.MakeRequest("beta.qbw", "bob", "PC-BOB"));
        bobBRetry.Status.Should().Be("Denied");

        // Alice still has beta
        var aliceLocks = await svc.GetMyLocksAsync(aliceApp);
        aliceLocks.Should().HaveCount(1);
        aliceLocks[0].FileKey.Should().Be("beta.qbw");
    }

    // ─── Scenario 4: Stale lock recovery ────────────────────────────────────

    [Fact]
    public async Task StaleRecovery_BackgroundExpiry_AllowsNewAcquisition()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync(timeoutMinutes: 5);

        var alice = TestHelpers.MakeRequest("company.qbw", "alice", "PC-001");
        var r = await svc.AcquireAsync(alice);

        // Simulate no heartbeat for 10 minutes
        var l = db.ActiveLocks.First(x => x.LockId == r.Lock!.LockId);
        l.LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        // Background job runs
        await svc.ExpireStaleLocksAsync();

        // Bob can now acquire
        var bob = TestHelpers.MakeRequest("company.qbw", "bob", "PC-002");
        var r2 = await svc.AcquireAsync(bob);
        r2.Status.Should().Be("Acquired");

        // Audit log should contain stale-expired event
        var log = await svc.GetAuditLogAsync(100, "company.qbw");
        log.Should().Contain(e => e.EventType == AuditEventType.StaleExpired);
    }

    // ─── Scenario 5: Admin force unlock ──────────────────────────────────────

    [Fact]
    public async Task AdminForceUnlock_ReleasesLock_AuditRecordWritten()
    {
        var (db, svc) = await TestHelpers.CreateInMemoryAsync();

        var alice = TestHelpers.MakeRequest("company.qbw", "alice", "PC-001");
        var r = await svc.AcquireAsync(alice);

        var forceResult = await svc.ForceReleaseAsync(new ForceReleaseRequest
        {
            LockId = r.Lock!.LockId,
            AdminUserName = "nathan",
            Reason = "Alice confirmed she closed the file."
        });

        forceResult.Success.Should().BeTrue();
        forceResult.Message.Should().Contain("WARNING");

        // Lock is force-released
        db.ActiveLocks.First(l => l.LockId == r.Lock!.LockId).Status
            .Should().Be(LockStatusEnum.ForceReleased);

        // Bob can now acquire
        var bob = TestHelpers.MakeRequest("company.qbw", "bob", "PC-002");
        var r2 = await svc.AcquireAsync(bob);
        r2.Status.Should().Be("Acquired");

        // Audit
        var log = await svc.GetAuditLogAsync(100, "company.qbw");
        log.Should().Contain(e => e.EventType == AuditEventType.ForceUnlock && e.UserName == "alice");
    }
}
