using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace GlassBar
{
    /// <summary>
    /// An app's recently opened files: the same list the Windows taskbar shows in an app's jump list. Windows keeps
    /// one list per app ID (AppUserModelID), so the ID is found first: from the app's open windows, from its Start
    /// menu shortcut, or remembered from an earlier window this session.
    /// </summary>
    internal static class JumpLists
    {
        private const string ExplorerAppId = "Microsoft.Windows.Explorer";

        // Program path -> app ID, learned from windows and Start menu shortcuts.
        private static readonly ConcurrentDictionary<string, string> KnownIds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _shortcutsScanned = DateTime.MinValue;
        private static int _scanning;

        /// <summary>Up to <paramref name="max"/> recent files or folders for the app, newest first. Empty if unknown.</summary>
        public static List<string> Recent(DockItem item, int max)
        {
            ScanShortcutsInBackground();

            string appId = AppIdFor(item);
            if (string.IsNullOrEmpty(appId))
            {
                return new List<string>();
            }

            return Query(appId, max);
        }

        private static string AppIdFor(DockItem item)
        {
            if (item.IsUwp)
            {
                return item.AppUserModelId;
            }

            string path = item.Path ?? "";

            // An open window knows its app ID; remember it for when the app is closed.
            foreach (ApplicationWindow window in item.Windows)
            {
                string id = window.AppUserModelID;
                if (!string.IsNullOrEmpty(id))
                {
                    if (path.Length > 0)
                    {
                        KnownIds[path] = id;
                    }

                    return id;
                }
            }

            if (string.Equals(Path.GetFileName(path), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                return ExplorerAppId;
            }

            if (KnownIds.TryGetValue(path, out string known))
            {
                return known;
            }

            // Shortcuts sometimes point at a launcher with the same name (Discord's Update stub, for instance).
            string name = Path.GetFileName(path);
            List<string> sameName = KnownIds.Where(k => string.Equals(Path.GetFileName(k.Key), name, StringComparison.OrdinalIgnoreCase))
                                            .Select(k => k.Value).Distinct().ToList();
            return sameName.Count == 1 ? sameName[0] : null;
        }

        /// <summary>
        /// Reads the app IDs of Start menu shortcuts (about a second), on ManagedShell's shell thread so the dock
        /// doesn't wait. Repeated at most every 10 minutes, to pick up newly installed apps.
        /// </summary>
        private static void ScanShortcutsInBackground()
        {
            if ((DateTime.Now - _shortcutsScanned).TotalMinutes < 10 || Interlocked.Exchange(ref _scanning, 1) == 1)
            {
                return;
            }

            Task.Factory.StartNew(() =>
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    foreach (string root in new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                    })
                    {
                        if (!Directory.Exists(root))
                        {
                            continue;
                        }

                        foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                        {
                            try
                            {
                                dynamic entry = shell.NameSpace(Path.GetDirectoryName(lnk))?.ParseName(Path.GetFileName(lnk));
                                if (entry == null)
                                {
                                    continue;
                                }

                                string id = entry.ExtendedProperty("System.AppUserModel.ID") as string;
                                string target = entry.ExtendedProperty("System.Link.TargetParsingPath") as string;
                                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target) &&
                                    target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    KnownIds.TryAdd(AppPaths.Canonical(target), id);
                                }
                            }
                            catch (Exception)
                            {
                                // Skip shortcuts that can't be read.
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // No shell available; recent lists then only work for apps that have a window open.
                }
                finally
                {
                    _shortcutsScanned = DateTime.Now;
                    Interlocked.Exchange(ref _scanning, 0);
                }
            }, CancellationToken.None, TaskCreationOptions.None, IconHelper.IconScheduler);
        }

        private static List<string> Query(string appId, int max)
        {
            List<string> result = new List<string>();
            try
            {
                IApplicationDocumentLists lists = (IApplicationDocumentLists)new ApplicationDocumentLists();
                try
                {
                    lists.SetAppID(appId);
                    Guid arrayId = typeof(IObjectArray).GUID;
                    if (lists.GetList(ADLT_RECENT, (uint)max, ref arrayId, out object listObject) != 0 || !(listObject is IObjectArray array))
                    {
                        return result;
                    }

                    array.GetCount(out uint count);
                    Guid itemId = typeof(IShellItem).GUID;
                    for (uint i = 0; i < count && result.Count < max; i++)
                    {
                        if (array.GetAt(i, ref itemId, out object entry) != 0 || !(entry is IShellItem item))
                        {
                            continue;
                        }

                        if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name) == 0)
                        {
                            string path = Marshal.PtrToStringUni(name);
                            Marshal.FreeCoTaskMem(name);
                            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                            {
                                result.Add(path); // files that were deleted or moved are left out
                            }
                        }

                        Marshal.ReleaseComObject(item);
                    }

                    Marshal.ReleaseComObject(array);
                }
                finally
                {
                    Marshal.ReleaseComObject(lists);
                }
            }
            catch (Exception)
            {
                // No recent list for this app.
            }

            return result;
        }

        #region COM interop

        private const int ADLT_RECENT = 0;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport]
        [Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")]
        private class ApplicationDocumentLists
        {
        }

        [ComImport]
        [Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationDocumentLists
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

            [PreserveSig]
            int GetList(int listType, uint itemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object list);
        }

        [ComImport]
        [Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectArray
        {
            void GetCount(out uint count);

            [PreserveSig]
            int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);

            [PreserveSig]
            int GetDisplayName(uint sigdn, out IntPtr name);
        }

        #endregion
    }
}
