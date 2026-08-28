using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FileProcessing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    valid_rows = table.Column<int>(type: "integer", nullable: false),
                    invalid_rows = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    errors_truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processing_errors",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    line_number = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    processed_file_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_errors", x => x.id);
                    table.ForeignKey(
                        name: "FK_processing_errors_processed_files_processed_file_id",
                        column: x => x.processed_file_id,
                        principalTable: "processed_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_files_client_received",
                table: "processed_files",
                columns: new[] { "client_id", "received_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_files_client_sha256",
                table: "processed_files",
                columns: new[] { "client_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_files_status",
                table: "processed_files",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_processing_errors_file",
                table: "processing_errors",
                column: "processed_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processing_errors");

            migrationBuilder.DropTable(
                name: "processed_files");
        }
    }
}
