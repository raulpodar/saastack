﻿using Infrastructure.Web.Api.Interfaces;

namespace Infrastructure.Web.Api.Operations.Shared.Ancillary;

[Route("/audits/deliver", ServiceOperation.Post)]
public class DeliverAuditRequest : UnTenantedRequest<DeliverMessageResponse>
{
    public required string Message { get; set; }
}