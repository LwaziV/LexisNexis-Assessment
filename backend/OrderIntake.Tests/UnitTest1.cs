using OrderIntake.Api;

namespace OrderIntake.Tests;

public class OrderServiceTests
{
    [Fact]
    public void Create_ComputesLineAndOrderTotalsOnServer()
    {
        var result = new OrderService().Create(ValidRequest());

        Assert.Equal(49.50m, result.Order.Items[0].LineTotal);
        Assert.Equal(49.50m, result.Order.Subtotal);
        Assert.Equal(49.50m, result.Order.Total);
        Assert.Equal(OrderStatus.Pending, result.Order.Status);
        Assert.True(result.WasCreated);
    }

    [Fact]
    public void Create_ReferenceRetryReturnsOriginalOrderWithoutCreatingDuplicate()
    {
        var service = new OrderService();
        var original = service.Create(ValidRequest("  po-2026-14 "));
        var retry = service.Create(ValidRequest("PO-2026-14") with
        {
            CustomerName = "Different retry payload"
        });

        Assert.True(original.WasCreated);
        Assert.False(retry.WasCreated);
        Assert.Equal(original.Order.Id, retry.Order.Id);
        Assert.Equal("Northwind Trading", retry.Order.CustomerName);
        Assert.Single(service.List().Items);
    }

    [Fact]
    public void List_ReturnsRequestedPageAndPaginationMetadata()
    {
        var service = new OrderService();
        for (var index = 0; index < 12; index++)
        {
            service.Create(ValidRequest($"PO-{index}"));
        }

        var firstPage = service.List(page: 1, pageSize: 5);
        var secondPage = service.List(page: 2, pageSize: 5);
        var lastPage = service.List(page: 3, pageSize: 5);

        Assert.Equal(12, firstPage.TotalCount);
        Assert.Equal(3, firstPage.TotalPages);
        Assert.Equal(5, firstPage.Items.Count);
        Assert.Equal(5, secondPage.Items.Count);
        Assert.Equal(2, lastPage.Items.Count);
        Assert.Empty(firstPage.Items.Select(order => order.Id).Intersect(secondPage.Items.Select(order => order.Id)));
    }

    [Fact]
    public void List_ClampsInvalidPageAndPageSize()
    {
        var service = new OrderService();
        service.Create(ValidRequest());
        var result = service.List(page: 500, pageSize: 500);

        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal(1, result.TotalPages);
        Assert.Single(result.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsNonPositiveQuantities(int quantity)
    {
        var request = ValidRequest() with
        {
            Items = [new CreateOrderItemRequest("SKU-1", "Widget", quantity, 16.50m)]
        };

        var exception = Assert.Throws<OrderValidationException>(() => new OrderService().Create(request));

        Assert.Contains("positive whole number", exception.Message);
    }

    [Fact]
    public void Create_RejectsNegativePrices()
    {
        var request = ValidRequest() with
        {
            Items = [new CreateOrderItemRequest("SKU-1", "Widget", 1, -0.01m)]
        };

        Assert.Throws<OrderValidationException>(() => new OrderService().Create(request));
    }

    [Theory]
    [InlineData("not-an-email@")]
    [InlineData("@example.com")]
    [InlineData("name@localhost")]
    public void Create_RejectsMalformedEmailAddresses(string email)
    {
        var request = ValidRequest() with { CustomerEmail = email };

        var exception = Assert.Throws<OrderValidationException>(() => new OrderService().Create(request));

        Assert.Contains("valid customer email", exception.Message);
    }

    [Fact]
    public void Create_AcceptsEmailWithDomain()
    {
        var result = new OrderService().Create(ValidRequest() with
        {
            CustomerEmail = "orders@northwind.example"
        });

        Assert.Equal("orders@northwind.example", result.Order.CustomerEmail);
    }

    [Fact]
    public void ChangeStatus_AllowsSalesFlowAndRejectsTerminalTransitions()
    {
        var service = new OrderService();
        var order = service.Create(ValidRequest()).Order;

        var confirmed = service.ChangeStatus(order.Id, OrderStatus.Confirmed);
        var fulfilled = service.ChangeStatus(order.Id, OrderStatus.Fulfilled);
        var invalid = service.ChangeStatus(order.Id, OrderStatus.Cancelled);

        Assert.Equal(OrderStatus.Confirmed, confirmed.Order?.Status);
        Assert.Equal(OrderStatus.Fulfilled, fulfilled.Order?.Status);
        Assert.Null(invalid.Order);
        Assert.Contains("cannot be changed", invalid.Error);
    }

    [Fact]
    public void ChangeStatus_AllowsPendingOrderToBeCancelled()
    {
        var service = new OrderService();
        var order = service.Create(ValidRequest()).Order;

        var result = service.ChangeStatus(order.Id, OrderStatus.Cancelled);

        Assert.Equal(OrderStatus.Cancelled, result.Order?.Status);
        Assert.Null(result.Error);
    }

    private static CreateOrderRequest ValidRequest(string reference = "PO-2026-14") => new(
        reference,
        "Northwind Trading",
        "orders@northwind.example",
        "zar",
        "Deliver before month end",
        [new CreateOrderItemRequest("SKU-1", "Widget", 3, 16.50m)]);
}
