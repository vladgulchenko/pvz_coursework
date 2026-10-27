using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pvz.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Login = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NormalizedLogin = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.CheckConstraint("CK_Employees_Hash", "length(PasswordHash) > 0");
                    table.CheckConstraint("CK_Employees_Login", "length(trim(Login)) BETWEEN 1 AND 80 AND length(NormalizedLogin) > 0");
                    table.CheckConstraint("CK_Employees_Name", "length(trim(Name)) BETWEEN 1 AND 120");
                    table.CheckConstraint("CK_Employees_Role", "Role IN (0, 1)");
                });

            migrationBuilder.CreateTable(
                name: "StorageLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Rack = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Cell = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageLocations", x => x.Id);
                    table.CheckConstraint("CK_StorageLocations_Cell", "length(Cell) BETWEEN 1 AND 40 AND Cell = upper(trim(Cell))");
                    table.CheckConstraint("CK_StorageLocations_Rack", "length(Rack) BETWEEN 1 AND 40 AND Rack = upper(trim(Rack))");
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    RecipientName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RecipientPhone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StorageUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageLocationId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_Dates", "StorageUntil > ReceivedAt");
                    table.CheckConstraint("CK_Orders_Number", "length(Number) BETWEEN 1 AND 80 AND Number = upper(trim(Number))");
                    table.CheckConstraint("CK_Orders_Placement", "(Status IN (0, 2) AND StorageLocationId IS NOT NULL) OR (Status IN (1, 3) AND StorageLocationId IS NULL)");
                    table.CheckConstraint("CK_Orders_Recipient", "length(trim(RecipientName)) BETWEEN 1 AND 120 AND length(trim(RecipientPhone)) BETWEEN 1 AND 40");
                    table.CheckConstraint("CK_Orders_Status", "Status IN (0, 1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_Orders_StorageLocations_StorageLocationId",
                        column: x => x.StorageLocationId,
                        principalTable: "StorageLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItems", x => x.Id);
                    table.CheckConstraint("CK_OrderItems_Name", "length(trim(Name)) BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_OrderItems_Quantity", "Quantity > 0");
                    table.ForeignKey(
                        name: "FK_OrderItems_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderStatusHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployeeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    NewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReturnReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatusHistory", x => x.Id);
                    table.CheckConstraint("CK_History_Reason", "NewStatus <> 2 OR length(trim(ReturnReason)) > 0 AND ReturnReason IS NOT NULL");
                    table.CheckConstraint("CK_History_Statuses", "NewStatus IN (0, 1, 2, 3) AND (PreviousStatus IS NULL OR PreviousStatus IN (0, 1, 2, 3))");
                    table.ForeignKey(
                        name: "FK_OrderStatusHistory_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderStatusHistory_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_NormalizedLogin",
                table: "Employees",
                column: "NormalizedLogin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OrderId",
                table: "OrderItems",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Number",
                table: "Orders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StorageLocationId",
                table: "Orders",
                column: "StorageLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHistory_EmployeeId",
                table: "OrderStatusHistory",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHistory_OrderId",
                table: "OrderStatusHistory",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocations_Rack_Cell",
                table: "StorageLocations",
                columns: new[] { "Rack", "Cell" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "OrderStatusHistory");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "StorageLocations");
        }
    }
}
