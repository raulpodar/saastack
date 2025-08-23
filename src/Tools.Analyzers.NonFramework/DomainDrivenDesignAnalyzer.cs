using System.Collections.Immutable;
using Common;
using Common.Extensions;
using Domain.Interfaces;
using Domain.Interfaces.Entities;
using Domain.Interfaces.ValueObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using QueryAny;
using Tools.Analyzers.Common;
using Tools.Analyzers.Common.Extensions;
using Tools.Analyzers.NonFramework.Extensions;

namespace Tools.Analyzers.NonFramework;

/// <summary>
///     An analyzer to correct the implementation of root aggregates, entities, value objects and domain events:
///     Aggregate Roots:
///     SAASDDD010: Error: Aggregate roots must have at least one Create() class factory method
///     SAASDDD011: Error: Create() class factory methods must return correct types
///     SAASDDD012: Error: Aggregate roots must raise a create event in the class factory
///     SAASDDD013: Error: Aggregate roots must only have private constructors
///     SAASDDD014: Error: Aggregate roots must have a <see cref="IRehydratableObject.Rehydrate" /> method
///     SAASDDD015: Error: Dehydratable aggregate roots must override the <see cref="IDehydratableEntity.Dehydrate" />
///     method
///     SAASDDD016: Error: Dehydratable aggregate roots must declare the <see cref="EntityNameAttribute" />
///     SAASDDD017: Error: Properties must not have public setters
///     SAASDDD018: Error: Aggregate roots must be marked as sealed
///     Entities:
///     SAASDDD020: Error: Entities must have at least one Create() class factory method
///     SAASDDD021: Error: Create() class factory methods must return correct types
///     SAASDDD022: Error: Entities must only have private constructors
///     SAASDDD023: Error: Dehydratable entities must have a <see cref="IRehydratableObject.Rehydrate" /> method
///     SAASDDD024: Error: Dehydratable entities must override the <see cref="IDehydratableEntity.Dehydrate" /> method
///     SAASDDD025: Error: Dehydratable entities must declare the <see cref="EntityNameAttribute" />
///     SAASDDD026: Error: Properties must not have public setters
///     SAASDDD027: Error: Entities must be marked as sealed
///     Value Objects:
///     SAASDDD030: Error: ValueObjects must have at least one Create() class factory method
///     SAASDDD031: Error: Create() class factory methods must return correct types
///     SAASDDD032: Error: ValueObjects must only have private constructors
///     SAASDDD033: Error: ValueObjects must have a <see cref="IRehydratableObject.Rehydrate" /> method
///     SAASDDD034: Error: Properties must not have public setters
///     SAASDDD035: Error: ValueObjects must only have immutable methods (or be overridden by the
///     <see cref="SkipImmutabilityCheckAttribute" />)
///     SAASDDD036: Warning: ValueObjects should be marked as sealed
///     SAASDDD037: Warning: Properties should be Optional{T} not nullable,
///     SAASDDD038: Error: Properties are all assigned in ValueObjectBase{T}.GetAtomicValues(),
///     DomainEvents:
///     SAASDDD040: Error: DomainEvents must be public
///     SAASDDD041: Warning: DomainEvents should be sealed
///     SAASDDD042: Error: DomainEvents must have a parameterless constructor
///     SAASDDD043: Information: Domain Event should be named in the past tense
///     SAASDDD044: Error: DomainEvents must have at least one Create() class factory method
///     SAASDDD045: Error: Create() class factory methods must return correct types
///     SAASDDD046: Error: Properties must have public getters and setters
///     SAASDDD047: Error: Properties must be required or nullable or initialized
///     SAASDDD048: Error: Properties must be nullable, not Optional{T} for interoperability
///     SAASDDD049: Error: Properties must have return type of primitives, List{TPrimitive}, Dictionary{string,TPrimitive},
///     or be enums, or other DTOs
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DomainDrivenDesignAnalyzer : DiagnosticAnalyzer
{
    public const string ClassFactoryMethodName = "Create";
    public const string
        ConstructorMethodCall =
            "RaiseCreateEvent"; // HACK: cannot use nameof() for AggregateRootBase<T>.RaiseCreateEvent (protected)
    public const string
        GetAtomicValuesMethodName =
            "GetAtomicValues"; // HACK: cannot use nameof() for ValueObjectBase<T>.GetAtomicValues (protected)
    internal static readonly SpecialType[] AllowableDomainEventPrimitives =
    [
        SpecialType.System_Boolean,
        SpecialType.System_String,
        SpecialType.System_UInt64,
        SpecialType.System_Int32,
        SpecialType.System_Int64,
        SpecialType.System_Double,
        SpecialType.System_Decimal,
        SpecialType.System_DateTime,
        SpecialType.System_Byte
    ];
    internal static readonly DiagnosticDescriptor Rule010 = "SAASDDD010".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD010Title), nameof(Resources.SAASDDD010Description),
        nameof(Resources.SAASDDD010MessageFormat));
    internal static readonly DiagnosticDescriptor Rule011 = "SAASDDD011".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_Description_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_MessageFormat_ClassFactoryWrongReturnType));
    internal static readonly DiagnosticDescriptor Rule012 = "SAASDDD012".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD012Title), nameof(Resources.SAASDDD012Description),
        nameof(Resources.SAASDDD012MessageFormat));
    internal static readonly DiagnosticDescriptor Rule013 = "SAASDDD013".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_Description_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_MessageFormat_ConstructorMustBePrivate));
    internal static readonly DiagnosticDescriptor Rule014 = "SAASDDD014".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustImplementRehydrate),
        nameof(Resources.Diagnostic_Description_MustImplementRehydrate),
        nameof(Resources.Diagnostic_MessageFormat_MustImplementRehydrate));
    internal static readonly DiagnosticDescriptor Rule015 = "SAASDDD015".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustImplementDehydrate),
        nameof(Resources.Diagnostic_Description_MustImplementDehydrate),
        nameof(Resources.Diagnostic_MessageFormat_MustImplementDehydrate));
    internal static readonly DiagnosticDescriptor Rule016 = "SAASDDD016".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustDeclareEntityNameAttribute),
        nameof(Resources.Diagnostic_Description_MustDeclareEntityNameAttribute),
        nameof(Resources.Diagnostic_MessageFormat_MustDeclareEntityNameAttribute));
    internal static readonly DiagnosticDescriptor Rule017 = "SAASDDD017".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_Description_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_MessageFormat_PropertyMustBeSettable));
    internal static readonly DiagnosticDescriptor Rule018 = "SAASDDD018".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassMustBeSealed),
        nameof(Resources.Diagnostic_Description_ClassMustBeSealed),
        nameof(Resources.Diagnostic_MessageFormat_ClassMustBeSealed));
    internal static readonly DiagnosticDescriptor Rule020 = "SAASDDD020".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD020Title), nameof(Resources.SAASDDD020Description),
        nameof(Resources.SAASDDD020MessageFormat));
    internal static readonly DiagnosticDescriptor Rule021 = "SAASDDD021".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_Description_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_MessageFormat_ClassFactoryWrongReturnType));
    internal static readonly DiagnosticDescriptor Rule022 = "SAASDDD022".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_Description_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_MessageFormat_ConstructorMustBePrivate));
    internal static readonly DiagnosticDescriptor Rule023 = "SAASDDD023".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustImplementRehydrate),
        nameof(Resources.Diagnostic_Description_MustImplementRehydrate),
        nameof(Resources.Diagnostic_MessageFormat_MustImplementRehydrate));
    internal static readonly DiagnosticDescriptor Rule024 = "SAASDDD024".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustImplementDehydrate),
        nameof(Resources.Diagnostic_Description_MustImplementDehydrate),
        nameof(Resources.Diagnostic_MessageFormat_MustImplementDehydrate));
    internal static readonly DiagnosticDescriptor Rule025 = "SAASDDD025".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustDeclareEntityNameAttribute),
        nameof(Resources.Diagnostic_Description_MustDeclareEntityNameAttribute),
        nameof(Resources.Diagnostic_MessageFormat_MustDeclareEntityNameAttribute));
    internal static readonly DiagnosticDescriptor Rule026 = "SAASDDD026".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_Description_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_MessageFormat_PropertyMustBeSettable));
    internal static readonly DiagnosticDescriptor Rule027 = "SAASDDD027".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassMustBeSealed),
        nameof(Resources.Diagnostic_Description_ClassMustBeSealed),
        nameof(Resources.Diagnostic_MessageFormat_ClassMustBeSealed));
    internal static readonly DiagnosticDescriptor Rule030 = "SAASDDD030".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD030Title), nameof(Resources.SAASDDD030Description),
        nameof(Resources.SAASDDD030MessageFormat));
    internal static readonly DiagnosticDescriptor Rule031 = "SAASDDD031".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_Description_ClassFactoryWrongReturnType),
        nameof(Resources.Diagnostic_MessageFormat_ClassFactoryWrongReturnType));
    internal static readonly DiagnosticDescriptor Rule032 = "SAASDDD032".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_Description_ConstructorMustBePrivate),
        nameof(Resources.Diagnostic_MessageFormat_ConstructorMustBePrivate));
    internal static readonly DiagnosticDescriptor Rule033 = "SAASDDD033".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_MustImplementRehydrate),
        nameof(Resources.Diagnostic_Description_MustImplementRehydrate),
        nameof(Resources.Diagnostic_MessageFormat_MustImplementRehydrate));
    internal static readonly DiagnosticDescriptor Rule034 = "SAASDDD034".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_Description_PropertyMustBeSettable),
        nameof(Resources.Diagnostic_MessageFormat_PropertyMustBeSettable));
    internal static readonly DiagnosticDescriptor Rule035 = "SAASDDD035".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD035Title), nameof(Resources.SAASDDD035Description),
        nameof(Resources.SAASDDD035MessageFormat));
    internal static readonly DiagnosticDescriptor Rule036 = "SAASDDD036".GetDescriptor(DiagnosticSeverity.Warning,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassShouldBeSealed),
        nameof(Resources.Diagnostic_Description_ClassShouldBeSealed),
        nameof(Resources.Diagnostic_MessageFormat_ClassShouldBeSealed));
    internal static readonly DiagnosticDescriptor Rule037 = "SAASDDD037".GetDescriptor(DiagnosticSeverity.Warning,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD037Title), nameof(Resources.SAASDDD037Description),
        nameof(Resources.SAASDDD037MessageFormat));
    internal static readonly DiagnosticDescriptor Rule038 = "SAASDDD038".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD038Title), nameof(Resources.SAASDDD038Description),
        nameof(Resources.SAASDDD038MessageFormat));
    internal static readonly DiagnosticDescriptor Rule040 = "SAASDDD040".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassMustBePublic),
        nameof(Resources.Diagnostic_Description_ClassMustBePublic),
        nameof(Resources.Diagnostic_MessageFormat_ClassMustBePublic));
    internal static readonly DiagnosticDescriptor Rule041 = "SAASDDD041".GetDescriptor(DiagnosticSeverity.Warning,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassShouldBeSealed),
        nameof(Resources.Diagnostic_Description_ClassShouldBeSealed),
        nameof(Resources.Diagnostic_MessageFormat_ClassShouldBeSealed));
    internal static readonly DiagnosticDescriptor Rule042 = "SAASDDD042".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_ClassMustHaveParameterlessConstructor),
        nameof(Resources.Diagnostic_Description_ClassMustHaveParameterlessConstructor),
        nameof(Resources.Diagnostic_MessageFormat_ClassMustHaveParameterlessConstructor));
    internal static readonly DiagnosticDescriptor Rule043 = "SAASDDD043".GetDescriptor(DiagnosticSeverity.Info,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD043Title), nameof(Resources.SAASDDD043Description),
        nameof(Resources.SAASDDD043MessageFormat));
    internal static readonly DiagnosticDescriptor Rule045 = "SAASDDD045".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD045Title), nameof(Resources.SAASDDD045Description),
        nameof(Resources.SAASDDD045MessageFormat));
    internal static readonly DiagnosticDescriptor Rule046 = "SAASDDD046".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_PropertyMustBeGettableAndSettable),
        nameof(Resources.Diagnostic_Description_PropertyMustBeGettableAndSettable),
        nameof(Resources.Diagnostic_MessageFormat_PropertyMustBeGettableAndSettable));
    internal static readonly DiagnosticDescriptor Rule047 = "SAASDDD047".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD047Title), nameof(Resources.SAASDDD047Description),
        nameof(Resources.SAASDDD047MessageFormat));
    internal static readonly DiagnosticDescriptor Rule048 = "SAASDDD048".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.Diagnostic_Title_PropertyMustBeNullableNotOptional),
        nameof(Resources.Diagnostic_Description_PropertyMustBeNullableNotOptional),
        nameof(Resources.Diagnostic_MessageFormat_PropertyMustBeNullableNotOptional));
    internal static readonly DiagnosticDescriptor Rule049 = "SAASDDD049".GetDescriptor(DiagnosticSeverity.Error,
        AnalyzerConstants.Categories.Ddd, nameof(Resources.SAASDDD049Title), nameof(Resources.SAASDDD049Description),
        nameof(Resources.SAASDDD049MessageFormat));
    private static readonly string[] IgnoredValueObjectMethodNames = [nameof(IDehydratableEntity.Dehydrate)];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            Rule010, Rule011, Rule012, Rule013, Rule014, Rule015, Rule016, Rule017, Rule018,
            Rule020, Rule021, Rule022, Rule023, Rule024, Rule025, Rule026, Rule027,
            Rule030, Rule031, Rule032, Rule033, Rule034, Rule035, Rule036, Rule037, Rule038,
            Rule040, Rule041, Rule042, Rule043, Rule045, Rule046, Rule047, Rule048, Rule049);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAggregateRoot, SyntaxKind.ClassDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeEntity, SyntaxKind.ClassDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeValueObject, SyntaxKind.ClassDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeDomainEvent, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeAggregateRoot(SyntaxNodeAnalysisContext context)
    {
        var methodSyntax = context.Node;
        if (methodSyntax is not ClassDeclarationSyntax classDeclarationSyntax)
        {
            return;
        }

        if (context.IsExcludedInNamespace(classDeclarationSyntax, AnalyzerConstants.PlatformNamespaces))
        {
            return;
        }

        if (classDeclarationSyntax.IsNotType<IAggregateRoot>(context))
        {
            return;
        }

        if (!classDeclarationSyntax.IsSealed())
        {
            context.ReportDiagnostic(Rule018, classDeclarationSyntax);
        }

        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var classFactoryMethods = allMethods
            .Where(method => method.IsPublicStaticMethod() && method.IsNamed(ClassFactoryMethodName))
            .ToList();
        if (classFactoryMethods.HasNone())
        {
            context.ReportDiagnostic(Rule010, classDeclarationSyntax);
        }
        else
        {
            foreach (var method in classFactoryMethods)
            {
                var allowedReturnTypes = context.GetAllowableClassFactoryReturnTypes(classDeclarationSyntax);
                if (context.HasIncorrectReturnType(method, allowedReturnTypes))
                {
                    var acceptableReturnTypes =
                        allowedReturnTypes.Select(allowable => allowable.ToDisplayString()).Join(" or ");
                    context.ReportDiagnostic(Rule011, method, acceptableReturnTypes);
                }

                if (context.IsMissingContent(method, ConstructorMethodCall))
                {
                    context.ReportDiagnostic(Rule012, method, ConstructorMethodCall);
                }
            }
        }

        if (!classDeclarationSyntax.HasOnlyPrivateInstanceConstructors(out var constructor))
        {
            context.ReportDiagnostic(Rule013, constructor!);
        }

        var dehydratable = context.IsDehydratableAggregateRoot(classDeclarationSyntax);
        if (!dehydratable.ImplementsRehydrate)
        {
            context.ReportDiagnostic(Rule014, classDeclarationSyntax);
        }

        if (dehydratable is { IsDehydratable: true, ImplementsDehydrate: false })
        {
            context.ReportDiagnostic(Rule015, classDeclarationSyntax);
        }

        if (dehydratable is { IsDehydratable: true, MarkedAsEntityName: false })
        {
            context.ReportDiagnostic(Rule016, classDeclarationSyntax);
        }

        var allProperties = classDeclarationSyntax.Members.Where(member => member is PropertyDeclarationSyntax)
            .Cast<PropertyDeclarationSyntax>()
            .ToList();
        if (allProperties.HasAny())
        {
            foreach (var property in allProperties)
            {
                if (property.HasPublicSetter())
                {
                    context.ReportDiagnostic(Rule017, property);
                }
            }
        }
    }

    private static void AnalyzeEntity(SyntaxNodeAnalysisContext context)
    {
        var methodSyntax = context.Node;
        if (methodSyntax is not ClassDeclarationSyntax classDeclarationSyntax)
        {
            return;
        }

        if (context.IsExcludedInNamespace(classDeclarationSyntax, AnalyzerConstants.PlatformNamespaces))
        {
            return;
        }

        if (classDeclarationSyntax.IsNotType<IEntity>(context))
        {
            return;
        }

        if (!classDeclarationSyntax.IsSealed())
        {
            context.ReportDiagnostic(Rule027, classDeclarationSyntax);
        }

        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var classFactoryMethods = allMethods
            .Where(method => method.IsPublicStaticMethod() && method.IsNamed(ClassFactoryMethodName))
            .ToList();
        if (classFactoryMethods.HasNone())
        {
            context.ReportDiagnostic(Rule020, classDeclarationSyntax);
        }
        else
        {
            foreach (var method in classFactoryMethods)
            {
                var allowedReturnTypes = context.GetAllowableClassFactoryReturnTypes(classDeclarationSyntax);
                if (context.HasIncorrectReturnType(method, allowedReturnTypes))
                {
                    var acceptableReturnTypes =
                        allowedReturnTypes.Select(allowable => allowable.ToDisplayString()).Join(" or ");
                    context.ReportDiagnostic(Rule021, method, acceptableReturnTypes);
                }
            }
        }

        if (!classDeclarationSyntax.HasOnlyPrivateInstanceConstructors(out var constructor))
        {
            context.ReportDiagnostic(Rule022, constructor!);
        }

        var dehydratable = context.IsDehydratableEntity(classDeclarationSyntax);
        if (dehydratable is { IsDehydratable: true, ImplementsRehydrate: false })
        {
            context.ReportDiagnostic(Rule023, classDeclarationSyntax);
        }

        if (dehydratable is { IsDehydratable: true, ImplementsDehydrate: false })
        {
            context.ReportDiagnostic(Rule024, classDeclarationSyntax);
        }

        if (dehydratable is { IsDehydratable: true, MarkedAsEntityName: false })
        {
            context.ReportDiagnostic(Rule025, classDeclarationSyntax);
        }

        var allProperties = classDeclarationSyntax.Members.Where(member => member is PropertyDeclarationSyntax)
            .Cast<PropertyDeclarationSyntax>()
            .ToList();
        if (allProperties.HasAny())
        {
            foreach (var property in allProperties)
            {
                if (property.HasPublicSetter())
                {
                    context.ReportDiagnostic(Rule026, property);
                }
            }
        }
    }

    private static void AnalyzeValueObject(SyntaxNodeAnalysisContext context)
    {
        var methodSyntax = context.Node;
        if (methodSyntax is not ClassDeclarationSyntax classDeclarationSyntax)
        {
            return;
        }

        if (context.IsExcludedInNamespace(classDeclarationSyntax, AnalyzerConstants.PlatformNamespaces))
        {
            return;
        }

        if (classDeclarationSyntax.IsNotType<IValueObject>(context))
        {
            return;
        }

        if (!classDeclarationSyntax.IsSealed())
        {
            context.ReportDiagnostic(Rule036, classDeclarationSyntax);
        }

        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var classFactoryMethods = allMethods
            .Where(method => method.IsPublicStaticMethod() && method.IsNamed(ClassFactoryMethodName))
            .ToList();
        if (classFactoryMethods.HasNone())
        {
            context.ReportDiagnostic(Rule030, classDeclarationSyntax);
        }
        else
        {
            foreach (var method in classFactoryMethods)
            {
                var allowedReturnTypes = context.GetAllowableClassFactoryReturnTypes(classDeclarationSyntax);
                if (context.HasIncorrectReturnType(method, allowedReturnTypes))
                {
                    var acceptableReturnTypes =
                        allowedReturnTypes.Select(allowable => allowable.ToDisplayString()).Join(" or ");
                    context.ReportDiagnostic(Rule031, method, acceptableReturnTypes);
                }
            }
        }

        if (!classDeclarationSyntax.HasOnlyPrivateInstanceConstructors(out var constructor))
        {
            context.ReportDiagnostic(Rule032, constructor!);
        }

        var dehydratable = context.IsDehydratableValueObject(classDeclarationSyntax);
        if (!dehydratable.ImplementsRehydrate)
        {
            context.ReportDiagnostic(Rule033, classDeclarationSyntax);
        }

        var allProperties = classDeclarationSyntax.Members.Where(member => member is PropertyDeclarationSyntax)
            .Cast<PropertyDeclarationSyntax>()
            .ToList();
        if (allProperties.HasAny())
        {
            foreach (var property in allProperties)
            {
                if (property.HasPublicSetter())
                {
                    context.ReportDiagnostic(Rule034, property);
                }

                if (property.IsNullableType(context))
                {
                    var returnType = property.GetGetterReturnType(context);
                    if (returnType.Exists())
                    {
                        var propertyType = returnType.WithoutNullable(context).Name;
                        context.ReportDiagnostic(Rule037, property, propertyType);
                    }
                }
            }
        }

        var getAtomicValuesMethod = classDeclarationSyntax.Members
            .Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .Where(method => method.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)))
            .FirstOrDefault(method => method.Identifier.Text == GetAtomicValuesMethodName);
        if (getAtomicValuesMethod.Exists())
        {
            var declaredProperties = classDeclarationSyntax.Members
                .Where(member => member is PropertyDeclarationSyntax)
                .Cast<PropertyDeclarationSyntax>()
                .Where(property => property.HasPublicGetterOnly())
                .Select(property => property.Identifier)
                .ToList();

            var body = getAtomicValuesMethod.Body;
            var initializedProperties = new List<SyntaxToken>();
            if (body.Exists())
            {
                var explicitExpressions = body.Statements
                    .OfType<ReturnStatementSyntax>()
                    .Select(rs => rs.Expression)
                    .OfType<ArrayCreationExpressionSyntax>()
                    .Select(acs => acs.Initializer)
                    .OfType<InitializerExpressionSyntax>()
                    .SelectMany(ies => ies.Expressions)
                    .ToList();
                var implicitExpressions = body.Statements
                    .OfType<ReturnStatementSyntax>()
                    .Select(rs => rs.Expression)
                    .OfType<ImplicitArrayCreationExpressionSyntax>()
                    .Select(acs => acs.Initializer)
                    .SelectMany(ies => ies.Expressions)
                    .ToList();
                var collectionExpressions = body.Statements
                    .OfType<ReturnStatementSyntax>()
                    .Select(rs => rs.Expression)
                    .OfType<CollectionExpressionSyntax>()
                    .ToList();
                var expressions = explicitExpressions
                    .Concat(implicitExpressions)
                    .Concat(collectionExpressions)
                    .ToList();

                var directAccess = expressions
                    .OfType<IdentifierNameSyntax>()
                    .Select(ins => ins.Identifier);
                var memberAccessedProperties = expressions
                    .OfType<MemberAccessExpressionSyntax>()
                    .Select(mae => mae.Expression)
                    .OfType<IdentifierNameSyntax>()
                    .Select(ins => ins.Identifier);
                var memberFunctionProperties = expressions
                    .OfType<InvocationExpressionSyntax>()
                    .Select(ies => ies.Expression)
                    .OfType<MemberAccessExpressionSyntax>()
                    .Select(mae => mae.Expression)
                    .OfType<IdentifierNameSyntax>()
                    .Select(ins => ins.Identifier);
                var extensionMethodFunctionProperties = expressions
                    .OfType<PostfixUnaryExpressionSyntax>()
                    .Select(ies => ies.Operand)
                    .OfType<InvocationExpressionSyntax>()
                    .Select(ies => ies.Expression)
                    .OfType<MemberAccessExpressionSyntax>()
                    .Select(mae => mae.Expression)
                    .OfType<IdentifierNameSyntax>()
                    .Select(ins => ins.Identifier);
                var collectionProperties = expressions
                    .OfType<CollectionExpressionSyntax>()
                    .SelectMany(ies => ies.Elements)
                    .Select(ins => ins.GetFirstToken());

                initializedProperties = directAccess
                    .Concat(memberAccessedProperties)
                    .Concat(memberFunctionProperties)
                    .Concat(extensionMethodFunctionProperties)
                    .Concat(collectionProperties)
                    .ToList();
            }

            if (initializedProperties.Count < declaredProperties.Count)
            {
                foreach (var declaredProperty in declaredProperties)
                {
                    if (initializedProperties.NotContains(prop => prop.Text == declaredProperty.Text))
                    {
                        context.ReportDiagnostic(Rule038, getAtomicValuesMethod, declaredProperty.Text);
                    }
                }
            }
        }

        var allImmutableMethods =
            classDeclarationSyntax.Members
                .Where(member => member is not ConstructorDeclarationSyntax)
                .Where(member => member is MethodDeclarationSyntax)
                .Cast<MethodDeclarationSyntax>()
                .Where(method => method.IsPublicOrInternalInstanceMethod())
                .Where(method => method.GetAttributeOfType<SkipImmutabilityCheckAttribute>(context).NotExists())
                .Where(method => IgnoredValueObjectMethodNames.NotContainsIgnoreCase(method.Identifier.Text));
        foreach (var method in allImmutableMethods)
        {
            var allowedReturnTypes = context.GetAllowableValueObjectMutableMethodReturnTypes(classDeclarationSyntax);
            if (context.HasIncorrectReturnType(method, allowedReturnTypes))
            {
                var acceptableReturnTypes =
                    allowedReturnTypes.Select(allowable => allowable.ToDisplayString()).Join(" or ");
                context.ReportDiagnostic(Rule035, method, acceptableReturnTypes,
                    nameof(SkipImmutabilityCheckAttribute));
            }
        }
    }

    private static void AnalyzeDomainEvent(SyntaxNodeAnalysisContext context)
    {
        var methodSyntax = context.Node;
        if (methodSyntax is not ClassDeclarationSyntax classDeclarationSyntax)
        {
            return;
        }

        if (context.IsExcludedInNamespace(classDeclarationSyntax, AnalyzerConstants.PlatformNamespaces))
        {
            return;
        }

        if (classDeclarationSyntax.IsNotType<IDomainEvent>(context))
        {
            return;
        }

        if (!context.IsNamedInPastTense(classDeclarationSyntax))
        {
            context.ReportDiagnostic(Rule043, classDeclarationSyntax);
        }

        if (!classDeclarationSyntax.IsPublic())
        {
            context.ReportDiagnostic(Rule040, classDeclarationSyntax);
        }

        if (!classDeclarationSyntax.IsSealed())
        {
            context.ReportDiagnostic(Rule041, classDeclarationSyntax);
        }

        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var classFactoryMethods = allMethods
            .Where(method => method.IsPublicStaticMethod() && method.IsNamed(ClassFactoryMethodName))
            .ToList();
        if (classFactoryMethods.HasAny())
        {
            foreach (var method in classFactoryMethods)
            {
                var allowedReturnTypes = context.GetAllowableDomainEventFactoryReturnTypes(classDeclarationSyntax);
                if (context.HasIncorrectReturnType(method, allowedReturnTypes))
                {
                    var acceptableReturnTypes =
                        allowedReturnTypes.Select(allowable => allowable.ToDisplayString()).Join(" or ");
                    context.ReportDiagnostic(Rule045, method, acceptableReturnTypes);
                }
            }
        }

        if (!classDeclarationSyntax.HasParameterlessConstructor())
        {
            context.ReportDiagnostic(Rule042, classDeclarationSyntax);
        }

        var allProperties = classDeclarationSyntax.Members.Where(member => member is PropertyDeclarationSyntax)
            .Cast<PropertyDeclarationSyntax>()
            .ToList();
        if (allProperties.HasAny())
        {
            foreach (var property in allProperties)
            {
                if (!property.HasPublicGetterAndSetter())
                {
                    context.ReportDiagnostic(Rule046, property);
                }

                if (!property.IsEnumOrNullableEnumType(context)
                    && !property.IsRequired()
                    && !property.IsInitialized())
                {
                    if (!property.IsNullableType(context))
                    {
                        context.ReportDiagnostic(Rule047, property);
                    }
                }

                if (!property.IsNullableType(context) && property.IsOptionalType(context))
                {
                    context.ReportDiagnostic(Rule048, property);
                }

                var allowedReturnTypes = context.GetAllowableDomainEventPropertyReturnTypes();
                if (context.HasIncorrectReturnType(property, allowedReturnTypes)
                    && !property.IsReturnTypeInNamespace(context, AnalyzerConstants.DomainEventTypesNamespace)
                    && !property.IsEnumOrNullableEnumType(context)
                    && !property.IsDtoOrNullableDto(context, allowedReturnTypes.ToList()))
                {
                    var acceptableReturnTypes =
                        allowedReturnTypes
                            .Where(allowable =>
                                !allowable.ToDisplayString().StartsWith("System.Collections")
                                && !allowable.ToDisplayString().EndsWith("?"))
                            .Select(allowable => allowable.ToDisplayString()).Join(" or ");
                    context.ReportDiagnostic(Rule049, property, acceptableReturnTypes);
                }
            }
        }
    }
}

