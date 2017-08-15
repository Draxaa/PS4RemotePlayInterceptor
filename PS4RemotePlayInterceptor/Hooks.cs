﻿using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PS4RemotePlayInterceptor
{
    /// <summary>
    /// EasyHook will look for a class implementing <see cref="EasyHook.IEntryPoint"/> during injection. This
    /// becomes the entry point within the target process after injection is complete.
    /// </summary>
    public class Hooks : EasyHook.IEntryPoint
    {
        /// <summary>
        /// Reference to the server interface
        /// </summary>
        private InjectionInterface _server = null;

        private static byte[] ToManagedArray(IntPtr pointer, int size)
        {
            byte[] managedArray = new byte[size];
            Marshal.Copy(pointer, managedArray, 0, size);
            return managedArray;
        }

        private static void RestoreUnmanagedArray(IntPtr pointer, int size, byte[] managedArray)
        {
            unsafe
            {
                byte* ptr = (byte*)pointer.ToPointer();
                for (var i = 0; i < size; i++)
                {
                    ptr[i] = managedArray[i];
                }
            }
        }

        #region Setup
        /// <summary>
        /// EasyHook requires a constructor that matches <paramref name="context"/> and any additional parameters as provided
        /// in the original call to <see cref="EasyHook.RemoteHooking.Inject(int, EasyHook.InjectionOptions, string, string, object[])"/>.
        /// 
        /// Multiple constructors can exist on the same <see cref="EasyHook.IEntryPoint"/>, providing that each one has a corresponding Run method (e.g. <see cref="Run(EasyHook.RemoteHooking.IContext, string)"/>).
        /// </summary>
        /// <param name="context">The RemoteHooking context</param>
        /// <param name="channelName">The name of the IPC channel</param>
        public Hooks(
            EasyHook.RemoteHooking.IContext context,
            string channelName)
        {
            // Connect to server object using provided channel name
            _server = EasyHook.RemoteHooking.IpcConnectClient<InjectionInterface>(channelName);

            // If Ping fails then the Run method will be not be called
            _server.Ping();
        }

        /// <summary>
        /// The main entry point for our logic once injected within the target process. 
        /// This is where the hooks will be created, and a loop will be entered until host process exits.
        /// EasyHook requires a matching Run method for the constructor
        /// </summary>
        /// <param name="context">The RemoteHooking context</param>
        /// <param name="channelName">The name of the IPC channel</param>
        public void Run(
            EasyHook.RemoteHooking.IContext context,
            string channelName)
        {
            // Injection is now complete and the server interface is connected
            _server.OnInjectionSuccess(EasyHook.RemoteHooking.GetCurrentProcessId());

            // Install hooks

            // CreateFile https://msdn.microsoft.com/en-us/library/windows/desktop/aa363858(v=vs.85).aspx
            var createFileHook = EasyHook.LocalHook.Create(
                EasyHook.LocalHook.GetProcAddress("kernel32.dll", "CreateFileW"),
                new CreateFile_Delegate(CreateFile_Hook),
                this);

            // ReadFile https://msdn.microsoft.com/en-us/library/windows/desktop/aa365467(v=vs.85).aspx
            var readFileHook = EasyHook.LocalHook.Create(
                EasyHook.LocalHook.GetProcAddress("kernel32.dll", "ReadFile"),
                new ReadFile_Delegate(ReadFile_Hook),
                this);

            // WriteFile https://msdn.microsoft.com/en-us/library/windows/desktop/aa365747(v=vs.85).aspx
            var writeFileHook = EasyHook.LocalHook.Create(
                EasyHook.LocalHook.GetProcAddress("kernel32.dll", "WriteFile"),
                new WriteFile_Delegate(WriteFile_Hook),
                this);

            // Activate hooks on all threads except the current thread
            createFileHook.ThreadACL.SetExclusiveACL(new Int32[] { 0 });
            readFileHook.ThreadACL.SetExclusiveACL(new Int32[] { 0 });
            writeFileHook.ThreadACL.SetExclusiveACL(new Int32[] { 0 });

            //_server.ReportLog("CreateFile, ReadFile and WriteFile hooks installed");

            // Wake up the process (required if using RemoteHooking.CreateAndInject)
            EasyHook.RemoteHooking.WakeUpProcess();

            try
            {
                // Loop until injector closes (i.e. IPC fails)
                while (true)
                {
                    System.Threading.Thread.Sleep(100);
                    _server.Ping();
                }
            }
            catch
            {
                // Ping() will raise an exception if host is unreachable
            }

            // Remove hooks
            createFileHook.Dispose();
            readFileHook.Dispose();
            writeFileHook.Dispose();

            // Finalise cleanup of hooks
            EasyHook.LocalHook.Release();
        }

        /// <summary>
        /// P/Invoke to determine the filename from a file handle
        /// https://msdn.microsoft.com/en-us/library/windows/desktop/aa364962(v=vs.85).aspx
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpszFilePath"></param>
        /// <param name="cchFilePath"></param>
        /// <param name="dwFlags"></param>
        /// <returns></returns>
        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern uint GetFinalPathNameByHandle(IntPtr hFile, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        #endregion

        #region CreateFileW Hook

        /// <summary>
        /// The CreateFile delegate, this is needed to create a delegate of our hook function <see cref="CreateFile_Hook(string, uint, uint, IntPtr, uint, uint, IntPtr)"/>.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="desiredAccess"></param>
        /// <param name="shareMode"></param>
        /// <param name="securityAttributes"></param>
        /// <param name="creationDisposition"></param>
        /// <param name="flagsAndAttributes"></param>
        /// <param name="templateFile"></param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall,
                    CharSet = CharSet.Unicode,
                    SetLastError = true)]
        delegate IntPtr CreateFile_Delegate(
                    String filename,
                    UInt32 desiredAccess,
                    UInt32 shareMode,
                    IntPtr securityAttributes,
                    UInt32 creationDisposition,
                    UInt32 flagsAndAttributes,
                    IntPtr templateFile);

        /// <summary>
        /// Using P/Invoke to call original method.
        /// https://msdn.microsoft.com/en-us/library/windows/desktop/aa363858(v=vs.85).aspx
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="desiredAccess"></param>
        /// <param name="shareMode"></param>
        /// <param name="securityAttributes"></param>
        /// <param name="creationDisposition"></param>
        /// <param name="flagsAndAttributes"></param>
        /// <param name="templateFile"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true, CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr CreateFileW(
            String filename,
            UInt32 desiredAccess,
            UInt32 shareMode,
            IntPtr securityAttributes,
            UInt32 creationDisposition,
            UInt32 flagsAndAttributes,
            IntPtr templateFile);

        /// <summary>
        /// The CreateFile hook function. This will be called instead of the original CreateFile once hooked.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="desiredAccess"></param>
        /// <param name="shareMode"></param>
        /// <param name="securityAttributes"></param>
        /// <param name="creationDisposition"></param>
        /// <param name="flagsAndAttributes"></param>
        /// <param name="templateFile"></param>
        /// <returns></returns>
        IntPtr CreateFile_Hook(
            String filename,
            UInt32 desiredAccess,
            UInt32 shareMode,
            IntPtr securityAttributes,
            UInt32 creationDisposition,
            UInt32 flagsAndAttributes,
            IntPtr templateFile)
        {
            try
            {
                string mode = string.Empty;
                switch (creationDisposition)
                {
                    case 1:
                        mode = "CREATE_NEW";
                        break;
                    case 2:
                        mode = "CREATE_ALWAYS";
                        break;
                    case 3:
                        mode = "OPEN_ALWAYS";
                        break;
                    case 4:
                        mode = "OPEN_EXISTING";
                        break;
                    case 5:
                        mode = "TRUNCATE_EXISTING";
                        break;
                }

                //// Log for debug
                //_server.ReportLog(
                //    string.Format("[{0}:{1}]: CREATE ({2}) \"{3}\"",
                //    EasyHook.RemoteHooking.GetCurrentProcessId(), EasyHook.RemoteHooking.GetCurrentThreadId()
                //    , mode, filename));

                // Send to server
                _server.OnCreateFile(filename.ToString(), mode);
            }
            catch
            {
                // swallow exceptions so that any issues caused by this code do not crash target process
            }

            // now call the original API...
            return CreateFileW(
                filename,
                desiredAccess,
                shareMode,
                securityAttributes,
                creationDisposition,
                flagsAndAttributes,
                templateFile);
        }

        #endregion

        #region ReadFile Hook

        /// <summary>
        /// The ReadFile delegate, this is needed to create a delegate of our hook function <see cref="ReadFile_Hook(IntPtr, IntPtr, uint, out uint, IntPtr)"/>.
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToRead"></param>
        /// <param name="lpNumberOfBytesRead"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        delegate bool ReadFile_Delegate(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        /// <summary>
        /// Using P/Invoke to call the orginal function
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToRead"></param>
        /// <param name="lpNumberOfBytesRead"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
        static extern bool ReadFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        /// <summary>
        /// The ReadFile hook function. This will be called instead of the original ReadFile once hooked.
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToRead"></param>
        /// <param name="lpNumberOfBytesRead"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        bool ReadFile_Hook(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped)
        {
            bool result = false;
            lpNumberOfBytesRead = 0;

            // Call original first so we have a value for lpNumberOfBytesRead
            result = ReadFile(hFile, lpBuffer, nNumberOfBytesToRead, out lpNumberOfBytesRead, lpOverlapped);

            try
            {
                // Retrieve filename from the file handle
                StringBuilder filename = new StringBuilder(255);
                GetFinalPathNameByHandle(hFile, filename, 255, 0);

                //// Log for debug
                //_server.ReportLog(
                //    string.Format("[{0}:{1}]: READ ({2} bytes) \"{3}\"",
                //    EasyHook.RemoteHooking.GetCurrentProcessId(), EasyHook.RemoteHooking.GetCurrentThreadId()
                //    , lpNumberOfBytesRead, filename));

                // Only respond if it is a device stream
                var size = 64;
                if (string.IsNullOrWhiteSpace(filename.ToString()) && lpNumberOfBytesRead == size)
                {
                    // Copy unmanaged array for server
                    byte[] managedArray = ToManagedArray(lpBuffer, size);

                    // Make sure it is a input report (USB type)
                    if (managedArray[0] == 0x1)
                    {
                        // Send to server
                        _server.OnReadFile(filename.ToString(), ref managedArray);

                        // Restore managedArray back to unmanaged array
                        RestoreUnmanagedArray(lpBuffer, size, managedArray);
                    }
                }
            }
            catch
            {
                // swallow exceptions so that any issues caused by this code do not crash target process
            }

            return result;
        }

        #endregion

        #region WriteFile Hook

        /// <summary>
        /// The WriteFile delegate, this is needed to create a delegate of our hook function <see cref="WriteFile_Hook(IntPtr, IntPtr, uint, out uint, IntPtr)"/>.
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToWrite"></param>
        /// <param name="lpNumberOfBytesWritten"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        delegate bool WriteFile_Delegate(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        /// <summary>
        /// Using P/Invoke to call original WriteFile method
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToWrite"></param>
        /// <param name="lpNumberOfBytesWritten"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WriteFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        /// <summary>
        /// The WriteFile hook function. This will be called instead of the original WriteFile once hooked.
        /// </summary>
        /// <param name="hFile"></param>
        /// <param name="lpBuffer"></param>
        /// <param name="nNumberOfBytesToWrite"></param>
        /// <param name="lpNumberOfBytesWritten"></param>
        /// <param name="lpOverlapped"></param>
        /// <returns></returns>
        bool WriteFile_Hook(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped)
        {
            bool result = false;

            // Call original first so we get lpNumberOfBytesWritten
            result = WriteFile(hFile, lpBuffer, nNumberOfBytesToWrite, out lpNumberOfBytesWritten, lpOverlapped);

            try
            {
                // Retrieve filename from the file handle
                StringBuilder filename = new StringBuilder(255);
                GetFinalPathNameByHandle(hFile, filename, 255, 0);

                //// Log for debug
                //_server.ReportLog(
                //    string.Format("[{0}:{1}]: WRITE ({2} bytes) \"{3}\"",
                //    EasyHook.RemoteHooking.GetCurrentProcessId(), EasyHook.RemoteHooking.GetCurrentThreadId()
                //    , lpNumberOfBytesWritten, filename));

                // Only respond if it is a device stream
                //if (string.IsNullOrWhiteSpace(filename.ToString()))
                //{
                //    // Copy unmanaged array for server
                //    var size = 32;
                //    byte[] managedArray = ToManagedArray(lpBuffer, size);

                //    // Send to server
                //    _server.OnWriteFile(filename.ToString(), ref managedArray);

                //    // Restore managedArray back to unmanaged array
                //    RestoreUnmanagedArray(lpBuffer, size, managedArray);
                //}
            }
            catch
            {
                // swallow exceptions so that any issues caused by this code do not crash target process
            }

            return result;
        }

        #endregion
    }
}
