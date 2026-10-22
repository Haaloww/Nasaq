using System.IO;
using Microsoft.Win32;
using Nasaq.Core;

namespace Nasaq.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nasaq";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var command = BuildCommand();
                key.SetValue(ValueName, command, RegistryValueKind.String);
                Log.Info("startup.enable", "Launch at sign-in enabled", ("command", command));
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Log.Info("startup.disable", "Launch at sign-in disabled");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("startup.apply", "Could not update the startup entry", ("reason", ex.Message));
        }
    }

    private static string BuildCommand()
    {
        var processPath = Environment.ProcessPath ?? "";
        var exeName = Path.GetFileNameWithoutExtension(processPath);
        if (exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Nasaq.dll");
            return $"\"{processPath}\" \"{assemblyPath}\"";
        }
        return $"\"{processPath}\"";
    }
}
