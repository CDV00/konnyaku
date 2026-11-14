using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Konnyaku.SharedKernel;
using NetArchTest.Rules;

namespace Konnyaku.ArchitectureTests;

/// <summary>Enforces the module seams of docs/06-evolution-strategy.md in CI.</summary>
public class ModuleBoundaryTests
{
    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Endpoints", "Contracts"];

    private static readonly Assembly[] ModuleAssemblies = Directory
        .GetFiles(AppContext.BaseDirectory, "Konnyaku.*.dll")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => name!.Split('.') is [_, _, var layer] && Layers.Contains(layer))
        .Select(name => Assembly.Load(name!))
        .ToArray();

    private static IEnumerable<string> ModuleNames =>
        ModuleAssemblies.Select(a => a.GetName().Name!.Split('.')[1]).Distinct();

    private static Assembly[] AssembliesOf(string module, params string[] layers) =>
        ModuleAssemblies.Where(a => a.GetName().Name!.Split('.') is [_, var m, var l] && m == module && layers.Contains(l)).ToArray();

    [Fact]
    public void Module_assemblies_are_discovered() =>
        ModuleNames.ShouldContain("Organization");

    [Fact]
    public void Modules_reach_other_modules_only_through_contracts()
    {
        foreach (var module in ModuleNames)
        {
            var forbidden = ModuleNames
                .Where(other => other != module)
                .SelectMany(other => Layers.Where(l => l != "Contracts").Select(l => $"Konnyaku.{other}.{l}"))
                .ToArray();
            if (forbidden.Length == 0)
            {
                continue;
            }

            var result = Types.InAssemblies(AssembliesOf(module, Layers)).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            result.IsSuccessful.ShouldBeTrue(Describe(result));
        }
    }

    [Fact]
    public void Contracts_depend_only_on_the_shared_kernel()
    {
        foreach (var module in ModuleNames)
        {
            var result = Types.InAssemblies(AssembliesOf(module, "Contracts")).ShouldNot().HaveDependencyOnAny(
                $"Konnyaku.{module}.Domain", $"Konnyaku.{module}.Application", "Konnyaku.Infrastructure",
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore").GetResult();
            result.IsSuccessful.ShouldBeTrue(Describe(result));
        }
    }

    [Fact]
    public void Domain_is_persistence_ignorant()
    {
        foreach (var module in ModuleNames)
        {
            var result = Types.InAssemblies(AssembliesOf(module, "Domain")).ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "Konnyaku.Infrastructure",
                $"Konnyaku.{module}.Application", $"Konnyaku.{module}.Infrastructure").GetResult();
            result.IsSuccessful.ShouldBeTrue(Describe(result));
        }
    }

    [Fact]
    public void Integration_events_are_versioned_sealed_records_in_contracts()
    {
        var events = ModuleAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(IIntegrationEvent).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToList();

        events.ShouldNotBeEmpty();
        foreach (var type in events)
        {
            type.Assembly.GetName().Name!.ShouldEndWith(".Contracts", customMessage: type.FullName);
            type.Name.ShouldMatch("V[0-9]+$", customMessage: type.FullName);
            type.IsSealed.ShouldBeTrue(type.FullName);
        }
    }

    /// <summary>
    /// Snapshot of every public contract. A breaking change must add a new version (…V2) instead of editing V1.
    /// Regenerate intentionally with KONNYAKU_UPDATE_SNAPSHOTS=1.
    /// </summary>
    [Fact]
    public void Public_contracts_match_snapshot()
    {
        var actual = DescribeContracts();
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("KONNYAKU_UPDATE_SNAPSHOTS") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, actual);
        }

        File.ReadAllText(path).ReplaceLineEndings("\n").ShouldBe(actual,
            "Public contracts changed. If intentional (new version, not an edit), rerun with KONNYAKU_UPDATE_SNAPSHOTS=1.");
    }

    private static string DescribeContracts()
    {
        var builder = new StringBuilder();
        foreach (var type in ModuleAssemblies
                     .Where(a => a.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal))
                     .SelectMany(a => a.GetExportedTypes())
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            builder.Append(type.FullName).Append('\n');
            var members = type.IsInterface
                ? type.GetMethods().Select(m =>
                    $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                : type.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}");
            foreach (var member in members.Where(m => !m.EndsWith(" EqualityContract", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                builder.Append("  ").Append(member).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "contracts.snapshot.txt");

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
