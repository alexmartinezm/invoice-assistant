using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableActionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommandHash",
                table: "pending_actions",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ExpectedResourceRevision",
                table: "pending_actions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionReason",
                table: "pending_actions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "action_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PendingActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CommandHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ErrorDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_executions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_ConversationId",
                table: "action_executions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_PendingActionId",
                table: "action_executions",
                column: "PendingActionId",
                unique: true,
                filter: "\"PendingActionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_Status_NextAttemptAt",
                table: "action_executions",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_UserId_CreatedAt",
                table: "action_executions",
                columns: new[] { "UserId", "CreatedAt" });

            // The empty default on CommandHash stays on purpose: during a rolling deployment the
            // previous build is still inserting proposals, and a NOT NULL column with no default
            // would turn its writes into 500s. The unfingerprinted rows it produces are refused at
            // approval time instead — see the deployment_upgrade branch in ActionResolver. Schema
            // first, behaviour second; a later migration can enforce it once no old build is left.

            // Proposals already open when this runs carry no fingerprint and no captured revision,
            // so there is nothing to bind an approval to. They live five minutes; the deployment
            // closes the handful in flight rather than replaying a command it cannot verify.
            migrationBuilder.Sql(
                """
                UPDATE pending_actions
                SET "Status" = 'Expired',
                    "ResolutionReason" = 'DeploymentUpgrade',
                    "ResolvedAt" = now()
                WHERE "Status" = 'Pending'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_executions");

            migrationBuilder.DropColumn(
                name: "CommandHash",
                table: "pending_actions");

            migrationBuilder.DropColumn(
                name: "ExpectedResourceRevision",
                table: "pending_actions");

            migrationBuilder.DropColumn(
                name: "ResolutionReason",
                table: "pending_actions");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "audit_events");
        }
    }
}
