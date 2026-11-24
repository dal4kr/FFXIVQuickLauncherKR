using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XIVLauncher.Common.Windows;

public static class AutoLoginDevice
{
    public static string CreateId()
    {
        var machineGuid = ReadMachineGuid();
        var volumeRoot = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory);
        uint serial = 0;
        if (!string.IsNullOrEmpty(volumeRoot))
            _ = GetVolumeInformation(volumeRoot, IntPtr.Zero, 0, out serial, out _, out _, IntPtr.Zero, 0);
        var computer = new StringBuilder(16);
        uint length = 16;
        var computerName = GetComputerName(computer, ref length) ? computer.ToString() : "";
        return Convert.ToHexString(MD5.HashData(Encoding.Unicode.GetBytes($"{machineGuid}|{serial:X8}|{computerName}"))).ToLowerInvariant();
    }

    private static string ReadMachineGuid()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key is not null) return key.GetValue("MachineGuid", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return "";
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, IntPtr name, uint size, out uint serial, out uint maximum, out uint flags, IntPtr fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", EntryPoint = "GetComputerNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComputerName(StringBuilder name, ref uint size);
}
