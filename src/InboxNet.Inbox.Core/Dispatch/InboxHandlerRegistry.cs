namespace InboxNet.Inbox.Dispatch;

internal sealed class InboxHandlerRegistry : IInboxHandlerRegistry
{
    private readonly IReadOnlyList<InboxHandlerRegistration> _registrations;

    public InboxHandlerRegistry(IEnumerable<InboxHandlerRegistration> registrations)
    {
        _registrations = registrations
            .OrderBy(r => r.Order)
            .ToList();
    }

    public IReadOnlyList<InboxHandlerRegistration> All => _registrations;

    public IReadOnlyList<InboxHandlerRegistration> GetMatching(string providerKey, string eventType)
    {
        var matches = new List<InboxHandlerRegistration>();
        foreach (var reg in _registrations)
        {
            if (reg.Matches(providerKey, eventType))
                matches.Add(reg);
        }
        return matches;
    }
}