internal static class DomainDrivenDesignExtensions
{
    private static INamedTypeSymbol[]? _allowableDomainEventPropertyReturnTypes;

    public static INamedTypeSymbol[] GetAllowableClassFactoryReturnTypes(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var errorType = context.Compilation.GetTypeByMetadataName(typeof(Error).FullName!)!;
        var resultType = context.Compilation.GetTypeByMetadataName(typeof(Result<,>).FullName!)!;
        var resultOfClassAndErrorType = resultType.Construct(classSymbol, errorType);

        return [classSymbol, resultOfClassAndErrorType];
    }

    public static INamedTypeSymbol[] GetAllowableDomainEventFactoryReturnTypes(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        return [classSymbol];
    }

    public static INamedTypeSymbol[] GetAllowableDomainEventPropertyReturnTypes(this SyntaxNodeAnalysisContext context)
    {
        // Cache this
        if (_allowableDomainEventPropertyReturnTypes is null)
        {
            var stringType = context.Compilation.GetSpecialType(SpecialType.System_String);
            var primitiveTypes = DomainDrivenDesignAnalyzer.AllowableDomainEventPrimitives
                .Select(context.Compilation.GetSpecialType).ToArray();

            var nullableOfType = context.Compilation.GetTypeByMetadataName(typeof(Nullable<>).FullName!)!;
            var nullableTypes = primitiveTypes
                .Select(primitive => nullableOfType.Construct(primitive)).ToArray();

            var listOfType = context.Compilation.GetTypeByMetadataName(typeof(List<>).FullName!)!;
            var listTypes = primitiveTypes
                .Select(primitive => listOfType.Construct(primitive)).ToArray();

            var dictionaryOfType = context.Compilation.GetTypeByMetadataName(typeof(Dictionary<,>).FullName!)!;
            var stringDictionaryTypes = primitiveTypes
                .Select(primitive => dictionaryOfType.Construct(stringType, primitive))
                .ToArray();

            _allowableDomainEventPropertyReturnTypes = primitiveTypes
                .Concat(nullableTypes)
                .Concat(listTypes)
                .Concat(stringDictionaryTypes).ToArray();
        }

        return _allowableDomainEventPropertyReturnTypes;
    }

