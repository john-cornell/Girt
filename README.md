<p align="center">
  <img src="src/Girt/Assets/app_icon.png" width="128" alt="Girt icon" />
</p>

<h1 align="center">Girt</h1>

**Girt** is a lightning-fast, modern Git client for Windows built with .NET 8, WPF, and CommunityToolkit.MVVM. It delivers high-performance commit graph visualization, smart branch association tracking, unified diff inspection, and streamlined staging with code-signed installer distribution.

---

## ✨ Features

### 📊 Git Commit DAG & Graph Canvas
- **Multi-Lane DAG Layout Engine**: Accurately computes commit branches, merge parents, bezier curve connection paths, and branch lanes.
- **Hardware-Accelerated UI Virtualization**: Smooth 60 FPS scrolling through 1,000+ commits with zero UI thread stuttering or memory bloat.
- **Search & Column Filters**: Instant debounced filtering by Commit Message, Author, Date, and SHA hash.

### ⑂ Branch Association & Lineage Visualization
- **View Modes**:
  - **All Branches**: View the full repository commit tree.
  - **Hide Unrelated**: Dynamically resolves trunk lineage ($T$) and active branch ancestry ($A \to B$), hiding unrelated feature branches and re-routing graph lanes.
  - **Dim Unrelated**: Softly mutes unrelated branches to 35% opacity while highlighting the active branch lineage vividly.

### 🔄 Repository Synchronization & Branch Tracking
- **F5 Refresh**: Quick one-key repository refresh.
- **⬇ Fetch Remotes**: One-click remote synchronization (`git fetch --all --prune`).
- **✨ New Branches Tracking**: Automatically detects newly created remote or local branches upon fetch/refresh with 1-click checkout.
- **⑂ Recent Branch Switcher**: Click the branch pill to jump to any of the last 10 local branches you checked out (read from the HEAD reflog; deleted branches are skipped).
- **📁 Recent Repository Switcher**: Click the repository pill to switch to a recently opened repository.
- **>_ Git Bash**: One click opens a Git Bash terminal in the current repository.
- **⤵ Update from develop**: One click brings the latest trunk (`develop`, `main`, `master` or `trunk`, whichever exists first) into the current branch. Girt stashes your uncommitted work, fast-forwards the local trunk from origin without checking it out, merges it in, then puts your work back. Right-click any local branch for "Merge latest '<branch>' into current" to do the same with another branch.
- **Branch Sidebar Memory**: The branch filter text and which sections and folders are collapsed are remembered per repository. Filter boxes have a clear (×) button.
- **Big-Repo Performance**: Stays responsive on repositories with tens of thousands of files and thousands of refs (virtualized lists, fsmonitor/untracked cache enabled automatically, refresh skipped when nothing changed).

### 📝 Streamlined Working Tree & Staging
- **Top-to-Bottom Workflow**:
  1. **UNSTAGED CHANGES** (Work in progress) $\to$ Stage down.
  2. **STAGED CHANGES** (Ready to commit) $\to$ Stash or Unstage up.
  3. **COMMIT MESSAGE & BUTTON** $\to$ Single multi-line commit message box directly beneath staged changes.
- **📦 Stash Staged Changes**: Stash staged files (`git stash push --staged`) without touching your unstaged work.
- **📥 Pop Top Stash**: Instant one-click restore of your most recent stash (`git stash pop`).
- **Undo / Soft & Hard Reset**: Safely undo or reset HEAD (`HEAD~1` soft or hard) with commit preview.
- **Multi-Select Stage / Discard**: Select several files (Ctrl/Shift-click) and stage or discard them all at once. Discard asks once for the whole selection and runs as a single git command.

### 🔀 Merge Conflict Editor
- **3-Pane Mine | Result | Theirs**: Resolve conflicts inside Girt. Panes are labelled with real branch names, never "ours/theirs", and "Mine" is always your own work, even during a rebase.
- **Per-Chunk Picks**: Take mine, theirs, both (either order), or type your own text. F8 jumps to the next conflict.
- **Whole-File Picks**: Keep mine / Take theirs resolves a file in one click.
- **Auto-Stage on Save**: A file with every chunk resolved is staged when saved. Girt refuses to stage a file that still has conflict markers.
- **Byte-Exact**: Line endings, BOM and encoding are kept exactly as they were.
- **.orig Cleanup**: After a conflicted merge finishes, Girt offers to send the merge tool's `.orig` backups to the Recycle Bin. Choose Ask / Always delete / Keep in Settings.

### 🎨 Dark / Light Mode
- Modern dark and light themes with persistent settings.
- Integrated unified diff viewer with syntax highlighting for additions, deletions, and hunk headers.

---

## 🚀 Installation & Building

### Pre-built Installer
Download and run **`GirtSetup.exe`** (Code-signed Inno Setup Installer) to install Girt for your user profile (`%LOCALAPPDATA%\Girt`).

### Building from Source

**Requirements:**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (for installer creation)

```powershell
# Clone the repository
git clone https://github.com/john-cornell/Girt.git
cd Girt

# Build Debug / Run
dotnet build
dotnet run --project src\Girt\Girt.csproj

# Run Automated Test Suite
dotnet test
```

### Creating the Signed Installer

To build the Release binary, sign the PE executables, and compile `GirtSetup.exe`:

```cmd
build-setup.bat
```

Output installer will be placed at `installer\Output\GirtSetup.exe`.

---

## 🧪 Testing

Girt includes a comprehensive xUnit test suite covering:
- Graph layout computations and lane slot allocation.
- Unified diff parsing (`DiffParser`).
- Branch association DAG lineage traversal ($T \to A \to B$).
- Staging, stashing, and snapshot diff tracking.

```powershell
dotnet test
```

---

## 📄 License
[Hippocratic License 3.0](LICENSE) — do what you want with it, just don't use it to violate human rights, exploit workers, or harm the environment.
