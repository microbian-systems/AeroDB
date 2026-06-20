using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage;

// Check what the default supportsSavepoints returns and what savepoint methods do
var t = typeof(IDbContextTransaction);

// Check SupportsSavepoints default
var spProp = t.GetProperty("SupportsSavepoints");
var defaultGetter = spProp!.GetMethod!;
Console.WriteLine($"SupportsSavepoints.IsAbstract = {defaultGetter.IsAbstract}");
Console.WriteLine($"SupportsSavepoints.IsVirtual = {defaultGetter.IsVirtual}");

// Check CreateSavepoint default (does it throw? what's in the IL?)
var csMethod = t.GetMethod("CreateSavepoint");
Console.WriteLine($"CreateSavepoint.IsAbstract = {csMethod!.IsAbstract}");

// Actually let's instantiate a DaliEfCoreTransaction and check SupportsSavepoints
// But we need the type info - let's just check via the Dali assembly
var daliAsm = Assembly.LoadFrom(@"D:\proj\microbians\Dali\src\Dali\bin\Debug\net10.0\Dali.dll");
var daliTxType = daliAsm.GetType("Dali.DaliEfCoreTransaction");
Console.WriteLine($"DaliEfCoreTransaction found: {daliTxType is not null}");

// Create instance via reflection
// var daliTx = Activator.CreateInstance(daliTxType!, [null!]); // won't work easily

// Just check if the type implements all interface members
var ifaceMap = daliTxType!.GetInterfaceMap(typeof(IDbContextTransaction));
Console.WriteLine("Interface map for DaliEfCoreTransaction -> IDbContextTransaction:");
for (int i = 0; i < ifaceMap.InterfaceMethods.Length; i++)
{
    Console.WriteLine($"  {ifaceMap.InterfaceMethods[i].Name} -> {ifaceMap.TargetMethods[i].Name}");
}