    public static INamedTypeSymbol[] GetAllowableValueObjectMutableMethodReturnTypes(
        this SyntaxNodeAnalysisContext context, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var errorType = context.Compilation.GetTypeByMetadataName(typeof(Error).FullName!)!;
        var resultType = context.Compilation.GetTypeByMetadataName(typeof(Result<,>).FullName!)!;
        var resultOfClassAndErrorType = resultType.Construct(classSymbol, errorType);

        return [classSymbol, resultOfClassAndErrorType];
    }

    public static bool HasIncorrectReturnType(this SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDeclarationSyntax, INamedTypeSymbol[] allowableTypes)
    {
        var semanticModel = context.SemanticModel;
        var compilation = context.Compilation;
        return semanticModel.HasIncorrectReturnType(compilation, methodDeclarationSyntax, allowableTypes);
    }

    public static bool HasIncorrectReturnType(this SyntaxNodeAnalysisContext context,
        PropertyDeclarationSyntax propertyDeclarationSyntax, INamedTypeSymbol[] allowableTypes)
    {
        var semanticModel = context.SemanticModel;
        var compilation = context.Compilation;
        return semanticModel.HasIncorrectReturnType(compilation, propertyDeclarationSyntax, allowableTypes);
    }

    public static DehydratableStatus IsDehydratableAggregateRoot(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        return context.SemanticModel.IsDehydratableAggregateRoot(context.Compilation, classDeclarationSyntax);
    }

