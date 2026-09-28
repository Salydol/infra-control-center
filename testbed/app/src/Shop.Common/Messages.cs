namespace Shop.Common;

public sealed record OrderCreated(long OrderId, long CustomerId, long ProductId, int Quantity, decimal Amount, DateTime CreatedAt);

public sealed record OrderPaid(long OrderId, long CustomerId, decimal Amount, DateTime PaidAt);

public static class Routing
{
    public const string Exchange = "shop.events";
    public const string DeadLetterExchange = "shop.dlx";
    public const string DeadLetterQueue = "shop.dead-letters";

    public const string OrderCreatedKey = "order.created";
    public const string OrderPaidKey = "order.paid";

    public const string PaymentsQueue = "payments.order-created";
    public const string NotificationsQueue = "notifications.order-paid";
}
