// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal sealed class StartupLink
    {
        internal string Target, Arguments, Description, WorkingDirectory, Icon;
        internal int ShowCommand, IconIndex;
        internal short HotKey;
        internal uint Flags;
    }
    internal static class ShellLinkInterop
    {
        private const uint HasExpandableIcon = 0x00004000;
        private const uint ExpandableIconSignature = 0xA0000007;
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkObject { }
        [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkDataList
        {
            void AddDataBlock(IntPtr data);
            void CopyDataBlock(uint signature, out IntPtr data);
            void RemoveDataBlock(uint signature);
            void GetFlags(out uint flags);
            void SetFlags(uint flags);
        }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetHotkey(out short value);
            void SetHotkey(short value);
            void GetShowCmd(out int value);
            void SetShowCmd(int value);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
        internal static StartupLink Read(string path)
        {
            object instance = new ShellLinkObject();
            try
            {
                ((IPersistFile)instance).Load(path, 0);
                var link = (IShellLinkW)instance;
                var target = new StringBuilder(32768); var arguments = new StringBuilder(32768);
                var description = new StringBuilder(1024); var working = new StringBuilder(32768); var icon = new StringBuilder(32768);
                int show, iconIndex; short hotkey;
                // SLGP_RAWPATH; never call Resolve, launch the target, or search for moved files.
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                link.GetArguments(arguments, arguments.Capacity); link.GetDescription(description, description.Capacity);
                link.GetWorkingDirectory(working, working.Capacity); link.GetShowCmd(out show);
                link.GetIconLocation(icon, icon.Capacity, out iconIndex); link.GetHotkey(out hotkey);
                uint flags; ((IShellLinkDataList)instance).GetFlags(out flags);
                return new StartupLink { Target = target.ToString(), Arguments = arguments.ToString(), Description = description.ToString(),
                    WorkingDirectory = working.ToString(), ShowCommand = show, Icon = icon.ToString(), IconIndex = iconIndex, HotKey = hotkey, Flags = flags };
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
        internal static void WriteNew(string path, StartupLink value)
        {
            object instance = new ShellLinkObject();
            try
            {
                var link = (IShellLinkW)instance;
                link.SetPath(value.Target); link.SetArguments(value.Arguments); link.SetDescription(value.Description);
                link.SetWorkingDirectory(value.WorkingDirectory); link.SetShowCmd(value.ShowCommand);
                link.SetIconLocation(value.Icon, value.IconIndex); link.SetHotkey(value.HotKey);
                var data = (IShellLinkDataList)instance;
                if (value.Flags != 0) data.SetFlags(value.Flags);
                // Shell can add an EXP_SZ_ICON block for an absolute Program Files
                // icon. Keep our explicitly supplied path literal, so the saved
                // shortcut still passes the same strict ownership checks as input.
                // Do not normalize an explicitly expandable icon or other flags.
                if (!String.IsNullOrEmpty(value.Icon) && Path.IsPathRooted(value.Icon) && value.Icon.IndexOf('%') < 0)
                {
                    uint flags; data.GetFlags(out flags);
                    if ((flags & HasExpandableIcon) != 0)
                    {
                        data.RemoveDataBlock(ExpandableIconSignature);
                        data.GetFlags(out flags);
                        data.SetFlags(flags & ~HasExpandableIcon);
                    }
                }
                ((IPersistFile)instance).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
    }
}
