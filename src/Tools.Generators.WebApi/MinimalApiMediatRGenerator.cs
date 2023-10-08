using System.Text;
using Infrastructure.WebApi.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Tools.Generators.WebApi.Extensions;

namespace Tools.Generators.WebApi;

/// <summary>
///     A source generators for converting <see cref="IWebApiService" /> to
///     Minimal API registrations and MediatR handlers
/// </summary>
[Generator]
public class MinimalApiMediatRGenerator : ISourceGenerator
{
    private const string Filename = "MinimalApiMediatRGeneratedHandlers.g.cs";
    private const string RegistrationClassName = "MinimalApiRegistration";
    private const string TestingOnlyDirective = "TESTINGONLY";

    private static readonly string[] RequiredUsingNamespaces =
    {
        "System", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Http",
        "Infrastructure.WebApi.Common"
    };

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var assemblyNamespace = context.Compilation.AssemblyName;
        var serviceClasses = GetWebApiServiceOperationsFromAssembly(context)
            .GroupBy(registrations => registrations.Class.TypeName)
            .ToList();

        var classUsingNamespaces = BuildUsingList(serviceClasses);
        var handlerClasses = new StringBuilder();
        var endpointRegistrations = new StringBuilder();
        foreach (var serviceRegistrations in serviceClasses)
        {
            BuildHandlerClasses(serviceRegistrations, handlerClasses);

            BuildEndpointRegistrations(serviceRegistrations, endpointRegistrations);
        }

        var fileSource = BuildFile(assemblyNamespace, classUsingNamespaces, endpointRegistrations.ToString(),
            handlerClasses.ToString());

        context.AddSource(Filename, SourceText.From(fileSource, Encoding.UTF8));

        return;

