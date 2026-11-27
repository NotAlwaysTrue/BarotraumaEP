#nullable enable

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Barotrauma.Plugins;

internal static class LuaCsCompatibility
{
    private const string UpdatePriorityOwner = "LuaCsForBarotrauma.UpdatePriority";
    private const string UpdatePriorityServiceName = "Barotrauma.LuaCs.UpdatePriorityService";

    internal static void DisableUpdatePriority(Assembly pluginAssembly)
    {
        string? assemblyName = pluginAssembly.GetName().Name;
        if (!IsLuaCsAssembly(assemblyName)) { return; }

        MethodInfo[] updateMethods =
        [
            AccessTools.DeclaredMethod(typeof(MapEntity), nameof(MapEntity.UpdateAll), [typeof(float), typeof(Camera)]),
            AccessTools.DeclaredMethod(typeof(Character), nameof(Character.UpdateAll), [typeof(float), typeof(Camera)])
        ];

        // LuaCs installs these patches from a second copy of its assembly in the default load context.
        Type? serviceType = updateMethods.Select(FindPriorityPrefix).FirstOrDefault(prefix => prefix != null)?.DeclaringType;
        if (serviceType == null) { return; }

        try
        {
            FindActiveService(serviceType)?.Dispose();
        }
        catch (Exception e)
        {
            DebugConsole.AddWarning($"Could not dispose LuaCs UpdatePriorityService: {e.Message}", pluginAssembly: pluginAssembly);
        }

        // Initialization can fail after installing the prefixes but before publishing the service instance.
        var harmony = new Harmony(UpdatePriorityOwner);
        foreach (MethodInfo update in updateMethods)
        {
            if (FindPriorityPrefix(update) is { } prefix)
            {
                harmony.Unpatch(update, prefix);
            }
        }

        DebugConsole.AddWarning("Disabled LuaCs UpdatePriorityService because it uses an incompatible simulation update path in BarotraumaEP.",
            pluginAssembly: pluginAssembly);

        MethodInfo? FindPriorityPrefix(MethodInfo update) => Harmony.GetPatchInfo(update)?.Prefixes
            .FirstOrDefault(patch => patch.owner == UpdatePriorityOwner &&
                patch.PatchMethod.DeclaringType?.FullName == UpdatePriorityServiceName &&
                patch.PatchMethod.Module.Assembly.GetName().Name == assemblyName)?.PatchMethod;
    }

    internal static bool IsHttpCallback(MethodInfo method)
    {
        // The source callback is a lambda inside NetworkingService.HttpRequest.
        if (!method.Name.StartsWith("<HttpRequest>", StringComparison.Ordinal) ||
            !IsLuaCsAssembly(method.Module.Assembly.GetName().Name)) { return false; }

        Type? declaringType = method.DeclaringType;
        while (declaringType?.DeclaringType is { } outerType)
        {
            declaringType = outerType;
        }
        return declaringType?.FullName == "Barotrauma.LuaCs.NetworkingService";
    }

    private static bool IsLuaCsAssembly(string? name) =>
        name is "LuaCsForBarotraumaPluginClient" or "LuaCsForBarotraumaPluginServer";

    private static IDisposable? FindActiveService(Type serviceType)
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        object? setup = serviceType.Assembly.GetType("Barotrauma.LuaCsSetup")?
            .GetField("_luaCsSetup", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        object? provider = setup?.GetType().GetField("_servicesProvider", privateInstance)?.GetValue(setup);
        return provider?.GetType().GetField("_systemInstances", privateInstance)?.GetValue(provider) is IEnumerable systems
            ? systems.OfType<IDisposable>().FirstOrDefault(system => system.GetType() == serviceType)
            : null;
    }
}
