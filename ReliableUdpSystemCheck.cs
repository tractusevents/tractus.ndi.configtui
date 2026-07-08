namespace Tractus.Ndi.ConfigTui;

internal sealed record ReliableUdpStatus(
    bool IsOptimized,
    string KernelRelease,
    string Summary,
    string Detail);

internal static class ReliableUdpSystemCheck
{
    private static readonly Version MinimumLinuxKernel = new(4, 18);

    public static ReliableUdpStatus GetStatus()
    {
        var release = GetKernelRelease();
        if (!OperatingSystem.IsLinux())
        {
            return new ReliableUdpStatus(
                IsOptimized: false,
                KernelRelease: release,
                Summary: "unknown: not Linux",
                Detail: "Reliable UDP optimization is checked only on Linux builds.");
        }

        if (!TryParseKernelVersion(release, out var version))
        {
            return new ReliableUdpStatus(
                IsOptimized: false,
                KernelRelease: release,
                Summary: "unknown: kernel not parsed",
                Detail: $"Could not parse Linux kernel release '{release}'. NDI Reliable UDP is best optimized with UDP GSO/UDP_SEGMENT support from Linux 4.18 or newer.");
        }

        var optimized = version >= MinimumLinuxKernel;
        return optimized
            ? new ReliableUdpStatus(
                IsOptimized: true,
                KernelRelease: release,
                Summary: $"optimized: Linux {release}",
                Detail: "Kernel is new enough for UDP GSO/UDP_SEGMENT, the Linux Reliable UDP optimization called out by NDI.")
            : new ReliableUdpStatus(
                IsOptimized: false,
                KernelRelease: release,
                Summary: $"not optimized: Linux {release}",
                Detail: "NDI recommends Linux 4.18 or newer for UDP GSO/UDP_SEGMENT. Reliable UDP can work, but CPU overhead may be higher on this kernel.");
    }

    private static string GetKernelRelease()
    {
        const string osReleasePath = "/proc/sys/kernel/osrelease";
        try
        {
            if (File.Exists(osReleasePath))
            {
                var release = File.ReadAllText(osReleasePath).Trim();
                if (release.Length > 0)
                {
                    return release;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Environment.OSVersion.VersionString;
    }

    private static bool TryParseKernelVersion(string release, out Version version)
    {
        version = new Version(0, 0);
        var parts = release.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        version = new Version(major, minor);
        return true;
    }
}