        static string BuildFile(string? assemblyNamespace, string allUsingNamespaces, string allEndpointRegistrations,
            string allHandlerClasses)
        {
            return $@"// <auto-generated/>
{allUsingNamespaces}
namespace {assemblyNamespace}
{{
    public static class {RegistrationClassName}
    {{
        public static void RegisterRoutes(this global::Microsoft.AspNetCore.Builder.WebApplication app)
        {{
    {allEndpointRegistrations}
        }}
    }}
}}

{allHandlerClasses}";
        }
    }

    private static string BuildUsingList(
        List<IGrouping<WebApiAssemblyVisitor.TypeName, WebApiAssemblyVisitor.ServiceOperationRegistration>>
            serviceClasses)
    {
        var usingList = new StringBuilder();

        var allNamespaces = serviceClasses.SelectMany(serviceClass => serviceClass)
            .SelectMany(registration => registration.Class.UsingNamespaces)
            .Concat(RequiredUsingNamespaces)
            .Distinct()
            .OrderByDescending(s => s)
            .ToList();

        allNamespaces.ForEach(@using => usingList.AppendLine($"using {@using};"));

        return usingList.ToString();
    }

    private static void BuildEndpointRegistrations(
        IGrouping<WebApiAssemblyVisitor.TypeName, WebApiAssemblyVisitor.ServiceOperationRegistration>
            serviceRegistrations, StringBuilder endpointRegistrations)
    {
        var serviceClassName = serviceRegistrations.Key.Name;
        var groupName = $"{serviceClassName.ToLowerInvariant()}Group";
        endpointRegistrations.AppendLine($@"        var {groupName} = app.MapGroup(string.Empty)
                .WithGroupName(""{serviceClassName}"")
                .AddEndpointFilter<global::Infrastructure.WebApi.Common.RequestCorrelationFilter>()
                .AddEndpointFilter<global::Infrastructure.WebApi.Common.ContentNegotiationFilter>();");

        foreach (var registration in serviceRegistrations)
        {
            if (registration.IsTestingOnly)
            {
                endpointRegistrations.AppendLine($"#if {TestingOnlyDirective}");
            }

            var routeEndpointMethodNames = ToMinimalApiRegistrationMethodNames(registration.OperationType);
            foreach (var routeEndpointMethod in routeEndpointMethodNames)
            {
                endpointRegistrations.AppendLine(
                    $"            {groupName}.{routeEndpointMethod}(\"{registration.RoutePath}\",");
                if (registration.OperationType == WebApiOperation.Get
                    || registration.OperationType == WebApiOperation.Search
                    || registration.OperationType == WebApiOperation.Delete)
                {
                    endpointRegistrations.AppendLine(
                        $"                async (global::MediatR.IMediator mediator, [global::Microsoft.AspNetCore.Http.AsParameters] global::{registration.RequestDtoType.FullName} request) =>");
                }
                else
                {
                    endpointRegistrations.AppendLine(
                        $"                async (global::MediatR.IMediator mediator, global::{registration.RequestDtoType.FullName} request) =>");
                }

                endpointRegistrations.AppendLine(
                    "                     await mediator.Send(request, global::System.Threading.CancellationToken.None));");
            }

            if (registration.IsTestingOnly)
            {
                endpointRegistrations.AppendLine("#endif");
            }
        }
    }

    private static void BuildHandlerClasses(
        IGrouping<WebApiAssemblyVisitor.TypeName, WebApiAssemblyVisitor.ServiceOperationRegistration>
            serviceRegistrations, StringBuilder handlerClasses)
    {
        var serviceClassNamespace = $"{serviceRegistrations.Key.FullName}MediatRHandlers";
        handlerClasses.AppendLine($"namespace {serviceClassNamespace}");
        handlerClasses.AppendLine("{");

        foreach (var registration in serviceRegistrations)
        {
            var handlerClassName = $"{registration.MethodName}_{registration.RequestDtoType.Name}_Handler";
            var constructorAndFields = BuildInjectorConstructorAndFields(handlerClassName,
                registration.Class.Constructors.ToList());

            if (registration.IsTestingOnly)
            {
                handlerClasses.AppendLine($"#if {TestingOnlyDirective}");
            }

            handlerClasses.AppendLine(
                $"    public class {handlerClassName} : global::MediatR.IRequestHandler<global::{registration.RequestDtoType.FullName},"
                + $" global::Microsoft.AspNetCore.Http.IResult>");
            handlerClasses.AppendLine("    {");
            if (constructorAndFields.HasValue())
            {
                handlerClasses.AppendLine(constructorAndFields);
            }

            handlerClasses.AppendLine($"        public async Task<global::Microsoft.AspNetCore.Http.IResult>"
                                      + $" Handle(global::{registration.RequestDtoType.FullName} request, global::System.Threading.CancellationToken cancellationToken)");
            handlerClasses.AppendLine("        {");
            if (!registration.IsAsync)
            {
                handlerClasses.AppendLine(
                    "            await Task.CompletedTask;");
            }

            var callingParameters = BuildInjectedParameters(registration.Class.Constructors.ToList());
            handlerClasses.AppendLine(
                $"            var api = new global::{registration.Class.TypeName.FullName}({callingParameters});");
            var asyncAwait = registration.IsAsync
                ? "await "
                : string.Empty;
            var hasCancellationToken = registration.HasCancellationToken
                ? ", cancellationToken"
                : string.Empty;
            handlerClasses.AppendLine(
                $"            var result = {asyncAwait}api.{registration.MethodName}(request{hasCancellationToken});");
            handlerClasses.AppendLine(
                $"            return result.HandleApiResult(global::Infrastructure.WebApi.Interfaces.WebApiOperation.{registration.OperationType});");
            handlerClasses.AppendLine("        }");
            handlerClasses.AppendLine("    }");
            if (registration.IsTestingOnly)
            {
                handlerClasses.AppendLine("#endif");
            }

            handlerClasses.AppendLine();
        }

        handlerClasses.AppendLine("}");
        handlerClasses.AppendLine();
    }

    private static string BuildInjectorConstructorAndFields(string? handlerClassName,
        List<WebApiAssemblyVisitor.Constructor> constructors)
    {
        var handlerClassConstructorAndFields = new StringBuilder();

        var injectorCtor = constructors.FirstOrDefault(ctor => ctor.IsInjectionCtor);
        if (injectorCtor is not null)
        {
            var parameters = injectorCtor.CtorParameters.ToList();
            foreach (var param in parameters)
            {
                handlerClassConstructorAndFields.AppendLine(
                    $"        private readonly global::{param.TypeName.FullName} _{param.VariableName};");
            }

            handlerClassConstructorAndFields.AppendLine();
            handlerClassConstructorAndFields.Append($"        public {handlerClassName}(");
            var paramsRemaining = parameters.Count();
            foreach (var param in parameters)
            {
                handlerClassConstructorAndFields.Append($"global::{param.TypeName.FullName} {param.VariableName}");
                if (--paramsRemaining > 0)
                {
                    handlerClassConstructorAndFields.Append(", ");
                }
            }

            handlerClassConstructorAndFields.AppendLine(")");
            handlerClassConstructorAndFields.AppendLine("        {");
            foreach (var param in parameters)
            {
                handlerClassConstructorAndFields.AppendLine(
                    $"            this._{param.VariableName} = {param.VariableName};");
            }

            handlerClassConstructorAndFields.AppendLine("        }");
        }

        return handlerClassConstructorAndFields.ToString();
    }

    private static string BuildInjectedParameters(List<WebApiAssemblyVisitor.Constructor> constructors)
    {
        var methodParameters = new StringBuilder();

        var injectorCtor = constructors.FirstOrDefault(ctor => ctor.IsInjectionCtor);
        if (injectorCtor is not null)
        {
            var parameters = injectorCtor.CtorParameters.ToList();

            var paramsRemaining = parameters.Count();
            foreach (var param in parameters)
            {
                methodParameters.Append($"this._{param.VariableName}");
                if (--paramsRemaining > 0)
                {
                    methodParameters.Append(", ");
                }
            }
        }

        return methodParameters.ToString();
    }

    private static List<WebApiAssemblyVisitor.ServiceOperationRegistration> GetWebApiServiceOperationsFromAssembly(
        GeneratorExecutionContext context)
    {
        var visitor = new WebApiAssemblyVisitor(context.CancellationToken, context.Compilation);
        visitor.Visit(context.Compilation.Assembly);
        return visitor.OperationRegistrations;
    }

    private static string[] ToMinimalApiRegistrationMethodNames(WebApiOperation operation)
    {
        return operation switch
        {
            WebApiOperation.Get => new[] { "MapGet" },
            WebApiOperation.Search => new[] { "MapGet" },
            WebApiOperation.Post => new[] { "MapPost" },
            WebApiOperation.PutPatch => new[] { "MapPut", "MapPatch" },
            WebApiOperation.Delete => new[] { "MapDelete" },
            _ => new[] { "MapGet" }
        };
    }
}