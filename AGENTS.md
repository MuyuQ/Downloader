# AGENTS.md - WYDownloader Project Guide

This document provides essential information for AI coding agents working on this repository.

## Project Overview

WYDownloader is a Windows desktop application built with WPF (.NET Framework 4.7.2) that provides direct-link file downloading with ZIP auto-extraction capabilities. The application features a modern Material Design UI and supports extensive configuration options.

## Build Commands

### Build the Solution
```bash
# Build Debug version
msbuild WYDownloader.sln /p:Configuration=Debug

# Build Release version
msbuild WYDownloader.sln /p:Configuration=Release

# Clean and rebuild
msbuild WYDownloader.sln /t:Rebuild /p:Configuration=Release
```

### Run the Application
```bash
# After building, run from:
.\bin\Debug\WYDownloader.exe
.\bin\Release\WYDownloader.exe
```

### Build using Visual Studio
- Open `WYDownloader.sln` in Visual Studio 2019/2022
- Press F5 to debug or Ctrl+F5 to run without debugging
- Build menu → Build Solution (Ctrl+Shift+B)

## Testing

This project does not currently have automated tests. Manual testing is required:
1. Build and run the application
2. Test download functionality with various URLs
3. Test ZIP extraction feature
4. Test pause/resume functionality
5. Verify configuration loading from `Resources/Config/config.ini`
6. Test background image rotation
7. Test custom download path

## Linting and Type Checking

No automated linting tools are configured. Follow the code style guidelines below.

## Project Structure

```
WYDownloader/
├── App.xaml / App.xaml.cs      # Application entry point
├── MainWindow.xaml             # UI layout (XAML)
├── MainWindow.xaml.cs          # UI logic
├── Core/                       # Core functionality layer
│   ├── ConfigManager.cs        # Configuration parsing and management
│   └── DownloadManager.cs      # Download management and logic
├── Resources/                  # Resource files
│   ├── Images/
│   │   ├── BG.JPG              # Default background image
│   │   └── background/         # Custom background images directory
│   ├── Config/
│   │   └── config.ini          # Configuration file
│   └── Icons/
│       └── ico.ico             # Application icon
├── Properties/                 # Assembly info and resources
└── bin/ obj/                   # Build outputs
```

## Dependencies

- MaterialDesignThemes (4.9.0)
- MaterialDesignColors (2.1.4)

## Code Style Guidelines

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Namespaces | PascalCase | `WYDownloader`, `WYDownloader.Core` |
| Classes | PascalCase | `MainWindow`, `ConfigManager`, `DownloadManager` |
| Public Methods | PascalCase | `LoadConfiguration()`, `GetDownloadUrl()` |
| Private Methods | PascalCase | `LoadBackgroundImageFromResource()` |
| Private Fields | camelCase | `httpClient`, `configManager`, `isDownloading` |
| Constants | PascalCase | `DownloadBufferSize`, `ProgressUpdateInterval` |
| Static Readonly | PascalCase | `ProgressUpdateInterval` |
| Parameters | camelCase | `sender`, `e`, `url`, `savePath` |
| Local Variables | camelCase | `fileName`, `totalBytes`, `buffer` |

### Imports and Using Statements

Group using statements by category, ordered alphabetically:
```csharp
// System namespaces first (alphabetically)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

// Then WPF namespaces
using System.Windows;
using System.Windows.Controls;

// Then third-party namespaces
using MaterialDesignThemes.Wpf;

// Then project namespaces
using WYDownloader.Core;

// Aliases last
using MessageBox = System.Windows.MessageBox;
```

### Code Formatting

- **Indentation**: 4 spaces (no tabs)
- **Braces**: Opening brace on same line
- **Max line length**: Keep lines readable, generally under 120 characters
- **Blank lines**: 
  - One blank line between method groups
  - One blank line between logical sections within methods
  - No blank lines after opening brace or before closing brace

### Types and Nullability

- Use `var` when type is obvious from context
- Use explicit types when type is not clear
- Always check for null before using reference types in public methods
- Use `string.IsNullOrEmpty()` and `string.IsNullOrWhiteSpace()` for string validation

### Error Handling

- Use try-catch blocks for operations that can fail (file I/O, network, parsing)
- Provide user-friendly error messages via `MessageBox.Show()`
- Log errors to debug output when appropriate: `System.Diagnostics.Debug.WriteLine()`
- Always dispose resources with `using` statements or explicit `Dispose()` calls
- Use `async/await` pattern for I/O-bound operations

```csharp
try
{
    // Operation that may fail
}
catch (Exception ex)
{
    MessageBox.Show("操作失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
}
```

### Async/Await Guidelines

- Use `async Task` for async methods that don't return a value
- Use `async Task<T>` for async methods that return a value
- Use `async void` only for event handlers
- Always pass `CancellationToken` to async operations
- Use `ConfigureAwait(false)` in library code (not needed in WPF UI code)

### WPF-Specific Patterns

- Use `Dispatcher.InvokeAsync()` for UI updates from background threads
- Event handlers follow `ObjectName_EventName` pattern: `BtnDownload_Click`
- XAML elements use descriptive names: `btnDownload`, `lblProgress`, `cmbDownloadLinks`
- Use `DependencyProperty` for bindable properties in custom controls
- Material Design controls are used throughout the application

### Constants and Magic Numbers

Define constants at class level:
```csharp
private const int DownloadBufferSize = 131072;
private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(300);
```

### Configuration

- `config.ini` contains runtime configuration (downloads, announcements, UI settings)
- `ConfigManager` class handles configuration parsing
- Supports both local file and embedded resource fallback
- Sections: `[Settings]`, `[Servers]`, `[Downloads]`, `[Announcement]`, `[UI]`

### New Configuration Options

The application now supports:
- **EnableResume**: Enable/disable resume download feature
- **DefaultDownloadPath**: Custom default download path
- **BackgroundImages**: List of background images for rotation (separated by `|`)

### Comments and Documentation

- Use XML documentation for public classes and methods:
```csharp
/// <summary>
/// Downloads a file from the specified URL with progress tracking.
/// </summary>
/// <param name="url">The URL to download from.</param>
/// <param name="filePath">The local file path to save to.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
```

- Chinese comments are acceptable for this project
- Add inline comments for complex logic
- Avoid redundant comments that simply restate the code

### File Organization

- One class per file (class name matches filename)
- XAML code-behind files share the base name with their XAML files
- Keep files focused on a single responsibility
- Core functionality is separated into the `Core/` directory

## Important Notes

- This is a Chinese-language project (UI text and comments may be in Chinese)
- Target framework: .NET Framework 4.7.2 (Windows 7 SP1+)
- The application uses `HttpClient` for downloads with configurable timeout
- ZIP extraction uses `System.IO.Compression`
- Background images and config are embedded as resources
- Support for download pause/resume and progress tracking
- Configuration supports multiple mirror URLs for download fallback
- Material Design is used for modern UI styling