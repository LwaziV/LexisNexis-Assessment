using System.Collections.Concurrent;
using System.Net.Mail;

namespace OrderIntake.Api;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Fulfilled,
    Cancelled
}

public sealed record OrderLine(
    string Sku,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record Order(
    Guid Id,
    string ExternalReference,
    string CustomerName,
    string CustomerEmail,
    string Currency,
    string? Notes,
    IReadOnlyList<OrderLine> Items,
    decimal Subtotal,
    decimal Total,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateOrderItemRequest(
    string? Sku,
    string? Name,
    int Quantity,
    decimal UnitPrice);

public sealed record CreateOrderRequest(
    string? ExternalReference,
    string? CustomerName,
    string? CustomerEmail,
    string? Currency,
    string? Notes,
    IReadOnlyList<CreateOrderItemRequest>? Items);

public sealed record ChangeOrderStatusRequest(OrderStatus Status);

public sealed record OrderLineResponse(
    string Sku,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    string ExternalReference,
    string CustomerName,
    string CustomerEmail,
    string Currency,
    string? Notes,
    IReadOnlyList<OrderLineResponse> Items,
    decimal Subtotal,
    decimal Total,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.ExternalReference,
        order.CustomerName,
        order.CustomerEmail,
        order.Currency,
        order.Notes,
        order.Items.Select(item => new OrderLineResponse(
            item.Sku, item.Name, item.Quantity, item.UnitPrice, item.LineTotal)).ToArray(),
        order.Subtotal,
        order.Total,
        order.Status,
        order.CreatedAt,
        order.UpdatedAt);
}

public sealed record CreateOrderResult(Order Order, bool WasCreated);
public sealed record OrderOperationResult(Order? Order, string? Error);
public sealed record PagedOrders(
    IReadOnlyList<Order> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed class OrderService
{
    private readonly ConcurrentDictionary<string, Order> _ordersByReference =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _createLock = new();

    public CreateOrderResult Create(CreateOrderRequest request)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            throw new OrderValidationException(validationError);
        }

        var reference = request.ExternalReference!.Trim();
        lock (_createLock)
        {
            if (_ordersByReference.TryGetValue(reference, out var existing))
            {
                return new CreateOrderResult(existing, false);
            }

            var items = request.Items!.Select(item => new OrderLine(
                item.Sku!.Trim(),
                item.Name!.Trim(),
                item.Quantity,
                item.UnitPrice,
                checked(item.Quantity * item.UnitPrice))).ToArray();
            var subtotal = items.Sum(item => item.LineTotal);
            var now = DateTimeOffset.UtcNow;
            var order = new Order(
                Guid.NewGuid(),
                reference,
                request.CustomerName!.Trim(),
                request.CustomerEmail!.Trim(),
                request.Currency!.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                items,
                subtotal,
                subtotal,
                OrderStatus.Pending,
                now,
                now);

            _ordersByReference[reference] = order;
            return new CreateOrderResult(order, true);
        }
    }

    public PagedOrders List(int page = 1, int pageSize = 10)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var ordered = _ordersByReference.Values
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .ToArray();
        var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)pageSize));
        page = Math.Min(page, totalPages);
        var items = ordered.Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue))
            .Take(pageSize)
            .ToArray();

        return new PagedOrders(items, page, pageSize, ordered.Length, totalPages);
    }

    public Order? Get(Guid id) => _ordersByReference.Values.FirstOrDefault(order => order.Id == id);

    public OrderOperationResult ChangeStatus(Guid id, OrderStatus nextStatus)
    {
        lock (_createLock)
        {
            var pair = _ordersByReference.FirstOrDefault(entry => entry.Value.Id == id);
            if (pair.Value is null)
            {
                return new OrderOperationResult(null, null);
            }

            var current = pair.Value;
            if (current.Status == nextStatus)
            {
                return new OrderOperationResult(current, null);
            }

            var allowed = current.Status switch
            {
                OrderStatus.Pending => nextStatus is OrderStatus.Confirmed or OrderStatus.Cancelled,
                OrderStatus.Confirmed => nextStatus is OrderStatus.Fulfilled or OrderStatus.Cancelled,
                _ => false
            };

            if (!allowed)
            {
                return new OrderOperationResult(
                    null,
                    $"An order in {current.Status} cannot be changed to {nextStatus}.");
            }

            var updated = current with { Status = nextStatus, UpdatedAt = DateTimeOffset.UtcNow };
            _ordersByReference[pair.Key] = updated;
            return new OrderOperationResult(updated, null);
        }
    }

    private static string? Validate(CreateOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalReference) || request.ExternalReference.Trim().Length > 80)
        {
            return "External reference is required and must be 80 characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Trim().Length > 160)
        {
            return "Customer name is required and must be 160 characters or fewer.";
        }

        var email = request.CustomerEmail?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 254 ||
            !MailAddress.TryCreate(email, out var parsedEmail) ||
            !string.Equals(parsedEmail.Address, email, StringComparison.OrdinalIgnoreCase) ||
            !parsedEmail.Host.Contains('.'))
        {
            return "A valid customer email is required.";
        }

        if (request.Currency is null || request.Currency.Trim().Length != 3 ||
            !request.Currency.Trim().All(char.IsAsciiLetter))
        {
            return "Currency must be a three-letter code, such as USD or ZAR.";
        }

        if (request.Items is null || request.Items.Count is < 1 or > 50)
        {
            return "An order must contain between 1 and 50 line items.";
        }

        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.Sku) || item.Sku.Trim().Length > 64 ||
                                      string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 160))
        {
            return "Every line item needs a SKU and name within the allowed length.";
        }

        if (request.Items.Any(item => item.Quantity <= 0))
        {
            return "Every line item quantity must be a positive whole number.";
        }

        if (request.Items.Any(item => item.UnitPrice < 0))
        {
            return "Line item prices cannot be negative.";
        }

        if (request.Notes?.Length > 2000)
        {
            return "Notes must be 2000 characters or fewer.";
        }

        return null;
    }
}

public sealed class OrderValidationException(string message) : Exception(message);