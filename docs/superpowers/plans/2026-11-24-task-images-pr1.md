# Images in Tasks (PR 1: paste and show) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Petre pastes or drops images into a task's comment box and description editor, sees them as thumbnails in the
tasks panel, and a Claude session can read each comment's text and its image paths from `task list`.

**Architecture:** Images are PNG/JPEG files under `<state dir>\task-images\<task id>\`, referenced from the markdown by
an image line with a path relative to the state dir. A Core helper (`TaskImages`) splits markdown into text and image
paths, joins them back, and expands relative paths to absolute ones for sessions. The panel renders the text through
`MarkdownBlocks` as today and the images as a separate thumbnail row, so comment folding (Ruling 63) is untouched.
App-side `TaskImageFiles` turns clipboard pictures and files into pending images and writes them on save.

**Tech Stack:** .NET 10, WPF, CSharpFunctionalExtensions, xUnit (Deskbar.Core.Tests, Deskbar.Windows.Tests).

**Spec:** `docs/superpowers/specs/2026-11-24-task-images-design.md` (PR 1 section).

## Global Constraints

- No new NuGet package; no WebView.
- Stored image line: `![image](task-images/<task id>/<yyyyMMdd-HHmmss-fff>[-n].png)` (`.jpg` for a JPEG file kept as is); relative to the state dir, never absolute.
- Only files inside `<state dir>\task-images\` with extension `.png`, `.jpg` or `.jpeg`, and no `..` segment, are rendered or opened.
- A picture wider than 4096 px is scaled down to 4096 px wide before saving.
- Files are written when a comment or description is SAVED, never on paste; deleting a task deletes its folder; closing keeps it.
- Everything a session reads (`task list`, SessionBriefing) shows absolute paths.
- Windows tests run on the hidden desktop: `pwsh scripts/test-hidden.ps1` (filtered: `-Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~<Class>'`). Core tests: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`.
- C# style per CLAUDE.md: expression bodies, `var`, readonly collections, Result for expected failures, ample comments that carry the why.

## Review Focus

