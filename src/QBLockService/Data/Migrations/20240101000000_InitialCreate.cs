using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QBLockService.Data.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ActiveLocks",
            columns: table => new
            {
                LockId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                FileKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                MachineName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                LocalPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                AppInstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                AcquiredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Active"),
                ReleasedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ActiveLocks", x => x.LockId);
            });

        migrationBuilder.CreateTable(
            name: "AuditLogs",
            columns: table => new
            {
                AuditId = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                FileKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                MachineName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                AppInstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                LockId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Details = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLogs", x => x.AuditId);
            });

        // Partial unique index: only one Active lock per FileKey
        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX UQ_ActiveLocks_FileKey_Active ON ActiveLocks(FileKey) WHERE Status = 'Active';");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_TimestampUtc",
            table: "AuditLogs",
            column: "TimestampUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_FileKey",
            table: "AuditLogs",
            column: "FileKey");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ActiveLocks");
        migrationBuilder.DropTable(name: "AuditLogs");
    }
}
