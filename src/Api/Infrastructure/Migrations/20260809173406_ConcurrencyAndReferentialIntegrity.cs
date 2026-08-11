using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConcurrencyAndReferentialIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These four AddColumn calls do not add a column. "xmin" is a system column every
            // Postgres table already has — Npgsql's migration generator recognises the name and the
            // rowVersion flag and emits nothing for it; this scaffolded call is EF's model-diff
            // bookkeeping, not schema. Confirmed by applying this migration and checking \d: no such
            // column appears, and SELECT xmin FROM ... resolves to the table's real one.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "outbox_messages",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "invoice_deliveries",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "ResponseHeadersJson",
                table: "idempotency_keys",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "action_executions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // Foreign keys validate existing rows by default, which would be the wrong migration to
            // write against a table this constraint might not already hold for. It is the right one
            // here: outbox, delivery and execution were introduced together in this same unreleased
            // change, under code that has always written them consistently — there is no prior data
            // for the constraint to conflict with.
            migrationBuilder.CreateIndex(
                name: "IX_invoice_deliveries_ExecutionId",
                table: "invoice_deliveries",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_DeliveryId",
                table: "action_executions",
                column: "DeliveryId");

            migrationBuilder.AddForeignKey(
                name: "FK_action_executions_invoice_deliveries_DeliveryId",
                table: "action_executions",
                column: "DeliveryId",
                principalTable: "invoice_deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_deliveries_action_executions_ExecutionId",
                table: "invoice_deliveries",
                column: "ExecutionId",
                principalTable: "action_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_deliveries_invoices_InvoiceId",
                table: "invoice_deliveries",
                column: "InvoiceId",
                principalTable: "invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_outbox_messages_invoice_deliveries_DeliveryId",
                table: "outbox_messages",
                column: "DeliveryId",
                principalTable: "invoice_deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_action_executions_invoice_deliveries_DeliveryId",
                table: "action_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_deliveries_action_executions_ExecutionId",
                table: "invoice_deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_deliveries_invoices_InvoiceId",
                table: "invoice_deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_outbox_messages_invoice_deliveries_DeliveryId",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_invoice_deliveries_ExecutionId",
                table: "invoice_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_action_executions_DeliveryId",
                table: "action_executions");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "invoice_deliveries");

            migrationBuilder.DropColumn(
                name: "ResponseHeadersJson",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "action_executions");
        }
    }
}
