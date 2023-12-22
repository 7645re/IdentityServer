// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.


using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer.EntityFramework.Entities;
using Duende.IdentityServer.EntityFramework.Extensions;
using Duende.IdentityServer.EntityFramework.Interfaces;
using Duende.IdentityServer.EntityFramework.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Duende.IdentityServer.EntityFramework;

/// <inheritdoc/>
public class TokenCleanupService : ITokenCleanupService
{
    private readonly OperationalStoreOptions _options;
    private readonly IPersistedGrantDbContext _persistedGrantDbContext;
    private readonly IOperationalStoreNotification _operationalStoreNotification;
    private readonly ILogger<TokenCleanupService> _logger;

    /// <summary>
    /// Constructor for TokenCleanupService.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="persistedGrantDbContext"></param>
    /// <param name="operationalStoreNotification"></param>
    /// <param name="logger"></param>
    public TokenCleanupService(
        OperationalStoreOptions options,
        IPersistedGrantDbContext persistedGrantDbContext,
        ILogger<TokenCleanupService> logger,
        IOperationalStoreNotification operationalStoreNotification = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.TokenCleanupBatchSize < 1) throw new ArgumentException("Token cleanup batch size interval must be at least 1");

        _persistedGrantDbContext = persistedGrantDbContext ?? throw new ArgumentNullException(nameof(persistedGrantDbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _operationalStoreNotification = operationalStoreNotification;
    }

    /// <inheritdoc/>
    public async Task CleanupGrantsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogTrace("Querying for expired grants to remove");

            await RemoveGrantsAsync(cancellationToken);
            await RemoveDeviceCodesAsync(cancellationToken);
            await RemovePushedAuthorizationRequestsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Exception removing expired grants: {exception}", ex.Message);
        }
    }

    /// <summary>
    /// Removes the stale persisted grants.
    /// </summary>
    /// <returns></returns>
    protected virtual async Task RemoveGrantsAsync(CancellationToken cancellationToken = default)
    {
        await RemoveExpiredPersistedGrantsAsync(cancellationToken);
        if (_options.RemoveConsumedTokens)
        {
            await RemoveConsumedPersistedGrantsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Removes the expired persisted grants.
    /// </summary>
    /// <returns></returns>
    protected virtual async Task RemoveExpiredPersistedGrantsAsync(CancellationToken cancellationToken = default)
    {
        var found = Int32.MaxValue;

        while (found >= _options.TokenCleanupBatchSize)
        {
            var query = _persistedGrantDbContext.PersistedGrants
                .Where(x => x.Expiration < DateTime.UtcNow)
                .OrderBy(x => x.Id);

            var expiredGrants = await query
                .Take(_options.TokenCleanupBatchSize)
                .AsNoTracking()
                .ToArrayAsync(cancellationToken);

            found = expiredGrants.Length;

            if (found > 0)
            {
                _logger.LogInformation("Removing {grantCount} expired grants", found);

                var foundIds = expiredGrants.Select(pg => pg.Id).ToArray();

                var deleteCount = await query
                    // Run the same query, but now use an interval instead of Take(). This is to
                    // ensure we get all the elements, even if a new element was added in the middle
                    // of the set.
                    .Where(pg => pg.Id >= foundIds.First() && pg.Id <= foundIds.Last())
                    // To be on the safe side, filter out any possibly newly added items
                    // with an id within the interval
                    .Where(pg => foundIds.Contains(pg.Id))
                    // And delete them.
                    .ExecuteDeleteAsync(cancellationToken);

                if (deleteCount != found)
                {
                    if (_operationalStoreNotification != null)
                    {
                        _logger.LogWarning("Tried to remove {grantCount} expired grants, but only {deleteCount} " +
                            "was deleted. This indicates a concurrency issue. Duplicate notifications may be " +
                            "sent to the registered IOperationalStoreNotification", found, deleteCount);
                    }
                    else
                    {
                        _logger.LogInformation("Tried to remove {grantCount} expired grants, but only {deleteCount} " +
                            "was deleted. This indicates a concurrency issue", found, deleteCount);
                    }
                }

                if (_operationalStoreNotification != null)
                {
                    await _operationalStoreNotification.PersistedGrantsRemovedAsync(expiredGrants);
                }
            }
        }
    }

    /// <summary>
    /// Removes the consumed persisted grants.
    /// </summary>
    /// <returns></returns>
    protected virtual async Task RemoveConsumedPersistedGrantsAsync(CancellationToken cancellationToken = default)
    {
        var found = Int32.MaxValue;

        var delay = TimeSpan.FromSeconds(_options.ConsumedTokenCleanupDelay);
        var consumedTimeThreshold = DateTime.UtcNow.Subtract(delay);

        while (found >= _options.TokenCleanupBatchSize)
        {
            var expiredGrants = await _persistedGrantDbContext.PersistedGrants
                .Where(x => x.ConsumedTime < consumedTimeThreshold)
                .OrderBy(x => x.ConsumedTime)
                .Take(_options.TokenCleanupBatchSize)
                .ToArrayAsync(cancellationToken);

            found = expiredGrants.Length;

            if (found > 0)
            {
                _logger.LogInformation("Removing {grantCount} consumed grants", found);

                _persistedGrantDbContext.PersistedGrants.RemoveRange(expiredGrants);

                var list = await _persistedGrantDbContext.SaveChangesWithConcurrencyCheckAsync<Entities.PersistedGrant>(_logger, cancellationToken);
                expiredGrants = expiredGrants.Except(list).ToArray();

                if (_operationalStoreNotification != null)
                {
                    await _operationalStoreNotification.PersistedGrantsRemovedAsync(expiredGrants);
                }
            }
        }
    }


    /// <summary>
    /// Removes the stale device codes.
    /// </summary>
    /// <returns></returns>
    protected virtual async Task RemoveDeviceCodesAsync(CancellationToken cancellationToken = default)
    {
        var found = Int32.MaxValue;

        while (found >= _options.TokenCleanupBatchSize)
        {
            var expiredCodes = await _persistedGrantDbContext.DeviceFlowCodes
                .Where(x => x.Expiration < DateTime.UtcNow)
                .OrderBy(x => x.DeviceCode)
                .Take(_options.TokenCleanupBatchSize)
                .ToArrayAsync(cancellationToken);

            found = expiredCodes.Length;

            if (found > 0)
            {
                _logger.LogInformation("Removing {deviceCodeCount} device flow codes", found);

                _persistedGrantDbContext.DeviceFlowCodes.RemoveRange(expiredCodes);

                var list = await _persistedGrantDbContext.SaveChangesWithConcurrencyCheckAsync<Entities.DeviceFlowCodes>(_logger, cancellationToken);
                expiredCodes = expiredCodes.Except(list).ToArray();

                if (_operationalStoreNotification != null)
                {
                    await _operationalStoreNotification.DeviceCodesRemovedAsync(expiredCodes);
                }
            }
        }
    }

    /// <summary>
    /// Removes stale pushed authorization requests.
    /// </summary>
    protected virtual async Task RemovePushedAuthorizationRequestsAsync(CancellationToken cancellationToken = default)
    {
        var x = await _persistedGrantDbContext.PushedAuthorizationRequests
            .Where(p => p.ExpiresAtUtc < DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        _logger.LogInformation("Removed {parCount} stale pushed authorization requests", x);
    }
}