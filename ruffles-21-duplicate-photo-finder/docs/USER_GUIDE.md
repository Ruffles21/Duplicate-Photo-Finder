# ruffles_21's Duplicate Photo Finder 1.2.1

A Windows desktop app for reviewing duplicate photos and videos. Exact matches use SHA-256 and byte comparison. Similarity matching helps find possible lookalikes for manual review. Media matching runs locally; the app does not upload your library.

## Run

Extract the Windows ZIP, then open `ruffles_21's Duplicate Photo Finder/Ruffles21.DuplicatePhotoFinder.exe`. Keep the other files beside the executable.

The standard package requires **Windows 10/11, x64, and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)**. A self-contained package, when built with the option below, includes the runtime.

1. Check **Exact duplicates**, **Similar photos & videos**, or both, then select folders. Subfolders are included, and scanning starts after the folder picker closes. Use **Manage folders** to combine different locations.
2. Watch the photo/video counts and progress. You can pause or stop a scan; fully verified exact groups remain available.
3. Switch between the **Exact** and **Similar** result tabs. Inspect groups in gallery, compact, or list view. **Search** and **All media / Photos / Videos** filters narrow the visible groups.
4. For exact duplicates, review the green **KEEP** copy. **Keep This Copy** sets a manual choice that global Auto Mark preserves. Use the Selection Assistant to choose a default keep rule for other groups.
5. Mark redundant exact copies, review the selected count and size, then use **Move Selected to Recycle Bin**. Removal requires confirmation and rechecks each candidate against its protected copy.

Filters change what is visible. Global selection and removal actions apply to all exact results, including hidden selections; the toolbar labels and selection summary show this scope.

## Scan modes

| Mode | What it finds | Removal |
| --- | --- | --- |
| Exact duplicates | Identical file contents, verified by full hashing and byte comparison. | Selected redundant copies can be moved to the Recycle Bin. |
| Similar photos & videos | Possible visual matches using perceptual image analysis and sampled video frames. | Review only. |
| Both checked | Exact duplicate groups and possible similar matches. | Only verified exact duplicates can be selected for recycling. |

Both options share one folder-discovery pass. Exact verification runs first, followed by similarity analysis; they do not perform simultaneous scan passes. Choose **Strict**, **Balanced**, or **Broader** to adjust similarity sensitivity. Broader settings can surface more candidates and more false matches.

Identical copies can also appear in Similar groups. Similar results appear progressively as the scan runs; their gallery can include a sampled video thumbnail when Windows can decode it.

Similarity targets visual near-duplicates using images or sampled frames. It does not recognize the same person, object, or scene across otherwise different photos and videos. Similarity is a heuristic, not proof that files are interchangeable. It can miss matches and group unrelated media. Crops, rotation, overlays, substantial edits, short clips, and different video timelines can affect results. Video matching depends on Windows being able to decode the sampled frames. Unsupported media can still participate in exact matching.

## Features

- Explicit KEEP, REMOVE, and unselected states. Inspecting a card never marks it for removal.
- Six keep rules, manual KEEP choices, per-group marking, and global mark/unmark actions.
- Search, photo/video filters, and gallery, compact, and list layouts.
- Background image previews with EXIF orientation, available metadata, enlargement, fit, actual size, and zoom.
- Open a file or its folder in Windows; export CSV or JSON reports for all groups in the current result tab, including groups hidden by filters. CSV text is escaped for spreadsheet use, and report export prevents writing over media.
- Scan pause/stop, progress during large-file reads, separate photo/video counts, and a warning log.
- Cancellable removal revalidation and a summary of completed, failed, and remaining work.
- Saved scan folders and Selection Assistant preferences, with invalid settings handled defensively.

## File safety

New exact results always start with one protected copy and nothing marked for removal. Auto Mark respects manually protected copies. Removal is unavailable while scanning, and similar matches cannot enter the removal workflow.

Before each removal, the app reopens the protected and selected copies, checks file state and identity, recomputes their hashes, and compares their contents. The protected copy stays open without write/delete sharing during the Windows operation. A missing or changed copy blocks that removal. Failed files remain available for review.

The app requests Windows Recycle Bin operations through `IFileOperation` and checks the Shell's per-item confirmation that a Recycle Bin item was created. It has no `File.Delete` fallback for media. Removal is limited to local fixed drives. Network and removable-drive files can be scanned and reviewed, but cannot be recycled through this app. Windows controls Recycle Bin availability, retention, and later emptying.

