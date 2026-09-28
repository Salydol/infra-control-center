using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icc.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CertificateSerial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CertificateNotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "join_tokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedByAgentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_join_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_inventories",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_inventories", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_agent_inventories_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_Hostname",
                table: "agents",
                column: "Hostname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_inventories");

            migrationBuilder.DropTable(
                name: "join_tokens");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
