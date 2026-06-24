using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage;

// Print all members of IDbContextTransaction
var t = typeof(IDbContextTransaction);
Console.WriteLine("=== IDbContextTransaction ===");
foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
{
    bool isAbstract = m.IsAbstract;
    bool isVirtual = m.IsVirtual && !m.IsFinal;
    bool isFinal = m.IsVirtual && m.IsFinal;
    string modifiers = "";
    if (isAbstract) modifiers = "abstract ";
    else if (isFinal) modifiers = "default(final) ";
    else if (isVirtual) modifiers = "default(virtual) ";
    Console.WriteLine($"  {modifiers}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
foreach (var p in t.GetProperties())
{
    var getter = p.GetMethod;
    bool hasDefaultImpl = getter is not null && getter.IsVirtual && !getter.IsAbstract;
    Console.WriteLine($"  {(hasDefaultImpl ? "default " : "abstract ")}property {p.Name} : {p.PropertyType.Name}");
}
