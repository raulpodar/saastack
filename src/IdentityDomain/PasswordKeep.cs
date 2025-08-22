﻿using Common;
using Common.Extensions;
using Domain.Common.Extensions;
using Domain.Common.ValueObjects;
using Domain.Interfaces;
using Domain.Interfaces.ValueObjects;
using IdentityDomain.DomainServices;
using JetBrains.Annotations;

namespace IdentityDomain;

public sealed class PasswordKeep : ValueObjectBase<PasswordKeep>
{
    public static readonly TimeSpan DefaultResetExpiry = TimeSpan.FromHours(2);

    public static Result<PasswordKeep, Error> Create()
    {
        return new PasswordKeep(Optional<string>.None, Optional<string>.None, Optional<DateTime>.None);
    }

    public static Result<PasswordKeep, Error> Create(IPasswordHasherService passwordHasherService, string passwordHash)
    {
        if (passwordHash.IsInvalidParameter(passwordHasherService.ValidatePasswordHash, nameof(passwordHash),
                Resources.PasswordKeep_InvalidPasswordHash, out var error1))
        {
            return error1;
        }

        return new PasswordKeep(passwordHash, Optional<string>.None, Optional<DateTime>.None);
    }

    private PasswordKeep(Optional<string> passwordHash, Optional<string> resetToken,
        Optional<DateTime> tokenExpires)
    {
        PasswordHash = passwordHash;
        ResetToken = resetToken;
        TokenExpires = tokenExpires;
    }

    public bool HasPassword => PasswordHash.HasValue;

    public bool IsResetInitiated => ResetToken.HasValue && TokenExpires.HasValue;

    public bool IsResetStillValid => IsResetInitiated && TokenExpires > DateTime.UtcNow;

    public Optional<string> PasswordHash { get; }

    public Optional<string> ResetToken { get; }

    public Optional<DateTime> TokenExpires { get; }

    [UsedImplicitly]
    public static ValueObjectFactory<PasswordKeep> Rehydrate()
    {
        return (property, _) =>
        {
            var parts = RehydrateToList(property, false);
            return new PasswordKeep(
                parts[0],
                parts[1],
                parts[2].ToOptional(val => val.FromIso8601()));
        };
    }

    protected override IEnumerable<object?> GetAtomicValues()
    {
        return [PasswordHash, ResetToken, TokenExpires];
    }

    public Result<PasswordKeep, Error> CompletePasswordReset(IPasswordHasherService passwordHasherService, string token,
        string passwordHash)
    {
        if (token.IsNotValuedParameter(nameof(token), out var error1))
        {
            return error1;
        }

        if (token.IsInvalidParameter(Validations.Credentials.Password.ResetToken, nameof(token),
                Resources.PasswordKeep_InvalidToken, out var error2))
        {
            return error2;
        }

        if (passwordHash.IsNotValuedParameter(nameof(passwordHash), out var error3))
        {
            return error3;
        }

        if (passwordHash.IsInvalidParameter(passwordHasherService.ValidatePasswordHash,
                nameof(passwordHash), Resources.PasswordKeep_InvalidPasswordHash, out var error4))
        {
            return error4;
        }

        if (!HasPassword)
        {
            return Error.RuleViolation(Resources.PasswordKeep_NoPasswordHash);
        }

        if (token.NotEqualsOrdinal(ResetToken))
        {
            return Error.RuleViolation(Resources.PasswordKeep_TokensNotMatch);
        }

        if (!IsResetStillValid)
        {
            return Error.PreconditionViolation(Resources.PasswordKeep_TokenExpired);
        }

        return new PasswordKeep(passwordHash, Optional<string>.None, Optional<DateTime>.None);
    }

    public Result<PasswordKeep, Error> InitiatePasswordReset(string token)
    {
        if (token.IsNotValuedParameter(nameof(token), out var error1))
        {
            return error1;
        }

        if (token.IsInvalidParameter(Validations.Credentials.Password.ResetToken, nameof(token),
                Resources.PasswordKeep_InvalidToken, out var error2))
        {
            return error2;
        }

        if (!HasPassword)
        {
            return Error.RuleViolation(Resources.PasswordKeep_NoPasswordHash);
        }

        var expiry = DateTime.UtcNow.Add(DefaultResetExpiry);
        return new PasswordKeep(PasswordHash, token, expiry);
    }

    public Result<PasswordKeep, Error> SetPassword(IPasswordHasherService passwordHasherService, string passwordHash)
    {
        if (passwordHash.IsNotValuedParameter(nameof(passwordHash), out var error1))
        {
            return error1;
        }

        if (passwordHash.IsInvalidParameter(passwordHasherService.ValidatePasswordHash,
                nameof(passwordHash), Resources.PasswordKeep_InvalidPasswordHash, out var error2))
        {
            return error2;
        }

        return new PasswordKeep(passwordHash, Optional<string>.None, Optional<DateTime>.None);
    }

#if TESTINGONLY

    public PasswordKeep TestingOnly_ExpireToken()
    {
        return new PasswordKeep(PasswordHash, ResetToken, DateTime.UtcNow.SubtractSeconds(1));
    }
#endif

    [SkipImmutabilityCheck]
    public Result<bool, Error> Verify(IPasswordHasherService passwordHasherService, string password)
    {
        if (password.IsNotValuedParameter(nameof(password), out var error1))
        {
            return error1;
        }

        if (password.IsInvalidParameter(pwd => passwordHasherService.ValidatePassword(pwd, false),
                nameof(password), Resources.PasswordKeep_InvalidPassword, out var error2))
        {
            return error2;
        }

        if (!HasPassword)
        {
            return Error.RuleViolation(Resources.PasswordKeep_NoPasswordHash);
        }

        return passwordHasherService.VerifyPassword(password, PasswordHash);
    }

    public Result<PasswordKeep, Error> VerifyReset(string token)
    {
        if (token.IsNotValuedParameter(nameof(token), out var error1))
        {
            return error1;
        }

        if (token.IsInvalidParameter(Validations.Credentials.Password.ResetToken, nameof(token),
                Resources.PasswordKeep_InvalidToken, out var error2))
        {
            return error2;
        }

        if (token.NotEqualsOrdinal(ResetToken))
        {
            return Error.RuleViolation(Resources.PasswordKeep_TokensNotMatch);
        }

        if (!IsResetStillValid)
        {
            return Error.PreconditionViolation(Resources.PasswordKeep_TokenExpired);
        }

        return this;
    }
}