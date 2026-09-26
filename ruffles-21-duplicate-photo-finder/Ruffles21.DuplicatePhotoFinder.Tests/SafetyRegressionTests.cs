using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ruffles21.DuplicatePhotoFinder;
using System.IO;

internal static class SafetyRegressionTests
{
    public static int Run(string root)
    {
        int passed = 0;
        string fixtures = Path.Combine(Path.GetFullPath(root), "safety-regression");
        Directory.CreateDirectory(fixtures);

        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException("FAILED: " + label);
            Console.WriteLine("PASS: " + label);
            passed++;
        }

        TException Expect<TException>(Action action, string label) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException exception) { Check(true, label); return exception; }
            throw new InvalidOperationException("FAILED: " + label);
        }

        string settingsPath = Path.Combine(fixtures, "settings.json");
        File.WriteAllText(settingsPath, "{ incomplete JSON");
        var settings = SettingsStore.Load(settingsPath);
        Check(settings.Folders.Count == 0 && settings.Rule == 0 && settings.View == "Gallery",
            "Corrupt settings recover usable defaults");

        File.WriteAllText(settingsPath, "null");
        settings = SettingsStore.Load(settingsPath);
        Check(settings.Folders.Count == 0 && settings.Preferred == "" && settings.Backups == "",
            "A null settings document recovers usable defaults");

        File.WriteAllText(settingsPath,
            "{\"Folders\":null,\"Preferred\":null,\"Backups\":null,\"Rule\":999,\"View\":null}");
        settings = SettingsStore.Load(settingsPath);
        Check(settings.Folders.Count == 0 && settings.Preferred == "" && settings.Backups == ""
            && settings.Rule == 5 && settings.View == "Gallery",
            "Null settings properties and invalid option values are normalized");

        string tooLong = Path.GetPathRoot(fixtures)! + new string('a', 40000);
        var malformedSettings = new
        {
            Folders = new string?[] { fixtures, fixtures.ToUpperInvariant(), null, "", "relative-folder", "C:\\invalid\0path", tooLong },
            Preferred = fixtures + "; relative-folder; " + tooLong,
            Backups = (string?)null,
            Rule = -10,
            View = "invalid-view"
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(malformedSettings));
        settings = SettingsStore.Load(settingsPath);
        Check(settings.Folders.Count == 1 && settings.Folders[0] == fixtures
            && settings.Preferred == fixtures && settings.Backups == ""
            && settings.Rule == 0 && settings.View == "Gallery",
            "Invalid, duplicate, and excessively long persisted folder paths are ignored");

        settings.View = "Compact";
        SettingsStore.Save(settingsPath, settings);
        var restoredSettings = SettingsStore.Load(settingsPath);
        Check(restoredSettings.View == "Compact" && restoredSettings.Folders.SequenceEqual(settings.Folders)
            && Directory.GetFiles(fixtures, "settings.json.*.tmp").Length == 0,
            "Atomic settings save round-trips preferences without leftover temporary files");

        string renamedSettings = Path.Combine(fixtures, "renamed-settings.json");
        var migrated = SettingsStore.Load(renamedSettings, settingsPath);
        Check(migrated.View == "Compact" && migrated.Folders.SequenceEqual(settings.Folders),
            "Renamed app reads earlier folder and view preferences");
        SettingsStore.Save(renamedSettings, migrated);
        Check(File.Exists(settingsPath) && File.Exists(renamedSettings),
            "Preference migration preserves the earlier settings file");

        byte[] originalBytes = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        string media = Path.Combine(fixtures, "protected.jpg");
        string duplicate = Path.Combine(fixtures, "redundant.jpg");
        File.WriteAllBytes(media, originalBytes);
        File.WriteAllBytes(duplicate, originalBytes);
        var groups = new List<Group>();
        new Scanner().Scan(new[] { fixtures }, groups.Add, _ => { }, CancellationToken.None);
        var group = groups.Single();
        var rows = ReportExporter.Snapshot(groups);

        Expect<IOException>(() => ReportExporter.Export(media, rows, "CSV"),
            "Report export rejects an existing media-file destination");
        Check(File.ReadAllBytes(media).SequenceEqual(originalBytes),
            "Rejected report export preserves all original media bytes");

        string wrongExtension = Path.Combine(fixtures, "existing.csv");
        File.WriteAllText(wrongExtension, "existing report");
        Expect<IOException>(() => ReportExporter.Export(wrongExtension, rows, "JSON"),
            "Report export rejects a mismatched file extension");
        Check(File.ReadAllText(wrongExtension) == "existing report",
            "Wrong-format export leaves the destination untouched");

        var destinationRow = new ReportRow(1, wrongExtension, 15,
            DateTime.UtcNow, DateTime.UtcNow, "KEEP", "fixture");
        Expect<IOException>(() => ReportExporter.Export(wrongExtension, new[] { destinationRow }, "CSV"),
            "Report export rejects a scanned path even with a valid report extension");
        Expect<ArgumentException>(() => ReportExporter.Export(Path.Combine(fixtures, "report.txt"), rows, "TXT"),
            "Report export rejects unsupported formats");

        string alias = Path.Combine(fixtures, "media-hardlink.csv");
        if (!CreateHardLink(alias, media, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ReportExporter.Export(alias, rows, "CSV");
        Check(File.ReadAllBytes(media).SequenceEqual(originalBytes)
            && File.ReadAllText(alias).StartsWith("Group,Path,Size,CreatedUTC", StringComparison.Ordinal),
            "Atomic report replacement preserves media behind a hard-link alias");
        using (var sourceStream = Scanner.Open(media))
        using (var reportStream = Scanner.Open(alias))
            Check(Scanner.Identity(sourceStream) != Scanner.Identity(reportStream),
                "Export replaces the destination link with an independent report file");

        string jsonPath = Path.Combine(fixtures, "report.json");
        ReportExporter.Export(jsonPath, rows, "JSON");
        var exportedRows = JsonSerializer.Deserialize<List<ReportRow>>(File.ReadAllText(jsonPath));
        Check(exportedRows != null && exportedRows.SequenceEqual(rows)
            && Directory.GetFiles(fixtures, "*.tmp").Length == 0,
            "JSON export preserves snapshot values and leaves no temporary files");

        Check(new[] { '=', '+', '-', '@', '\t', '\r', '\n' }.All(prefix =>
            ReportExporter.QuoteCsv(prefix + "payload").StartsWith("\"'", StringComparison.Ordinal)),
            "CSV export neutralizes formula and control-character prefixes");
        Check(ReportExporter.QuoteCsv("line one,\"quoted\"\nline two") == "\"line one,\"\"quoted\"\"\nline two\"",
            "CSV export keeps commas, embedded quotes, and newlines inside one field");

        group.AutoMark();
        var candidate = group.Photos.Single(photo => photo.Marked);
        bool recycled = false;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Expect<OperationCanceledException>(() => SafeRemoval.Remove(candidate, _ => recycled = true, canceled.Token),
                "Already canceled removal stops before invoking the recycle callback");
        }
        Check(!recycled, "Canceled removal never reaches its injected recycle operation");

        using (var cancelDuringVerification = new CancellationTokenSource())
            Expect<OperationCanceledException>(() => SafeRemoval.Remove(candidate, _ => recycled = true,
                cancelDuringVerification.Token, _ => cancelDuringVerification.Cancel()),
                "Cancellation from verification progress interrupts revalidation");

        using (var cancelBeforeRecycle = new CancellationTokenSource())
            Expect<OperationCanceledException>(() => SafeRemoval.Remove(candidate, _ => recycled = true,
                cancelBeforeRecycle.Token, progress =>
                {
                    if (progress.StartsWith("Moving to Recycle Bin", StringComparison.Ordinal))
                        cancelBeforeRecycle.Cancel();
                }), "Cancellation at the final progress update still prevents the recycle callback");
        Check(!recycled && File.ReadAllBytes(media).SequenceEqual(originalBytes)
            && File.ReadAllBytes(duplicate).SequenceEqual(originalBytes),
            "Canceled removal leaves both fixture files unchanged");

        // A no-delete-sharing handle also prevents any native move if this guard regresses.
        using (var lockedFixture = Scanner.Open(duplicate))
        {
            string wrongIdentity = Scanner.Identity(lockedFixture) + ":wrong";
            var exception = Expect<IOException>(() => Recycle.MoveVerified(duplicate, wrongIdentity, CancellationToken.None),
                "Native recycle entry rejects a mismatched file identity before mutation");
            Check(exception.Message.Contains("replaced", StringComparison.OrdinalIgnoreCase)
                && File.ReadAllBytes(duplicate).SequenceEqual(originalBytes),
                "Identity rejection reports replacement and keeps the fixture intact");
        }

        // Simulate Shell callbacks and HRESULTs without creating a native file operation.
        using (var shellCancellation = new CancellationTokenSource())
        {
            var sink = new Recycle.RecycleProgressSink("unused", shellCancellation.Token);
            var exception = Expect<OperationCanceledException>(() => Recycle.ExecuteShellOperation(() =>
            {
                shellCancellation.Cancel();
                int result = sink.PreDeleteItem(0x80, null!);
                throw new COMException("Simulated Shell callback abort.", result);
            }, () => sink.Recycled, shellCancellation.Token),
                "A canceled native pre-delete callback is reported as cancellation");
            Check(exception.CancellationToken == shellCancellation.Token && exception.InnerException is COMException,
                "Native cancellation retains its token and underlying Shell error");
        }

        using (var completedCancellation = new CancellationTokenSource())
        {
            bool confirmedRecycled = false;
            Recycle.ExecuteShellOperation(() =>
            {
                confirmedRecycled = true;
                completedCancellation.Cancel();
                throw new COMException("Simulated error after a confirmed recycle.");
            }, () => confirmedRecycled, completedCancellation.Token);
            Check(confirmedRecycled, "A confirmed recycle remains successful despite later cancellation and COM failure");

            Recycle.ExecuteShellOperation(() => { }, () => true, completedCancellation.Token);
            Check(true, "Successful native completion wins over a late cancellation request");
            Expect<OperationCanceledException>(() => Recycle.ExecuteShellOperation(() => { }, () => false, completedCancellation.Token),
                "An unconfirmed native completion still honors cancellation without a COM exception");
        }

        var nativeFailure = new COMException("Simulated uncanceled Shell failure.");
        var reportedFailure = Expect<COMException>(() => Recycle.ExecuteShellOperation(() => throw nativeFailure,
            () => false, CancellationToken.None), "An uncanceled native error remains an error");
        Check(ReferenceEquals(nativeFailure, reportedFailure), "Uncanceled native errors preserve the original exception");

        return passed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr reserved);
}
