using Microsoft.AspNetCore.Http.HttpResults;

namespace OrderIntake.Api;

public static class OrderEndpoints
{
    public static WebApplication MapOrderEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Orders");

        orders.MapGet("", (OrderService service, int page = 1, int pageSize = 10) =>
        {
            var result = service.List(page, pageSize);
            return TypedResults.Ok(new PagedOrdersResponse(
                result.Items.Select(OrderResponse.From).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount,
                result.TotalPages));
        })
            .WithName("ListOrders");

        orders.MapGet("/{id:guid}", Results<Ok<OrderResponse>, NotFound> (Guid id, OrderService service) =>
        {
            var order = service.Get(id);
            return order is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(OrderResponse.From(order));
        }).WithName("GetOrder");

        orders.MapPost("", IResult (CreateOrderRequest request, OrderService service) =>
        {
            try
            {
                var result = service.Create(request);
                var response = OrderResponse.From(result.Order);
                return result.WasCreated
                    ? TypedResults.Created($"/api/orders/{response.Id}", response)
                    : TypedResults.Ok(response);
            }
            catch (OrderValidationException exception)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Order could not be created",
                    detail: exception.Message);
            }
            catch (OverflowException)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Order total is too large",
                    detail: "The quantity and unit price produce a value outside the supported range.");
            }
        }).WithName("CreateOrder");

        orders.MapPatch("/{id:guid}/status", IResult (
            Guid id,
            ChangeOrderStatusRequest request,
            OrderService service) =>
        {
            if (!Enum.IsDefined(request.Status))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid order status",
                    detail: "Choose Pending, Confirmed, Fulfilled, or Cancelled.");
            }

            var result = service.ChangeStatus(id, request.Status);
            if (result.Order is null && result.Error is null)
            {
                return TypedResults.NotFound();
            }

            if (result.Error is not null)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Status change not allowed",
                    detail: result.Error);
            }

            return TypedResults.Ok(OrderResponse.From(result.Order!));
        }).WithName("ChangeOrderStatus");

        return app;
    }
}

public sealed record PagedOrdersResponse(
    IReadOnlyList<OrderResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);