    public static DehydratableStatus IsDehydratableAggregateRoot(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var implementsRehydrate =
            ImplementsAggregateRehydrateMethod(semanticModel, compilation, classDeclarationSyntax, allMethods);
        var implementsDehydrate =
            ImplementsDehydrateMethod(semanticModel, compilation, classDeclarationSyntax, allMethods);
        var hasAttribute = semanticModel.HasEntityNameAttribute(compilation, classDeclarationSyntax);
        return new DehydratableStatus(implementsDehydrate, implementsRehydrate, hasAttribute,
            () => implementsDehydrate || hasAttribute);
    }

    public static DehydratableStatus IsDehydratableEntity(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        return context.SemanticModel.IsDehydratableEntity(context.Compilation, classDeclarationSyntax);
    }

    public static DehydratableStatus IsDehydratableValueObject(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        return context.SemanticModel.IsDehydratableValueObject(context.Compilation, classDeclarationSyntax);
    }

    public static bool IsMissingContent(this SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDeclarationSyntax, string match)
    {
        return !methodDeclarationSyntax.GetBody()
            .Contains(match);
    }

    public static bool IsNamedInPastTense(this SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        var typeSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (typeSymbol is null)
        {
            return false;
        }

        var name = typeSymbol.Name;

        //HACK: this will work when using a regular verb, but not when using irregular verbs
        return name.IsMatchWith("(ed)([vV][0-9]*){0,1}$");
    }

