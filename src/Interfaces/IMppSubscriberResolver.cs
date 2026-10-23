namespace ThinkBase.AI.MPP.Interfaces
{
    /// <summary>
    /// Tells the Machine Payments Protocol middleware whether the call it is looking at already
    /// belongs to a subscriber, in which case it is billed to that subscription and is never
    /// challenged for payment. How a subscriber is recognised — an API key, a bearer token, a
    /// session — belongs to the application, so each application supplies its own implementation.
    /// When no implementation is registered every call that carries no payment credential is
    /// challenged.
    /// </summary>
    public interface IMppSubscriberResolver
    {
        /// <summary>
        /// The identity of the subscriber the request is made by, or <c>null</c> when the request
        /// is made by no subscriber and so has to be paid for.
        /// </summary>
        Task<string?> GetSubscriberIdAsync(HttpContext context, CancellationToken cancellationToken = default);
    }
}
