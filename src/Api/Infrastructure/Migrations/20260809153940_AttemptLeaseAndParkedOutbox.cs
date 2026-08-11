using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AttemptLeaseAndParkedOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Widened for AwaitingReconciliation, the status that keeps an ambiguous delivery out of
            // the dispatcher's Pending predicate. Twenty-two characters into a varchar(20) is a
            // failed insert on the one path that must not fail: the one that records "we do not know
            // whether the customer got this".
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "outbox_messages",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            // Nullable, and null means two different things by design: an execution that has never
            // been claimed, and one that is waiting on a delivery the outbox owns. Neither is an
            // attempt somebody is holding, which is the only thing the lease is asked about.
            //
            // Rows already Executing when this runs get null, so they read as delivery-owned rather
            // than abandoned. The ones that are genuinely abandoned settle from their receipt on the
            // next approach, or stay unsettled for a person — never re-executed on a guess.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttemptExpiresAt",
                table: "action_executions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptExpiresAt",
                table: "action_executions");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "outbox_messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);
        }
    }
}
