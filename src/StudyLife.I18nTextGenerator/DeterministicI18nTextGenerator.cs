using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Toolbelt.Blazor.I18nText;
using Toolbelt.Blazor.I18nText.Compiler.Shared;

namespace StudyLife.I18nTextGenerator;

/// <summary>
/// Generates the typed I18nText classes exactly like Toolbelt.Blazor.I18nText's own source
/// generator (same options, same <see cref="I18nTextCompiler"/>, same hint names and code), but
/// adds them to the compilation sorted by hint name. The package's generator adds them from a
/// ConcurrentBag filled by a Parallel.ForEach, so their order - and with it the type table, the
/// user-string heap, the PDB document table and finally the assembly's MVID - changed on every
/// build. See docs/REPRODUCIBLE-BUILDS.md.
/// </summary>
[Generator]
public sealed class DeterministicI18nTextGenerator : ISourceGenerator
{
    private static readonly DiagnosticDescriptor CompileError = new(
        id: "SLI18N001",
        title: "I18nText compile error",
        messageFormat: "{0}",
        category: "I18nText",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor VersionMismatch = new(
        id: "SLI18N002",
        title: "I18nText package versions differ",
        messageFormat: "Toolbelt.Blazor.I18nText is {0} but StudyLife.I18nTextGenerator is built against Toolbelt.Blazor.I18nText.SourceGenerator {1}; update both to the same version",
        category: "I18nText",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var options = context.AnalyzerConfigOptions.GlobalOptions;
        if (!options.TryGetValue("build_property.I18nTextUseSourceGenerator", out _))
        {
            return;
        }

        // The generated classes implement interfaces of the Toolbelt.Blazor.I18nText runtime
        // library, so the compiler code used here must come from the same release. A bump of
        // only one of the two packages fails the build instead of silently mixing versions.
        var compilerVersion = typeof(I18nTextCompiler).Assembly.GetName().Version;
        var runtime = context.Compilation.ReferencedAssemblyNames.FirstOrDefault(a => a.Name == "Toolbelt.Blazor.I18nText");
        if (runtime is not null && runtime.Version != compilerVersion)
        {
            context.ReportDiagnostic(Diagnostic.Create(VersionMismatch, Location.None, runtime.Version, compilerVersion));
            return;
        }

        var projectDir = Require(options, "ProjectDir");
        var errors = new List<string>();
        var compilerOptions = new I18nTextCompilerOptions(projectDir)
        {
            I18nTextSourceDirectory = Path.Combine(projectDir, Require(options, "I18nTextSourceDirectory")),
            OutDirectory = Require(options, "I18nTextIntermediateDir"),
            NameSpace = Require(options, "I18nTextNamespace"),
            DisableSubNameSpace = bool.Parse(Require(options, "I18nTextDisableSubNameSpace")),
            FallBackLanguage = Require(options, "I18nTextFallBackLanguage"),
            LogMessage = _ => { },
            LogError = ex => { lock (errors) { errors.Add(ex.Message); } },
        };

        var sourceDirectory = compilerOptions.I18nTextSourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sourceFiles = context.AdditionalFiles
            .Select(file => file.Path)
            .Where(path => path.StartsWith(sourceDirectory, StringComparison.Ordinal))
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new I18nTextSourceFile(path, Encoding.UTF8))
            .ToArray();

        var generated = new List<KeyValuePair<string, string>>();
        I18nTextCompiler.Compile(sourceFiles, compilerOptions, (_, item, codeLines) =>
        {
            var source = new KeyValuePair<string, string>(item.TypeFileName + ".g.cs", string.Join("\n", codeLines));
            lock (generated)
            {
                generated.Add(source);
            }
        }, context.CancellationToken);

        foreach (var error in errors.OrderBy(e => e, StringComparer.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(CompileError, Location.None, error));
        }

        foreach (var source in generated.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            context.AddSource(source.Key, source.Value);
        }
    }

    private static string Require(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions options, string property)
    {
        return options.TryGetValue("build_property." + property, out var value)
            ? value
            : throw new InvalidOperationException($"MSBuild property {property} is not visible to the compiler.");
    }
}
