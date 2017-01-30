﻿using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GVFS.Common
{
    public static class NativeMethods
    {
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_FILE_EXISTS = 80;

        public enum FileAttributes : uint
        {
            FILE_ATTRIBUTE_READONLY            = 1,
            FILE_ATTRIBUTE_HIDDEN              = 2,
            FILE_ATTRIBUTE_SYSTEM              = 4,
            FILE_ATTRIBUTE_DIRECTORY           = 16,
            FILE_ATTRIBUTE_ARCHIVE             = 32,
            FILE_ATTRIBUTE_DEVICE              = 64,
            FILE_ATTRIBUTE_NORMAL              = 128,
            FILE_ATTRIBUTE_TEMPORARY           = 256,
            FILE_ATTRIBUTE_SPARSEFILE          = 512,
            FILE_ATTRIBUTE_REPARSEPOINT        = 1024,
            FILE_ATTRIBUTE_COMPRESSED          = 2048,
            FILE_ATTRIBUTE_OFFLINE             = 4096,
            FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 8192,
            FILE_ATTRIBUTE_ENCRYPTED           = 16384,
            FILE_FLAG_FIRST_PIPE_INSTANCE      = 524288,
            FILE_FLAG_OPEN_NO_RECALL           = 1048576,
            FILE_FLAG_OPEN_REPARSE_POINT       = 2097152,
            FILE_FLAG_POSIX_SEMANTICS          = 16777216,
            FILE_FLAG_BACKUP_SEMANTICS         = 33554432,
            FILE_FLAG_DELETE_ON_CLOSE          = 67108864,
            FILE_FLAG_SEQUENTIAL_SCAN          = 134217728,
            FILE_FLAG_RANDOM_ACCESS            = 268435456,
            FILE_FLAG_NO_BUFFERING             = 536870912,
            FILE_FLAG_OVERLAPPED               = 1073741824,
            FILE_FLAG_WRITE_THROUGH            = 2147483648
        }

        public enum FileAccess : uint
        {
            FILE_READ_DATA            = 1,
            FILE_LIST_DIRECTORY       = 1,
            FILE_WRITE_DATA           = 2,
            FILE_ADD_FILE             = 2,
            FILE_APPEND_DATA          = 4,
            FILE_ADD_SUBDIRECTORY     = 4,
            FILE_CREATE_PIPE_INSTANCE = 4,
            FILE_READ_EA              = 8,
            FILE_WRITE_EA             = 16,
            FILE_EXECUTE              = 32,
            FILE_TRAVERSE             = 32,
            FILE_DELETE_CHILD         = 64,
            FILE_READ_ATTRIBUTES      = 128,
            FILE_WRITE_ATTRIBUTES     = 256,
            SPECIFIC_RIGHTS_ALL       = 65535,
            DELETE                    = 65536,
            READ_CONTROL              = 131072,
            STANDARD_RIGHTS_READ      = 131072,
            STANDARD_RIGHTS_WRITE     = 131072,
            STANDARD_RIGHTS_EXECUTE   = 131072,
            WRITE_DAC                 = 262144,
            WRITE_OWNER               = 524288,
            STANDARD_RIGHTS_REQUIRED  = 983040,
            SYNCHRONIZE               = 1048576,
            FILE_GENERIC_READ         = 1179785,
            FILE_GENERIC_EXECUTE      = 1179808,
            FILE_GENERIC_WRITE        = 1179926,
            STANDARD_RIGHTS_ALL       = 2031616,
            FILE_ALL_ACCESS           = 2032127,
            ACCESS_SYSTEM_SECURITY    = 16777216,
            MAXIMUM_ALLOWED           = 33554432,
            GENERIC_ALL               = 268435456,
            GENERIC_EXECUTE           = 536870912,
            GENERIC_WRITE             = 1073741824,
            GENERIC_READ              = 2147483648
        }

        public static SafeFileHandle OpenFile(
            string filePath,
            FileMode fileMode,
            FileAccess fileAccess,
            FileShare fileShare,
            FileAttributes fileAttributes)
        {
            SafeFileHandle output = CreateFile(filePath, fileAccess, fileShare, IntPtr.Zero, fileMode, fileAttributes | FileAttributes.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (output.IsInvalid)
            {
                ThrowWin32Exception(Marshal.GetLastWin32Error());
            }

            return output;
        }

        /// <summary>
        /// Lock specified directory, so it can't be deleted or renamed by any other process
        /// The trick is to open a handle on the directory (FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT) 
        /// and keep it. If it is a junction the second option is required, and if it is a standard directory it is ignored.
        /// Caller must call Close() or Dispose() on the returned safe handle to release the lock
        /// </summary>
        /// <param name="path">Path to existing directory junction</param>
        /// <returns>SafeFileHandle</returns>
        public static SafeFileHandle LockDirectory(string path)
        {
            SafeFileHandle result = CreateFile(
                path, 
                FileAccess.GENERIC_READ, 
                FileShare.Read, 
                IntPtr.Zero, 
                FileMode.Open, 
                FileAttributes.FILE_FLAG_BACKUP_SEMANTICS | FileAttributes.FILE_FLAG_OPEN_REPARSE_POINT, 
                IntPtr.Zero);
            if (result.IsInvalid)
            {
                ThrowWin32Exception(Marshal.GetLastWin32Error());
            }

            return result;
        }

        public static void ThrowWin32Exception(int error, params int[] ignoreErrors)
        {
            if (ignoreErrors.Any(ignored => ignored == error))
            {
                return;
            }

            if (error == ERROR_FILE_EXISTS)
            {
                throw new Win32FileExistsException();
            }

            throw new Win32Exception(error);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            [In] string lpFileName,
            [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
            FileShare dwShareMode,
            [In] IntPtr lpSecurityAttributes,
            [MarshalAs(UnmanagedType.U4)]FileMode dwCreationDisposition,
            [MarshalAs(UnmanagedType.U4)]FileAttributes dwFlagsAndAttributes,
            [In] IntPtr hTemplateFile);

        public class Win32FileExistsException : Win32Exception
        {
            public Win32FileExistsException()
                : base(NativeMethods.ERROR_FILE_EXISTS)
            {
            }
        }
    }
}
