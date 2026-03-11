using System;
using System.Reflection;
var asm = Assembly.LoadFrom(@"src\Leviathan.TUI\bin\Debug\net10.0\Hex1b.dll");
foreach (var type in asm.GetExportedTypes())
{
    if (type.Name == "Hex1bApp" || type.Name == "InputBindingsBuilder" || type.Name == "InteractableContext")
    {
        Console.WriteLine($"=== {type.FullName} ===");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"))})");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  Property: {p.PropertyType.Name} {p.Name}");
    }
}
