using System.Reflection;
using System.Reflection.Emit;
using BlogEngine.Core.Accounts.Contracts;

namespace BlogEngine.UnitTests.Architecture;

public sealed class DependencyTests
{
    [Fact]
    public void CoreHasNoFrameworkDependenciesOrForbiddenModuleEdges()
    {
        var assembly = typeof(CurrentActor).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.IdentityModel" }
                .Any(prefix => reference.Name!.StartsWith(prefix, StringComparison.Ordinal)));
        var modules = new[] { "Accounts", "Publishing", "Discussions", "Engagement", "ReadModels" };
        var allowed = new Dictionary<string, string[]>
        {
            ["Accounts"] = [],
            ["Publishing"] = ["Accounts"],
            ["Discussions"] = ["Accounts", "Publishing"],
            ["Engagement"] = ["Accounts", "Publishing"],
            ["ReadModels"] = ["Accounts", "Publishing", "Discussions", "Engagement"],
        };
        foreach (var type in assembly.GetTypes())
        {
            var module = Module(type);
            if (module is null) { continue; }
            var references = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.FieldType)
                .Concat(type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
                .Concat(type.GetInterfaces()).Concat(BodyReferences(type));
            foreach (var reference in references.SelectMany(Expand))
            {
                var other = Module(reference);
                if (other is null || other == module) { continue; }
                Assert.Contains(other, allowed[module]);
                Assert.Contains(".Contracts", reference.Namespace ?? string.Empty, StringComparison.Ordinal);
            }
        }

        string? Module(Type type) => modules.FirstOrDefault(module =>
            type.Namespace?.StartsWith("BlogEngine.Core." + module, StringComparison.Ordinal) == true);
    }

    private static IEnumerable<Type> Expand(Type type) =>
        new[] { type }.Concat(type.IsGenericType ? type.GetGenericArguments().SelectMany(Expand) : []);

    private static IEnumerable<Type> BodyReferences(Type type)
    {
        var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => unchecked((ushort)code.Value));
        var methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var method in methods)
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null) { continue; }
            for (var index = 0; index < bytes.Length;)
            {
                var first = bytes[index++];
                var code = opCodes[first == 0xfe ? (ushort)(0xfe00 | bytes[index++]) : first];
                if (code.OperandType is OperandType.InlineType or OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)
                {
                    var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, index), type.GetGenericArguments(),
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (member is Type referenced) { yield return referenced; }
                    else if (member?.DeclaringType is { } declaring) { yield return declaring; }
                }
                index += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, index),
                    _ => 4,
                };
            }
        }
    }
}
