using System.Reflection;

namespace Beachtro.Architecture.Tests;

/// <summary>T-19: Domain has no EF/WPF/Google; Application references Domain only; UI holds no DbContext.</summary>
public sealed class DependencyRuleTests
{
    private static readonly Assembly Domain = Assembly.Load("Beachtro.Domain");
    private static readonly Assembly ApplicationLayer = Assembly.Load("Beachtro.Application");
    private static readonly Assembly Infrastructure = Assembly.Load("Beachtro.Infrastructure");
    private static readonly Assembly Desktop = Assembly.Load("BeachtroPOS");

    private static readonly string[] ForbiddenInCore =
    [
        "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "PresentationFramework",
        "PresentationCore", "WindowsBase", "Google", "Serilog", "Microsoft.Extensions",
    ];

    private static string[] References(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name!).ToArray();

    private static bool IsFramework(string name) =>
        name is "System" or "netstandard" or "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal);

    [Fact]
    public void Domain_references_only_the_base_framework()
    {
        var offenders = References(Domain).Where(n => !IsFramework(n)).ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_references_only_Domain_and_the_base_framework()
    {
        var offenders = References(ApplicationLayer).Where(n => !IsFramework(n) && n != "Beachtro.Domain").ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_layers_do_not_reference_persistence_ui_or_cloud_libraries()
    {
        foreach (var asm in new[] { Domain, ApplicationLayer })
        {
            var hits = References(asm)
                .Where(n => ForbiddenInCore.Any(f => n.StartsWith(f, StringComparison.Ordinal)))
                .ToArray();
            Assert.True(hits.Length == 0, $"{asm.GetName().Name} references {string.Join(", ", hits)}");
        }
    }

    [Fact]
    public void Infrastructure_does_not_reference_the_desktop_ui()
    {
        Assert.DoesNotContain(References(Infrastructure), n => n is "BeachtroPOS" or "PresentationFramework");
    }

    [Fact]
    public void Desktop_types_do_not_hold_or_inject_a_DbContext()
    {
        var offenders = new List<string>();
        foreach (var type in Desktop.GetTypes())
        {
            var memberTypes = type
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));

            foreach (var t in memberTypes)
            {
                for (var b = t; b is not null; b = b.BaseType)
                {
                    if (b.FullName == "Microsoft.EntityFrameworkCore.DbContext")
                    {
                        offenders.Add($"{type.FullName} -> {t.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }
}
