using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdempotencyReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ResponseJson",
                table: "idempotency_keys",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "ActionExecutionId",
                table: "idempotency_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "idempotency_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "idempotency_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "idempotency_keys",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_ExpiresAt",
                table: "idempotency_keys",
                column: "ExpiresAt");

            // Rows written by the previous build are settled — they only ever existed once the
            // handler had returned — but they carry no CompletedAt, and the new replay path reads a
            // null one as "still running". Backfilling records what was already true.
            //
            // Their expiry is the window the old code enforced by ignoring them on read: 24 hours.
            // Writing it down makes the lookup and the unique index agree about when a key is free,
            // which they never did before. Until then they replay under the old operation check.
            migrationBuilder.Sql(
                """
                UPDATE idempotency_keys
                SET "CompletedAt" = "CreatedAt",
                    "ExpiresAt" = "CreatedAt" + INTERVAL '24 hours'
                WHERE "CompletedAt" IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_idempotency_keys_ExpiresAt",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "ActionExecutionId",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "idempotency_keys");

            migrationBuilder.AlterColumn<string>(
                name: "ResponseJson",
                table: "idempotency_keys",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
