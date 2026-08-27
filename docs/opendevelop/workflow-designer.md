# OpenDevelop Workflow Designer

This document is owned by the CoreWF repository because the designer, its out-of-process host,
its tests, and its CoreWF runtime dependency are versioned here under
`src/OpenDevelop.AddIn/`. OpenDevelop supplies the IDE shell and Addin SDK only. The installed
integration target is `/Applications/OpenDevelop.app` on macOS; set `OPENDEVELOP_APP_PATH` to
test another installed application binary.

Tracking note for [orosandrei/Rehosted-Workflow-Designer#21](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/21)
("Integration with OpenDevelop and LibreWPF"), opened by this repo's maintainer against their own
WF rehosting sample project. That issue also asks to fold in
[#19](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/19) (.NET 8 migration) and
[#18](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/18) (step/step-in/step-over
debugging) where possible.

## Historical context

OpenDevelop formerly contained `src/AddIns/DisplayBindings/WorkflowDesigner/`, an unbuilt
SharpDevelop-era single-process `.addin` from commit `137bb94ea0` ("Replace WorkflowDesigner
with simple designer for Workflow Foundation 4.0"). It has now been removed from OpenDevelop;
the maintained implementation is `src/OpenDevelop.AddIn/WorkflowDesigner/` in this repository.
The old implementation was:

- `WorkflowDisplayBinding.cs` — `ISecondaryDisplayBinding` that sniffs the root `xmlns` of any
  opened `.xaml` file for `http://schemas.microsoft.com/netfx/2009/xaml/activities` and, if it
  matches, attaches a secondary view.
- `WorkflowDesignerViewContent.cs` — wraps `System.Activities.Design.WorkflowDesigner` directly,
  hosts its `.View` as the WPF content and `.PropertyInspectorView` in the Properties pad, and
  round-trips `.Load()`/`.Flush()`/`.Text` against the `OpenedFile` stream.
- Targets `net4.0`, references `System.Activities.Design`, `System.Activities.Design.Base`,
  `System.Activities.Core.Design` — .NET Framework reference assemblies that ship only inside the
  Windows `%windir%\Microsoft.NET\Framework\...` GAC, not as NuGet packages for `net5.0`+.

This is the opposite shape from every other designer in OpenDevelop: WinForms/WPF/WinUI/MewUI/GTK4
all converged on the out-of-process DDP host pattern, precisely because their design surfaces had to run
somewhere other than "in-process, full .NET Framework, Windows-only." The workflow addin never
needed to make that jump because it was written for, and abandoned on, classic SharpDevelop/.NET
Framework — before OpenDevelop's LibreWPF/net10.0/cross-platform baseline existed at all.

## The actual blocker: there is no ported design surface to reuse

Every other OpenDevelop designer had *something* to converge on: WPF's engine is open-sourced
(`dotnet/wpf`, reused via `externals/vscode-wpf`), WinForms' designer surface ships as source in
`dotnet/winforms`, and the WinUI/Uno path reuses XAML Studio's renderer. Windows Workflow
Foundation's **design-time** surface never got that treatment:

- `System.Activities` (the runtime) was ported to modern .NET by UiPath as
  [CoreWF](https://github.com/UiPath/CoreWF) and is genuinely cross-platform (`net6.0`+,
  `net6.0-windows`, works on macOS/Linux for executing/tracking workflows).
- `System.Activities.Design` / `System.Activities.Core.Design` / `System.Activities.Presentation`
  (the rehosted **designer** — the actual drag/drop canvas, activity designers, expression
  editor) were **never open-sourced and never ported**. UiPath tracked this explicitly in
  [CoreWF#58 "Porting Workflow Designer to .NET Core"](https://github.com/UiPath/corewf/issues/58)
  and it was never completed; their stated direction was a browser-based designer instead of a
  ported WPF one. These assemblies remain .NET Framework 4.x-only, Windows-only, closed-source.
- Andrei Oros's own [2017 write-up](https://andreioros.com/blog/windows-workflow-foundation-2017/)
  (linked from the Rehosted-Workflow-Designer README) reaches the same conclusion: the designer
  is stuck on .NET Framework even as the runtime moves forward.

So unlike WPF/WinForms/WinUI, there is no upstream engine to link, port, or reuse as source —
`System.Activities.Design`'s actual designer-surface code isn't available to port at all. Any
"rehost the WF designer on LibreWPF" plan has to treat the classic designer as a sealed,
Windows-only, .NET Framework-only artifact and design around it, not through it.

## Decision: no existing designer to reuse — build a new one on CoreWF

The instinct to check whether `orosandrei/Rehosted-Workflow-Designer` itself could just become
"the .NET 10 designer" doesn't pan out: its `RehostedDesigner.csproj` is an old-style,
`packages.config`-based project pinned to `TargetFrameworkVersion v4.5.2` (last feature commit in
2021; everything since is dependency-bump PRs), and it references the same closed-source
`System.Activities.Design`/`.Presentation` assemblies as this repo's dead addin. There is no
version of it, or of anything else found so far, that runs on .NET 6+ — that matches CoreWF's own
unresolved [issue #58](https://github.com/UiPath/corewf/issues/58). A Windows-only `net48`
out-of-process host (HWND-embedding the real Microsoft designer, as sketched in an earlier draft
of this note) would work, but was rejected: the project-wide direction is .NET 10 across the
board with CoreWF as the workflow foundation, and a `net48`-only child would be a permanent,
Windows-only exception to that, not a stepping stone off of it.

So the only option that reaches "cross-platform, .NET 10, CoreWF-based" is to build a genuinely
new design surface — in the same sense that OpenDevelop's WPF/WinForms/WinUI designers are real,
from-scratch (or adapted-source) design surfaces, not thin wrappers around a Microsoft-owned
control. CoreWF supplies the activity/expression *model* (the `System.Activities.Statements.*`
types, `DynamicActivity`, XAML round-trip via `ActivityXamlServices`) but nothing to render a
canvas from it — that half has to be written.

```text
OpenDevelop (net10.0, LibreWPF shell, any OS)
        │ DDP over the shared pooled-host RPC transport (designer-common.md)
        ▼
WorkflowDesigner.SurfaceHost (net10.0, out-of-process child)
        - loads the .xaml activity tree via CoreWF's ActivityXamlServices (real runtime types,
          not a hand-rolled XAML parser — mirrors designer-common.md's "run the real runtime
          object, don't reimplement it" rule)
        - owns the live activity object graph, selection/edit model, and undo history for the
          document lease, same as WpfDesign.SurfaceHost owns the WPF DOM today
        - a WPF canvas in the *host* renders that activity tree as boxes/connectors (Sequence,
          Flowchart, If, custom activities, ...); the parent process gets a rendered
          frame/hit-test surface over DDP, not a live control reference
        - Toolbox catalog derived by reflecting CoreWF/project activity assemblies inside the
          host, sent to the parent as data (same shape as the other four backends' Toolbox RPC)
        - Properties pad driven by ordinary `TypeDescriptor`/reflection over the selected
          activity, marshalled through the shared Properties adapter contract, not
          System.Activities.Design's PropertyInspectorView
WorkflowDesigner.AddIn (in the main process)
        - ISecondaryDisplayBinding + view content, thin RPC client over the host, same shape as
          WpfDesign.AddIn/FormsDesigner's parent-side pieces
```

This follows the same shared-host/DDP shape every other backend already converged on — WinForms, WPF, WinUI/Uno,
MewUI and GTK4 all run the real runtime object in a child process and speak the wire protocol to
it, and there's no reason for the workflow designer to be the exception. Loading arbitrary
project-authored activity assemblies (custom activities are just CLR types the target project
references) into the child is exactly the kind of untrusted-code boundary the OOP model exists
for in the first place — it must not run in OpenDevelop's own process.

Scope carried over from the earlier draft, still true:

- **Debugging (issue #18)** is a `WorkflowApplication`/`TrackingParticipant` integration against
  CoreWF, wired into the existing DAP plumbing ([`debugging.md`](debugging.md)) — independent of
  the canvas work and cross-platform on its own.
- **Issue #19** (.NET 8/CoreWF migration) is subsumed: there's no migration path for the old
  `System.Activities.Design`-based code, so the new addin starts on .NET 10/CoreWF directly
  rather than migrating the dead code forward.

## Phased plan

1. **Retire the old OpenDevelop addin — done.** Its `WorkflowDisplayBinding` and
   `WorkflowDesignerViewContent` were wrappers around `System.Activities.Design`, the closed
   Windows-only assembly this implementation intentionally avoids.
2. **Use CoreWF as the runtime dependency — done.** The addin host references this repository's
   `UiPath.Workflow` project, so it is tested against the forked .NET 10 implementation rather
   than an independently published NuGet package.
3. **Minimal host + addin pair — done.** `WorkflowDesigner.Host` (child) and `WorkflowDesigner`
   (the addin, `ICSharpCode.WorkflowDesigner`) live under `src/OpenDevelop.AddIn/` and deploy to
   an OpenDevelop checkout or installed application.
   `WorkflowDocument` in the host loads/saves through `ActivityXamlServices.CreateBuilderReader`/
   `CreateBuilderWriter` (the same mechanism the classic in-process designer used internally) —
   confirmed end to end in a scratch probe: load → walk via `WorkflowInspectionServices` → read/
   write an `InArgument<T>` literal via reflection → round-trip save. On the addin side,
   `WorkflowDesignerHostClient`/`WorkflowPropertyAdapter` are adapted directly from
   `MewUIDesignerHostClient`/`WpfSurfaceElementPropertyAdapter` rather than written fresh, and
   `WorkflowDesignerViewContent` renders nested boxes with a real toolbox add/delete round-trip
   (collection containers plus single-child CoreWF slots such as `If.Then`/`Else` and
   `While.Body`). It also already adopted the shared-shell contracts
   from designer-common.md's 2026-08-24 "push for more code reuse" pass —
   `DesignerSelectionController`'s ordered multi-selection, `DesignerPadController` for the
   Outline/Properties bridge, `DesignerMultiPropertyAdapter` for multi-select property editing,
   and `DesignerCommandController` for the Delete command — instead of the hand-rolled
   event-wiring an earlier draft of this file used, so it doesn't start life as a sixth
   almost-identical copy of that plumbing. Versioned CoreWF XAML history powers standard
   Undo/Redo, and toolbox insertion supports both double-click and drag/drop. The workflow-level
   Arguments panel is also live: it projects the `ActivityBuilder` properties, creates, deletes,
   renames, retargets argument types and edits literal defaults through the host, and every mutation participates in the
   same history. A root-scope Variables panel provides the same create/delete/rename/type/default,
   versioned and undoable baseline for activities that expose a CoreWF `Variables` collection.
   Nested scope, custom-activity discovery and the supported literal/variable/C#/VB expression
   forms are now implemented. The `Expression…` command opens a multi-line editor for every
   writable selected property. The editor validates C# and VB syntax and semantic references through
   the host, including workflow variables, the project output and resolved project assemblies.
   It also offers lightweight contextual completion for workflow variables, CoreWF types and
   C#/VB language terms. Public types from the project output and its resolved references are
   enumerated through Roslyn metadata (without loading the user's assemblies), so selecting a
   completion produces a usable fully-qualified type name. Full Roslyn IntelliSense remains step 4 work.
4. **Toolbox + additional activity shapes**: `If` and a baseline `Flowchart` are available from
   the toolbox. Dropping into a Flowchart creates a CoreWF `FlowStep`, wires it after the previous
   step and sets the first step as `StartNode`; deleting a step reconnects its predecessor.
   Project and referenced custom activities are discovered by the host, and Flowchart nodes have
   persisted freeform positioning. `FlowDecision` is now a toolbox
   node with an editable condition and explicit True/False branch connectors; when inserted after
   a linear run it is automatically linked from that run's final unconnected `FlowStep`, while
   its outgoing branches must be chosen explicitly.
   The current sequential links are visibly marked between Flowchart steps. Select a source then
   use **Connect** (or **True →**/**False →** for a decision) to arm a connection, and click its
   target step; `Esc` cancels the gesture. The previous Ctrl-click-two-nodes interaction remains
   available. `Connect` rewires a `FlowStep.Next` edge (cycles are rejected until loop semantics
   are represented by decision nodes). Flowchart nodes now render in a freeform graph canvas with
   their real `Next`, True and False edges labelled and colored. Hold **Shift** while dragging a
   node to reposition it; positions are stored in a sibling
   `<workflow>.xaml.workflowdesigner.layout.json` sidecar so CoreWF XAML stays portable and free
   of design-only metadata. The same sidecar retains activity breakpoint paths across reopen.
   Nodes expose drag ports: teal for `Next`, green for True and red for False. Drag a port to a
   target node to create that edge; the armed source/target gesture remains available as a
   keyboard/mouse-accessible alternative. **Clear Next**, **Clear True**, and **Clear False**
   remove the selected source edge without deleting either node, participating in normal undo/redo.
   **Auto Layout** discards hand-positioned coordinates and restores the stable default grid.
   Deleting a `FlowDecision` disconnects all incoming step/branch edges rather than retaining a
   reference to a removed CoreWF node; it never guesses which of the decision's two branches should
   become the replacement edge. Deleting a `FlowStep`, conversely, retargets incoming linear and
   decision edges to that step's existing successor, preserving its already-defined fall-through.
   The toolbox is now catalogued by the CoreWF host through `design/capabilities`, rather than a
   parent-side hard-coded list, and its shared controller provides live filtering (`Ctrl+T` focuses
   the filter). The document snapshot now carries the managed project output; the host reflects
   safe, default-constructible `Activity` types from it into a Project category and also honors
   DLLs copied beside the already-built project output are supplied as Referenced activity entries
   without running MSBuild on the workbench dispatcher. This follows
   designer-common.md's shared project-type catalog contract: interface designers apply the same
   transport to custom Controls/FrameworkElements, while Workflow applies it to Activities. This
   is the CoreWF equivalent of the upstream sample's `ActivityLibraries/` probing. The toolbox
   summary makes the active/total and Project/Referenced discovery counts visible while filtering.
   In `ProjectCodeMode=Disabled` safe mode the host exposes only CoreWF's built-in catalog and
   never loads a project or reference DLL.
   The property pad accepts `@variableName` for compatible `InArgument<T>`/`OutArgument<T>`/
   `InOutArgument<T>` and direct `Activity<T>` values, producing a real CoreWF variable reference.
   Lookup follows the activity's lexical scope outward, so a nested variable shadows an equally
   named root variable. Prefix a compatible `InArgument<T>` value with `=` to store a CoreWF
   `CSharpValue<T>`, or with `=vb:` to store a CoreWF `VisualBasicValue<T>` expression. Unsupported
   expression forms remain read-only. Empty expressions and variables with a mismatched
   type are rejected before mutation. Arguments update in place and Variables restore the old item
   if a validated replacement is rejected by a collection, so a failed edit preserves metadata.
   The expression editor supports CoreWF C# and VB value expressions through Roslyn; it remains
   intentionally lightweight until language-service diagnostics are added.

### UX parity target: Visual Studio's Workflow Designer

The instruction from here on is explicit: match Microsoft's own rehosted designer's user
experience, not invent a new one. The full reference is
[`MicrosoftDocs/visualstudio-docs/docs/workflow-designer`](https://github.com/MicrosoftDocs/visualstudio-docs/tree/main/docs/workflow-designer)
(mirrored at [learn.microsoft.com/visualstudio/workflow-designer](https://learn.microsoft.com/en-us/visualstudio/workflow-designer/developing-applications-with-the-workflow-designer)) —
per-activity-designer pages (Sequence, If, Flowchart, ...), dialog-box references, and the shell
docs below. Concrete conventions to replicate, most-to-least load-bearing:

- **Three-part shell** ([`workflow-designer-shell-features.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/workflow-designer-shell-features.md)):
  a breadcrumb bar above the canvas, the canvas itself, and a shell bar below it with zoom in/out,
  fit-to-screen, and an overview map (a viewport rectangle over a thumbnail of the whole tree —
  the designer is virtualized, so undrawn regions show blank until scrolled into view once).
  `WorkflowDesignerViewContent` now supplies breadcrumb drill-in, in-place and global tree
  expansion, shared zoom/fit chrome and a clickable activity-tree overview. The overview's
  overview includes a live viewport rectangle and clicking its background scrolls the canvas to
  that vertical region.
- **Breadcrumb drill-in — implemented for activity trees** ([`how-to-use-breadcrumb-navigation.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-breadcrumb-navigation.md)):
  double-click an activity to make it the new root (fully expanded, ancestors listed as breadcrumb
  buttons); click an ancestor to go back up; chevrons expand/collapse an activity in place, with
  global Expand All/Collapse All/Restore. The baseline Flowchart currently participates as a
  sequential activity tree; once decisions and graph positioning are introduced, it should opt
  out of this tree-only in-place expansion model like `Switch` and `TryCatch`.
- **Arguments/Variables designers** ([`how-to-use-the-argument-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-argument-designer.md),
  [`how-to-use-the-variable-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-variable-designer.md)):
  buttons in the canvas's lower-left corner open a tabular grid with a `Create Argument`/
  `Create Variable` blank row (name/direction/type/default, or name/type/scope/default); Delete
  key removes the selected row. The addin now exposes an **Arguments** panel from the breadcrumb
  bar (and `Ctrl+E`, then `A`): it reads the workflow `ActivityBuilder` properties and supports
  create, delete, rename, type changes and literal default-value changes, with each operation versioned and undoable. It currently
  supports the common `String`, `Int32`, `Boolean`, `Double`, `Decimal`, `TimeSpan`, `DateTime`,
  and `Guid` types plus fully
  qualified CLR type names. The adjacent **Variables** panel (and `Ctrl+E`, then `V`) provides
  create/delete/rename/type/literal-default editing for the root activity's `Variables` collection
  and displays its scope. Nested activity scopes are now enumerated and selected by stable
  structural path in the Variables panel, so create/update/delete applies to the chosen owning
  activity or State (the current activity is selected by default). The scope dropdown is supplied
  by the host and therefore lists only objects with a real CoreWF `Variables` collection, rather
  than every projected tree node. The Arguments panel also supports
   CoreWF `In`/`Out`/`InOut` directions, persisted as the matching generic argument type; only
   `In` accepts a default, either a literal, a `=C#`, or a `=vb:` expression. Variables accept
   the same literal/C#/VB default forms and preserve them through CoreWF XAML round-tripping.
- **Expression editor — baseline done** ([`how-to-use-the-expression-editor.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-expression-editor.md)):
  `Expression…` in the designer command strip opens a property picker and multi-line editor for
  the selected activity (or Flow Decision). It writes through the normal versioned property RPC,
  so literal values, `@variableName`, `=C#`, and `=vb:` expressions receive the same validation,
  undo/redo and persistence as inline property edits. This is deliberately a lightweight editor:
  **Validate** performs host-side Roslyn compilation validation for both C# and VB without mutating
  the document. It supplies the selected property's lexically visible workflow variables and
  readable workflow `In`/`InOut` arguments at their declared types (with local variables
  shadowing equally named arguments), plus trusted platform,
  project-output and resolved reference assemblies, so syntax, unknown identifiers, invalid
  members and available referenced types are checked.

  Expression *editing* and validation are deliberately separated from execution for now. The
  current CoreWF/UiPath.Workflow runtime can persist and validate `CSharpValue<T>` and
  `VisualBasicValue<T>`, but its generated-expression-root execution path still throws for a
  dynamically loaded `ActivityBuilder` workflow (the same failure tracked by
  [CoreWF#226](https://github.com/UiPath/CoreWF/issues/226)). The isolated host therefore runs
  literal and variable-based workflows today, and reports a clear, actionable host diagnostic
  before starting a workflow that requires expression compilation. It never pretends that editor
  validation means that expression execution is available. This boundary also keeps the expression
  compiler out of the OpenDevelop process.
  **Complete** supplies matching workflow variables and readable workflow arguments, CoreWF activity types, public project/reference
  types (as fully-qualified names, read through Roslyn metadata without loading the DLLs) and language terms and
  inserts a selected result at the caret; after `Math.`, `DateTime.` and the language's string type
  it also supplies matching public static members (for example `Math.Abs`), including fully-qualified
  project/reference types read from Roslyn metadata. It also offers instance members for the
  supported typed workflow variables (for example `count.ToString`) and for variables of public
  project/reference model types, including constructed generic variables such as `List<Int32>`.
  `Ctrl+Space` invokes
  the same completion action. It is
  intentionally not full Roslyn IntelliSense. Enumerable variables also surface common LINQ
  extension shapes such as `Where`, `Select` and `OrderBy` in this lightweight completion list;
  expression validation otherwise retains its explicit compilation context and does not imply
  an automatic project-wide import set.
- **Toolbox interaction details — done** ([`how-to-add-activities-to-the-toolbox.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-add-activities-to-the-toolbox.md)):
  empty containers now show **"Drop activity here"** hint text instead of rendering blank, and an
  activity's `DisplayName` is directly editable on its header via double-click (a `TextBlock` that
  swaps for a `TextBox`, committing on Enter/lost-focus, reverting on Escape) — not only through
  the Properties pad — matching `sequence-activity-designer.md`'s callout for `Sequence`.
  Dropping `If`, `While`, or `DoWhile` also seeds a `false` condition, and `Delay` receives a
  zero duration, so CoreWF can validate and
  render the new node immediately; it can then be replaced with the intended expression. The
  property projection treats both `InArgument<T>` and direct `Activity<T>` value slots as editable
  expressions, so `If`, `While`, and `DoWhile` conditions can be edited as literals, `=C#`, or
  `=vb:`.
  `TryCatch` also exposes its non-activity `Catch<TException>` delegate surface explicitly:
  **Catch (Exception)** creates a real `Catch<Exception>` branch, projects as a stable Catch node,
  and accepts one handler activity through normal toolbox insertion. Its editable **Exception type**
  and **Exception name** properties rebuild or name the matching typed delegate while retaining that handler (and reject duplicate
  catch types). Removing the handler or Catch
  node mutates the actual CoreWF delegate/collection and uses the normal XAML history.
  Generic `Switch<T>` is represented initially by an explicit **Switch (Int32)** toolbox entry,
  which supplies a valid `Expression` default. **Switch Case** creates the next unused integer
  case and **Switch Default** creates the default branch. A Case node's **Case value** property
  can be changed to any unused integer without replacing its handler. Each branch owns a real `Sequence`
  handler and is projected with stable `$case-<value>`/`$default` paths for normal nested editing,
  deletion, undo/redo and CoreWF XAML round-tripping.
  `Pick` now exposes its non-activity `PickBranch` collection in the same manner: **Pick Branch**
  creates a branch with stable `$pick-<index>` identity, and normal toolbox insertion fills its
  Trigger then Action slots. Branches and either slot can be deleted independently; the projection
  round-trips the real CoreWF `PickBranch` objects rather than flattening them into an activity list.
  The toolbox also supplies **For Each (Int32)** for the otherwise-open generic `ForEach<T>` shape.
  It starts with an editable empty C# `Values` expression seed (`new int[0]`) and exposes the typed delegate
  Body as a stable `$foreach-body` node. Its real typed `item` delegate argument is exposed as an
  editable **Item name** property and is available to expression validation and completion within
  that body, so normal toolbox insertion, nested editing, deletion,
  debugging paths and CoreWF XAML round-tripping all work without treating the delegate as a hidden
  implementation child.
  **Parallel For Each (Int32)** applies the same `Values` seed and `$parallelforeach-body`
  delegate treatment, preserving its CoreWF parallel scheduling semantics while making the body
  editable in the normal designer tree.
  **StateMachine** now projects its non-activity `State` collection as stable `$state-<index>`
  nodes. **State** creates and selects the initial state when needed; its DisplayName, final
  flag and Initial selection are editable (selecting Initial moves the single initial state), and
  normal toolbox insertion fills its Entry then Exit activity slots.
  Final states reject Exit and Transition insertion, and a State cannot be marked final until an
  existing Exit/Transition has been removed, matching CoreWF's validation rules before runtime.
  When deleting the current initial State, the designer selects another non-final State when one
  exists; it never promotes a Final State to Initial, which CoreWF rejects.
  These nodes round-trip the real CoreWF `State` objects rather than flattening state behavior
  into ordinary activity children. **Transition** creates stable child nodes under its source
  state; its target is selected by State name in the expression editor (persisted as its stable
  `$state-<index>` node id), and its Trigger then Action slots accept normal toolbox activities.
  Removing a transition or either slot mutates the matching
  CoreWF object; removing a target state safely disconnects inbound transitions.
  A transition is event-driven: a `Delay` Trigger (zero duration by default) provides a simple
  executable transition path to another or final state; an absent Trigger intentionally waits
  for an external event rather than auto-advancing. Because the current Run command has no
  external event source, it detects an initial State whose transitions all lack Trigger and
  reports this actionable condition before starting instead of leaving a 30-second timeout.
  State-owned variables participate in the normal Variables panel and are lexically available to
  that state's Entry/Exit/Transition expression validation and completion.
  Breakpoints within an Entry/Exit or Transition Trigger/Action retain their virtual State path
  and resolve to the corresponding CoreWF runtime activity when the workflow runs.
- **Zoom/Fit shell chrome — done**: `WorkflowDesignerViewContent` now hosts its canvas inside the
  shared `ICSharpCode.SharpDevelop.Widgets.DesignerCanvas` control every other backend (WinForms,
  WPF, WinUI/Uno, GTK4, MewUI) already uses for its zoom/fit/gridlines/theme toolbar
  (designer-common.md) — the closest in-repo equivalent to VS's shell-bar docs
  (`workflow-designer-shell-features.md`), reused as-is rather than building bespoke chrome.
  Gridlines/Theme/ShowNames/DesignSize are left off (`Capabilities = Zoom | Fit`) since none map
  to an activity tree. Breadcrumb drill-in and a clickable overview are implemented; coupling
  the overview includes a live `ScrollViewer` viewport rectangle and background click-to-scroll.
- **Keyboard shortcuts** ([`keyboard-shortcuts-in-the-workflow-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/keyboard-shortcuts-in-the-workflow-designer.md)):
  a full `Ctrl+E, <letter>` shortcut family (Arguments, Variables, expand/collapse, flowchart
  connect, next-item focus, ...). `Ctrl+E`, then `A` opens Arguments, `V` opens root-scope
  Variables, `E` opens the selected item's expression editor, `X` expands the current drill-in
  tree, `C` collapses it and `R` restores the root view. Additional workflow-specific commands
  are added with their corresponding panels/features.

None of the per-activity-designer pages (`if-activity-designer.md`, `flowchart-activity-designer.md`,
`assign-activity-designer.md`, etc.) are reproduced here; read the relevant one directly from the
docs tree above when implementing that activity's box in step 4 — each documents its exact
property surface and designer-specific behavior (e.g. `Flowchart`'s connector gestures).
5. **CoreWF execution/tracking integration** for #18, independent of 2–4 and shippable on all
   platforms once the runtime dependency from step 2 is in place. The execution baseline is now
   live: **Run** asks the isolated host to invoke the current `Activity` with `WorkflowInvoker`
   and reports completion, output values, or the underlying runtime/validation error in the
   designer diagnostic bar. It also returns CoreWF `ActivityStateRecord` events (shown as a
   compact activity/state trace). Run is asynchronous in the addin and **Stop** aborts the active
  `WorkflowApplication` through the host. The activity-tree command strip also provides
   **Breakpoint**, **Continue**, **Step**, **Step Over**, and **Step Out**: breakpoints are stored as structural activity paths,
   resolve to CoreWF runtime activity IDs at run start, and synchronously pause execution at an
   `Executing` tracking record. Continue releases that pause; Step pauses at the next activity,
   Step Over pauses at the next activity in the same structural scope, and Step Out pauses after
   the current parent scope is left. The paused activity is selected in the designer and breakpoints are
   visibly marked. Virtual handler paths for `Switch` cases/default and `TryCatch` handlers are
   resolved to their real runtime activities too, so breakpoints inside these designer-only branch
   nodes pause at the correct selected element. This is workflow-level stepping, not yet DAP source-level step-in/over/out or
   editor breakpoint integration.
6. **Non-Windows verification**: since the new addin targets net10.0 cross-platform rather than
   being Windows-gated, explicitly test load/render/edit/save on macOS (matching this repo's
   LibreWPF baseline) as part of accepting each phase above, not as an afterthought.

The CoreWF host/addin baseline and the interaction slices recorded above are implemented; this
note remains the architecture and phased roadmap for the unfinished workflow-specific features.
