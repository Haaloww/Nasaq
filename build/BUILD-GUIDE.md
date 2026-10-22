# Nasaq — one-click Windows installer guide

This folder turns the Nasaq project into one file you can double-click to install:

```
D:\Nasaq\Nasaq\dist\Nasaq-Setup-1.0.0.exe
```

The installed app is **self-contained**: people who receive the installer do **not**
need to download .NET. It installs for the current user, so there is **no admin
password prompt** and no UAC window.

You only need to do Step 1 once. **Inno Setup is already installed on this PC**,
so you can start at Step 2. Step 1 is kept for a new PC or a reinstall.

---

## What is already on this PC

| Tool | Status | Needed for |
| --- | --- | --- |
| .NET 8 SDK (8.0.426) | Already installed | Compiling Nasaq |
| Inno Setup 6 (6.7.3) | **Installed** | Wrapping the app into a setup .exe |
| FFmpeg | Downloaded automatically by the build | Audio and video conversion |

---

## Step 1 — Install Inno Setup 6 (already done here)

Inno Setup is the free program that creates the final setup .exe. It is only
needed on **your** PC to build; people who install Nasaq do not need it.

**This PC already has Inno Setup 6.7.3 installed, so skip to Step 2.** These are
the mouse-only instructions for a new PC, or if you ever uninstall it:

1. Open your web browser and go to:

   **https://jrsoftware.org/isdl.php**

2. Click the big **Download Inno Setup** button near the top of the page.
3. If the browser asks, click **Save**. When the download finishes, click the
   downloaded file (usually at the bottom of the browser, or in your **Downloads**
   folder). Its name looks like `innosetup-6.x.x.exe`.
4. If Windows asks **“Do you want to allow this app to make changes?”**, click **Yes**.
5. On the welcome page, click **Next**.
6. Select **I accept the agreement**, then click **Next**.
7. Click **Next** on the destination page (the default folder is fine).
8. Click **Next** on the Start Menu page.
9. Click **Next** on the additional tasks page.
10. Click **Install** and wait for it to finish.
11. Click **Finish**. (You can untick “Launch Inno Setup” first.)

Nothing needs to be typed. The default install location is detected
automatically by the build script.

> **Only if the .NET 8 SDK is ever missing:** go to
> https://dotnet.microsoft.com/download/dotnet/8.0, click the **x64** button next
> to **.NET 8.0 SDK**, run the downloaded file, click **Install**, then **Close**.

---

## Step 2 — Build the installer (about 2–5 minutes)

1. Open **File Explorer**.
2. Go to this folder:

   ```
   D:\Nasaq\Nasaq\build
   ```

3. Double-click **build.cmd**.
4. A black window opens and shows its progress. The first build downloads
   FFmpeg (about 115 MB), so it takes longer. Later builds reuse the cached copy.
5. Wait until it says **Build finished successfully**.
6. Press any key or close the window.

The finished installer appears here:

```
D:\Nasaq\Nasaq\dist\Nasaq-Setup-1.0.0.exe
```

### What the build does, in plain English

1. Copies Nasaq into `build\publish` together with the .NET runtime.
2. Downloads FFmpeg once, caches it in `build\cache`, and puts it in
   `publish\tools` so audio and video work immediately.
3. Runs Nasaq's own self-test (23 checks) against the published copy.
4. Uses Inno Setup to wrap everything into the single setup .exe in `dist`.

If any step fails, the window stops and prints the reason instead of producing a
broken installer.

---

## Step 3 — Test the installer (optional but recommended)

1. Open **File Explorer** and go to `D:\Nasaq\Nasaq\dist`.
2. Double-click **Nasaq-Setup-1.0.0.exe**.
3. If you see **“Windows protected your PC”**, click **More info**, then
   **Run anyway**. This appears because the installer is not code-signed; it does
   not mean anything is wrong.
4. Click **Next**, then **Next**, then **Install**.
5. Click **Finish**. Nasaq opens.

It installs to:

```
%LocalAppData%\Programs\Nasaq
```

