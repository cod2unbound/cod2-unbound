using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsEngineInventory(string installRoot, IGameFiles files)
{
    internal ImmutableArray<GameBinary> Installed =>
        [.. from edition in Enum.GetValues<GameEdition>()
            from role in Enum.GetValues<EngineRole>()
            let folder = edition == GameEdition.Cod2x ? "cod2x" : "original"
            let name = role == EngineRole.Client ? "cod2_win32.exe" : "cod2_win32_ded.exe"
            let relative = folder + "/" + name
            where files.IsRegularFile(installRoot, relative)
            select new GameBinary(edition, role, WindowsFiles.Under(installRoot, relative))];

    internal ImmutableArray<GameProcessIdentity> FindRunning(EngineRole? role, CancellationToken cancellationToken)
    {
        var paths = Installed.Where(binary => role is null || binary.Role == role)
            .Select(binary => binary.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = ImmutableArray.CreateBuilder<GameProcessIdentity>();
        foreach (string name in paths.Select(path => Path.GetFileNameWithoutExtension(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!process.HasExited && process.MainModule?.FileName is { } image && paths.Contains(image))
                            identities.Add(new(process.Id, WindowsEngineProcess.CreationTime(process.SafeHandle)));
                    }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException) { }
                }
            }
        }
        return identities.ToImmutable();
    }
}
