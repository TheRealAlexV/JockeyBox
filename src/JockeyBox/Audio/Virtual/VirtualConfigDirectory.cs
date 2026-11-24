using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Virtual;

/// <summary>
/// Owns the one directory the app and the elevated host share. Creating it is part of
/// publishing the device, because the file is useless until this exists and the native
/// driver reads it from a different account than the app writes it with.
///
/// <para><b>Why this is not just <c>Directory.CreateDirectory</c>.</b> A standard user
/// may create the folder (BUILTIN\Users holds "create folder" on
/// <c>%ProgramData%</c>) and becomes its creator-owner, which means the folder can
/// exist with an ACL that lets nobody else in. It may also already exist because an
/// installer, or a previous run of a different account, made it. So the ACL is set
/// explicitly and re-asserted when the folder is already there; a failure is logged
/// with the path and thrown, never swallowed.</para>
///
/// <para><b>What the DACL says.</b> Users read (the driver runs as SYSTEM and reads),
/// Administrators and the installing user write (the operator's app writes). Inheritance
/// is preserved so SYSTEM keeps its access.</para>
/// </summary>
internal static class VirtualConfigDirectory
{
    /// <summary>
    /// Create <paramref name="directory"/> if it is missing and make sure it carries
    /// the shared DACL. Throws <see cref="UnauthorizedAccessException"/> with the path
    /// when it cannot, so a silent two-channel fallback is never the outcome.
    /// </summary>
    public static void Ensure(string directory)
    {
        if (Directory.Exists(directory))
        {
            // Not ours to create: try to re-assert the DACL, but a folder someone else
            // owns may refuse. The save that follows is the real permission test.
            TryApply(directory, throwOnFailure: false);
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"cannot create '{directory}' for the virtual device config; " +
                "the driver will fall back to two silent channels", ex);
        }

        // We are the creator, so this can only fail if the filesystem refuses ACLs.
        TryApply(directory, throwOnFailure: true);
    }

    private static void TryApply(string directory, bool throwOnFailure)
    {
        try
        {
            var security = new DirectorySecurity();

            // Keep the inherited ACEs (SYSTEM, Administrators, Users) and add ours on
            // top, so this is a repair rather than a replacement of the machine policy.
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            var user = WindowsIdentity.GetCurrent().User;
            if (user != null)
                security.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    inherit, PropagationFlags.None, AccessControlType.Allow));

            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            if (throwOnFailure)
                throw new UnauthorizedAccessException(
                    $"cannot set the shared ACL on '{directory}': {ex.Message}; " +
                    "a standard user may not be able to save the virtual device config",
                    ex);

            Log.Warn($"virtual device config directory '{directory}' already existed and its " +
                     $"ACL could not be re-asserted ({ex.Message}); saving may still work");
        }
    }
}
