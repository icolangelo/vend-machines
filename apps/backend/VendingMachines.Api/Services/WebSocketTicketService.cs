using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace VendingMachines.Api.Services;

public sealed record WebSocketTicket(string Token, Guid UserId, Guid CompanyId, DateTime ExpiresAt);

public sealed class WebSocketTicketService
{
    private readonly ConcurrentDictionary<string, WebSocketTicket> _tickets = new();

    public WebSocketTicket Issue(Guid userId, Guid companyId)
    {
        Cleanup();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ticket = new WebSocketTicket(token, userId, companyId, DateTime.UtcNow.AddSeconds(60));
        _tickets[token] = ticket;
        return ticket;
    }

    public bool TryConsume(string token, out WebSocketTicket ticket)
    {
        if (_tickets.TryRemove(token, out var found) && found.ExpiresAt > DateTime.UtcNow)
        {
            ticket = found;
            return true;
        }

        ticket = null!;
        return false;
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _tickets)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _tickets.TryRemove(item.Key, out _);
            }
        }
    }
}
