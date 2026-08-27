using System.Collections.Concurrent;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Storage;

namespace Wolfstare.Core.Tests.Storage;

public sealed class InMemoryBlockListRepository : IBlockListRepository
{
    private readonly ConcurrentDictionary<Guid, BlockList> _lists = new();

    public Task<IReadOnlyList<BlockList>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BlockList>>(_lists.Values.ToList());

    public Task<BlockList?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_lists.GetValueOrDefault(id));

    public Task SaveAsync(BlockList list, CancellationToken ct = default)
    {
        _lists[list.Id] = list;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _lists.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<Guid, BlockSession> _sessions = new();

    public Task<IReadOnlyList<BlockSession>> GetActiveAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BlockSession>>(_sessions.Values.ToList());

    public Task SaveAsync(BlockSession session, CancellationToken ct = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid sessionId, CancellationToken ct = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}
