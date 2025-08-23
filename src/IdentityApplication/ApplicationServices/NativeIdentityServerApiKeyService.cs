using Application.Common;
using Application.Common.Extensions;
using Application.Interfaces;
using Application.Persistence.Common.Extensions;
using Application.Resources.Shared;
using Application.Services.Shared;
using Common;
using Domain.Common.Identity;
using Domain.Common.ValueObjects;
using Domain.Services.Shared;
using Domain.Shared;
using IdentityApplication.Persistence;
using IdentityApplication.Persistence.ReadModels;
using IdentityDomain;
using IdentityDomain.DomainServices;

namespace IdentityApplication.ApplicationServices;

/// <summary>
///     Provides a native API Keys service for managing and persisting API Keys
/// </summary>
public class NativeIdentityServerApiKeyService : IIdentityServerApiKeyService
{
    private const string ProviderName = "apikey";
    private readonly IAPIKeyHasherService _apiKeyHasherService;
    private readonly IEndUsersService _endUsersService;
    private readonly IIdentifierFactory _identifierFactory;
    private readonly IRecorder _recorder;
    private readonly IAPIKeysRepository _repository;
    private readonly ITokensService _tokensService;
    private readonly IUserProfilesService _userProfilesService;

    public NativeIdentityServerApiKeyService(IRecorder recorder, IIdentifierFactory identifierFactory,
        ITokensService tokensService, IAPIKeyHasherService apiKeyHasherService, IEndUsersService endUsersService,
        IUserProfilesService userProfilesService, IAPIKeysRepository repository)
    {
        _recorder = recorder;
        _identifierFactory = identifierFactory;
        _tokensService = tokensService;
        _apiKeyHasherService = apiKeyHasherService;
        _endUsersService = endUsersService;
        _userProfilesService = userProfilesService;
        _repository = repository;
    }

    public async Task<Result<EndUserWithMemberships, Error>> AuthenticateAsync(ICallerContext caller, string apiKey,
        CancellationToken cancellationToken)
    {
        var keyToken = _tokensService.ParseApiKey(apiKey);
        if (!keyToken.HasValue)
        {
            return Error.NotAuthenticated();
        }

        var retrievedApiKey = await _repository.FindByAPIKeyTokenAsync(keyToken.Value.Token, cancellationToken);
        if (retrievedApiKey.IsFailure)
        {
            return retrievedApiKey.Error;
        }

        if (!retrievedApiKey.Value.HasValue)
        {
            return Error.NotAuthenticated();
        }

        var root = retrievedApiKey.Value.Value;
        if (!root.IsStillValid)
        {
            return Error.NotAuthenticated();
        }

        var retrievedUser =
            await _endUsersService.GetMembershipsPrivateAsync(caller, root.UserId, cancellationToken);
        if (retrievedUser.IsFailure)
        {
            return Error.NotAuthenticated();
        }

        var user = retrievedUser.Value;
        if (user.Status != EndUserStatus.Registered)
        {
            return Error.NotAuthenticated();
        }

        if (user.Access == EndUserAccess.Suspended)
        {
            _recorder.AuditAgainst(caller.ToCall(), user.Id,
                Audits.APIKeysApplication_Authenticate_AccountSuspended,
                "User {Id} tried to authenticate a APIKey with a suspended account", user.Id);
            return Error.EntityExists(Resources.NativeIdentityServerApiKeyService_AccountSuspended);
        }

        var maintenance = Caller.CreateAsMaintenance(caller);
        var profiled = await _userProfilesService.GetProfilePrivateAsync(maintenance, user.Id, cancellationToken);
        if (profiled.IsFailure)
        {
            return profiled.Error;
        }

        var profile = profiled.Value;
        _recorder.AuditAgainst(caller.ToCall(), user.Id,
            Audits.APIKeysApplication_Authenticate_Succeeded,
            "User {Id} succeeded to authenticate with APIKey", user.Id);
        _recorder.TrackUsageFor(caller.ToCall(), user.Id, UsageConstants.Events.UsageScenarios.Generic.UserLogin,
            user.ToLoginUserUsage(ProviderName, profile));

        return user;
    }

