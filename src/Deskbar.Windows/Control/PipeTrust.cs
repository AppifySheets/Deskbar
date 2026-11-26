using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CSharpFunctionalExtensions;

namespace Deskbar.Windows.Control;

// WHETHER THE CONTROL CLIENT TRUSTS THE PIPE THAT ANSWERED. Since Claude Code's hooks send a session's prompt over the
// pipe and print its reply into the session's context, a pipe of Deskbar's name made by anything else must neither
// hear the request nor be believed. ControlClient asks this right after connecting, before it writes a byte.
//
// Two facts about the pipe decide it, read from the client's own handle:
//
//   * ITS OWNER. It must be this user, or this process's default owner, or the Administrators group while this process
//     runs elevated. An elevated Deskbar owns its pipe as Administrators (.NET's CurrentUserOnly makes the server's
//     default owner the owner, and an elevated token's default owner is Administrators). A client's default owner is
//     usually the same as its server's, except that Git Bash (MSYS) gives its processes the user as default owner, so
//     an elevated hook started by Git Bash sees an Administrators-owned pipe from an elevated Deskbar: measured on PR
//     #423's CI, where .NET's own CurrentUserOnly check threw and the client died. Any other owner is another user's.
//   * ITS MANDATORY LABEL (security review of fb50f0b, I1). A Low-integrity process of this user (a sandboxed helper,
//     say) can make a pipe of the name, and it is owned by this user like Deskbar's. What gives it away is its label:
//     a Low process's objects carry S:(ML;;NW;;;LW). Measured: before this check the client sent its request to such a
//     pipe and printed its fake reply with exit 0. A pipe with no label is Medium, the default; anything below Medium
//     is refused, and so is a label that does not read as an integrity level.
//
// Trusted is pure so every case is a unit test (PipeTrustTests); the reading is apart from the deciding.
public static class PipeTrust
{
    const string Administrators = "S-1-5-32-544";
    const string IntegrityPrefix = "S-1-16-";
    const int MediumRid = 0x2000;

    public static bool Trusted(string? owner, string user, string? defaultOwner, bool elevated, string? label) =>
        owner is not null
        && AtLeastMedium(label)
        && (Same(owner, user) || Same(owner, defaultOwner) || (Same(owner, Administrators) && elevated));

    // No label is Medium. A label is the SID S-1-16-<level>; Medium is 0x2000.
    static bool AtLeastMedium(string? label) =>
        label is null
        || (label.StartsWith(IntegrityPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(label[IntegrityPrefix.Length..], out var level) && level >= MediumRid);

    static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The decision for a connected client handle: its owner and label read from Windows, this process's identity.
    public static Result Check(NamedPipeClientStream pipe) =>
        LabelOf(pipe.SafePipeHandle).Bind(label =>
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            using var me = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            return Trusted(owner, me.User!.Value, me.Owner?.Value, elevated, label.GetValueOrDefault())
                ? Result.Success()
                : Result.Failure(label.HasValue && !AtLeastMedium(label.Value)
                    ? "A low integrity process answered on Deskbar's control pipe, not Deskbar; nothing was sent."
                    : "Something other than this user's Deskbar answered on its control pipe; nothing was sent.");
        });