No administrator prompt appears.

---

## Smaller installer without FFmpeg

If you want a much smaller download and your users already have FFmpeg:

1. Go to `D:\Nasaq\Nasaq\build`.
2. Double-click **build-lite.cmd** instead of `build.cmd`.

Image and document conversion still work out of the box. Audio and video
conversion need FFmpeg, which Nasaq finds automatically in the app folder,
its `tools` folder, `PATH`, or WinGet.

---

## Rebuilding after code changes

1. Change the code as usual.
2. Double-click `D:\Nasaq\Nasaq\build\build.cmd` again.
3. Send the new file from `dist`.

The build always starts from a clean `publish` folder, so no stale files are
carried over. Old installers in `dist` are left alone; delete them whenever you
like.

To change the version number, edit this line in `Nasaq.csproj`:

```xml
<Version>1.0.0</Version>
```

The installer name and the version shown in Windows follow it automatically.

---

## Sending the app to other people

Send only this file:

```
dist\Nasaq-Setup-1.0.0.exe
```

They double-click it and follow the wizard. They do not need .NET, Inno Setup,
or administrator rights. The full build also bundles FFmpeg; the lite build
requires FFmpeg for audio and video.

Because the installer is not code-signed, Windows SmartScreen may show the
“More info → Run anyway” warning on their PC too. Code signing removes that
warning but requires a paid certificate; it is not needed for the app to work.

---

## Publishing on GitHub Releases

The project includes an automated release pipeline at
`.github/workflows/release.yml`. Pushing a tag such as `v1.0.0` makes GitHub
build the installer on a Windows machine, run the self-test, and attach
`Nasaq-Setup-1.0.0.exe` to a GitHub Release. No secrets are required; GitHub
provides the token automatically.

### One-time: put the project on GitHub

The project is not a Git repository yet. Choose one of these routes.

**Mouse-only route (GitHub Desktop)**

1. Download GitHub Desktop from https://desktop.github.com/download/ and install
   it (double-click the file, then **Next** → **Install** → **Finish**).
2. Open GitHub Desktop and sign in to your GitHub account.
3. Click **File** → **Add local repository**.
4. Choose `D:\Nasaq\Nasaq`.
5. GitHub Desktop says the folder is not a Git repository. Click the
   **create a repository** link.
6. Name it `Nasaq`, keep the suggested path, and click **Create repository**.
7. In the left pane, all project files appear as changes. Type `Initial commit`
   in the summary box and click **Commit to main**.
8. Click **Publish repository** at the top, choose public or private, and click
   **Publish repository**.

The `.gitignore` file keeps `bin`, `obj`, `dist`, and the large build folders out
of the repository, so the upload stays small.

**Command-line route**

1. Create an empty repository on GitHub. Do not add a README, `.gitignore`, or
   license there.
2. Open a terminal in `D:\Nasaq\Nasaq` and run:

```bat
git init
git config user.name "Your Name"
git config user.email "you@example.com"
git add .
git commit -m "Initial commit"
git branch -M main
git remote add origin https://github.com/YOUR-USER/Nasaq.git
git push -u origin main
```

### Every release (automatic, recommended)

1. Bump the version in `Nasaq.csproj`, for example from `1.0.0` to `1.1.0`:

   ```xml
   <Version>1.1.0</Version>
   ```

2. Commit and push the change.
3. Double-click **`build\release.cmd`**.
   It reads the version, pushes the current branch, creates tag `v1.1.0`, and
   pushes the tag.
4. Open your repository on GitHub and click the **Actions** tab. The
   **Build and Release** run takes about 5–10 minutes.
5. Open the **Releases** page. `Nasaq-Setup-1.1.0.exe` is attached to the new
   release.

The workflow checks that the tag matches `<Version>` exactly. A tag `v1.1.0`
requires `<Version>1.1.0</Version>`; otherwise the run stops with a clear error.

**Alternative ways to create the tag**

- **GitHub Desktop:** open **History**, right-click the latest commit, click
  **Create Tag**, type `v1.1.0`, then click **Push origin**.
