namespace NeonLedger.Events;

public static class EventTypes
{
    public const string TransactionPosted = "transaction.posted";
    public const string PaymentCreated = "payment.created";
    public const string PaymentSettled = "payment.settled";
    public const string PaymentCanceled = "payment.canceled";
    public const string PaymentReturned = "payment.returned";
}
