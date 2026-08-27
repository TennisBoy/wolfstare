using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;

namespace Wolfstare.Core.Storage;

/// <summary>Persistence for block lists. Implemented in the service layer.</summary>
public interface IBlockListRepository
{
    Task<IReadOnlyList<BlockList>> GetAllAsync(CancellationToken ct = default);

    Task<BlockList?> GetAsync(Guid id, CancellationToken ct = default);

    Task SaveAsync(BlockList list, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Persistence for active sessions. Only active sessions are stored — a session that ends is
/// removed, so anything present at startup is a session that must be resumed.
/// </summary>
public interface ISessionRepository
{
    Task<IReadOnlyList<BlockSession>> GetActiveAsync(CancellationToken ct = default);

    Task SaveAsync(BlockSession session, CancellationToken ct = default);

    Task RemoveAsync(Guid sessionId, CancellationToken ct = default);
}