    public static bool IsSingleValueValueObject(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var symbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (symbol is null)
        {
            return false;
        }

        var singleValueObject = compilation.GetTypeByMetadataName(typeof(ISingleValueObject<>).FullName!)!;
        return symbol.AllInterfaces.Any(@interface => @interface.OriginalDefinition.IsOfType(singleValueObject));
    }

    private static string GetBody(this MethodDeclarationSyntax methodDeclarationSyntax)
    {
        var body = methodDeclarationSyntax.Body;
        if (body.Exists())
        {
            return body.ToFullString();
        }

        var expressionBody = methodDeclarationSyntax.ExpressionBody;
        if (expressionBody.Exists())
        {
            return expressionBody.ToFullString();
        }

        return string.Empty;
    }

    private static DehydratableStatus IsDehydratableEntity(this SemanticModel semanticModel, Compilation compilation,
        ClassDeclarationSyntax classDeclarationSyntax)
    {
        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var implementsRehydrate =
            ImplementsEntityRehydrateMethod(semanticModel, compilation, classDeclarationSyntax, allMethods);
        var implementsDehydrate =
            ImplementsDehydrateMethod(semanticModel, compilation, classDeclarationSyntax, allMethods);
        var hasAttribute = semanticModel.HasEntityNameAttribute(compilation, classDeclarationSyntax);
        return new DehydratableStatus(implementsDehydrate, implementsRehydrate, hasAttribute,
            () => implementsRehydrate || hasAttribute || implementsRehydrate);
    }

