using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSLKeepAliveTray
{
    internal static class StartMenuShortcutManager
    {
        internal const string AppUserModelId = "CherishLJ.WSLKeepAliveTray";
        internal const string ShortcutName = "WSL KeepAlive Tray.lnk";
        private const ushort VtLpwstr = 31;
        private const ushort VtBstr = 8;
        private const int PropVariantBufferSize = 32;
        private const uint ShcneCreate = 0x00000002;
        private const uint ShcneDelete = 0x00000004;
        private const uint ShcnfPathW = 0x00000005;
        private const uint ShcnfFlushNoWait = 0x00002000;

        internal static string ShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    ShortcutName);
            }
        }

        internal static string BuildArguments(string distro)
        {
            return "--show --distro " + distro.Replace("\"", string.Empty);
        }

        internal static void Install(string executablePath, string distro)
        {
            string shortcutPath = ShortcutPath;
            string expectedExecutable = Path.GetFullPath(executablePath);
            if (File.Exists(shortcutPath))
            {
                ShortcutInfo existing = Read(shortcutPath);
                if (!PathsEqual(existing.TargetPath, expectedExecutable))
                {
                    throw new InvalidOperationException(
                        "Refusing to overwrite an unrelated Start menu shortcut: " + shortcutPath);
                }
                File.Delete(shortcutPath);
                Notify(shortcutPath, ShcneDelete);
            }

            Create(
                shortcutPath,
                expectedExecutable,
                BuildArguments(distro),
                Path.GetDirectoryName(expectedExecutable) ?? string.Empty,
                expectedExecutable,
                "Keep WSL running and open the live resource monitor.",
                AppUserModelId);

            ShortcutInfo verified = Read(shortcutPath);
            if (!PathsEqual(verified.TargetPath, expectedExecutable) ||
                !string.Equals(verified.Arguments, BuildArguments(distro), StringComparison.Ordinal) ||
                !string.Equals(verified.AppUserModelId, AppUserModelId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The Start menu shortcut failed verification.");
            }
        }

        internal static bool RemoveIfOwned(string executablePath)
        {
            string shortcutPath = ShortcutPath;
            if (!File.Exists(shortcutPath))
            {
                return false;
            }
            ShortcutInfo existing = Read(shortcutPath);
            if (!PathsEqual(existing.TargetPath, executablePath))
            {
                return false;
            }
            File.Delete(shortcutPath);
            Notify(shortcutPath, ShcneDelete);
            return true;
        }

        internal static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void Create(
            string shortcutPath,
            string targetPath,
            string arguments,
            string workingDirectory,
            string iconPath,
            string description,
            string appUserModelId)
        {
            IShellLinkW shellLink = (IShellLinkW)new ShellLinkClass();
            try
            {
                shellLink.SetPath(targetPath);
                shellLink.SetArguments(arguments);
                shellLink.SetWorkingDirectory(workingDirectory);
                shellLink.SetIconLocation(iconPath, 0);
                shellLink.SetDescription(description);
                shellLink.SetShowCmd(1);

                IPropertyStore propertyStore = (IPropertyStore)shellLink;
                PropertyKey key = AppUserModelIdKey();
                IntPtr value = AllocatePropVariant();
                try
                {
                    Marshal.WriteInt16(value, 0, unchecked((short)VtLpwstr));
                    Marshal.WriteIntPtr(value, 8, Marshal.StringToCoTaskMemUni(appUserModelId));
                    Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, value));
                    Marshal.ThrowExceptionForHR(propertyStore.Commit());
                }
                finally
                {
                    PropVariantClear(value);
                    Marshal.FreeCoTaskMem(value);
                }

                ((IPersistFile)shellLink).Save(shortcutPath, true);
                Notify(shortcutPath, ShcneCreate);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }

        private static ShortcutInfo Read(string shortcutPath)
        {
            IShellLinkW shellLink = (IShellLinkW)new ShellLinkClass();
            try
            {
                ((IPersistFile)shellLink).Load(shortcutPath, 0);
                StringBuilder targetPath = new StringBuilder(32768);
                StringBuilder arguments = new StringBuilder(32768);
                shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 0);
                shellLink.GetArguments(arguments, arguments.Capacity);
                return new ShortcutInfo
                {
                    TargetPath = targetPath.ToString(),
                    Arguments = arguments.ToString(),
                    AppUserModelId = ReadAppUserModelId((IPropertyStore)shellLink)
                };
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }

        private static string ReadAppUserModelId(IPropertyStore propertyStore)
        {
            PropertyKey key = AppUserModelIdKey();
            IntPtr value = AllocatePropVariant();
            try
            {
                Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, value));
                ushort variantType = unchecked((ushort)Marshal.ReadInt16(value, 0));
                IntPtr pointerValue = Marshal.ReadIntPtr(value, 8);
                if (pointerValue == IntPtr.Zero) return string.Empty;
                if (variantType == VtLpwstr) return Marshal.PtrToStringUni(pointerValue) ?? string.Empty;
                if (variantType == VtBstr) return Marshal.PtrToStringBSTR(pointerValue);
                return string.Empty;
            }
            finally
            {
                PropVariantClear(value);
                Marshal.FreeCoTaskMem(value);
            }
        }

        private static IntPtr AllocatePropVariant()
        {
            IntPtr value = Marshal.AllocCoTaskMem(PropVariantBufferSize);
            for (int offset = 0; offset < PropVariantBufferSize; offset += 4)
            {
                Marshal.WriteInt32(value, offset, 0);
            }
            return value;
        }

        private static PropertyKey AppUserModelIdKey()
        {
            return new PropertyKey(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                5);
        }

        private static void Notify(string shortcutPath, uint eventId)
        {
            SHChangeNotify(
                eventId,
                ShcnfPathW | ShcnfFlushNoWait,
                shortcutPath,
                IntPtr.Zero);
        }

        private sealed class ShortcutInfo
        {
            public string TargetPath { get; set; }
            public string Arguments { get; set; }
            public string AppUserModelId { get; set; }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkClass
        {
        }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder filePath, int maximumPath, IntPtr findData, uint flags);
            void GetIDList(out IntPtr itemIdList);
            void SetIDList(IntPtr itemIdList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maximumCharacters);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumCharacters);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumCharacters);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out ushort hotkey);
            void SetHotkey(ushort hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maximumPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
            void Resolve(IntPtr windowHandle, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string filePath);
        }

        [ComImport]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint propertyCount);
            [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
            [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
            [PreserveSig] int Commit();
        }

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int PropVariantClear(IntPtr value);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(
            uint eventId,
            uint flags,
            [MarshalAs(UnmanagedType.LPWStr)] string item1,
            IntPtr item2);
    }
}