- **GitHub website:** open **Releases** → **Draft a new release**, type a new tag
  `v1.1.0`, and click **Publish release**. The workflow will attach the installer
  to that release.

**Manual upload (no GitHub Actions)**

If you do not want to use Actions, you can publish the file by hand:

1. Open `D:\Nasaq\Nasaq\dist`.
2. In your repository, open **Releases** → **Draft a new release**.
3. Create tag `v1.0.0`.
4. Drag `Nasaq-Setup-1.0.0.exe` onto the release page.
5. Click **Publish release**.

**Test the workflow without creating a release**

Open **Actions** → **Build and Release** → **Run workflow**. This builds the
installer and uploads it as a downloadable workflow artifact, but does not create
a release.

**What the workflow does**

- Runs on `windows-latest` with the .NET 8 SDK.
- Uses Inno Setup 6 (normally preinstalled; Chocolatey is the fallback).
- Caches the FFmpeg download between runs.
- Runs `build\build.ps1`, including the 23-check self-test.
- Uploads the installer as a workflow artifact and, for tags, to the Release.
- Prints the installer size and SHA-256 in the run summary.

> The installer remains unsigned, so SmartScreen may show the “More info →
> Run anyway” warning on download. Removing that warning requires a code-signing
> certificate and repository secrets; the workflow does not include signing.

---

## Uninstalling

1. Open **Settings**.
2. Go to **Apps** → **Installed apps** (Windows 11) or **Apps & features** (Windows 10).
3. Find **Nasaq** and click **Uninstall**.
4. The uninstaller asks whether to also delete your settings, logs and downloaded
   tools. Choose **No** to keep them for a future reinstall.

Your settings live in `%APPDATA%\Nasaq` and are never stored in the install
folder, so upgrades do not lose them.

---

## If something goes wrong

| What you see | What to do |
| --- | --- |
| “Inno Setup 6 was not found” | Install it using Step 1, then double-click `build.cmd` again. |
| The black window closes immediately | Double-click `build.cmd` again and read the last lines. |
| The self-test fails | Open `build\selftest\selftest-report.txt`; the failing check is marked `FAIL`. |
| “Windows protected your PC” | Click **More info** → **Run anyway**. |
| Antivirus flags `ffmpeg.exe` | It is the standard FFmpeg build from gyan.dev; allow it, or use `build-lite.cmd`. |
| FFmpeg download fails | The app still builds without it. Run `build.cmd` again later, or use `build-lite.cmd`. |

---

## Verified on this PC

The current `dist\Nasaq-Setup-1.0.0.exe` (102.7 MB) was test-installed and
uninstalled on 2026-10-23:

- 251 files installed, including `tools\ffmpeg.exe` (FFmpeg 9.0.2).
- The installed app passed all **23 self-test checks**, including PDF rendering.
- The Start Menu shortcut and the Windows uninstall entry were created and
  removed cleanly.
- Settings in `%APPDATA%\Nasaq` were preserved after uninstall.

---

## Files in this folder

| File | Purpose |
| --- | --- |
| `build.cmd` | **Double-click this to build.** Full installer with FFmpeg. |
| `build-lite.cmd` | Double-click for a smaller installer without FFmpeg. |
| `release.cmd` | Double-click to tag the current version and start a GitHub Release build. |
| `build.ps1` | The actual build logic (publish, FFmpeg, self-test, Inno Setup). |
| `installer.iss` | Inno Setup script: shortcuts, uninstaller, upgrade identity. |
| `FFmpeg-NOTICE.txt` | License notice copied next to the bundled FFmpeg. |
| `publish/` | Generated: the self-contained app folder. |
| `cache/` | Generated: the downloaded FFmpeg archive, reused on later builds. |
| `selftest/` | Generated: self-test outputs and `selftest-report.txt`. |
| `..\dist\` | Generated: the final `Nasaq-Setup-<version>.exe`. |
| `..\.github\workflows\release.yml` | GitHub Actions pipeline that builds and publishes releases. |
