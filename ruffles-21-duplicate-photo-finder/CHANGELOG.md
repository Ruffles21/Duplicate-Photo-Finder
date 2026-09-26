# Changelog

## 1.2.1

- Correctly report cancellation during a Windows Recycle Bin callback.
- Refresh active search filters after removing a matching copy from a surviving group.
- Preserve newer Fit or zoom choices while a full-size preview is loading.
- Add regression coverage for these cases.
- Provide a one-paragraph README, separate user/GitHub guides, and a repeatable clean source packaging script.

## 1.2.0

Renamed to **ruffles_21's Duplicate Photo Finder**, with updated application/source names and migration of existing preferences.

### Added

- Independent **Exact duplicates** and **Similar photos & videos** scan options; select one or both. Both share folder discovery and run exact verification before similarity analysis.
- Strict, Balanced, and Broader similarity settings. Similarity results use perceptual photo analysis and codec-dependent sampled video frames, and are review only.
- Search and photo/video group filters, with explicit scope for actions that include hidden selections.
- Cancellation and progress during removal revalidation.
- A solution, consistent formatting settings, SDK selection, a PowerShell build/test/package script, and Windows GitHub Actions CI.

### Improved

- Global Auto Mark preserves manually selected KEEP copies.
- Background previews honor image orientation and avoid unnecessary work for stale selections.
- Removal identity validation and handling of partial completion, cancellation, and failures.
- Report export protects media paths, and persisted settings tolerate invalid or missing values.
- Results labels, empty states, and selection feedback.
- Regression coverage for the added safety and review behavior.

Similarity matching targets visual near-duplicates, rather than identifying the same person or scene. It is a heuristic and cannot guarantee that every similar file is found. Only verified exact duplicates can be recycled through the app.

## 1.1.0

- Folder selection starts scanning immediately.
- Visible scan activity, photo/video counts, and progress while hashing large files.
- Metadata-first discovery avoids reading files that cannot have an exact duplicate by size.
- Broader phone-video, camera RAW, and modern image extension support.
- Clear results for folders with no media or no exact duplicates.

## 1.0.0

- Native Windows exact-duplicate scanning using SHA-256 and byte comparison.
- Protected KEEP copies, explicit removal marking, and Recycle Bin integration.
- Gallery, compact, and list layouts with previews and media metadata.
- Selection Assistant rules, CSV/JSON export, scan pause/stop, and warning logs.
