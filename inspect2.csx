using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"C:\Users\stf\.nuget\packages\hex1b\0.114.0\lib\net8.0\Hex1b.dll");

// InputBindingsBuilder
var ibb = asm.GetType("Hex1b.Input.InputBindingsBuilder");
if (ibb != null) {
    Console.WriteLine("=== InputBindingsBuilder ===");
    foreach (var m in ibb.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}

// IsPrintableText - check if it's static
var isPrint = ibb?.GetMethod("IsPrintableText", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
if (isPrint != null)
    Console.WriteLine($"\nIsPrintableText: static={isPrint.IsStatic}, params={string.Join(", ", isPrint.GetParameters().Select(p => p.ParameterType.Name))}");

// CharacterBinding
var cb = asm.GetType("Hex1b.Input.CharacterBinding");
if (cb != null) {
    Console.WriteLine("\n=== CharacterBinding ===");
    foreach (var p in cb.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

// Hex1bKeyEvent
var ke = asm.GetType("Hex1b.Input.Hex1bKeyEvent");
if (ke != null) {
    Console.WriteLine("\n=== Hex1bKeyEvent ===");
    foreach (var p in ke.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

// InputRouter
var ir = asm.GetType("Hex1b.Input.InputRouter");
if (ir != null) {
    Console.WriteLine("\n=== InputRouter ===");
    foreach (var m in ir.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.NonPublic))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}

// Hex1bKey enum
var hk = asm.GetType("Hex1b.Input.Hex1bKey");
if (hk != null) {
    Console.WriteLine("\n=== Hex1bKey (Escape value) ===");
    var val = Enum.Parse(hk, "Escape");
    Console.WriteLine($"  Escape = {(int)val}");
}
