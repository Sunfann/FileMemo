using System.Runtime.InteropServices;
using System.Text;

namespace FileMemo.App.Interop;

/// <summary>
/// Win32 P/Invoke 集合：全局热键、剪贴板监听、NTFS File ID 读取、卷 GUID、
/// 桌面 SysListView32 选中项、USN Journal（P1 实时追踪）。
/// 全部带有失败兜底，非 NTFS 卷或权限不足时返回 null / 空，由上层降级。
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------
    //  全局热键 RegisterHotKey
    // ------------------------------------------------------------------
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ------------------------------------------------------------------
    //  剪贴板监听 AddClipboardFormatListener
    // ------------------------------------------------------------------
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ------------------------------------------------------------------
    //  窗口样式（隐藏热键消息窗口）
    // ------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ------------------------------------------------------------------
    //  NTFS File ID / 卷信息  CreateFile + GetFileInformationByHandle
    // ------------------------------------------------------------------
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint FILE_SHARE_DELETE = 0x04;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

    /// <summary>读取文件/目录的 NTFS File ID 与卷 GUID（需求 3.5.1 主指纹）。</summary>
    public static (long fileId, string? volumeGuid)? GetNtfsIdentity(string path)
    {
        IntPtr h = CreateFile(path, 0 /* 仅需元数据 */, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            if (!GetFileInformationByHandle(h, out var info)) return null;
            long fileId = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
            string? volGuid = TryGetVolumeGuid(path);
            return (fileId, volGuid);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>读取卷序列号（网络盘 / 非 NTFS 场景作为辅助指纹，需求 3.5.2）。</summary>
    public static uint? TryGetVolumeSerial(string path)
    {
        IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return info.VolumeSerialNumber;
        }
        finally { CloseHandle(h); }
    }

    public static string? TryGetVolumeGuid(string path)
    {
        try
        {
            var mount = new StringBuilder(261);
            if (!GetVolumePathName(path, mount, (uint)mount.Capacity)) return null;
            var vol = new StringBuilder(261);
            if (!GetVolumeNameForVolumeMountPoint(mount.ToString(), vol, (uint)vol.Capacity)) return null;
            return vol.ToString();   // 形如 \\?\Volume{guid}\
        }
        catch { return null; }
    }

    /// <summary>
    /// 通过 File ID 打开文件/目录句柄（OpenFileById）。
    /// USN Journal 只给出「父目录 FRN」，当其不在缓存中时需要靠本 API 反查出父目录的真实路径，
    /// 否则无法把重命名/移动后的子项路径拼接正确。
    /// </summary>
    public static IntPtr OpenByFileId(string volumeHint, long fileId)
    {
        if (string.IsNullOrWhiteSpace(volumeHint)) return INVALID_HANDLE_VALUE;

        // 关键修复（原先 File ID 反查恒失败）：
        //   1) 打开「卷」必须带 FILE_FLAG_BACKUP_SEMANTICS，否则 CreateFile 对目录/卷根一定失败，
        //      OpenFileById 拿不到有效卷句柄 → 返回 INVALID，File ID 反查、USN 父目录解析全部失效，
        //      表现为「文件移动后备注丢失、只有移回原路径才恢复」。
        //   2) 卷句柄优先用卷 GUID 设备路径（形如 \\?\Volume{guid}\），盘符被复用也不受影响；
        //      其次尝试标准卷句柄 \\.\C:，最后退回原始盘符根 C:\。
        foreach (var hint in VolumeHandleCandidates(volumeHint))
        {
            IntPtr hv = CreateFile(hint, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (hv == INVALID_HANDLE_VALUE) continue;
            try
            {
                var fid = new FILE_ID_DESCRIPTOR
                {
                    dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                    Type = 0,          // FileIdType
                    FileId = fileId
                };
                IntPtr h = OpenFileById(hv, ref fid, 0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
                if (h != INVALID_HANDLE_VALUE) return h;   // 命中，返回文件/目录句柄
            }
            finally { CloseHandle(hv); }
        }
        return INVALID_HANDLE_VALUE;
    }

    /// <summary>
    /// 生成用于 OpenFileById 的「卷句柄候选路径」，按可靠性排序并去重：
    ///   卷 GUID 设备路径 → \\.\C: → C:\。volumeHint 可以是卷 GUID（\\?\Volume{...}\）或盘符根（C:\）。
    /// </summary>
    private static IEnumerable<string> VolumeHandleCandidates(string volumeHint)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var v = s!.Trim();
            if (v.Length == 0) return;
            if (seen.Add(v)) list.Add(v);
        }

        var hint = volumeHint.Trim();

        // 1) 已是卷 GUID 设备路径：必须以反斜杠结尾（\\?\Volume{guid}\）
        if (hint.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
            Add(hint.TrimEnd('\\') + "\\");

        // 2) 盘符根 C:\ → 推导卷句柄 \\.\C:
        if (hint.Length >= 2 && hint[1] == ':')
        {
            var letter = char.ToUpperInvariant(hint[0]);
            Add($@"\\.\{letter}:");
            Add($@"{letter}:\");
        }

        // 3) 原样兜底
        Add(hint);

        return list;
    }

    /// <summary>由文件/目录句柄取 NTFS File ID（低 64 位）。</summary>
    public static long? GetFileIdFromHandle(IntPtr h)
    {
        if (h == INVALID_HANDLE_VALUE) return null;
        if (!GetFileInformationByHandle(h, out var info)) return null;
        return ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    /// <summary>
    /// 打开一个目录并返回其父目录的 File ID（用于向上递归拼路径）。
    /// 取不到时返回 null。
    /// </summary>
    public static long? GetFileId(string path)
    {
        IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return null;
        try { return GetFileIdFromHandle(h); }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct FILE_ID_DESCRIPTOR
    {
        [FieldOffset(0)] public uint dwSize;
        [FieldOffset(4)] public int Type;       // 0=FileIdType
        [FieldOffset(8)] public long FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenFileById(
        IntPtr hVolumeHint, ref FILE_ID_DESCRIPTOR lpFileId, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFinalPathNameByHandle(
        IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const uint VOLUME_NAME_DOS = 0x0;

    /// <summary>由句柄取规范化后的 DOS 路径（\\?\ 前缀会被去掉）。失败返回 null。</summary>
    public static string? GetPathFromHandle(IntPtr h)
    {
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (n == 0) return null;
            if (n > sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
                if (n == 0) return null;
            }
            var p = sb.ToString();
            if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p.Substring(8);
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p.Substring(4);
            return p;
        }
        catch { return null; }
    }

    // ==================================================================
    //  桌面（Progman / WorkerW → SHELLDLL_DefView → SysListView32）选中项
    //  桌面不在 Shell.Application.Windows() 集合里，也不能稳定依赖
    //  FindWindowSW 返回，故此处用 Win32 直接读取列表视图选中项。
    // ==================================================================
    public const int LVM_FIRST = 0x1000;
    public const int LVM_GETSELECTEDCOUNT = LVM_FIRST + 50;
    public const int LVM_GETNEXTITEM = LVM_FIRST + 12;
    public const int LVM_GETITEMTEXTW = LVM_FIRST + 115;
    public const int LVNI_SELECTED = 0x0002;
    public const int LVIF_TEXT = 0x0001;

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesWritten);

    /// <summary>定位桌面 SysListView32 句柄（兼容 Win10/11 的 Progman / WorkerW 承载）。</summary>
    public static IntPtr FindDesktopListView()
    {
        try
        {
            // 常规路径：Progman → SHELLDLL_DefView → SysListView32
            IntPtr progman = FindWindow("Progman", null);
            IntPtr defView = progman != IntPtr.Zero
                ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
                : IntPtr.Zero;

            // 壁纸幻灯片 / Win11：DefView 被挂在某个 WorkerW 之下
            if (defView == IntPtr.Zero)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows((hwnd, _) =>
                {
                    IntPtr dv = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { found = dv; return false; }
                    return true;
                }, IntPtr.Zero);
                defView = found;
            }

            if (defView == IntPtr.Zero) return IntPtr.Zero;
            return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 读取桌面列表视图当前选中的项名称（仅文件名，不含目录）。
    /// 通过跨进程内存读写完成 LVM_GETITEMTEXTW。返回空列表表示无选中或读取失败。
    /// </summary>
    public static List<string> GetDesktopSelectedNames()
    {
        var names = new List<string>();
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return names;

        uint pid;
        GetWindowThreadProcessId(listView, out pid);
        if (pid == 0) return names;

        IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return names;

        IntPtr remoteBuf = IntPtr.Zero;
        try
        {
            int selectedCount = (int)SendMessage(listView, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (selectedCount <= 0) return names;

            const int itemStructSize = 128;   // LVITEM 结构体 + 名称缓冲
            const int textBufferSize = 512;   // 单条名称 UTF-16 缓冲
            int totalSize = itemStructSize + textBufferSize;

            remoteBuf = VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBuf == IntPtr.Zero) return names;

            int index = -1;
            for (int i = 0; i < selectedCount; i++)
            {
                // 取下一个选中项的行号（首个用 -1 起点）
                index = (int)SendMessage(listView, LVM_GETNEXTITEM, (IntPtr)index, (IntPtr)LVNI_SELECTED);
                if (index < 0) break;

                var item = new LVITEM
                {
                    mask = LVIF_TEXT,
                    iItem = index,
                    iSubItem = 0,
                    pszText = (IntPtr)(remoteBuf.ToInt64() + itemStructSize),
                    cchTextMax = textBufferSize / 2,
                };

                byte[] itemBytes = StructToBytes(item);
                if (!WriteProcessMemory(hProc, remoteBuf, itemBytes, (IntPtr)itemBytes.Length, out _)) continue;

                SendMessage(listView, LVM_GETITEMTEXTW, (IntPtr)index, remoteBuf);

                byte[] textBytes = new byte[textBufferSize];
                if (!ReadProcessMemory(hProc, (IntPtr)(remoteBuf.ToInt64() + itemStructSize), textBytes, (IntPtr)textBytes.Length, out _)) continue;

                string name = Encoding.Unicode.GetString(textBytes);
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                if (name.Length > 0) names.Add(name);
            }
        }
        catch { /* 读取失败：上层回退 */ }
        finally
        {
            if (remoteBuf != IntPtr.Zero) VirtualFreeEx(hProc, remoteBuf, IntPtr.Zero, MEM_RELEASE);
            CloseHandle(hProc);
        }
        return names;
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return bytes;
    }

    // ==================================================================
    //  USN Journal（P1：实时捕获重命名 / 移动 / 修改）
    // ==================================================================
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

    /// <summary>USN_JOURNAL_DATA_V0。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>READ_USN_JOURNAL_DATA_V0。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    /// <summary>USN_RECORD_V2 头部（变长 FileName 跟随其后）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct USN_RECORD_V2
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
    }

    // USN_REASON_*
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_DATA_EXTEND = 0x00000002;
    public const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref READ_USN_JOURNAL_DATA lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>查询卷的 USN Journal 基本信息；失败（非 NTFS / 无权限）返回 null。</summary>
    public static USN_JOURNAL_DATA? QueryUsnJournal(string volumeRoot)
    {
        IntPtr h = CreateFile(volumeRoot, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            int size = Marshal.SizeOf<USN_JOURNAL_DATA>();
            IntPtr outBuf = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, outBuf, (uint)size, out _, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStructure<USN_JOURNAL_DATA>(outBuf);
            }
            finally { Marshal.FreeHGlobal(outBuf); }
        }
        finally { CloseHandle(h); }
    }
}