The app rechecks file identity around removal, but Windows does not offer an atomic compare-and-recycle operation through this API. Avoid moving or modifying the same media with another application while removing duplicates. Keep an independent backup for irreplaceable media.

Stop during removal cancels pending verification and leaves already recycled files in the Recycle Bin. An operation that Windows has already begun may finish before the stop takes effect. Restore recycled files through the Windows Recycle Bin.

Scans skip linked folders, reparse points/cloud placeholders, and repeated hard-link identities. They deduplicate overlapping folder selections and report unreadable files. Media metadata is not edited.

## Practical limits

- Exact matching compares file contents, not filenames or appearance. A metadata-only change produces a different exact file; use Similar mode to investigate possible visual matches.
- HEIC, RAW, and some other photo/video formats need compatible Windows codecs. Missing previews do not prevent exact comparison. Scanning recognizes supported extensions; it does not certify that each file contains valid media.
- Oldest/newest keep rules use file creation time, not EXIF capture time. Ties use deterministic path ordering.
- Thumbnail work is bounded and result controls are virtualized. Scan records and similarity data still use memory proportional to the library; virtualization does not make the scan dataset memory-free.
- Actual-size preview decodes the full image and can use substantial memory for very large photos.
- Recoverable-space totals describe logical redundant file sizes. Compression, hard links, and filesystem deduplication can change the actual free space gained.
- Layout tests use 5,000 synthetic groups. This verifies UI virtualization and layout, not the speed of scanning a real 5,000-group library. Storage speed, file sizes, codecs, and scan mode affect performance.

Preferences are stored in `%LOCALAPPDATA%/Ruffles21DuplicatePhotoFinder/settings.json`. Existing preferences from the former `%LOCALAPPDATA%/LuigiPhotoKeeper/settings.json` location are migrated when the new settings file does not exist. Reports contain file paths and media metadata; review their contents before sharing them.

## Build and test

Use Windows and the .NET 8 SDK selected by `global.json`. There are no third-party NuGet dependencies. From PowerShell in the source folder:

```powershell
./build.ps1
```

The script restores and builds both projects, runs the test executable, publishes an x64 Windows application, and creates `artifacts/ruffles_21s-Duplicate-Photo-Finder-Windows-v1.2.1.zip`. Every publish uses a fresh staging folder so old release files cannot leak into the package. It prints the executable folder and ZIP paths at completion.

Optional commands:

```powershell
# Include the .NET runtime in the release ZIP.
./build.ps1 -SelfContained

# Explicitly opt into recycling a newly generated disposable test duplicate.
./build.ps1 -IncludeRecycleTest

# Verify Windows video decoding against a generated sample clip.
./build.ps1 -IncludeVideoTest

# Use a particular SDK installation.
./build.ps1 -DotnetPath 'C:/SDK/dotnet/dotnet.exe'
```

The script uses `.tools/dotnet/dotnet.exe` when that local SDK exists, otherwise `dotnet` on PATH. SDK downloads and .NET runtime packs may require network access on the first build. To build or run tests without creating a package:

```powershell
dotnet build Ruffles21.DuplicatePhotoFinder.sln -c Release
dotnet run --project Ruffles21.DuplicatePhotoFinder.Tests -c Release --no-build
```

Default tests create fixtures only under `artifacts/tests` and render offscreen layout images under `artifacts`. They do not scan personal folders or run actual Recycle Bin operations. The optional recycle test acts only on a file it creates for that run. The Windows GitHub Actions workflow runs the default tests and uploads the Windows ZIP and available layout previews.

## Source layout

| Location | Purpose |
| --- | --- |
| `Ruffles21.DuplicatePhotoFinder/` | WPF application, scan models, previews, and Windows Recycle Bin integration. |
| `Ruffles21.DuplicatePhotoFinder.Tests/` | Fixture-based regression checks and offscreen UI verification. |
| `build.ps1` | Local build, test, and release packaging. |
| `package-source.ps1` | Clean source folder and ZIP for GitHub. |
| `.github/workflows/build.yml` | Windows CI build and artifact upload. |
| `CHANGELOG.md` | Version history. |

Build output, local SDKs, synced reference material, and machine-specific project context are excluded by `.gitignore`. No license has been selected for this source yet.

Implementation references: [WPF control virtualization](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls), [Windows file-operation flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags), and [per-item deletion results](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem).
