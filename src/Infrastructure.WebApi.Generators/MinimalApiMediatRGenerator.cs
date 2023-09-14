using System.Text;
using Infrastructure.WebApi.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Infrastructure.WebApi.Generators;

[Generator]
public class MinimalApiMediatRGenerator : ISourceGenerator
{
    private const string Filename = "MinimalApiMediatRGeneratedHandlers.g.cs";
    private const string RegistrationClassName = "MinimalApiRegistration";
    private const string TestingOnlyDirective = "TESTINGONLY";

    private static readonly string[] RequiredUsingNamespaces =
        { "System", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Http" };

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var assemblyNamespace = context.Compilation.AssemblyName;
        var handlers = GetWebApiServiceOperationsFromAssembly(context);

        var handlerClasses = new StringBuilder();
        var endpointRegistrations = new StringBuilder();
        foreach (var handler in handlers)
        {
            var constructor = BuildConstructor(handler.RequestDtoType.Name, handler.Class.CtorParameters.ToList());
            BuildHandlerClasses(handler, constructor, handlerClasses);
            BuildEndpointRegistrations(handler, endpointRegistrations);
        }

        var classUsingNamespaces = BuildUsingList(handlers);
        var fileSource = BuildFile(assemblyNamespace, classUsingNamespaces, endpointRegistrations.ToString(),
            handlerClasses.ToString());

        context.AddSource(Filename, SourceText.From(fileSource, Encoding.UTF8));

        return;

        static string BuildFile(string? assemblyNamespace, string usingNamespaces, string handlerRegistrations,
            string handlerClasses)
        {
            return $@"// <auto-generated/>
{usingNamespaces}
namespace {assemblyNamespace};

public static class {RegistrationClassName}
{{
    public static void RegisterRoutes(this global::Microsoft.AspNetCore.Builder.WebApplication app)
    {{
{handlerRegistrations}
    }}
}}

{handlerClasses}";
        }

        static string BuildUsingList(IEnumerable<WebApiProjectVisitor.ApiServiceOperationRegistration> handlers)
        {
            var usingList = new StringBuilder();

            var allNamespaces = handlers.SelectMany(handler => handler.Class.UsingNamespaces)
                .Concat(RequiredUsingNamespaces)
                .Distinct()
                .OrderByDescending(s => s)
                .ToList();

            allNamespaces.ForEach(@using =>
                usingList.AppendLine($@"using {@using};"));

            return usingList.ToString();
        }

        static void BuildEndpointRegistrations(WebApiProjectVisitor.ApiServiceOperationRegistration registration,
            StringBuilder endpointRegistrations)
        {
            var minimalApiMapMethodName = ToMinimalApiMapMethodName(registration.OperationType);
            if (registration.IsTestingOnly)
            {
                endpointRegistrations.AppendLine($@"#if {TestingOnlyDirective}");
            }

            endpointRegistrations.AppendLine(
                $@"        app.Map{minimalApiMapMethodName}(""{registration.RoutePath}"",");
            endpointRegistrations.AppendLine(
                $@"            async (global::MediatR.IMediator mediator, [global::Microsoft.AspNetCore.Http.AsParameters] global::{registration.RequestDtoType.FullName} request) =>");
            endpointRegistrations.AppendLine(
                @"                await mediator.Send(request, global::System.Threading.CancellationToken.None));");
            if (registration.IsTestingOnly)
            {
                endpointRegistrations.AppendLine(@"#endif");
            }
        }

        static void BuildHandlerClasses(WebApiProjectVisitor.ApiServiceOperationRegistration registration,
            string? constructor, StringBuilder handlerClasses)
        {
            handlerClasses.AppendLine($@"
public class {registration.RequestDtoType.Name}Handler : global::MediatR.IRequestHandler<global::{registration.RequestDtoType.FullName}, global::Microsoft.AspNetCore.Http.IResult>
{{
{constructor}");
            if (registration.IsTestingOnly)
            {
                handlerClasses.AppendLine($@"#if {TestingOnlyDirective}");
            }

            var requestBody = new StringBuilder();
            requestBody.Append(!string.IsNullOrEmpty(registration.MethodBody)
                ? $@"{registration.MethodBody}"
                : @"    {}");
            handlerClasses.AppendLine(
                $@"    public async Task<global::Microsoft.AspNetCore.Http.IResult> Handle(global::{registration.RequestDtoType.FullName} request, global::System.Threading.CancellationToken cancellationToken)
{requestBody}");
            if (registration.IsTestingOnly)
            {
                handlerClasses.AppendLine(@"#endif");
            }

            handlerClasses.AppendLine(@"
}");
        }

        static string BuildConstructor(string? requestTypeName,
            List<WebApiProjectVisitor.ConstructorParameter> constructorParameters)
        {
            var handlerClassConstructor = new StringBuilder();
            if (constructorParameters.Any())
            {
                foreach (var param in constructorParameters)
                {
                    handlerClassConstructor.AppendLine(
                        $@"    private readonly global::{param.TypeName.FullName} _{param.VariableName};");
                }

                handlerClassConstructor.AppendLine();
                handlerClassConstructor.Append($@"    public {requestTypeName}Handler(");
                var paramsRemaining = constructorParameters.Count();
                foreach (var param in constructorParameters)
                {
                    handlerClassConstructor.Append($@"global::{param.TypeName.FullName} {param.VariableName}");
                    if (--paramsRemaining > 0)
                    {
                        handlerClassConstructor.Append(", ");
                    }
                }

                handlerClassConstructor.AppendLine(@")");
                handlerClassConstructor.AppendLine("    {");
                foreach (var param in constructorParameters)
                {
                    handlerClassConstructor.AppendLine(
                        $@"        this._{param.VariableName} = {param.VariableName};");
                }

                handlerClassConstructor.AppendLine("    }");
            }

            return handlerClassConstructor.ToString();
        }

        static List<WebApiProjectVisitor.ApiServiceOperationRegistration> GetWebApiServiceOperationsFromAssembly(
            GeneratorExecutionContext context)
        {
            var visitor = new WebApiProjectVisitor(context.CancellationToken, context.Compilation);
            visitor.Visit(context.Compilation.Assembly);
            return visitor.OperationRegistrations;
        }

        static string ToMinimalApiMapMethodName(WebApiOperation operation)
        {
            return operation switch
            {
                WebApiOperation.Get => "Get",
                WebApiOperation.Search => "Get",
                WebApiOperation.Post => "Post",
                WebApiOperation.PutPatch => "Put",
                WebApiOperation.Delete => "Delete",
                _ => "Get"
            };
        }
    }
}