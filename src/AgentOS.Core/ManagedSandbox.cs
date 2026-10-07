using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AgentOS.Core;

/// <summary>A Windows AppContainer with no network capabilities. Creation failure never falls back.</summary>
internal sealed class ManagedSandbox : IDisposable
{
    private readonly string _name = "AgentOS." + Guid.NewGuid().ToString("N");
    private readonly List<(string Path, FileSystemAccessRule Rule)> _grants = [];
    private IntPtr _sid;
    private bool _disposed;
    private readonly string? _authPath;
    private string RecordPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "sandboxes", _name + ".json");
    public string Workspace { get; }
    public string Identity => _name;
    public string? EnvironmentSha256 { get; private set; }

    public ManagedSandbox(string workspace, IEnumerable<string>? readableTools = null, string? authPath = null)
    {
        _authPath = authPath;
        Workspace = SafePaths.Project(workspace);
        var hr = CreateAppContainerProfile(_name, _name, "agent-os task command isolation", IntPtr.Zero, 0, out _sid);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            using var owner = Process.GetCurrentProcess();
            Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
            File.WriteAllText(RecordPath + ".next", System.Text.Json.JsonSerializer.Serialize(new SandboxOwner(_name, Workspace, owner.Id, owner.StartTime.ToUniversalTime().Ticks, authPath)));
            File.Move(RecordPath + ".next", RecordPath);
            Grant(Workspace, FileSystemRights.Modify);
            // The low-integrity command must be able to write its explicitly delegated private tree.
            SetLowIntegrity(Workspace);
            foreach (var tool in readableTools ?? []) if (Directory.Exists(tool)) Grant(SafePaths.Project(tool), FileSystemRights.ReadAndExecute);
        }
        catch { Dispose(); throw; }
    }

    private static void SetLowIntegrity(string path)
    {
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _));
        try
        {
            Check(GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _));
            var result = SetNamedSecurityInfo(path, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally { LocalFree(descriptor); }
    }

    private void Grant(string path, FileSystemRights rights, bool inherit = true)
    {
        var identity = new SecurityIdentifier(_sid);
        var rule = new FileSystemAccessRule(identity, rights, inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl(); acl.AddAccessRule(rule);
        if (inherit) directory.SetAccessControl(acl);
        else Check(SetFileSecurity(path, 4, acl.GetSecurityDescriptorBinaryForm()));
        _grants.Add((path, rule));
    }

    public async Task<CommandResult> RunAsync(string script, string logPath, Action<string>? output, CancellationToken cancel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var temp = Path.Combine(Workspace, Directory.Exists(Path.Combine(Workspace, ".git")) ? ".git" : ".agent-os", "agent-os-temp"); Directory.CreateDirectory(temp);
        var gate = "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; New-PSDrive -Name Work -PSProvider FileSystem -Root " + Commands.Quote(Workspace) + " | Out-Null; Set-Location Work:\\; [Environment]::CurrentDirectory=" + Commands.Quote(Workspace) + "; " +
            "function global:Invoke-AgentOSProgram { param([string]$Program,[string[]]$Arguments) " +
            "$start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=$Program; $start.Arguments=($Arguments | ForEach-Object { '\"'+($_ -replace '(\\*)\"','$1$1\\\"' -replace '(\\+)$','$1$1')+'\"' }) -join ' '; " +
            "$start.WorkingDirectory=[Environment]::CurrentDirectory; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; " +
            "$child=[Diagnostics.Process]::Start($start); $child.StandardInput.Close(); $out=$child.StandardOutput.ReadToEndAsync(); $err=$child.StandardError.ReadToEndAsync(); $child.WaitForExit(); " +
            "[Console]::Write($out.GetAwaiter().GetResult()); [Console]::Error.Write($err.GetAwaiter().GetResult()); $global:LASTEXITCODE=$child.ExitCode; $child.Dispose() }; " +
            "function global:git { throw 'Use agent_os_git. Native Git cannot open the Windows null device from this AppContainer.' }; " +
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Text.UTF8Encoding]::new($false); $ProgressPreference='SilentlyContinue'; " +
            "$global:LASTEXITCODE=0; try { & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(" + Commands.Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(script))) + ")))); if (!$?) {exit 1}; exit $global:LASTEXITCODE } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var command = QuoteArgument(Commands.PowerShell) + " -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(gate));
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var handles = new[] { stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle() };
        using var job = new WindowsJob();
        IntPtr attributes = IntPtr.Zero, capabilities = IntPtr.Zero, handleList = IntPtr.Zero, environment = IntPtr.Zero;
        ProcessInformation pi = default;
        try
        {
            nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = _sid }, capabilities, false);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilities, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length); Marshal.Copy(handles, 0, handleList, handles.Length);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handleList, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero));
            // No inherited credentials, proxy settings, startup injection or environment secrets.
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SystemDrive"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\'),
                ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["ALLUSERSPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["COMSPEC"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System) + ";" + Path.GetDirectoryName(Commands.PowerShell) + ";C:\\Program Files\\Git\\cmd",
                ["TEMP"] = temp, ["TMP"] = temp, ["USERPROFILE"] = temp,
                ["LOCALAPPDATA"] = temp, ["APPDATA"] = temp, ["HOME"] = temp,
                ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "NUL", ["DOTNET_CLI_HOME"] = temp,
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            };
            EnvironmentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', variables.Select(x => x.Key + "=" + x.Value)))));
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var startup = new StartupInfoEx { AttributeList = attributes, StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StdInput = handles[0], StdOutput = handles[1], StdError = handles[2] } };
            Check(CreateProcess(Commands.PowerShell, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x80000 | 0x4 | 0x400 | 0x8000000, environment, Workspace, ref startup, out pi));
            using var process = Process.GetProcessById((int)pi.ProcessId);
            job.Attach(process); // Attach while suspended, before a model command can run.
            stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle(); stdin.DisposeLocalCopyOfClientHandle(); stdin.Dispose();
            using var canceled = cancel.Register(job.Stop);
            cancel.ThrowIfCancellationRequested();
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var text = new StringBuilder(); var errors = new StringBuilder();
            using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            var sync = new object();
            async Task Pump(Stream stream, StringBuilder destination, string prefix)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        lock (sync)
                        {
                            if (destination.Length < 1_000_000) destination.AppendLine(line);
                            log.WriteLine(prefix + line);
                        }
                        output?.Invoke(prefix + line);
                    }
                }
                catch { job.Stop(); throw; }
            }
            var readers = Task.WhenAll(Pump(stdout, text, ""), Pump(stderr, errors, "stderr: "));
            try
            {
                await process.WaitForExitAsync(cancel);
                job.Stop(); await readers.WaitAsync(TimeSpan.FromSeconds(10)); cancel.ThrowIfCancellationRequested();
                return new(process.ExitCode, text.ToString(), errors.ToString());
            }
            finally { job.Stop(); try { await readers.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
        }
        finally
        {
            job.Stop();
            if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
            if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
        foreach (var (path, rule) in _grants.AsEnumerable().Reverse())
        {
            if (!Directory.Exists(path)) continue;
            var directory = new DirectoryInfo(path); var acl = directory.GetAccessControl(); acl.RemoveAccessRuleAll(rule);
            Check(SetFileSecurity(path, 4, acl.GetSecurityDescriptorBinaryForm()));
        }
        if (_sid != IntPtr.Zero) { FreeSid(_sid); _sid = IntPtr.Zero; }
        var hr = DeleteAppContainerProfile(_name); if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        File.Delete(RecordPath);
        }
        finally { if (_authPath != null) File.Delete(_authPath); }
    }
    internal static void RecoverProfiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "sandboxes");
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "AgentOS.*.json"))
        {
            SandboxOwner owner;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                owner = System.Text.Json.JsonSerializer.Deserialize<SandboxOwner>(stream) ?? throw new InvalidDataException("Sandbox ownership record is empty.");
            }
            catch (FileNotFoundException) { continue; }
            if (Path.GetFileNameWithoutExtension(file) != owner.Name || !Guid.TryParseExact(owner.Name[8..], "N", out _)) throw new InvalidDataException("Sandbox ownership record is invalid.");
            try { using var process = Process.GetProcessById(owner.Pid); if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.Started) continue; }
            catch (ArgumentException) { }
            catch (Win32Exception) { continue; }
            var result = DeleteAppContainerProfile(owner.Name);
            if (result != 0 && result != unchecked((int)0x80070002)) Marshal.ThrowExceptionForHR(result);
            if (owner.AuthPath != null)
            {
                if (Path.GetFileName(owner.AuthPath) != "auth.json") throw new InvalidDataException("Sandbox credential path is invalid.");
                if (Directory.Exists(Path.GetDirectoryName(owner.AuthPath))) SafePaths.Project(Path.GetDirectoryName(owner.AuthPath)!);
                File.Delete(owner.AuthPath);
            }
            File.Delete(file);
        }
    }
    private sealed record SandboxOwner(string Name, string Workspace, int Pid, long Started, string? AuthPath);
    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Cb; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Length; public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, byte[] descriptor);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode)] private static extern uint SetNamedSecurityInfo(string path, uint kind, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
