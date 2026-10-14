using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace DynamicCampaignJapanese;

public static class ModActivation
{
    public static bool IsEnabled(Type fileManagerType, string path)
    {
        var instance = fileManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)
            ?? throw new MissingMemberException(fileManagerType.FullName, "Instance");
        var manager = instance.GetValue(null)
            ?? throw new InvalidOperationException("Sea Power's file manager is not ready.");
        var directoriesProperty = fileManagerType.GetProperty("Directories")
            ?? throw new MissingMemberException(fileManagerType.FullName, "Directories");
        var directories = directoriesProperty.GetValue(manager) as IEnumerable
            ?? throw new InvalidOperationException("Sea Power's mod directories are unavailable.");
        var normalized = Normalize(path);
        foreach (var directory in directories)
        {
            if (directory == null) continue;
            var directoryType = directory.GetType();
            var directoryInfoProperty = directoryType.GetProperty("DirectoryInfo")
                ?? throw new MissingMemberException(directoryType.FullName, "DirectoryInfo");
            var info = directoryInfoProperty.GetValue(directory) as DirectoryInfo
                ?? throw new InvalidOperationException("A mod directory has no directory information.");
            if (string.Equals(Normalize(info.FullName), normalized, StringComparison.OrdinalIgnoreCase))
            {
                var checkedProperty = directoryType.GetProperty("IsChecked")
                    ?? throw new MissingMemberException(directoryType.FullName, "IsChecked");
                return (bool)checkedProperty.GetValue(directory)!;
            }
        }
        throw new InvalidOperationException("The mod folder was not registered by Sea Power: " + path);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
