using System;
using System.Reflection;

var asm = Assembly.LoadFrom(@"C:\Users\stf\.nuget\packages\hex1b\0.114.0\lib\net8.0\Hex1b.dll");
var termType = asm.GetType("Hex1b.Hex1bTerminal");
var ctrlCharType = asm.GetType("Hex1b.ControlCharacterToken");

// Check ControlCharacterToken constructors
Console.WriteLine("ControlCharacterToken ctors:");
foreach (var ctor in ctrlCharType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    Console.WriteLine($"  ({string.Join(", ", Array.ConvertAll(ctor.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"))})");

// Check ControlCharacterToken properties
Console.WriteLine("Properties:");
foreach (var p in ctrlCharType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");

// Check ControlCharToKeyEvent
var method = termType.GetMethod("ControlCharToKeyEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
Console.WriteLine($"\nControlCharToKeyEvent: {(method != null ? $"static={method.IsStatic}" : "NOT FOUND")}");
