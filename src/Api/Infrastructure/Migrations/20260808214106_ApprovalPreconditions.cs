using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalPreconditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // jsonb → json on the three columns whose bytes are promised back to somebody. jsonb
            // normalises what it stores, so an approval's frozen arguments came back re-ordered and
            // its command hash no longer matched them. The same argument applies to a replayed
            // response: "the first answer" should be the first answer, not an equivalent one.
            // Nothing queries inside any of these, so jsonb was buying nothing.
            migrationBuilder.AlterColumn<string>(
                name: "ArgsJson",
                table: "pending_actions",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");

            // Existing invoices start where a new one does. This is not a count of past changes —
            // nothing recorded those — it is the value an ETag and an If-Match will agree on from
            // now on, and 1 keeps it consistent with what the domain assigns to anything created
            // after this runs.
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<string>(
                name: "ResponseJson",
                table: "idempotency_keys",
                type: "json",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ResultJson",
                table: "action_executions",
                type: "json",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "invoices");

            migrationBuilder.AlterColumn<string>(
                name: "ArgsJson",
                table: "pending_actions",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json");

            migrationBuilder.AlterColumn<string>(
                name: "ResponseJson",
                table: "idempotency_keys",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ResultJson",
                table: "action_executions",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);
        }
    }
}