    public async Task<Result<APIKey, Error>> CreateAPIKeyForUserAsync(ICallerContext caller, string userId,
        string description, DateTime? expiresOn, CancellationToken cancellationToken)
    {
        var keyToken = _tokensService.CreateAPIKey();

        var created = APIKeyRoot.Create(_recorder, _identifierFactory, _apiKeyHasherService, userId.ToId(), keyToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var apiKey = created.Value;
        // ReSharper disable once MergeConditionalExpression
        var parameterized = apiKey.SetParameters(description, expiresOn.HasValue
            ? expiresOn.Value
            : Optional<DateTime>.None);
        if (parameterized.IsFailure)
        {
            return parameterized.Error;
        }

        var saved = await _repository.SaveAsync(apiKey, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        apiKey = saved.Value;
        var expired = await ExpireAllOtherAPIKeysForUserAsync(caller, userId.ToId(), apiKey.Id, cancellationToken);
        if (expired.IsFailure)
        {
            return expired.Error;
        }

        _recorder.TraceInformation(caller.ToCall(), "APIKey {Id} was created for user {User}", apiKey.Id, userId);
        return apiKey.ToApiKey(keyToken.ApiKey, description);
    }

    public async Task<Result<Error>> DeleteAPIKeyForUserAsync(ICallerContext caller, string id, string userId,
        CancellationToken cancellationToken)
    {
        var retrieved = await _repository.LoadAsync(id.ToId(), cancellationToken);
        if (retrieved.IsFailure)
        {
            return retrieved.Error;
        }

        var apiKey = retrieved.Value;
        var deleted = apiKey.Delete(userId.ToId());
        if (deleted.IsFailure)
        {
            return deleted.Error;
        }

        var saved = await _repository.SaveAsync(apiKey, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        apiKey = saved.Value;
        _recorder.TraceInformation(caller.ToCall(), "APIKey {Id} was deleted by {User}", apiKey.Id, userId);

        return Result.Ok;
    }

    public async Task<Result<Error>> RevokeAPIKeyAsync(ICallerContext caller, string id,
        CancellationToken cancellationToken)
    {
        var retrieved = await _repository.LoadAsync(id.ToId(), cancellationToken);
        if (retrieved.IsFailure)
        {
            return retrieved.Error;
        }

        var apiKey = retrieved.Value;
        var revokerRoles = Roles.Create(caller.Roles.All);
        if (revokerRoles.IsFailure)
        {
            return revokerRoles.Error;
        }

        var revoked = apiKey.Revoke(revokerRoles.Value);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        var saved = await _repository.SaveAsync(apiKey, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        apiKey = saved.Value;
        _recorder.TraceInformation(caller.ToCall(), "APIKey {Id} was revoked for {User}", apiKey.Id, apiKey.UserId);

        return Result.Ok;
    }

    public async Task<Result<SearchResults<APIKey>, Error>> SearchAllAPIKeysForUserAsync(ICallerContext caller,
        string userId,
        SearchOptions searchOptions, GetOptions getOptions, CancellationToken cancellationToken)
    {
        var searched = await _repository.SearchAllForUserAsync(userId.ToId(), searchOptions, cancellationToken);
        if (searched.IsFailure)
        {
            return searched.Error;
        }

        var apiKeys = searched.Value;
        _recorder.TraceInformation(caller.ToCall(), "All APIKeys were fetched for user {User}", userId);

        return apiKeys.ToSearchResults(searchOptions, key => key.ToApiKey());
    }

    private async Task<Result<Error>> ExpireAllOtherAPIKeysForUserAsync(ICallerContext caller, Identifier userId,
        Identifier apiKeyIdToIgnore, CancellationToken cancellationToken)
    {
        var retrievedUnexpired = await _repository.SearchAllUnexpiredForUserAsync(userId, cancellationToken);
        if (retrievedUnexpired.IsFailure)
        {
            return retrievedUnexpired.Error;
        }

        var unexpiredApiKeys = retrievedUnexpired.Value.Results;
        foreach (var unexpiredApiKey in unexpiredApiKeys)
        {
            var unexpiredAPIKeyId = unexpiredApiKey.Id.Value.ToId();
            if (unexpiredAPIKeyId == apiKeyIdToIgnore)
            {
                continue;
            }

            var retrieved = await _repository.LoadAsync(unexpiredAPIKeyId, cancellationToken);
            if (retrieved.IsFailure)
            {
                return retrieved.Error;
            }

            var apiKey = retrieved.Value;
            var expired = apiKey.ForceExpire(userId);
            if (expired.IsFailure)
            {
                return expired.Error;
            }

            var saved = await _repository.SaveAsync(apiKey, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error;
            }

            apiKey = saved.Value;
            _recorder.TraceInformation(caller.ToCall(), "APIKey {Id} was expired for {User}", apiKey.Id, userId);
        }

        return Result.Ok;
    }
}

internal static class ANativeIdentityServerApiKeyServiceExtensions
{
    public static APIKey ToApiKey(this APIKeyRoot apiKey, string key, string description)
    {
        return new APIKey
        {
            Description = description,
            ExpiresOnUtc = apiKey.ExpiresOn.ToNullable(),
            Key = key,
            UserId = apiKey.UserId,
            Id = apiKey.Id
        };
    }

    public static APIKey ToApiKey(this APIKeyAuth apiKey)
    {
        return new APIKey
        {
            Description = apiKey.Description,
            ExpiresOnUtc = apiKey.ExpiresOn.ToNullable<DateTime?, DateTime>(),
            Key = apiKey.KeyToken,
            UserId = apiKey.UserId,
            Id = apiKey.Id
        };
    }
}