1. A comment of images only (empty text) posts, renders and lists; Enter with neither text nor images does nothing. (Task 6)
2. A redraw while images are pending (a session's status line changes) keeps them, like the text draft. (Task 6)
3. A missing or unreadable image file shows the line's text in the row, never throws, and a later redraw tries again (no cached failure). (Task 5)
4. A session's comment naming `task-images/../state.json` or `C:\Windows\x.png` is plain text, not a thumbnail, and nothing opens. (Tasks 1, 5)
5. Ctrl+V with only text on the clipboard still pastes text into the box. (Task 6)

---

### Task 1: TaskImages, the markdown side (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskImages.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskImagesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record TaskMarkdown(string Text, IReadOnlyList<string> Images)`
  - `public static class TaskImages`
    - `const string Folder = "task-images"`
    - `TaskMarkdown Split(string markdown)`: image lines out, the rest as text (trimmed of trailing blank lines). An image line is a whole line `![alt](path)` or `![alt](<path>)` whose path passes `IsOurs`.
    - `string Join(string text, IEnumerable<string> images)`: text, a blank line, then one `![image](path)` per image; no blank line when text is empty; text alone when there are no images.
    - `bool IsOurs(string relative)`: starts with `task-images/`, no `..` segment, no backslash, extension png/jpg/jpeg (case-insensitive).
    - `string RelativeFor(Guid taskId, DateTimeOffset at, int index, bool jpeg)`: `task-images/<id>/<yyyyMMdd-HHmmss-fff>.png`, with `-<index>` before the extension when index > 0.
    - `Maybe<string> Resolve(string relative, string stateDir)`: the absolute path when `IsOurs`, else none.
    - `string Expand(string markdown, string stateDir)`: every image line of ours rewritten to `![image](<absolute path>)`.

- [ ] **Step 1: Write the failing tests** in `TaskImagesTests`:
  - `Split_takes_image_lines_out`: `"Look\n\n![image](task-images/abc/1.png)"` gives Text `"Look"`, Images `["task-images/abc/1.png"]`.
  - `Split_keeps_foreign_and_inline_images_as_text`: `"see ![x](C:/Windows/a.png)"`, `"![x](task-images/../state.json)"` and `"![x](https://e.com/a.png)"` each give themselves as Text and no Images.
  - `Join_then_Split_round_trips` for text+2 images, images only (`Join("", [p])` is `"![image](p)"`), text only.
  - `RelativeFor_names_by_time_and_index`: `RelativeFor(id, 2026-11-24T05:06:07.089Z, 0, false)` is `$"task-images/{id}/20261124-050607-089.png"`; index 2 and jpeg give `...-089-2.jpg`.
  - `Expand_makes_paths_absolute`: with stateDir `C:\S`, `"a\n![image](task-images/t/1.png)"` becomes `"a\n![image](<C:\S\task-images\t\1.png>)"`.
  - `Resolve_refuses_outside_paths`: `..`, backslashes, `.exe`, a path not under `task-images/` give none.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~TaskImagesTests`: FAIL (type missing).
- [ ] **Step 3: Implement `TaskImages`** in Core, one compiled regex for the image line, header comment pointing at the spec and saying why paths are relative.
- [ ] **Step 4: Run** the same filter: PASS.
- [ ] **Step 5: Commit** `feat: TaskImages reads and writes image lines in task markdown`.

### Task 2: Sessions read comments and image paths (Core)

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (constructor, `Delete`)
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs:196-225` (`Describe`)
- Modify: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (`Start`, `Prompt`, `Detail`)
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs:146,165` (pass the expander)
- Modify: `src/Deskbar.Core/Tasks/TaskViews.cs:165` (`PlainFirstLine`)
- Modify: `src/Deskbar.App/App.xaml.cs:1403` (pass `stateDir`)
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskServiceTests.cs`, `RemoteControlTaskTests.cs`, `Tasks/SessionBriefingTests.cs`, `Tasks/TaskViewsTests.cs`

**Interfaces:**
- Consumes: `TaskImages.Expand`, `TaskImages.Split`.
- Produces:
  - `TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null, string? filesRoot = null)`
  - `string TaskService.ExpandImages(string markdown)`: `TaskImages.Expand` against `filesRoot`, identity when it is null.
  - `TaskService.Delete` also deletes `<filesRoot>\task-images\<id>` (missing folder is fine; an IO failure is traced, the delete still succeeds).
  - `SessionBriefing.Start(ClaudeHookEvent e, Maybe<TaskItem> own, IReadOnlyList<TaskItem> offered, Func<string, string>? expand = null)` and `Prompt(..., Func<string, string>? expand = null)`.

- [ ] **Step 1: Write the failing tests:**
  - TaskService: `Delete_removes_the_tasks_image_folder` (temp root holding `task-images/<id>/a.png`; after Delete the folder is gone) and `Delete_without_a_folder_succeeds`.
  - RemoteControl: `Task_list_thread_carries_each_comments_markdown_with_absolute_image_paths` (thread entry has `markdown` equal to the expanded text); `description` is expanded the same way.
  - SessionBriefing: `Description_images_follow_the_cut_whole`: a 300-character description plus one image line gives the cut text and then `images in the description: <absolute path>; read them with your Read tool`; a description of images only gives no quoted text, just the images sentence.
  - TaskViews: `PlainFirstLine_skips_image_lines` (`"![image](task-images/t/1.png)\nFix it"` gives `"Fix it"`) and `PlainFirstLine_of_images_only_is_image` (gives `"image"`).
- [ ] **Step 2: Run** the four test classes: FAIL.
- [ ] **Step 3: Implement.** RemoteControl reads `service.ExpandImages`; replace the comment "The comments' text is the window's, as before" with why the text is now listed (sessions read Petre's comments; spec). ClaudeHook passes `service.ExpandImages` to both briefing calls. App passes `stateDir` as `filesRoot`.
- [ ] **Step 4: Run** `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`: PASS.
- [ ] **Step 5: Commit** `feat: task list and the session briefing carry comment text and image paths`.

### Task 3: TaskImageFiles, pictures in and out of files (App)

**Files:**
- Create: `src/Deskbar.App/TaskImageFiles.cs`
- Modify: `src/Deskbar.App/ReportImages.cs` (make `Decode`, `Encode`, `Scaled`, `Thumbnail`, `LooksLikeImage` reachable as `internal`; no behaviour change)
- Test: `tests/Deskbar.Windows.Tests/TaskImageFilesTests.cs`

**Interfaces:**
- Consumes: `TaskImages.RelativeFor`, `TaskImages.Resolve`, `ReportImages` internals.
- Produces (`static class TaskImageFiles`):
  - `sealed record Pending(byte[] Bytes, bool Jpeg, ImageSource Thumbnail)`
  - `Result<Pending> FromBitmap(BitmapSource image)`: PNG, scaled to 4096 px wide when wider.
  - `Result<Pending> FromFile(string path)`: PNG or JPEG bytes kept as they are when the picture is at most 4096 px wide; anything else decoded and handled as `FromBitmap`.
  - `IReadOnlyList<Result<Pending>> FromData(IDataObject data)`: image files in a file drop list (by `LooksLikeImage`), else a bitmap, else empty. Used for Ctrl+V (`Clipboard.GetDataObject()`, wrapped in `Result.Try`: the clipboard throws while another program holds it) and for drops.
  - `Result<IReadOnlyList<string>> Save(string stateDir, Guid taskId, IReadOnlyList<Pending> images, DateTimeOffset at)`: writes each file (creating the folder), returns the relative paths in order; on any failure deletes what it wrote and fails.
  - `Maybe<ImageSource> Thumbnail(string stateDir, string relative, int decodeWidth)`: decoded at that width, frozen, cached by absolute path and last-write time. A failure is NOT cached (CLAUDE.md trap: never cache a lookup failure that can succeed later).

- [ ] **Step 1: Write the failing tests:**
  - `FromBitmap_scales_a_wide_picture_to_4096` (a 5000x10 BitmapSource gives a PNG 4096 wide).
  - `FromFile_keeps_a_small_png_byte_for_byte`.
  - `Save_writes_files_and_returns_relative_paths` (temp state dir; two images; files exist at `TaskImages.Resolve`; names carry `-1` for the second).
  - `Thumbnail_of_a_missing_file_is_none_and_retries` (none; then write the file; then some).
- [ ] **Step 2: Run** `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~TaskImageFilesTests'`: FAIL.
- [ ] **Step 3: Implement** with a header comment naming the spec and the 4096 rule's reason (a session is not handed a huge image).
- [ ] **Step 4: Run** the same: PASS.
- [ ] **Step 5: Commit** `feat: TaskImageFiles turns pasted pictures into task image files`.

### Task 4: Petre may add a Read rule for the images (App) — ONLY IF Petre says yes

Petre was asked on 2026-11-24 whether Deskbar may add one allow rule to `~/.claude/settings.json`. Skip this task if he
says no; then Task 7's docs say a default-mode session asks before reading an image.

**Files:**
- Modify: `src/Deskbar.App/ClaudeHooks.cs` (`Sync`, `Apply`)
- Test: `tests/Deskbar.Windows.Tests/ClaudeSkillTests.cs` (where ClaudeHooks.Sync is tested today)

**Interfaces:**
- Produces: `ClaudeHooks.Sync(bool on, bool tasks, string settingsPath, string askCommand, string taskCommand, string? imagesRule = null)`; `imagesRule` is `Read(//c/Users/<user>/AppData/Roaming/Deskbar/task-images/**)` built from the state dir (drive letter lowercased, slashes forward, `//` prefix as Claude Code's absolute-path rule syntax).

- [ ] **Step 1: Write the failing tests:** with `tasks` on, `permissions.allow` gains exactly one rule ending `/task-images/**)`; Sync again changes nothing (file untouched); with `tasks` off the rule goes and every other allow entry stays; a rule for an older state dir path (Deskbar's own, recognised by `/Deskbar/task-images/**)`) is replaced.
- [ ] **Step 2: Run**: FAIL. **Step 3: Implement** inside the same parse/backup/temp-move path, with the header's list of promises extended to the one permission entry. **Step 4: Run**: PASS. **Step 5: Commit** `feat: Claude Code sessions may read task images without a prompt`.

### Task 5: Thumbnails in the thread and the description (App)

**Files:**
- Create: `src/Deskbar.App/TaskImageRow.cs`
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs:1081` (`DescriptionBlock`), `:1343` (comment body)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`

**Interfaces:**
- Consumes: `TaskImages.Split`, `TaskImageFiles.Thumbnail`.
- Produces: `static class TaskImageRow` with `FrameworkElement Of(string stateDir, IReadOnlyList<string> images, Action<string> open)`: a WrapPanel of thumbnails, each at most 160 DIP high and the row's width, aspect kept, thin border, tooltip "Open full size", `Tag = "task-image"`; a click calls `open(absolutePath)`. An image whose thumbnail is none shows its relative path as dim text. `TasksWindow` gets `internal Action<string> OpenImage` defaulting to a shell open (traced on failure, as `MarkdownBlocks.OpenInBrowser`), and a `StateDir` property set by its creator (App) so tests point it at a temp folder.

- [ ] **Step 1: Write the failing tests:** a comment `"Look\n\n![image](task-images/<id>/1.png)"` (file written in a temp state dir) draws its body without the image line and one element tagged `task-image` after it, outside the clipped body; clicking it calls `OpenImage` with the absolute path; the same comment with the file missing shows the path text; an expanded task whose description has an image shows the row under `DescriptionBlock`.
- [ ] **Step 2: Run** hidden tests filtered to TasksBoardTests: FAIL. **Step 3: Implement**: the body renders `Split(markdown).Text`; `IsLong` keeps judging the text only; the row is added after the body (and after the description block), so "Show more" folds text, never images. Thumbnails load at 320 px decode width. **Step 4: Run**: PASS. **Step 5: Commit** `feat: the tasks panel shows images in comments and descriptions`.

### Task 6: Paste and drop in the comment box and the description editor (App)

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (`CommentBox` ~1480-1545, `Post` ~1575, `DescriptionBox` and the editor's save, ~1620+)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`

**Interfaces:**
- Consumes: `TaskImageFiles.FromData`, `TaskImageFiles.Save`, `TaskImages.Join`, `TaskImages.Split`, `TaskImageRow`.
- Produces (test hooks): `internal bool PasteImages(Guid id, IDataObject data)` (comment box), `internal IReadOnlyList<TaskImageFiles.Pending> PendingImages(Guid id)`, and `TypeComment` unchanged; for the editor, `internal bool PasteIntoDescription(IDataObject data)`.

- [ ] **Step 1: Write the failing tests:**
  - `Pasted_image_posts_with_the_text`: PasteImages with a bitmap data object, TypeComment "See this" + Enter: the posted markdown is `Join("See this", [relative])` and the file exists.
  - `Image_alone_posts` (empty text, one image, Enter posts).
  - `Text_only_clipboard_is_left_to_the_box` (PasteImages with a text-only data object returns false, nothing pending).
  - `Pending_images_survive_a_redraw` (paste, trigger a board change, PendingImages still has one).
  - `Removing_a_pending_image_drops_it` (the row's x).
  - `Description_editor_keeps_existing_images_and_adds_pasted_ones`: a description `"Brief\n\n![image](task-images/<id>/1.png)"` opens in the editor as text `"Brief"` with one thumbnail; paste one more; Enter saves `Join("Brief", [old, new])`; Esc saves nothing and writes no file.
- [ ] **Step 2: Run**: FAIL.
- [ ] **Step 3: Implement.** Ctrl+V in `PreviewKeyDown` of both boxes calls the paste first and only marks the key handled when images were taken (text paste stays the TextBox's). `AllowDrop` plus `PreviewDragOver`/`Drop` on both boxes. Pending images live in a `Dictionary<Guid, List<Pending>>` beside `drafts`. `Post` saves files first (`Save` with `now`), then comments; a refused comment deletes the files it just wrote and keeps the pending images. The hint becomes "Markdown works. Paste or drop images. Enter adds, Shift+Enter starts a new line." The editor box shows `Split(description).Text`; save joins the kept and new images.
- [ ] **Step 4: Run** hidden Windows tests in full, and Core tests: PASS.
- [ ] **Step 5: Commit** `feat: paste or drop images into a task's comment or description`.

### Task 7: Docs, manual steps, release notes

**Files:**
- Modify: `docs/features/tasks.md` (the tasks window section: pasting, thumbnails, click to open, where files live, deleted with the task; the scripts section: `task list` thread now carries `markdown`)
- Modify: `docs/superpowers/notes/manual-test-script.md` (steps: Win+Shift+S then Ctrl+V into a comment; drop a PNG file; image-only comment; description image; open full size; delete a task removes its folder; a session reads the path from `task list`)
- Modify: `docs/superpowers/specs/2026-11-24-task-images-design.md` only if the build departed from it (images render as a row after the text rather than inside MarkdownBlocks: say so and why, folding)

- [ ] **Step 1:** Write the docs; name the clip to re-film (`tasks-window`, with a pasted screenshot in a comment) in the front matter's comment.
- [ ] **Step 2:** `dotnet build Deskbar.sln -c Release -warnaserror`: 0 warnings, 0 errors. Full Core tests and hidden Windows tests: green.
- [ ] **Step 3: Commit** `docs: images in tasks`.
- [ ] **Step 4:** Push, open the PR with a `## Release notes` section for users ("Paste a screenshot into a task's comment or description with Ctrl+V, or drop an image file on it..."), arm the base watch from CLAUDE.md, and watch the PR's CI to green.
