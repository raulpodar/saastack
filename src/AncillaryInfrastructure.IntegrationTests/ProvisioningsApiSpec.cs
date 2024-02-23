using System.Text.Json;
using ApiHost1;
using Application.Interfaces.Services;
using Application.Persistence.Shared;
using Application.Persistence.Shared.ReadModels;
using Common;
using Common.Extensions;
using FluentAssertions;
using Infrastructure.Web.Api.Common.Extensions;
using Infrastructure.Web.Api.Operations.Shared.Ancillary;
using Infrastructure.Web.Api.Operations.Shared.Organizations;
using IntegrationTesting.WebApi.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace AncillaryInfrastructure.IntegrationTests;

[Trait("Category", "Integration.Web")]
[Collection("API")]
public class ProvisioningsApiSpec : WebApiSpec<Program>
{
    private readonly IProvisioningMessageQueue _provisioningMessageQueue;

    public ProvisioningsApiSpec(WebApiSetup<Program> setup) : base(setup, OverrideDependencies)
    {
        EmptyAllRepositories();
        _provisioningMessageQueue = setup.GetRequiredService<IProvisioningMessageQueue>();
        _provisioningMessageQueue.DestroyAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task WhenDeliverProvisioning_ThenDelivers()
    {
        var login = await LoginUserAsync(LoginUser.Operator);
        var tenantId = login.User.Profile!.DefaultOrganizationId!;

        var request = new DeliverProvisioningRequest
        {
            Message = new ProvisioningMessage
            {
                MessageId = "amessageid",
                TenantId = tenantId,
                CallId = "acallid",
                CallerId = "acallerid",
                Settings = new Dictionary<string, TenantSetting>
                {
                    { "aname1", new TenantSetting("avalue") },
                    { "aname2", new TenantSetting(99) },
                    { "aname3", new TenantSetting(true) }
                }
            }.ToJson()!
        };
        var result = await Api.PostAsync(request, req => req.SetHMACAuth(request, "asecret"));

        result.Content.Value.IsDelivered.Should().BeTrue();

#if TESTINGONLY
        var organization = await Api.GetAsync(new GetOrganizationSettingsRequest
        {
            Id = tenantId
        }, req => req.SetJWTBearerToken(login.AccessToken));

        organization.Content.Value.Settings!.Count.Should().Be(3);
        organization.Content.Value.Settings["aname1"].As<JsonElement>().GetString().Should().Be("avalue");
        organization.Content.Value.Settings["aname2"].As<JsonElement>().GetString().Should().Be("99");
        organization.Content.Value.Settings["aname3"].As<JsonElement>().GetString().Should().Be("True");
#endif
    }

#if TESTINGONLY
    [Fact]
    public async Task WhenDrainAllProvisioningsAndSomeWithUnknownTenancies_ThenDrains()
    {
        var login = await LoginUserAsync();
        var tenantId = login.User.Profile!.DefaultOrganizationId;
        var call = CallContext.CreateCustom("acallid", "acallerid", tenantId);
        await _provisioningMessageQueue.PushAsync(call, new ProvisioningMessage
        {
            MessageId = "amessageid1",
            TenantId = tenantId,
            Settings = new Dictionary<string, TenantSetting>
            {
                { "aname1", new TenantSetting("avalue1") },
                { "aname2", new TenantSetting(99) },
                { "aname3", new TenantSetting(true) }
            }
        }, CancellationToken.None);
        await _provisioningMessageQueue.PushAsync(call, new ProvisioningMessage
        {
            MessageId = "amessageid3",
            TenantId = "anothertenantid",
            Settings = new Dictionary<string, TenantSetting>
            {
                { "aname1", new TenantSetting("avalue3") },
                { "aname2", new TenantSetting(999) },
                { "aname3", new TenantSetting(false) }
            }
        }, CancellationToken.None);

        var request = new DrainAllProvisioningsRequest();
        await Api.PostAsync(request, req => req.SetHMACAuth(request, "asecret"));

        var organization = await Api.GetAsync(new GetOrganizationSettingsRequest
        {
            Id = tenantId!
        }, req => req.SetJWTBearerToken(login.AccessToken));

        organization.Content.Value.Settings!.Count.Should().Be(3);
        organization.Content.Value.Settings["aname1"].As<JsonElement>().GetString().Should().Be("avalue1");
        organization.Content.Value.Settings["aname2"].As<JsonElement>().GetString().Should().Be("99");
        organization.Content.Value.Settings["aname3"].As<JsonElement>().GetString().Should().Be("True");
    }
#endif

    private static void OverrideDependencies(IServiceCollection services)
    {
        // nothing here yet
    }
}