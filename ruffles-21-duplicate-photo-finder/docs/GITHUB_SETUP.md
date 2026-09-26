# Add this project to GitHub

1. Extract the GitHub source ZIP. Open the folder containing `README.md`, `build.ps1`, and `Ruffles21.DuplicatePhotoFinder.sln`.
2. Create a repository in your GitHub account. Suggested name: `ruffles-21-duplicate-photo-finder`. Leave the initial README, license, and gitignore options unchecked because this package already includes the project files; you can choose a license separately.
3. Add the extracted folder as a local repository in GitHub Desktop, commit the files, then publish it to your account. Choose public or private as you prefer. Alternatively, use the commands below with your actual repository URL.

```powershell
git init -b main
git add .
git commit -m "Add duplicate photo finder"
git remote add origin https://github.com/YOUR-USERNAME/YOUR-REPOSITORY.git
git push -u origin main
```

Commit the source files, including the `.github` folder and the dotfiles. The included `.gitignore` excludes generated `bin`, `obj`, `artifacts`, and local SDK folders. Do not upload the source ZIP itself as a substitute for its extracted contents.

The Windows workflow builds and tests the app after a push or pull request and attaches its Windows package as a workflow artifact. To provide a direct app download, create a GitHub Release and attach the Windows app ZIP to that release. Build a package with the runtime included using `./build.ps1 -SelfContained`.

To recreate the clean source package, run `./package-source.ps1`. This creates a fresh folder and ZIP under `artifacts`; it does not publish anything. No license is selected in this package.
