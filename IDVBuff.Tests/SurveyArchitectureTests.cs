using System.Reflection;
using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Tests;

public sealed class SurveyArchitectureTests
{
    private static readonly string[] ForbiddenCoreReferences =
    [
        "OpenCvSharp",
        "Microsoft.UI.Xaml",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw"
    ];

    [Fact]
    public void CoreAssembliesKeepInfrastructureDependenciesOut()
    {
        AssertForbiddenReferencesAbsent(typeof(SurveyProject).Assembly);
        AssertForbiddenReferencesAbsent(typeof(ISurveyCoordinator).Assembly);
        AssertForbiddenReferencesAbsent(typeof(SurveyCoordinator).Assembly);
    }

    [Fact]
    public void ContractsExposeOnlyTypedTransportNeutralValues()
    {
        var contracts = typeof(ISurveyCoordinator).Assembly;
        var publicMembers = contracts.GetExportedTypes()
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMembers(BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly));
        foreach (var member in publicMembers)
        {
            foreach (var type in ReferencedTypes(member))
            {
                Assert.NotEqual(typeof(object), type);
                Assert.False((type.Namespace ?? string.Empty).StartsWith("OpenCvSharp", StringComparison.Ordinal));
                Assert.False((type.Namespace ?? string.Empty).StartsWith("Microsoft.UI", StringComparison.Ordinal));
                Assert.False((type.Namespace ?? string.Empty).StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
            }
        }
    }

    private static void AssertForbiddenReferencesAbsent(Assembly assembly)
    {
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        foreach (var forbidden in ForbiddenCoreReferences)
            Assert.DoesNotContain(references, name => name.StartsWith(forbidden, StringComparison.Ordinal));
    }

    private static IEnumerable<Type> ReferencedTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo eventInfo when eventInfo.EventHandlerType is not null => [eventInfo.EventHandlerType],
        _ => []
    };
}
