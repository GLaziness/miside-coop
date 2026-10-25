using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;

namespace MiSideCoop.Bridge;

// "call <Type> <Method> [args...]", "get <Type> <member>" and "set <Type> <member> <value>" on
// the first live object of a game type (or its statics), through the interop proxies. For
// exploring the game from test scripts.
internal static class GameReflection
{
    private static readonly Assembly[] GameAssemblies = { typeof(PlayerMove).Assembly, typeof(GlobalGame).Assembly };

    public static (bool, string) Call(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (false, "usage: call <Type> <Method> [args]");
        var (target, type, err) = FindFirst(parts[0]);
        if (err != null) return (false, err);
        var args = parts.Skip(2).ToArray();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == parts[1] && m.GetParameters().Length == args.Length);
        if (method == null) return (false, $"{type.Name}.{parts[1]} with {args.Length} args not found");
        if (!method.IsStatic && target == null) return (false, "no live " + type.Name);
        var ps = method.GetParameters();
        var values = new object[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = Convert(args[i], ps[i].ParameterType);
        var result = method.Invoke(method.IsStatic ? null : target, values);
        return (true, result?.ToString() ?? "");
    }

    public static (bool, string) Get(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return (false, "usage: get <Type> <member>");
        var (target, type, err) = FindFirst(parts[0]);
        if (err != null) return (false, err);
        var prop = type.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        if (prop == null) return (false, $"{type.Name}.{parts[1]} not found");
        if (!prop.GetMethod.IsStatic && target == null) return (false, "no live " + type.Name);
        return (true, prop.GetValue(prop.GetMethod.IsStatic ? null : target)?.ToString() ?? "null");
    }

    public static (bool, string) Set(string rest)
    {
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return (false, "usage: set <Type> <member> <value>");
        var (target, type, err) = FindFirst(parts[0]);
        if (err != null) return (false, err);
        var prop = type.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        if (prop?.SetMethod == null) return (false, $"{type.Name}.{parts[1]} not found or read-only");
        if (!prop.SetMethod.IsStatic && target == null) return (false, "no live " + type.Name);
        prop.SetValue(prop.SetMethod.IsStatic ? null : target, Convert(parts[2], prop.PropertyType));
        return (true, prop.GetValue(prop.GetMethod.IsStatic ? null : target)?.ToString() ?? "null");
    }

    private static (object target, Type type, string err) FindFirst(string typeName)
    {
        var type = GameAssemblies.Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
        if (type == null) return (null, null, "no game type " + typeName);
        if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) return (null, type, null);
        var found = UnityEngine.Object.FindObjectOfType(Il2CppType.From(type));
        if (found == null) return (null, type, null); // static members still work
        var proxy = Activator.CreateInstance(type, found.Pointer);
        return (proxy, type, null);
    }

    private static object Convert(string s, Type t)
    {
        if (t == typeof(string)) return s;
        if (t == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
        if (t == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);
        if (t == typeof(bool)) return bool.Parse(s);
        throw new ArgumentException("unsupported parameter type " + t.Name);
    }
}
