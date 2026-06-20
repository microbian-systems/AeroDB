using System.Reflection;

var asm = Assembly.LoadFrom(@"D:\nuget-cache\microsoft.entityframeworkcore\10.0.8\lib\net10.0\Microsoft.EntityFrameworkCore.dll");
var t = asm.GetType("Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction");
if (t is not null)
{
    Console.WriteLine("=== IDbContextTransaction ===");
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  {m.MemberType} {m.Name}");

    Console.WriteLine();
}

var tm = asm.GetType("Microsoft.EntityFrameworkCore.Storage.IDbContextTransactionManager");
if (tm is not null)
{
    Console.WriteLine("=== IDbContextTransactionManager ===");
    foreach (var m in tm.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  {m.MemberType} {m.Name}");

    Console.WriteLine();
}

var irs = asm.GetType("Microsoft.EntityFrameworkCore.Infrastructure.IResettableService");
if (irs is not null)
{
    Console.WriteLine("=== IResettableService ===");
    foreach (var m in irs.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  {m.MemberType} {m.Name}");
}
