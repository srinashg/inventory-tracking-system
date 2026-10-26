using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class InventoryServiceTests : DatabaseTestBase
{
    public InventoryServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<T> RunAsync<T>(Func<InventoryService, Task<T>> action, ListLogger<InventoryService>? logger = null)
    {
        await using var db = NewContext();
        return await action(new InventoryService(db, logger ?? new ListLogger<InventoryService>()));
    }

    // ======================================================================================================
    // GetInventory
    // ======================================================================================================

    [Fact]
    public async Task GetInventory_ReturnsOnHandReservedAndAvailable_OrderedBySku()
    {
        var warehouse = await SeedWarehouseAsync();
        var otherWarehouse = await SeedWarehouseAsync("Seattle", "Seattle, WA");
        var laptop = await SeedProductAsync("LAP-1", name: "Laptop");
        var cable = await SeedProductAsync("CAB-1", name: "Cable");
        await SeedBalanceAsync(laptop, warehouse, onHand: 10, reserved: 2);
        await SeedBalanceAsync(cable, warehouse, onHand: 50);
        await SeedBalanceAsync(cable, otherWarehouse, onHand: 99);

        var result = await RunAsync(s => s.GetInventoryAsync(warehouse));

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { "CAB-1", "LAP-1" }, result.Items.Select(i => i.Sku));
        var laptopRow = result.Items[1];
        Assert.Equal((laptop, "Laptop", 10, 2, 8), (laptopRow.ProductId, laptopRow.Name, laptopRow.OnHand, laptopRow.Reserved, laptopRow.Available));
        Assert.Equal(50, result.Items[0].Available);
    }

    [Fact]
    public async Task GetInventory_FullyReservedStock_HasZeroAvailable()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 5, reserved: 5);

        var result = await RunAsync(s => s.GetInventoryAsync(warehouse));

        Assert.Equal(0, result.Items[0].Available);
    }

    [Fact]
    public async Task GetInventory_WarehouseWithNoBalances_ReturnsEmptyPage()
    {
        var warehouse = await SeedWarehouseAsync();

        var result = await RunAsync(s => s.GetInventoryAsync(warehouse));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetInventory_Paging_ReturnsRequestedSlice()
    {
        var warehouse = await SeedWarehouseAsync();
        for (var i = 1; i <= 3; i++)
        {
            var product = await SeedProductAsync($"P-{i}");
            await SeedBalanceAsync(product, warehouse, onHand: i);
        }

        var page2 = await RunAsync(s => s.GetInventoryAsync(warehouse, page: 2, pageSize: 2));

        Assert.Equal(new[] { "P-3" }, page2.Items.Select(i => i.Sku));
        Assert.Equal(3, page2.TotalCount);
    }

    [Theory]
    [InlineData(0, 1, 50)]
    [InlineData(-5, 1, 50)]
    [InlineData(1, 0, 50)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 201)]
    public async Task GetInventory_InvalidInput_IsRejected(long warehouseId, int page, int pageSize)
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.GetInventoryAsync(warehouseId, page, pageSize)));
    }

    [Fact]
    public async Task GetInventory_UnknownWarehouse_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.GetInventoryAsync(999)));
    }

    // ======================================================================================================
    // ReceiveStock
    // ======================================================================================================

    [Fact]
    public async Task ReceiveStock_FirstReceipt_CreatesBalanceAndLogsMovement()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        var response = await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 10, "Supplier delivery")));

        Assert.Equal((10, 0, 10), (response.OnHand, response.Reserved, response.Available));
        Assert.Equal((10, 0), await GetBalanceAsync(product, warehouse));

        var movement = Assert.Single(await GetMovementsAsync());
        Assert.Equal(response.MovementId, movement.MovementId);
        Assert.Equal(MovementType.Receipt, movement.Type);
        Assert.Equal(10, movement.QuantityChange);
        Assert.Equal("Supplier delivery", movement.Reason);
        Assert.Null(movement.OrderId);
    }

    [Fact]
    public async Task ReceiveStock_SecondReceipt_AddsToExistingBalance()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 10, null)));

        var response = await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 5, null)));

        Assert.Equal(15, response.OnHand);
        Assert.Equal(2, (await GetMovementsAsync()).Count);
    }

    [Fact]
    public async Task ReceiveStock_KeepsExistingReservedQuantity()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10, reserved: 4);

        var response = await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 5, null)));

        Assert.Equal((15, 4, 11), (response.OnHand, response.Reserved, response.Available));
    }

    [Fact]
    public async Task ReceiveStock_InactiveProduct_IsAllowed()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync(isActive: false);

        var response = await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 3, null)));

        Assert.Equal(3, response.OnHand);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public async Task ReceiveStock_QuantityAtLimits_Succeeds(int quantity)
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        var response = await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, quantity, null)));

        Assert.Equal(quantity, response.OnHand);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task ReceiveStock_QuantityOutOfRange_IsRejected_AndNothingChanges(int quantity)
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, quantity, null))));

        Assert.Contains("quantity", ex.Errors.Keys);
        Assert.Null(await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ReceiveStock_MissingIds_AreRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(0, 0, 5, null))));
    }

    [Fact]
    public async Task ReceiveStock_UnknownProduct_ThrowsNotFound()
    {
        var warehouse = await SeedWarehouseAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(999, warehouse, 5, null))));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ReceiveStock_UnknownWarehouse_ThrowsNotFound()
    {
        var product = await SeedProductAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, 999, 5, null))));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ReceiveStock_WouldOverflowStoredQuantity_ThrowsConflict_AndKeepsBalance()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: int.MaxValue - 10);

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 11, null))));

        Assert.Equal((int.MaxValue - 10, 0), await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ReceiveStock_FailureWhileLoggingMovement_RollsBackTheBalanceChange()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        await using (await FailOnMovementInsertAsync(MovementType.Receipt))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 5, null))));
            Assert.Contains("forced test failure", ex.ToString()); // the failure really came from the database
        }

        Assert.Equal((10, 0), await GetBalanceAsync(product, warehouse)); // the +5 was undone
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ReceiveStock_ConcurrentFirstReceipts_AllAreCounted()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 1, null))))));

        Assert.Equal((30, 0), await GetBalanceAsync(product, warehouse));
        Assert.Equal(30, (await GetMovementsAsync()).Count);
    }

    [Fact]
    public async Task ReceiveStock_LogsOperationOutcomeAndReference()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        var logger = new ListLogger<InventoryService>();

        await RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 5, null)), logger);
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.ReceiveStockAsync(new ReceiveStockRequest(product, warehouse, 0, null)), logger));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("ReceiveStock") && e.Message.Contains("succeeded") && e.Message.Contains($"product {product}"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("ReceiveStock") && e.Message.Contains("rejected"));
    }

    // ======================================================================================================
    // AdjustStock
    // ======================================================================================================

    [Theory]
    [InlineData(3, 13)]
    [InlineData(-3, 7)]
    public async Task AdjustStock_AppliesSignedChange_AndLogsMovementWithReason(int change, int expectedOnHand)
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        var response = await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, change, " Recount ")));

        Assert.Equal((expectedOnHand, 0, expectedOnHand), (response.OnHand, response.Reserved, response.Available));
        var movement = Assert.Single(await GetMovementsAsync());
        Assert.Equal(response.MovementId, movement.MovementId);
        Assert.Equal(MovementType.Adjustment, movement.Type);
        Assert.Equal(change, movement.QuantityChange);
        Assert.Equal("Recount", movement.Reason);
    }

    [Fact]
    public async Task AdjustStock_DownToExactlyReserved_Succeeds()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10, reserved: 4);

        var response = await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -6, "Damaged")));

        Assert.Equal((4, 4, 0), (response.OnHand, response.Reserved, response.Available));
    }

    [Fact]
    public async Task AdjustStock_BelowReserved_ThrowsConflict_AndChangesNothing()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10, reserved: 4);

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -7, "Damaged"))));

        Assert.Equal((10, 4), await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task AdjustStock_ToExactlyZeroWithNothingReserved_Succeeds()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 3);

        var response = await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -3, "Write-off")));

        Assert.Equal(0, response.OnHand);
    }

    [Fact]
    public async Task AdjustStock_BelowZero_ThrowsConflict()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 3);

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -4, "Too much"))));

        Assert.Equal((3, 0), await GetBalanceAsync(product, warehouse));
    }

    [Theory]
    [InlineData(0, "Reason")]
    [InlineData(1_000_001, "Reason")]
    [InlineData(-1_000_001, "Reason")]
    [InlineData(5, "")]
    [InlineData(5, "   ")]
    public async Task AdjustStock_InvalidChangeOrReason_IsRejected(int change, string reason)
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, change, reason))));

        Assert.Equal((10, 0), await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task AdjustStock_ReasonLongerThan255_IsRejected()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 1, new string('x', 256)))));

        Assert.Contains("reason", ex.Errors.Keys);
    }

    [Fact]
    public async Task AdjustStock_ReasonOf255Characters_Succeeds()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        var response = await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 1, new string('x', 255))));

        Assert.Equal(11, response.OnHand);
    }

    [Fact]
    public async Task AdjustStock_UnknownProductOrWarehouse_ThrowsNotFound()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(999, warehouse, 1, "Reason"))));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, 999, 1, "Reason"))));
    }

    [Fact]
    public async Task AdjustStock_NoBalanceYet_ThrowsNotFound()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 5, "Reason"))));

        Assert.Contains("receipt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await GetBalanceAsync(product, warehouse));
    }

    [Fact]
    public async Task AdjustStock_WouldOverflowStoredQuantity_ThrowsConflict()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: int.MaxValue - 5);

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 6, "Reason"))));

        Assert.Equal((int.MaxValue - 5, 0), await GetBalanceAsync(product, warehouse));
    }

    [Fact]
    public async Task AdjustStock_FailureWhileLoggingMovement_RollsBackTheBalanceChange()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        await using (await FailOnMovementInsertAsync(MovementType.Adjustment))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 5, "Reason"))));
            Assert.Contains("forced test failure", ex.ToString());
        }

        Assert.Equal((10, 0), await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task AdjustStock_TwoConcurrentRemovalsOfMostStock_ExactlyOneSucceeds()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);

        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -8, "Damaged")));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal((2, 0), await GetBalanceAsync(product, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task AdjustStock_LogsOperationOutcomeAndReference()
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);
        var logger = new ListLogger<InventoryService>();

        await RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, 1, "Recount")), logger);
        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.AdjustStockAsync(new AdjustStockRequest(product, warehouse, -500, "Too much")), logger));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("AdjustStock") && e.Message.Contains("succeeded"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("AdjustStock") && e.Message.Contains("rejected"));
    }
}
