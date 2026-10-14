using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ProductOrderApi.Data;
using Xunit;

namespace ProductOrderApi.IntegrationTests;

public sealed class ApiIntegrationTests : IAsyncLifetime
{
    private IntegrationTestHost _host = null!;

    private HttpClient Client => _host.Client;

    public async Task InitializeAsync()
    {
        _host = await IntegrationTestHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Health_reports_healthy_when_the_database_is_available()
    {
        using var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_route_returns_a_complete_problem_details_response()
    {
        const string path = "/definitely-missing";

        using var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var problem = document.RootElement;
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", problem.GetProperty("title").GetString());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Empty_405_and_415_responses_are_written_as_problem_details()
    {
        using var methodNotAllowed = await Client.DeleteAsync("/health");
        AssertProblem(methodNotAllowed, HttpStatusCode.MethodNotAllowed);

        using var unsupportedMediaType = await Client.PostAsync(
            "/products",
            new StringContent("{}", Encoding.UTF8, "text/plain"));
        AssertProblem(unsupportedMediaType, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Twenty_parallel_orders_for_last_item_allow_exactly_one_order()
    {
        var product = await CreateProductAsync(stock: 1);
        using var gate = new ManualResetEventSlim(false);

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await CreateOrderResponseAsync(product.Id, quantity: 1);
            }))
            .ToArray();

        gate.Set();
        var responses = await Task.WhenAll(attempts);

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            Assert.DoesNotContain(responses, response =>
                response.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.Conflict);

            var storedProduct = await GetProductAsync(product.Id);
            Assert.Equal(0, storedProduct.Stock);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Repeated_idempotency_key_replays_order_and_rejects_a_different_body()
    {
        var product = await CreateProductAsync(stock: 10);
        var key = $"sequential-{Guid.NewGuid():N}";

        using var first = await CreateOrderResponseAsync(product.Id, quantity: 2, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstOrder = await ReadRequiredAsync<OrderResponse>(first);

        using var replay = await CreateOrderResponseAsync(product.Id, quantity: 2, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.TryGetValues("Idempotent-Replayed", out var replayedValues));
        Assert.Equal("true", Assert.Single(replayedValues));
        var replayedOrder = await ReadRequiredAsync<OrderResponse>(replay);

        Assert.Equal(firstOrder.Id, replayedOrder.Id);

        using var mismatch = await CreateOrderResponseAsync(product.Id, quantity: 3, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);

        var storedProduct = await GetProductAsync(product.Id);
        Assert.Equal(8, storedProduct.Stock);
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_create_one_order_and_reserve_once()
    {
        var product = await CreateProductAsync(stock: 20);
        var key = $"parallel-{Guid.NewGuid():N}";
        using var gate = new ManualResetEventSlim(false);

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await CreateOrderResponseAsync(product.Id, quantity: 1, key);
            }))
            .ToArray();

        gate.Set();
        var responses = await Task.WhenAll(attempts);

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.OK));

            var orders = new List<OrderResponse>();
            foreach (var response in responses)
            {
                orders.Add(await ReadRequiredAsync<OrderResponse>(response));
            }

            Assert.Single(orders.Select(order => order.Id).Distinct());
            Assert.Equal(1, await _host.CountOrdersForIdempotencyKeyAsync(key));
            Assert.All(
                responses.Where(response => response.StatusCode == HttpStatusCode.OK),
                response => Assert.Equal(
                    "true",
                    Assert.Single(response.Headers.GetValues("Idempotent-Replayed"))));

            var storedProduct = await GetProductAsync(product.Id);
            Assert.Equal(19, storedProduct.Stock);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Duplicate_product_lines_are_merged_and_reserve_the_summed_quantity()
    {
        var product = await CreateProductAsync(stock: 10, price: 7.25m);

        using var response = await CreateOrderResponseAsync(
        [
            new CreateOrderItemRequest(product.Id, 2),
            new CreateOrderItemRequest(product.Id, 3)
        ]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadRequiredAsync<OrderResponse>(response);
        var item = Assert.Single(order.Items);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(5, item.Quantity);
        Assert.Equal(7.25m, item.UnitPrice);
        Assert.Equal(36.25m, order.Total);
        Assert.Equal(5, (await GetProductAsync(product.Id)).Stock);
    }

    [Fact]
    public async Task Confirm_success_marks_order_confirmed_and_is_idempotent()
    {
        ResetPaymentStub();
        _host.Payment.Succeed();
        var product = await CreateProductAsync(stock: 2, price: 12.50m);
        var order = await CreateOrderAsync(product.Id, quantity: 2);

        using var confirmedResponse = await Client.PostAsync($"/orders/{order.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        var confirmed = await ReadRequiredAsync<OrderResponse>(confirmedResponse);
        Assert.Equal("Confirmed", confirmed.Status);

        using var replayResponse = await Client.PostAsync($"/orders/{order.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = await ReadRequiredAsync<OrderResponse>(replayResponse);
        Assert.Equal("Confirmed", replayed.Status);

        var call = Assert.Single(_host.Payment.Calls);
        Assert.Equal(order.Id, call.OrderId);
        Assert.Equal(25.00m, call.Amount);
    }

    [Fact]
    public async Task Concurrent_confirm_requests_charge_exactly_once()
    {
        ResetPaymentStub();
        var paymentGate = _host.Payment.BlockFirstCallThenSucceed(ignoreCancellation: true);
        var product = await CreateProductAsync(stock: 1);
        var order = await CreateOrderAsync(product.Id);

        var firstConfirmTask = Client.PostAsync($"/orders/{order.Id}/confirm", null);
        try
        {
            await paymentGate.Entered.WaitAsync(TimeSpan.FromSeconds(3));
            using var competingResponse = await Client
                .PostAsync($"/orders/{order.Id}/confirm", null)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.Conflict, competingResponse.StatusCode);
        }
        finally
        {
            paymentGate.Release();
        }

        using var confirmedResponse = await firstConfirmTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        Assert.Equal(
            "Confirmed",
            (await ReadRequiredAsync<OrderResponse>(confirmedResponse)).Status);
        Assert.Single(_host.Payment.Calls);
    }

    [Fact]
    public async Task Provider_success_after_client_disconnect_still_confirms_the_order()
    {
        ResetPaymentStub();
        var paymentGate = _host.Payment.BlockFirstCallThenSucceed(ignoreCancellation: true);
        var product = await CreateProductAsync(stock: 1);
        var order = await CreateOrderAsync(product.Id);
        using var requestCancellation = new CancellationTokenSource();

        var confirmTask = Client.PostAsync(
            $"/orders/{order.Id}/confirm",
            content: null,
            requestCancellation.Token);
        try
        {
            await paymentGate.Entered.WaitAsync(TimeSpan.FromSeconds(3));
            requestCancellation.Cancel();
            await paymentGate.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            paymentGate.Release();
        }

        try
        {
            using var response = await confirmTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            // The client may stop observing the response, but server finalization must continue.
        }

        var storedOrder = await WaitForOrderStatusAsync(order.Id, "Confirmed");
        Assert.Equal("Confirmed", storedOrder.Status);
        Assert.Single(_host.Payment.Calls);
    }

    [Fact]
    public async Task Confirm_decline_returns_402_and_restores_pending_status()
    {
        ResetPaymentStub();
        _host.Payment.Decline();
        var product = await CreateProductAsync(stock: 1);
        var order = await CreateOrderAsync(product.Id);

        using var response = await Client.PostAsync($"/orders/{order.Id}/confirm", null);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("Pending", (await GetOrderAsync(order.Id)).Status);
        Assert.Single(_host.Payment.Calls);
    }

    [Fact]
    public async Task Confirm_timeout_returns_504_then_a_retry_can_succeed()
    {
        ResetPaymentStub();
        _host.Payment.TimeOut();
        var product = await CreateProductAsync(stock: 1);
        var order = await CreateOrderAsync(product.Id);

        using var timedOut = await Client.PostAsync($"/orders/{order.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Equal("Pending", (await GetOrderAsync(order.Id)).Status);

        _host.Payment.Succeed();
        using var retry = await Client.PostAsync($"/orders/{order.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Confirmed", (await ReadRequiredAsync<OrderResponse>(retry)).Status);

        var calls = _host.Payment.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(order.Id, call.OrderId));
    }

    [Fact]
    public async Task Confirm_provider_error_returns_502_and_restores_pending_status()
    {
        ResetPaymentStub();
        _host.Payment.Fail();
        var product = await CreateProductAsync(stock: 1);
        var order = await CreateOrderAsync(product.Id);

        using var response = await Client.PostAsync($"/orders/{order.Id}/confirm", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Pending", (await GetOrderAsync(order.Id)).Status);
        Assert.Single(_host.Payment.Calls);
    }

    [Fact]
    public async Task Cancel_restores_stock_once_and_confirmed_order_cannot_be_cancelled()
    {
        ResetPaymentStub();
        _host.Payment.Succeed();

        var cancellableProduct = await CreateProductAsync(stock: 3);
        var cancellableOrder = await CreateOrderAsync(cancellableProduct.Id, quantity: 2);
        Assert.Equal(1, (await GetProductAsync(cancellableProduct.Id)).Stock);

        using var cancelled = await Client.PostAsync($"/orders/{cancellableOrder.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await ReadRequiredAsync<OrderResponse>(cancelled)).Status);
        Assert.Equal(3, (await GetProductAsync(cancellableProduct.Id)).Stock);

        using var cancelledAgain = await Client.PostAsync($"/orders/{cancellableOrder.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelledAgain.StatusCode);
        Assert.Equal(3, (await GetProductAsync(cancellableProduct.Id)).Stock);

        var confirmedProduct = await CreateProductAsync(stock: 1);
        var confirmedOrder = await CreateOrderAsync(confirmedProduct.Id);
        using var confirmed = await Client.PostAsync($"/orders/{confirmedOrder.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var rejectedCancel = await Client.PostAsync($"/orders/{confirmedOrder.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, rejectedCancel.StatusCode);
        Assert.Equal(0, (await GetProductAsync(confirmedProduct.Id)).Stock);
    }

    [Fact]
    public async Task Invalid_requests_use_problem_details_and_missing_resources_return_404()
    {
        using var invalidProduct = await Client.PostAsJsonAsync(
            "/products",
            new CreateProductRequest("", -1m, -1));
        AssertProblem(invalidProduct, HttpStatusCode.BadRequest);

        using var emptyOrder = await Client.PostAsJsonAsync(
            "/orders",
            new CreateOrderRequest(Array.Empty<CreateOrderItemRequest>()));
        AssertProblem(emptyOrder, HttpStatusCode.BadRequest);

        using var invalidQuantity = await Client.PostAsJsonAsync(
            "/orders",
            new CreateOrderRequest([new CreateOrderItemRequest(Guid.NewGuid(), 0)]));
        AssertProblem(invalidQuantity, HttpStatusCode.BadRequest);

        using var missingProduct = await Client.GetAsync($"/products/{Guid.NewGuid()}");
        AssertProblem(missingProduct, HttpStatusCode.NotFound);

        using var missingOrder = await Client.GetAsync($"/orders/{Guid.NewGuid()}");
        AssertProblem(missingOrder, HttpStatusCode.NotFound);

        using var orderForMissingProduct = await CreateOrderResponseAsync(Guid.NewGuid());
        AssertProblem(orderForMissingProduct, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Database_seeder_can_run_twice_without_creating_duplicates()
    {
        Assert.Equal(0, await _host.CountProductsAsync());

        await _host.SeedDatabaseAsync();
        await CreateOrderAsync(SeedProductIds.StandardWidget, quantity: 3);
        Assert.Equal(97, (await GetProductAsync(SeedProductIds.StandardWidget)).Stock);

        await _host.SeedDatabaseAsync();

        Assert.Equal(4, await _host.CountProductsAsync());
        Assert.Equal(1, (await GetProductAsync(SeedProductIds.ConcurrencyWidget)).Stock);
        Assert.Equal(97, (await GetProductAsync(SeedProductIds.StandardWidget)).Stock);
        Assert.Equal(25, (await GetProductAsync(SeedProductIds.PremiumWidget)).Stock);
        Assert.Equal(0, (await GetProductAsync(SeedProductIds.OutOfStockWidget)).Stock);
    }

    private void ResetPaymentStub()
    {
        _host.Payment.ClearCalls();
        _host.Payment.Succeed();
    }

    private async Task<ProductResponse> CreateProductAsync(
        int stock,
        decimal price = 10.00m)
    {
        using var response = await Client.PostAsJsonAsync(
            "/products",
            new CreateProductRequest($"Test product {Guid.NewGuid():N}", price, stock));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return await ReadRequiredAsync<ProductResponse>(response);
    }

    private async Task<ProductResponse> GetProductAsync(Guid productId)
    {
        using var response = await Client.GetAsync($"/products/{productId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRequiredAsync<ProductResponse>(response);
    }

    private async Task<OrderResponse> CreateOrderAsync(Guid productId, int quantity = 1)
    {
        using var response = await CreateOrderResponseAsync(productId, quantity);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return await ReadRequiredAsync<OrderResponse>(response);
    }

    private async Task<HttpResponseMessage> CreateOrderResponseAsync(
        Guid productId,
        int quantity = 1,
        string? idempotencyKey = null)
    {
        return await CreateOrderResponseAsync(
            [new CreateOrderItemRequest(productId, quantity)],
            idempotencyKey);
    }

    private async Task<HttpResponseMessage> CreateOrderResponseAsync(
        IReadOnlyCollection<CreateOrderItemRequest> items,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest(items))
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await Client.SendAsync(request);
    }

    private async Task<OrderResponse> WaitForOrderStatusAsync(
        Guid orderId,
        string expectedStatus)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        OrderResponse order;
        do
        {
            order = await GetOrderAsync(orderId);
            if (order.Status == expectedStatus)
            {
                return order;
            }

            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(expectedStatus, order.Status);
        return order;
    }

    private async Task<OrderResponse> GetOrderAsync(Guid orderId)
    {
        using var response = await Client.GetAsync($"/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRequiredAsync<OrderResponse>(response);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(value);
    }

    private static void AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(response.Content.ReadAsStream());
        Assert.Equal((int)expectedStatus, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }
}
