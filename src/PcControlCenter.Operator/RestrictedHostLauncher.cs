using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcControlCenter.Operator;

/// <summary>
/// Starts the Operator host with a normal-user token when PC Control Center itself runs elevated (started with "Run as
/// administrator", or on a machine where every process is elevated, such as a CI runner). The host then has the rights
/// of a standard user at medium integrity: Windows' own isolation (UIPI) keeps it away from elevated windows, and it can
/// not use administrator rights even if E's policy and the host guard were both bypassed. The token is Windows' Safer
/// "normal user" level computed from the shell's own token, the same as <c>runas /trustlevel:0x20000</c>.
/// </summary>
/// <remarks>
/// The protocol still runs over the child's own stdin/stdout (inherited anonymous pipes, only these three handles are
/// inherited). The process is created suspended so it is inside the job object before it runs any code.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class RestrictedHostLauncher
{
    private const uint SaferScopeIdUser = 2;
    private const uint SaferLevelIdNormalUser = 0x20000;
    private const uint SaferLevelOpen = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const string MediumIntegritySid = "S-1-16-8192";
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly nint ProcThreadAttributeHandleList = 0x00020002;

    /// <summary>A started (still suspended) host: call <see cref="Resume"/> once events are wired up.</summary>
    internal sealed class LaunchedHost : IDisposable
    {
        private nint _thread;

        internal LaunchedHost(Process process, nint thread, AnonymousPipeServerStream toHost, AnonymousPipeServerStream fromHost, AnonymousPipeServerStream errors)
        {
            Process = process;
            _thread = thread;
            ToHost = toHost;
            FromHost = fromHost;
            Errors = errors;
        }

        public Process Process { get; }

        /// <summary>The host's stdin.</summary>
        public Stream ToHost { get; }

        /// <summary>The host's stdout (the protocol).</summary>
        public Stream FromHost { get; }

        /// <summary>The host's stderr (diagnostics, logged by the shell).</summary>
        public Stream Errors { get; }

        public void Resume()
        {
            var thread = Interlocked.Exchange(ref _thread, 0);
            if (thread != 0)
            {
                var result = ResumeThread(thread);
                var error = Marshal.GetLastPInvokeError();
                CloseHandle(thread);
                if (result == uint.MaxValue)
                {
                    throw new System.ComponentModel.Win32Exception(error);
                }
            }
        }

        public void Dispose()
        {
            var thread = Interlocked.Exchange(ref _thread, 0);
            if (thread != 0)
            {
                CloseHandle(thread);
            }

            ToHost.Dispose();
            FromHost.Dispose();
            Errors.Dispose();
        }
    }

    /// <param name="beforeResume">Runs with the new process handle before it runs any code (job object assignment).</param>
    public static LaunchedHost Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, Action<nint> beforeResume)
    {
        var commandLine = WindowsCommandLine.Build([executable, .. arguments]);
        var toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var errors = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        ProcessInformation created = default;
        nint token = 0, attributes = 0, handleList = 0, commandBuffer = 0;
        var attributesInitialized = false;
        try
        {
            token = NormalUserToken();
            nint[] handles =
            [
                toHost.ClientSafePipeHandle.DangerousGetHandle(),
                fromHost.ClientSafePipeHandle.DangerousGetHandle(),
                errors.ClientSafePipeHandle.DangerousGetHandle(),
            ];

            // Only these three handles are inherited, whatever else in the shell happens to be inheritable.
            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(nint.Size * handles.Length);
            Marshal.Copy(handles, 0, handleList, handles.Length);
            Check(UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList, nint.Size * handles.Length, 0, 0));

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.StartupInfo.StdInput = handles[0];
            startup.StartupInfo.StdOutput = handles[1];
            startup.StartupInfo.StdError = handles[2];

            // CreateProcess may write into the command line buffer: a native copy, not the managed string.
            commandBuffer = Marshal.StringToHGlobalUni(commandLine);
            Check(CreateProcessAsUser(token, Path.IsPathFullyQualified(executable) ? executable : null, commandBuffer, 0, 0, true,
                ExtendedStartupInfoPresent | CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, 0, workingDirectory, ref startup, out created));
        }
        catch
        {
            toHost.Dispose();
            fromHost.Dispose();
            errors.Dispose();
            throw;
        }
        finally
        {
            if (attributesInitialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            if (attributes != 0)
            {
                Marshal.FreeHGlobal(attributes);
            }

            if (handleList != 0)
            {
                Marshal.FreeHGlobal(handleList);
            }

            if (commandBuffer != 0)
            {
                Marshal.FreeHGlobal(commandBuffer);
            }

            if (token != 0)
            {
                CloseHandle(token);
            }
        }

        // The child has its own copies now.
        toHost.DisposeLocalCopyOfClientHandle();
        fromHost.DisposeLocalCopyOfClientHandle();
        errors.DisposeLocalCopyOfClientHandle();
        try
        {
            // Our process handle keeps the id from being reused while the Process object opens its own.
            var process = Process.GetProcessById(created.ProcessId);
            _ = process.Handle;
            beforeResume(created.Process);
            return new LaunchedHost(process, created.Thread, toHost, fromHost, errors);
        }
        catch
        {
            TerminateProcess(created.Process, 1);
            CloseHandle(created.Thread);
            toHost.Dispose();
            fromHost.Dispose();
            errors.Dispose();
            throw;
        }
        finally
        {
            CloseHandle(created.Process);
        }
    }

    private static nint NormalUserToken()
    {
        Check(SaferCreateLevel(SaferScopeIdUser, SaferLevelIdNormalUser, SaferLevelOpen, out var level, 0));
        try
        {
            Check(SaferComputeTokenFromLevel(level, 0, out var token, 0, 0));
            try
            {
                SetMediumIntegrity(token);
                return token;
            }
            catch
            {
                CloseHandle(token);
                throw;
            }
        }
        finally
        {
            SaferCloseLevel(level);
        }
    }

    private static void SetMediumIntegrity(nint token)
    {
        Check(ConvertStringSidToSid(MediumIntegritySid, out var sid));
        try
        {
            var label = new TokenMandatoryLabel { Label = new SidAndAttributes { Sid = sid, Attributes = SeGroupIntegrity } };
            Check(SetTokenInformation(token, TokenIntegrityLevel, ref label, Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(sid)));
        }
        finally
        {
            LocalFree(sid);
        }
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags, out nint levelHandle, nint reserved);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferComputeTokenFromLevel(nint levelHandle, nint inAccessToken, out nint outAccessToken, uint flags, nint reserved);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCloseLevel(nint levelHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string sid, out nint sidPointer);

    [LibraryImport("advapi32.dll")]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(nint token, int informationClass, ref TokenMandatoryLabel information, int length);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(nint token, string? applicationName, nint commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>Builds a Windows command line that the C runtime splits back into exactly the given arguments.</summary>
public static class WindowsCommandLine
{
    public static string Build(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var i = 0; i < argument.Length;)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == argument.Length)
                {
                    // Before the closing quote: every backslash doubled.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[i] == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1).Append('"');
                    i++;
                }
                else
                {
                    builder.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                builder.Append("\\\"");
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }
}
