using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notificationsaudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudienceSegment",
                table: "MarketingBroadcasts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContentJson",
                table: "MarketingBroadcasts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "MarketingBroadcasts",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Preheader",
                table: "MarketingBroadcasts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplyToEmail",
                table: "MarketingBroadcasts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledAtUtc",
                table: "MarketingBroadcasts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "MarketingBroadcasts",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcasts_Status_ScheduledAtUtc",
                table: "MarketingBroadcasts",
                columns: new[] { "Status", "ScheduledAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketingBroadcasts_Status_ScheduledAtUtc",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "AudienceSegment",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "ContentJson",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "Preheader",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "ReplyToEmail",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "ScheduledAtUtc",
                table: "MarketingBroadcasts");

            migrationBuilder.DropColumn(
                name: "SenderName",
                table: "MarketingBroadcasts");
        }
    }
}
