using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentOS.Core;

// Trusted runtime adapters also need lifetime ownership: no Git writer starts before job admission.
internal static class SuspendedCommand
{
    public static async Task<CommandResult> Run(ProcessStartInfo start, string? input, CancellationToken cancel)
    {
        var executable = start.FileName;
        if (!Path.IsPathFullyQualified(executable))
            executable = (start.Environment["PATH"] ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path.Trim('"'), start.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? start.FileName : start.FileName + ".exe")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Executable was not found: " + start.FileName);
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var job = new WindowsJob();
        var handles = new[] { stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle() };
        IntPtr attributes = IntPtr.Zero, list = IntPtr.Zero, environment = IntPtr.Zero; ProcessInformation pi = default;
        try
        {
            nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); attributes = Marshal.AllocHGlobal((nint)size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            list = Marshal.AllocHGlobal(handles.Length * IntPtr.Size); Marshal.Copy(handles, 0, list, handles.Length);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, list, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment.Where(x => x.Value != null).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var startup = new StartupInfoEx { Attributes = attributes, Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, Input = handles[0], Output = handles[1], Error = handles[2] } };
            var command = new StringBuilder(Quote(executable) + " " + string.Join(' ', start.ArgumentList.Select(Quote)));
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x4 | 0x400 | 0x80000 | 0x8000000, environment, start.WorkingDirectory, ref startup, out pi));
            using var process = Process.GetProcessById((int)pi.Id); job.Attach(process);
            stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle(); stdin.DisposeLocalCopyOfClientHandle();
            using var output = new StreamReader(stdout, Encoding.UTF8); using var error = new StreamReader(stderr, Encoding.UTF8);
            var readingOutput = output.ReadToEndAsync(cancel); var readingError = error.ReadToEndAsync(cancel);
            using var stopping = cancel.Register(job.Stop); cancel.ThrowIfCancellationRequested();
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (var writer = new StreamWriter(stdin, new UTF8Encoding(false), leaveOpen: true))
            { if (input != null) await writer.WriteAsync(input.AsMemory(), cancel); await writer.FlushAsync(cancel); }
            stdin.Dispose();
            await process.WaitForExitAsync(cancel); job.Stop();
            return new(process.ExitCode, await readingOutput, await readingError);
        }
        finally
        {
            job.Stop(); if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread); if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list); if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\""); int slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); quoted.Append(character); slashes = 0;
        }
        quoted.Append('\\', slashes * 2); quoted.Append('"'); return quoted.ToString();
    }
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    { public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short Show, ReservedLength; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
