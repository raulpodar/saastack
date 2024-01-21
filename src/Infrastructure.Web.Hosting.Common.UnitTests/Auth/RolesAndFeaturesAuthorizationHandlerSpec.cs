using System.Security.Claims;
using Application.Interfaces;
using Domain.Interfaces.Authorization;
using FluentAssertions;
using Infrastructure.Interfaces;
using Infrastructure.Web.Hosting.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Moq;
using Xunit;
using FeatureLevel = Domain.Interfaces.Authorization.FeatureLevel;

namespace Infrastructure.Web.Hosting.Common.UnitTests.Auth;

[Trait("Category", "Unit")]
public class RolesAndFeaturesAuthorizationHandlerSpec
{
    private readonly Mock<ICallerContext> _caller;
    private readonly RolesAndFeaturesAuthorizationHandler _handler;

    public RolesAndFeaturesAuthorizationHandlerSpec()
    {
        _caller = new Mock<ICallerContext>();
        var callerContextFactory = new Mock<ICallerContextFactory>();
        callerContextFactory.Setup(ccf => ccf.Create())
            .Returns(_caller.Object);
        _handler = new RolesAndFeaturesAuthorizationHandler(callerContextFactory.Object);
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasNoRolesOrFeaturesAndRequirementHasNoneEither_ThenSucceeds()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features).Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasNoRolesOrFeaturesAndRequirementHasRole_ThenFails()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features).Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(
            new ICallerContext.CallerRoles(new[] { new RoleLevel("arole") }, Array.Empty<RoleLevel>()),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingRole);
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasNoRolesOrFeaturesAndRequirementHasFeature_ThenFails()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features).Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures(new[] { new FeatureLevel("afeature") }, Array.Empty<FeatureLevel>()));
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingFeature);
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasSameRoleAsRequirement_ThenSucceeds()
    {
        _caller.Setup(cc => cc.Roles)
            .Returns(new ICallerContext.CallerRoles(new[] { new RoleLevel("arole") }, Array.Empty<RoleLevel>()));
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(
            new ICallerContext.CallerRoles(new[] { new RoleLevel("arole") }, Array.Empty<RoleLevel>()),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasDifferentRoleAsRequirement_ThenFailed()
    {
        _caller.Setup(cc => cc.Roles)
            .Returns(new ICallerContext.CallerRoles(new[] { new RoleLevel("arole") }, Array.Empty<RoleLevel>()));
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(
            new ICallerContext.CallerRoles(new[] { new RoleLevel("anotherrole") }, Array.Empty<RoleLevel>()),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingRole);
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasGrandParentRoleOfRequirement_ThenSucceeds()
    {
#if TESTINGONLY
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles(
            new[] { PlatformRoles.TestingOnlySuperUser },
            Array.Empty<RoleLevel>()));
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(
            new ICallerContext.CallerRoles(new[] { PlatformRoles.TestingOnly }, Array.Empty<RoleLevel>()),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
#endif
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasChildRoleOfRequirement_ThenFails()
    {
#if TESTINGONLY
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles(new[] { PlatformRoles.TestingOnly },
            Array.Empty<RoleLevel>()));
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures());
        var requirement = new RolesAndFeaturesRequirement(
            new ICallerContext.CallerRoles(new[] { PlatformRoles.TestingOnlySuperUser }, Array.Empty<RoleLevel>()),
            new ICallerContext.CallerFeatures());
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingRole);
#endif
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasSameFeaturesAsRequirement_ThenSucceeds()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Paid3 },
                Array.Empty<FeatureLevel>()));
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Paid3 }, Array.Empty<FeatureLevel>()));
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasDifferentFeaturesAsRequirement_ThenFails()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures(new[] { new FeatureLevel("afeature") },
                Array.Empty<FeatureLevel>()));
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures(new[] { new FeatureLevel("anotherfeature") },
                Array.Empty<FeatureLevel>()));
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingFeature);
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasGrandParentFeatureOfRequirement_ThenSucceeds()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Paid3 },
                Array.Empty<FeatureLevel>()));
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Basic }, Array.Empty<FeatureLevel>()));
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void WhenHandleRequirementAsyncAndCallerHasChildFeatureOfRequirement_ThenFails()
    {
        _caller.Setup(cc => cc.Roles).Returns(new ICallerContext.CallerRoles());
        _caller.Setup(cc => cc.Features)
            .Returns(new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Basic },
                Array.Empty<FeatureLevel>()));
        var requirement = new RolesAndFeaturesRequirement(new ICallerContext.CallerRoles(),
            new ICallerContext.CallerFeatures(new[] { PlatformFeatures.Paid3 }, Array.Empty<FeatureLevel>()));
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement },
            ClaimsPrincipal.Current!, null);

        _handler.HandleAsync(context);

        context.HasFailed.Should().BeTrue();
        context.FailureReasons.Should().ContainSingle(x =>
            x.Message == Resources.RolesAndFeaturesAuthorizationHandler_HandleRequirementAsync_MissingFeature);
    }
}