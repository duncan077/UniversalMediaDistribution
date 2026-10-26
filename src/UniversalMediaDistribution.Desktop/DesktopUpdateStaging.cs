using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace UniversalMediaDistribution.Desktop;

internal sealed partial class DesktopApplicationUpdateService
{
    private const string ExecutableBaseName =
        "UniversalMediaDistribution.Desktop";

    /// <summary>
    /// Seconds the helper waits for this process to exit. Shutdown cancels the
    /// daily provider refresh and closes the torrent engine before it returns,
    /// and neither is instant, so this is generous: giving up means the update
    /// is silently skipped.
    /// </summary>
    private const int WaitAttempts = 180;

    /// <summary>
    /// Quotes a directory for cmd without leaving a backslash against the
    /// closing quote. <c>"C:\dir\"</c> reads as an escaped quote, so the
    /// argument never terminates and robocopy takes the rest of the line —
    /// including the destination and every switch — as one source path. That is
    /// what silently broke every update from 0.7.11 onward. Trimming the
    /// separator is not enough on its own because a drive root keeps it, so a
    /// trailing <c>.</c> stands in for the removed separator.
    /// </summary>
    internal static string QuoteForCommand(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(full)
            ? $"\"{full}.\""
            : $"\"{full}\"";
    }

    /// <summary>
    /// Extracts the verified archive next to it and writes the helper script
    /// that replaces the install directory once this process exits. Nothing in
    /// the install directory is touched here: a failure now leaves the running
    /// application untouched.
    /// </summary>
    private DesktopUpdateHandoff StagePayload(
        string archivePath,
        string versionDirectory)
    {
        var payloadDirectory = Path.Combine(versionDirectory, "payload");
        if (Directory.Exists(payloadDirectory))
        {
            Directory.Delete(payloadDirectory, recursive: true);
        }

        Directory.CreateDirectory(payloadDirectory);
        Extract(archivePath, payloadDirectory);

        var payloadRoot = ResolvePayloadRoot(payloadDirectory);
        // AppContext.BaseDirectory ends in a directory separator. Leaving that
        // separator immediately before the closing quote makes robocopy treat
        // the next quoted argument as part of the source path.
        var installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_options.InstallDirectory));
        var executableName = ExecutableBaseName +
            (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        if (!File.Exists(Path.Combine(payloadRoot, executableName)))
        {
            throw new InvalidDataException(
                $"The release archive does not contain '{executableName}'.");
        }

        var requiresElevation = !IsDirectoryWritable(installDirectory);
        if (requiresElevation && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                $"'{installDirectory}' is not writable. Reinstall the app in a " +
                "writable location or update it with your package manager.");
        }

        var executablePath = Path.Combine(installDirectory, executableName);
        var scriptPath = Path.Combine(
            versionDirectory,
            OperatingSystem.IsWindows()
                ? "apply-update.cmd"
                : "apply-update.sh");
        var handoff = new DesktopUpdateHandoff(
            scriptPath,
            payloadRoot,
            installDirectory,
            executablePath,
            requiresElevation);
        File.WriteAllText(
            scriptPath,
            OperatingSystem.IsWindows()
                ? CreateWindowsScript(handoff, versionDirectory)
                : CreateUnixScript(handoff, versionDirectory));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        return handoff;
    }

    private static void Extract(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var target = ResolveEntryPath(destination, entry.FullName);
                if (entry.FullName.EndsWith('/') ||
                    entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(target) ?? destination);
                entry.ExtractToFile(target, overwrite: true);
            }

            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            var target = ResolveEntryPath(destination, entry.Name);
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or
                TarEntryType.V7RegularFile))
            {
                // Links and devices are never part of a release payload.
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(target) ?? destination);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Rejects archive entries that would escape the payload directory.
    /// </summary>
    private static string ResolveEntryPath(
        string destination,
        string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new InvalidDataException(
                "The release archive contains an unnamed entry.");
        }

        var root = Path.GetFullPath(destination) +
            Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(
            Path.Combine(destination, entryName.Replace('\\', '/')));
        if (!target.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The release archive entry '{entryName}' escapes the payload directory.");
        }

        return target;
    }

    /// <summary>
    /// Publish archives usually wrap the app in a single versioned folder.
    /// </summary>
    internal static string ResolvePayloadRoot(string payloadDirectory)
    {
        var current = payloadDirectory;
        for (var depth = 0; depth < 4; depth++)
        {
            var files = Directory.GetFiles(current);
            var directories = Directory.GetDirectories(current);
            if (files.Length > 0 || directories.Length != 1)
            {
                return current;
            }

            current = directories[0];
        }

        return current;
    }

    internal static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(
                directory,
                ".umd-update-" + Guid.CreateVersion7().ToString("N") + ".tmp");
            using (var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for this process to exit, keeps a rollback copy, replaces the
    /// installed files, and starts the new version.
    /// </summary>
    private static string CreateWindowsScript(
        DesktopUpdateHandoff handoff,
        string versionDirectory)
    {
        var processId = Environment.ProcessId;
        var backupDirectory = Path.Combine(versionDirectory, "backup");
        var logPath = Path.Combine(versionDirectory, "apply-update.log");
        var quotedInstall = QuoteForCommand(handoff.InstallDirectory);
        var quotedPayload = QuoteForCommand(handoff.PayloadDirectory);
        var quotedBackup = QuoteForCommand(backupDirectory);
        // Launching through Explorer drops the administrator token so the app
        // does not keep running elevated after the update.
        var relaunch = handoff.RequiresElevation
            ? $"start \"\" explorer.exe \"{handoff.ExecutablePath}\""
            : $"start \"\" \"{handoff.ExecutablePath}\"";
        return $"""
            @echo off
            setlocal
            set "LOG={logPath}"
            > "%LOG%" echo Waiting for process {processId} to exit.
            set /a attempts=0
            :wait
            rem Match the image name, not the pid: the pid also appears in the
            rem memory column, so "50932" matches a row reading "50,932 K" and
            rem the helper waits for a process that already exited.
            tasklist /nh /fi "PID eq {processId}" 2>nul | find /i "{ExecutableBaseName}" >nul
            if errorlevel 1 goto ready
            set /a attempts+=1
            if %attempts% GEQ {WaitAttempts} (
              >> "%LOG%" echo The application did not exit within {WaitAttempts} seconds. The update was not applied.
              exit /b 1
            )
            rem ping, not timeout: timeout aborts immediately when stdin is not a
            rem console, which would burn every attempt in milliseconds and
            rem report a hung application while it was still shutting down.
            ping -n 2 127.0.0.1 >nul
            goto wait
            :ready
            if exist {quotedBackup} rmdir /s /q {quotedBackup}
            robocopy {quotedInstall} {quotedBackup} /e /nfl /ndl /njh /njs /np >> "%LOG%"
            if errorlevel 8 (
              >> "%LOG%" echo The rollback copy failed. The update was not applied.
              {relaunch}
              exit /b 1
            )
            rem /purge removes files the new version dropped. Without it a
            rem release that deletes files leaves them behind for ever, which
            rem matters most going from the Mono layout of roughly a hundred
            rem managed assemblies to a native build of eight files. The backup
            rem above is taken first, so this stays recoverable.
            robocopy {quotedPayload} {quotedInstall} /e /purge /nfl /ndl /njh /njs /np >> "%LOG%"
            if errorlevel 8 (
              >> "%LOG%" echo The update failed. Restoring the previous version.
              goto rollback
            )
            rem Robocopy reports 0 when it copied nothing at all. That is not
            rem success here — a release always differs from what it replaces —
            rem and it is what a malformed command line looks like from the
            rem outside. Going unchecked is why a broken update still reported
            rem that it had been applied.
            if not errorlevel 1 (
              >> "%LOG%" echo Robocopy copied no files. The update was not applied.
              goto rollback
            )
            if not exist "{handoff.ExecutablePath}" (
              >> "%LOG%" echo The updated install has no executable. Restoring the previous version.
              goto rollback
            )
            >> "%LOG%" echo Update applied.
            {relaunch}
            endlocal
            exit /b 0
            :rollback
            robocopy {quotedBackup} {quotedInstall} /e /purge /nfl /ndl /njh /njs /np >> "%LOG%"
            {relaunch}
            endlocal
            exit /b 1

            """;
    }

    private static string CreateUnixScript(
        DesktopUpdateHandoff handoff,
        string versionDirectory)
    {
        var processId = Environment.ProcessId;
        var backupDirectory = Path.Combine(versionDirectory, "backup");
        var logPath = Path.Combine(versionDirectory, "apply-update.log");
        return $"""
            #!/bin/sh
            set -u
            log="{logPath}"
            echo "Waiting for process {processId} to exit." > "$log"
            attempts=0
            while kill -0 {processId} 2>/dev/null; do
              attempts=$((attempts + 1))
              if [ "$attempts" -ge 60 ]; then
                echo "The application did not exit in time." >> "$log"
                exit 1
              fi
              sleep 1
            done
            rm -rf "{backupDirectory}"
            mkdir -p "{backupDirectory}"
            if ! cp -a "{handoff.InstallDirectory}/." "{backupDirectory}/" >> "$log" 2>&1; then
              echo "The rollback copy failed. The update was not applied." >> "$log"
              "{handoff.ExecutablePath}" >/dev/null 2>&1 &
              exit 1
            fi
            if cp -a "{handoff.PayloadDirectory}/." "{handoff.InstallDirectory}/" >> "$log" 2>&1; then
              echo "Update applied." >> "$log"
            else
              echo "The update failed. Restoring the previous version." >> "$log"
              cp -a "{backupDirectory}/." "{handoff.InstallDirectory}/" >> "$log" 2>&1
            fi
            chmod +x "{handoff.ExecutablePath}" 2>/dev/null
            "{handoff.ExecutablePath}" >/dev/null 2>&1 &

            """;
    }
}
