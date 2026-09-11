using System.IO;

namespace FiveMDiagnostics.App.Wpf.Updates;

using FiveMDiagnostics.App.Wpf.Properties;

/// <summary>
/// Thrown when an update failed <em>and</em> putting the previous files back failed
/// too, which is the one outcome where the install is left as a mix of two versions.
/// It is a separate type because it has to be handled differently from every other
/// update failure: the backup is the only remaining copy of the replaced files, so
/// the work folder must survive, and the app must not be started again as if nothing
/// had happened.
/// </summary>
internal sealed class UpdateRollbackException : Exception
{
    private const int NamedFileLimit = 5;

    public UpdateRollbackException(
        string backupDirectory,
        string installDirectory,
        IReadOnlyList<string> unrestoredFiles,
        Exception innerException)
        : base(BuildMessage(backupDirectory, installDirectory, unrestoredFiles), innerException)
    {
        BackupDirectory = backupDirectory;
        UnrestoredFiles = unrestoredFiles;
    }

    /// <summary>Where the replaced files were copied before they were overwritten.</summary>
    public string BackupDirectory { get; }

    public IReadOnlyList<string> UnrestoredFiles { get; }

    private static string BuildMessage(
        string backupDirectory,
        string installDirectory,
        IReadOnlyList<string> unrestoredFiles)
    {
        var names = string.Join(
            Environment.NewLine,
            unrestoredFiles.Take(NamedFileLimit).Select(path => "  " + Path.GetFileName(path)));
        if (unrestoredFiles.Count > NamedFileLimit)
        {
            names += Environment.NewLine
                + "  "
                + string.Format(Strings.Culture, Strings.UpdateRollbackMoreFilesFormat, unrestoredFiles.Count - NamedFileLimit);
        }

        return string.Format(
            Strings.Culture,
            Strings.UpdateRollbackFormat,
            unrestoredFiles.Count,
            names,
            backupDirectory,
            installDirectory);
    }
}
