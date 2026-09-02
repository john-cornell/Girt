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

### 📝 Streamlined Working Tree & Staging
- **Top-to-Bottom Workflow**:
  1. **UNSTAGED CHANGES** (Work in progress) $\to$ Stage down.
  2. **STAGED CHANGES** (Ready to commit) $\to$ Stash or Unstage up.
  3. **COMMIT MESSAGE & BUTTON** $\to$ Single multi-line commit message box directly beneath staged changes.
- **📦 Stash Staged Changes**: Stash staged files (`git stash push --staged`) without touching your unstaged work.
- **📥 Pop Top Stash**: Instant one-click restore of your most recent stash (`git stash pop`).
- **Undo / Soft & Hard Reset**: Safely undo or reset HEAD (`HEAD~1` soft or hard) with commit preview.

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
