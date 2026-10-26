using System.Reflection;
using System.Runtime.CompilerServices;

namespace CodexQuota.Tray;

/// <summary>Framework edition resolves private managed dependencies from this EXE only.</summary>
internal static class Bootstrap
{
#if NETFRAMEWORK
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
#endif
    [STAThread]
    private static void Main(string[] args)
    {
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
#endif
        StartApplication(args);
    }

    // Keep dependency-using code out of the bootstrap JIT before the resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartApplication(string[] args) => Program.Run(args);

#if NETFRAMEWORK
    private static Assembly? ResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name) || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Loaded)
        {
            if (Loaded.TryGetValue(name, out var existing)) return existing;
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Embedded." + name + ".dll");
            if (resource is null) return null;
            using var bytes = new MemoryStream();
            resource.CopyTo(bytes);
            var assembly = Assembly.Load(bytes.ToArray());
            Loaded[name] = assembly;
            return assembly;
        }
    }
#endif
}
