using Common;
using Domain.Common.ValueObjects;
using Domain.Events.Shared.Ancillary.EmailDelivery;
using Domain.Events.Shared.Ancillary.SmsDelivery;
using Domain.Shared;
using Domain.Shared.Ancillary;
using Created = Domain.Events.Shared.Ancillary.Audits.Created;
using DeliveryConfirmed = Domain.Events.Shared.Ancillary.EmailDelivery.DeliveryConfirmed;
using DeliveryFailureConfirmed = Domain.Events.Shared.Ancillary.EmailDelivery.DeliveryFailureConfirmed;
using SendingAttempted = Domain.Events.Shared.Ancillary.EmailDelivery.SendingAttempted;
using SendingFailed = Domain.Events.Shared.Ancillary.EmailDelivery.SendingFailed;
using SendingSucceeded = Domain.Events.Shared.Ancillary.EmailDelivery.SendingSucceeded;

namespace AncillaryDomain;

public static class Events
{
    public static class EmailDelivery
    {
        public static Domain.Events.Shared.Ancillary.EmailDelivery.Created Created(Identifier id,
            QueuedMessageId messageId, Optional<Identifier> organizationId, DatacenterLocation hostRegion)
        {
            return new Domain.Events.Shared.Ancillary.EmailDelivery.Created(id)
            {
                When = DateTime.UtcNow,
                MessageId = messageId,
                OrganizationId = organizationId.ToNullable(orgId => orgId.Text),
                HostRegion = hostRegion.Code
            };
        }

        public static DeliveryConfirmed DeliveryConfirmed(Identifier id, string receiptId, DateTime when)
        {
            return new DeliveryConfirmed(id)
            {
                When = when,
                ReceiptId = receiptId
            };
        }

        public static DeliveryFailureConfirmed DeliveryFailureConfirmed(Identifier id, string receiptId, DateTime when,
            string reason)
        {
            return new DeliveryFailureConfirmed(id)
            {
                When = when,
                ReceiptId = receiptId,
                Reason = reason
            };
        }

        public static EmailDetailsChanged EmailDetailsChanged(Identifier id, Optional<string> subject,
            Optional<string> body, Optional<string> templateId, Optional<Dictionary<string, string>> substitutions,
            EmailRecipient to, IReadOnlyList<string>? tags)
        {
            return new EmailDetailsChanged(id)
            {
                ContentType = templateId.HasValue
                    ? DeliveredEmailContentType.Templated
                    : DeliveredEmailContentType.Html,
                Subject = subject,
                Body = body,
                TemplateId = templateId,
                Substitutions = substitutions.HasValue
                    ? substitutions.Value
                    : new Dictionary<string, string>(),
                ToEmailAddress = to.EmailAddress,
                ToDisplayName = to.DisplayName,
                Tags = [..tags ?? new List<string>()]
            };
        }

        public static SendingAttempted SendingAttempted(Identifier id, DateTime when)
        {
            return new SendingAttempted(id)
            {
                When = when
            };
        }

        public static SendingFailed SendingFailed(Identifier id, DateTime when)
        {
            return new SendingFailed(id)
            {
                When = when
            };
        }

        public static SendingSucceeded SendingSucceeded(Identifier id, Optional<string> receiptId, DateTime when)
        {
            return new SendingSucceeded(id)
            {
                When = when,
                ReceiptId = receiptId.ToNullable()
            };
        }
    }

    public static class SmsDelivery
    {
        public static Domain.Events.Shared.Ancillary.SmsDelivery.Created Created(Identifier id,
            QueuedMessageId messageId, Optional<Identifier> organizationId, DatacenterLocation hostRegion)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.Created(id)
            {
                When = DateTime.UtcNow,
                MessageId = messageId,
                OrganizationId = organizationId.ToNullable(orgId => orgId.Text),
                HostRegion = hostRegion.Code
            };
        }

        public static Domain.Events.Shared.Ancillary.SmsDelivery.DeliveryConfirmed DeliveryConfirmed(Identifier id,
            string receiptId, DateTime when)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.DeliveryConfirmed(id)
            {
                When = when,
                ReceiptId = receiptId
            };
        }

        public static Domain.Events.Shared.Ancillary.SmsDelivery.DeliveryFailureConfirmed DeliveryFailureConfirmed(
            Identifier id, string receiptId, DateTime when,
            string reason)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.DeliveryFailureConfirmed(id)
            {
                When = when,
                ReceiptId = receiptId,
                Reason = reason
            };
        }

        public static Domain.Events.Shared.Ancillary.SmsDelivery.SendingAttempted SendingAttempted(Identifier id,
            DateTime when)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.SendingAttempted(id)
            {
                When = when
            };
        }

        public static Domain.Events.Shared.Ancillary.SmsDelivery.SendingFailed SendingFailed(Identifier id,
            DateTime when)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.SendingFailed(id)
            {
                When = when
            };
        }

        public static Domain.Events.Shared.Ancillary.SmsDelivery.SendingSucceeded SendingSucceeded(Identifier id,
            Optional<string> receiptId, DateTime when)
        {
            return new Domain.Events.Shared.Ancillary.SmsDelivery.SendingSucceeded(id)
            {
                When = when,
                ReceiptId = receiptId.ToNullable()
            };
        }

        public static SmsDetailsChanged SmsDetailsChanged(Identifier id, string body,
            PhoneNumber to, IReadOnlyList<string>? tags)
        {
            return new SmsDetailsChanged(id)
            {
                Body = body,
                ToPhoneNumber = to.Number,
                Tags = [..tags ?? new List<string>()]
            };
        }
    }

    public static class Audits
    {
        public static Created Created(Identifier id, Identifier againstId, Optional<Identifier> organizationId,
            string auditCode, Optional<string> messageTemplate, TemplateArguments templateArguments,
            DatacenterLocation hostRegion)
        {
            return new Created(id)
            {
                OrganizationId = organizationId.ToNullable(orgId => orgId.Text),
                AgainstId = againstId,
                AuditCode = auditCode,
                MessageTemplate = messageTemplate.ValueOrDefault ?? string.Empty,
                TemplateArguments = templateArguments.Items,
                HostRegion = hostRegion.Code
            };
        }
    }
}