    // The pipe's mandatory label as an integrity SID (S-1-16-...), or none when it has no label (Medium). Read with
    // GetSecurityInfo(LABEL_SECURITY_INFORMATION) on the client's handle; the label is the SACL's mandatory label ACE
    // (type 0x11), whose body is a mask and then the SID. What GetSecurityInfo allocates is freed here.
    public static Result<Maybe<string>> LabelOf(SafeHandle handle)
    {
        var error = GetSecurityInfo(handle, KernelObject, LabelInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var sacl, out var descriptor);
        if (error != 0) return Result.Failure<Maybe<string>>($"The control pipe's label could not be read (error {error}).");
        try
        {
            if (sacl == IntPtr.Zero) return Maybe<string>.None;
            // The ACL's size is the 16-bit field at offset 2 of its header.
            var bytes = new byte[Marshal.ReadInt16(sacl, 2) & 0xFFFF];
            Marshal.Copy(sacl, bytes, 0, bytes.Length);
            return new RawAcl(bytes, 0).OfType<CustomAce>()
                .Where(ace => (int)ace.AceType == MandatoryLabelAce && ace.OpaqueLength >= 12)
                .Select(ace => new SecurityIdentifier(ace.GetOpaque()!, 4).Value)
                .TryFirst();
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    // THE SERVER'S IMAGE, FOR `claude-permission` ONLY (the re-review's N1). The owner and the label say a pipe is this
    // user's at Medium or above; they cannot tell Deskbar from any other program of the same user. For the permission
    // hook that matters: the reply is the one-time code, and a program that served Deskbar's pipe name while Deskbar is
    // closed (the hook entry stays in settings.json) or took it in the second a starting Deskbar waits to make it
    // (ControlPipeServer's back-off) could answer with a code of its own, learn the slot from --slot and write
    // "<code> allow" into it: an allow with no click. So before this verb's request is written, the pipe's SERVER PROCESS
    // must be this client's own executable (the `ctl` path: Deskbar.exe started as the client), or Deskbar.exe beside it
    // (deskbar-cli.exe, the Store package's console client, sits in the same package folder as the app). Read with
    // GetNamedPipeServerProcessId and QueryFullProcessImageNameW; a path that cannot be read on either side is a refusal.
    // The boundary, said plainly: a process of the same user that can replace or patch Deskbar's executable, or inject
    // code into it, is out of scope; this stops a program that merely serves the pipe's name.
    //
    // The other verbs keep the owner and label check alone: their replies are JSON for a person or context for a session,
    // and none of them hands out something that answers for the user.

    /// <summary>The executables a pipe server may be for `claude-permission`: this client's own, and Deskbar.exe beside it.</summary>
    public static IReadOnlyList<string> TrustedImages(string? ownImage) =>
        ownImage is { Length: > 0 } own && Path.GetDirectoryName(own) is { } folder ? [own, Path.Combine(folder, "Deskbar.exe")] : [];

    /// <summary>Whether a server's image is one of <paramref name="trusted"/>: full paths compared without case; nothing read is no.</summary>
    public static bool SameImage(string? serverImage, IEnumerable<string> trusted) =>
        Normal(serverImage) is { } server && trusted.Select(Normal).Any(t => string.Equals(t, server, StringComparison.OrdinalIgnoreCase));

    static string? Normal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The decision for `claude-permission`: the server's image (or why it could not be read) against this client's.</summary>
    public static Result CheckServerImage(Result<string> serverImage, string? ownImage) =>
        serverImage.IsSuccess && SameImage(serverImage.Value, TrustedImages(ownImage))
            ? Result.Success()
            : Result.Failure(serverImage.IsFailure
                ? $"The program serving Deskbar's control pipe could not be checked ({serverImage.Error}); nothing was sent."
                : "Something other than Deskbar serves its control pipe; nothing was sent.");

    /// <summary>The full path of the process serving the pipe the handle is connected to.</summary>
    public static Result<string> ServerImageOf(SafeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid))
            return Result.Failure<string>($"no server process id (error {Marshal.GetLastPInvokeError()})");
        var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return Result.Failure<string>($"process {pid} could not be opened (error {Marshal.GetLastPInvokeError()})");
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : Result.Failure<string>($"the image of process {pid} could not be read (error {Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            CloseHandle(process);
        }
    }

    const uint QueryLimitedInformation = 0x1000;  // PROCESS_QUERY_LIMITED_INFORMATION

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    const int KernelObject = 6;           // SE_KERNEL_OBJECT
    const uint LabelInformation = 0x10;   // LABEL_SECURITY_INFORMATION
    const int MandatoryLabelAce = 0x11;   // SYSTEM_MANDATORY_LABEL_ACE_TYPE

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetSecurityInfo(SafeHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LocalFree(IntPtr memory);
}
