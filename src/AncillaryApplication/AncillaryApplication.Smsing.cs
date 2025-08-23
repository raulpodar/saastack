using AncillaryApplication.Persistence.ReadModels;
using AncillaryDomain;
using Application.Common.Extensions;
using Application.Interfaces;
using Application.Persistence.Common.Extensions;
using Application.Persistence.Shared.Extensions;
using Application.Persistence.Shared.ReadModels;
using Application.Resources.Shared;
using Common;
using Common.Extensions;
using Domain.Common.ValueObjects;
using Domain.Shared;

namespace AncillaryApplication;

partial class AncillaryApplication
{
    public async Task<Result<Error>> ConfirmSmsDeliveredAsync(ICallerContext caller, string receiptId,
        DateTime deliveredAt, CancellationToken cancellationToken)
    {
        var retrieved = await _smsDeliveryRepository.FindByReceiptIdAsync(receiptId, cancellationToken);
        if (retrieved.IsFailure)
        {
            return retrieved.Error;
        }

        if (!retrieved.Value.HasValue)
        {
            return Result.Ok;
        }

        var sms = retrieved.Value.Value;
        var delivered = sms.ConfirmDelivery(receiptId, deliveredAt);
        if (delivered.IsFailure)
        {
            if (delivered.Error.Is(ErrorCode.RuleViolation))
            {
                return Result.Ok;
            }

            return delivered.Error;
        }

        var saved = await _smsDeliveryRepository.SaveAsync(sms, false, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        sms = saved.Value;
        _recorder.TraceInformation(caller.ToCall(), "Sms {Receipt} confirmed delivered for {For}",
            receiptId, sms.Recipient.Value.Number);

        return Result.Ok;
    }

    public async Task<Result<Error>> ConfirmSmsDeliveryFailedAsync(ICallerContext caller, string receiptId,
        DateTime failedAt, string reason, CancellationToken cancellationToken)
    {
        var retrieved = await _smsDeliveryRepository.FindByReceiptIdAsync(receiptId, cancellationToken);
        if (retrieved.IsFailure)
        {
            return retrieved.Error;
        }

        if (!retrieved.Value.HasValue)
        {
            return Result.Ok;
        }

        var sms = retrieved.Value.Value;
        var delivered = sms.ConfirmDeliveryFailed(receiptId, failedAt, reason);
        if (delivered.IsFailure)
        {
            if (delivered.Error.Is(ErrorCode.RuleViolation))
            {
                return Result.Ok;
            }

            return delivered.Error;
        }

        var saved = await _smsDeliveryRepository.SaveAsync(sms, false, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        sms = saved.Value;
        _recorder.TraceInformation(caller.ToCall(), "Sms {Receipt} confirmed delivery failed for {For}",
            receiptId, sms.Recipient.Value.Number);

        return Result.Ok;
    }

#if TESTINGONLY
    public async Task<Result<Error>> DrainAllSmsesAsync(ICallerContext caller, CancellationToken cancellationToken)
    {
        await _smsMessageQueue.DrainAllQueuedMessagesAsync(
            message => SendSmsInternalAsync(caller, message, cancellationToken), cancellationToken);

        _recorder.TraceInformation(caller.ToCall(), "Drained all sms messages");

        return Result.Ok;
    }
#endif

    public async Task<Result<SearchResults<DeliveredSms>, Error>> SearchAllSmsDeliveriesAsync(ICallerContext caller,
        DateTime? sinceUtc, string? organizationId, IReadOnlyList<string>? tags, SearchOptions searchOptions,
        GetOptions getOptions, CancellationToken cancellationToken)
    {
        var sinceWhen = sinceUtc ?? DateTime.UtcNow.SubtractDays(14);
        var searched =
            await _smsDeliveryRepository.SearchAllAsync(sinceWhen, organizationId, tags, searchOptions,
                cancellationToken);
        if (searched.IsFailure)
        {
            return searched.Error;
        }

        var deliveries = searched.Value;
        _recorder.TraceInformation(caller.ToCall(), "All sms deliveries since {Since} were fetched",
            sinceUtc.ToIso8601());

        return deliveries.ToSearchResults(searchOptions, delivery => delivery.ToDeliveredSms());
    }

    public async Task<Result<bool, Error>> SendSmsAsync(ICallerContext caller, string messageAsJson,
        CancellationToken cancellationToken)
    {
        var rehydrated = messageAsJson.RehydrateQueuedMessage<SmsMessage>();
        if (rehydrated.IsFailure)
        {
            return rehydrated.Error;
        }

        var sent = await SendSmsInternalAsync(caller, rehydrated.Value, cancellationToken);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        _recorder.TraceInformation(caller.ToCall(), "Sent sms message: {Message}", messageAsJson);
        return true;
    }

    private async Task<Result<bool, Error>> SendSmsInternalAsync(ICallerContext caller, SmsMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Message.IsInvalidParameter(x => x.Exists(), nameof(SmsMessage.Message), out _))
        {
            return Error.RuleViolation(Resources.AncillaryApplication_Sms_MissingMessage);
        }

        var messageId = QueuedMessageId.Create(message.MessageId!);
        if (messageId.IsFailure)
        {
            return messageId.Error;
        }

        var retrieved = await _smsDeliveryRepository.FindByMessageIdAsync(messageId.Value, cancellationToken);
        if (retrieved.IsFailure)
        {
            return retrieved.Error;
        }

        var body = message.Message!.Body;
        var recipientPhoneNumber = PhoneNumber.Create(message.Message!.ToPhoneNumber!);
        if (recipientPhoneNumber.IsFailure)
        {
            return recipientPhoneNumber.Error;
        }

        var recipient = recipientPhoneNumber.Value;
        var tags = message.Message!.Tags;

        SmsDeliveryRoot sms;
        var found = retrieved.Value.HasValue;
        if (found)
        {
            sms = retrieved.Value.Value;
        }
        else
        {
            var created = SmsDeliveryRoot.Create(_recorder, _idFactory, messageId.Value, message.TenantId.HasValue()
                ? message.TenantId.ToId()
                : Optional<Identifier>.None, caller.HostRegion);
            if (created.IsFailure)
            {
                return created.Error;
            }

            sms = created.Value;

            var detailed = sms.SetSmsDetails(body, recipient, tags);
            if (detailed.IsFailure)
            {
                return detailed.Error;
            }
        }

        var makeAttempt = sms.AttemptSending();
        if (makeAttempt.IsFailure)
        {
            return makeAttempt.Error;
        }

        var isAlreadyDelivered = makeAttempt.Value;
        if (isAlreadyDelivered)
        {
            _recorder.TraceInformation(caller.ToCall(), "Sms {Id} or {For} (from {Region}) is already sent",
                sms.Id, sms.Recipient.Value.Number, message.OriginHostRegion ?? DatacenterLocations.Unknown.Code);
            return true;
        }

        var saved = await _smsDeliveryRepository.SaveAsync(sms, true, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        sms = saved.Value;
        var sent = await _smsDeliveryService.SendAsync(caller, body!, recipient.Number, tags, cancellationToken);
        if (sent.IsFailure)
        {
            var failed = sms.FailedSending();
            if (failed.IsFailure)
            {
                return failed.Error;
            }

            var savedFailure = await _smsDeliveryRepository.SaveAsync(sms, false, cancellationToken);
            if (savedFailure.IsFailure)
            {
                return savedFailure.Error;
            }

            _recorder.TraceInformation(caller.ToCall(),
                "Sending of sms {Id} for delivery for {For} (from {Region}), failed",
                sms.Id, savedFailure.Value.Recipient.Value.Number,
                message.OriginHostRegion ?? DatacenterLocations.Unknown.Code);

            return sent.Error;
        }

        var succeeded = sms.SucceededSending(sent.Value.ReceiptId.ToOptional());
        if (succeeded.IsFailure)
        {
            return succeeded.Error;
        }

        var updated = await _smsDeliveryRepository.SaveAsync(sms, false, cancellationToken);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        sms = updated.Value;
        _recorder.TraceInformation(caller.ToCall(), "Sent sms {Id} for delivery for {For} (from {Region})",
            sms.Id, sms.Recipient.Value.Number, message.OriginHostRegion ?? DatacenterLocations.Unknown.Code);

        return true;
    }
}

public static class AncillaryMSmsingConversionExtensions
{
    public static DeliveredSms ToDeliveredSms(this SmsDelivery sms)
    {
        return new DeliveredSms
        {
            Created = sms.Created.ToNullable<DateTime?, DateTime>(x => x!.Value) ?? DateTime.UtcNow,
            Attempts = sms.Attempts.ToNullable(att => att.Attempts.ToList()) ?? [],
            Body = sms.Body,
            IsSent = sms.Sent.HasValue,
            SentAt = sms.Sent.ToNullable<DateTime?, DateTime>(),
            ToPhoneNumber = sms.ToPhoneNumber,
            Id = sms.Id,
            OrganizationId = sms.OrganizationId,
            IsDelivered = sms.Delivered.HasValue,
            DeliveredAt = sms.Delivered.ToNullable<DateTime?, DateTime>(),
            IsDeliveryFailed = sms.DeliveryFailed.HasValue,
            FailedDeliveryAt = sms.DeliveryFailed.ToNullable<DateTime?, DateTime>(),
            FailedDeliveryReason = sms.DeliveryFailedReason.ToNullable(),
            Tags = sms.Tags.ToNullable(tags => tags.FromJson<List<string>>()!) ?? []
        };
    }
}