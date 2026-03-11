using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"D:\_Utenti\stf.APLITA\Source\Repos\Leviathan\src\Leviathan.TUI\bin\Debug\net10.0\Hex1b.dll");

// Look for drag-related types/methods
foreach (var type in asm.GetTypes())
{
    if (type.Name.Contains("Drag", StringComparison.OrdinalIgnoreCase) ||
        type.Name.Contains("InputBindings", StringComparison.OrdinalIgnoreCase) ||
        type.Name.Contains("Mouse", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"\n=== {type.FullName} (public={type.IsPublic}) ===");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  Prop: {p.PropertyType.Name} {p.Name}");
    }
}

// Also check if InputBindingsBuilder has Drag method
var ibb = asm.GetTypes().FirstOrDefault(t => t.Name == "InputBindingsBuilder");
if (ibb != null) {
    Console.WriteLine($"\n=== ALL InputBindingsBuilder methods ===");
    foreach (var m in ibb.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