    private static DehydratableStatus IsDehydratableValueObject(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var allMethods = classDeclarationSyntax.Members.Where(member => member is MethodDeclarationSyntax)
            .Cast<MethodDeclarationSyntax>()
            .ToList();
        var implementsRehydrate =
            ImplementsValueObjectRehydrateMethod(semanticModel, compilation, classDeclarationSyntax, allMethods);
        return new DehydratableStatus(true, implementsRehydrate, false, () => true);
    }

    private static bool HasIncorrectReturnType(this SemanticModel semanticModel, Compilation compilation,
        MethodDeclarationSyntax methodDeclarationSyntax, INamedTypeSymbol[] allowableTypes)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclarationSyntax);
        if (methodSymbol is null)
        {
            return true;
        }

        var returnType = methodSymbol.ReturnType;
        if (returnType.IsVoid(compilation))
        {
            return true;
        }

        foreach (var allowableType in allowableTypes)
        {
            if (returnType.IsOfType(allowableType))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasIncorrectReturnType(this SemanticModel semanticModel, Compilation compilation,
        PropertyDeclarationSyntax propertyDeclarationSyntax, INamedTypeSymbol[] allowableTypes)
    {
        var propertySymbol = semanticModel.GetDeclaredSymbol(propertyDeclarationSyntax);
        if (propertySymbol is null)
        {
            return true;
        }

        if (propertySymbol.GetMethod is null)
        {
            return true;
        }

        var returnType = propertySymbol.GetMethod.ReturnType;
        if (returnType.IsVoid(compilation))
        {
            return true;
        }

        foreach (var allowableType in allowableTypes)
        {
            if (returnType.IsOfType(allowableType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether the class implements the <see cref="IDehydratableEntity.Dehydrate" /> method
    /// </summary>
    private static bool ImplementsDehydrateMethod(this SemanticModel semanticModel, Compilation compilation,
        ClassDeclarationSyntax classDeclarationSyntax, IEnumerable<MethodDeclarationSyntax> allMethods)
    {
        var allowableTypes = GetAllowableDehydrateReturnType(semanticModel, compilation, classDeclarationSyntax);
        return allMethods
            .Any(method => method.IsPublicOverrideMethod()
                           && method.IsNamed(nameof(IDehydratableEntity.Dehydrate))
                           && !HasIncorrectReturnType(semanticModel, compilation, method, allowableTypes));
    }

    private static INamedTypeSymbol[] GetAllowableDehydrateReturnType(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var propertiesType = compilation.GetTypeByMetadataName(typeof(HydrationProperties).FullName!)!;
        return [propertiesType];
    }

    /// <summary>
    ///     Whether the class implements the <see cref="IRehydratableObject.Rehydrate" /> method
    /// </summary>
    private static bool ImplementsAggregateRehydrateMethod(this SemanticModel semanticModel, Compilation compilation,
        ClassDeclarationSyntax classDeclarationSyntax, IEnumerable<MethodDeclarationSyntax> allMethods)
    {
        var allowableTypes =
            GetAllowableAggregateRehydrateReturnType(semanticModel, compilation, classDeclarationSyntax);
        return allMethods
            .Any(method => method.IsPublicStaticMethod()
                           && method.IsNamed(nameof(IRehydratableObject.Rehydrate))
                           && !semanticModel.HasIncorrectReturnType(compilation, method, allowableTypes));
    }

    /// <summary>
    ///     Whether the class implements the <see cref="IRehydratableObject.Rehydrate" /> method
    /// </summary>
    private static bool ImplementsEntityRehydrateMethod(this SemanticModel semanticModel, Compilation compilation,
        ClassDeclarationSyntax classDeclarationSyntax, IEnumerable<MethodDeclarationSyntax> allMethods)
    {
        var allowableTypes = GetAllowableEntityRehydrateReturnType(semanticModel, compilation, classDeclarationSyntax);
        return allMethods
            .Any(method => method.IsPublicStaticMethod()
                           && method.IsNamed(nameof(IRehydratableObject.Rehydrate))
                           && !semanticModel.HasIncorrectReturnType(compilation, method, allowableTypes));
    }

    /// <summary>
    ///     Whether the class implements the <see cref="IRehydratableObject.Rehydrate" /> method
    /// </summary>
    private static bool ImplementsValueObjectRehydrateMethod(this SemanticModel semanticModel, Compilation compilation,
        ClassDeclarationSyntax classDeclarationSyntax, IEnumerable<MethodDeclarationSyntax> allMethods)
    {
        var allowableTypes =
            GetAllowableValueObjectRehydrateReturnType(semanticModel, compilation, classDeclarationSyntax);
        return allMethods
            .Any(method => method.IsPublicStaticMethod()
                           && method.IsNamed(nameof(IRehydratableObject.Rehydrate))
                           && !semanticModel.HasIncorrectReturnType(compilation, method, allowableTypes));
    }

    private static INamedTypeSymbol[] GetAllowableAggregateRehydrateReturnType(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var factoryType = compilation.GetTypeByMetadataName(typeof(AggregateRootFactory<>).FullName!)!;
        var factoryOfClass = factoryType.Construct(classSymbol);

        return [factoryOfClass];
    }

    private static INamedTypeSymbol[] GetAllowableEntityRehydrateReturnType(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var factoryType = compilation.GetTypeByMetadataName(typeof(EntityFactory<>).FullName!)!;
        var factoryOfClass = factoryType.Construct(classSymbol);

        return [factoryOfClass];
    }

    private static INamedTypeSymbol[] GetAllowableValueObjectRehydrateReturnType(this SemanticModel semanticModel,
        Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        var classSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            return [];
        }

        var factoryType = compilation.GetTypeByMetadataName(typeof(ValueObjectFactory<>).FullName!)!;
        var factoryOfClass = factoryType.Construct(classSymbol);

        return [factoryOfClass];
    }

    public class DehydratableStatus
    {
        private readonly Func<bool> _isDehydratable;

        public DehydratableStatus(bool implementsDehydrate, bool implementsRehydrate, bool markedWithEntityAttribute,
            Func<bool> isDehydratable)
        {
            _isDehydratable = isDehydratable;
            ImplementsDehydrate = implementsDehydrate;
            ImplementsRehydrate = implementsRehydrate;
            MarkedAsEntityName = markedWithEntityAttribute;
        }

        public bool ImplementsDehydrate { get; }

        public bool ImplementsRehydrate { get; }

        public bool IsDehydratable => _isDehydratable();

        public bool MarkedAsEntityName { get; }
    }
}